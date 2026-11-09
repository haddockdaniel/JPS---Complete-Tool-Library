using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jps.Core.Data;

namespace Jps.Tools.Tools.ArchiveBillPrint
{
    /// <summary>What the user picked in step 2. Null/empty members are criteria that aren't used.</summary>
    internal sealed class SelectionCriteria
    {
        public DateTime? DateFrom { get; set; }
        public DateTime? DateTo { get; set; }
        public int? NumberFrom { get; set; }
        public int? NumberTo { get; set; }
        public string Client { get; set; }
        public string Matter { get; set; }
        public List<int> CsvBills { get; set; }
        public bool BalanceOnly { get; set; }
        public bool UseOr { get; set; }
    }

    /// <summary>One selected invoice for one matter (a multi-matter invoice gives one row per matter, as in the original).</summary>
    internal sealed class BillRow
    {
        public int BillNo { get; set; }
        public int MatSys { get; set; }
        public int CliSys { get; set; }
        public string ClientCode { get; set; }
        public string ClientName { get; set; }
        public string MatterCode { get; set; }
        public string MatterName { get; set; }
        public string BillDate { get; set; }
    }

    /// <summary>Everything to write for one invoice: its PDF file(s) and the base names its expense attachments hang off.</summary>
    internal sealed class BillJob
    {
        public int BillNo { get; set; }
        public BillRow First { get; set; }
        public List<string> PdfPaths { get; } = new List<string>();
        public List<string> AttachmentNames { get; } = new List<string>();
    }

    internal sealed class PrintPlan
    {
        public string Folder { get; set; }
        public bool Attachments { get; set; }
        public int SelectedInvoices { get; set; }
        public List<BillRow> NoArchive { get; } = new List<BillRow>();
        public int AlreadyPrinted { get; set; }
        public List<BillJob> ThisRun { get; } = new List<BillJob>();
        /// <summary>Invoices that still need printing but are over the per-run limit.</summary>
        public int LeftForNextRun { get; set; }
    }

    internal sealed class PrintOutcome
    {
        public int InvoicesPrinted { get; set; }
        public int PdfFilesWritten { get; set; }
        public int AttachmentsWritten { get; set; }
        public int Failed { get; set; }
        public bool Cancelled { get; set; }
        public int NotReached { get; set; }
        public DataTable Problems { get; set; }
    }

    /// <summary>All database reads and file writing for the Archive Bill Bulk Print tool (read-only against Juris).</summary>
    internal sealed class ArchiveBillPrintService
    {
        public const int MaxInvoicesPerRun = 2500;
        public const string DefaultNaming = "[Client Code]-[Matter Code]-[Bill-To Nickname]-[Invoice Number].pdf";

        /// <summary>
        /// The original tool's field names. Still accepted (patterns people already use keep working); the plain-English
        /// names in <see cref="FriendlyTokens"/> are turned into these by <see cref="Normalize"/> before anything else.
        /// [Clisys] / [Matsys] (Juris's internal client/matter numbers) are accepted but no longer offered on screen.
        /// </summary>
        public static readonly string[] Tokens =
        {
            "[ClientNum]", "[MatterNum]", "[ClientName]", "[MatterName]", "[BillNum]", "[BillDate]",
            "[Clisys]", "[Matsys]", "[BilAdrNick]", "[NowDate]", "[NowTime]",
        };

        /// <summary>Plain-English field names shown on screen, and the original field each one stands for.</summary>
        public static readonly KeyValuePair<string, string>[] FriendlyTokens =
        {
            new KeyValuePair<string, string>("[Client Code]", "[ClientNum]"),
            new KeyValuePair<string, string>("[Matter Code]", "[MatterNum]"),
            new KeyValuePair<string, string>("[Client Name]", "[ClientName]"),
            new KeyValuePair<string, string>("[Matter Name]", "[MatterName]"),
            new KeyValuePair<string, string>("[Invoice Number]", "[BillNum]"),
            new KeyValuePair<string, string>("[Invoice Date]", "[BillDate]"),
            new KeyValuePair<string, string>("[Bill-To Nickname]", "[BilAdrNick]"),
            new KeyValuePair<string, string>("[Print Date and Time]", "[NowTime]"),
            new KeyValuePair<string, string>("[Print Date]", "[NowDate]"),
        };

        /// <summary>The pattern with every plain-English field name replaced by the original field name (any case).</summary>
        public static string Normalize(string naming)
        {
            var text = naming ?? "";
            foreach (var pair in FriendlyTokens)
                text = ReplaceToken(text, pair.Key, pair.Value);
            return text;
        }

        // Bills that went to A/R and were not later reversed/voided (LedgerHistory types A/B/C).
        private const string BilledFilter =
            "lhtype in ('3', '4') and lhbillnbr not in (select lhbillnbr from ledgerhistory where lhtype in ('A', 'B', 'C'))";

        private const string Cols =
            @"matsysnbr, clisysnbr, dbo.jfn_FormatClientCode(clicode) as clicode, clireportingname,
              dbo.jfn_FormatMatterCode(MatCode) as matcode, matreportingname, arbillnbr, convert(varchar(10), arbilldate, 101) as BillDate";

        private const string ExpenseAttachmentJoins =
            @"from Attachment aa
              inner join ExpenseEntryAttachment ea on ea.AttachmentID = aa.id
              inner join ExpenseEntry ee on ee.EntryID = ea.EntryID
              inner join ExpenseEntryLink el on ee.EntryID = el.EntryID
              inner join BilledExpenses be on el.EBDID = be.beid
              where aa.AttachmentType = 0";

        private readonly JurisSession _session;

        public ArchiveBillPrintService(JurisSession session)
        {
            _session = session;
        }

        // ---------- lists ----------

        // Clients with at least one bill that wasn't reversed. Formats each client code once (the old version formatted
        // it three times per client and joined Client twice): about 0.4 s instead of 2.8 s on a 45,000-client firm.
        public const string ClientListSql =
            @"select K, K as Code, Name from (
                select dbo.jfn_FormatClientCode(c.CliCode) as K, c.CliReportingName as Name from Client c
                where c.CliSysNbr in (select m.MatCliNbr from LedgerHistory lh inner join ARBill on ArBillNbr = lh.LHBillNbr
                                      inner join Matter m on m.MatSysNbr = lh.LHMatter
                                      where lh.LHType in ('3', '4')
                                        and not exists (select 1 from LedgerHistory r where r.LHBillNbr = lh.LHBillNbr and r.LHType in ('A', 'B', 'C')))) x
              order by K";

        public const string MatterListSql =
            @"select dbo.jfn_FormatMatterCode(matcode) as K, dbo.jfn_FormatMatterCode(matcode) as Code, matreportingname as Name from matter
              where matsysnbr in (select distinct matsysnbr from ledgerhistory inner join arbill on arbillnbr = lhbillnbr
                  inner join matter on matsysnbr = lhmatter inner join client on clisysnbr = matclinbr
                  where " + BilledFilter + @" and dbo.jfn_FormatClientCode(clicode) = @Cli)
              order by dbo.jfn_FormatMatterCode(matcode)";

        // ---------- validation helpers ----------

        /// <summary>Same rule as the original (ends in .pdf; only tokens, dashes and spaces), but tokens are matched in any case.</summary>
        public static bool IsValidNaming(string naming)
        {
            naming = Normalize(naming);
            var text = naming.Trim();
            if (!text.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return false;
            text = text.Substring(0, text.Length - 4);
            foreach (var token in Tokens)
                text = Regex.Replace(text, Regex.Escape(token), "", RegexOptions.IgnoreCase);
            return text.Replace("-", "").Replace(" ", "").Length == 0 && UsesAnyToken(naming);
        }

        public static bool Uses(string naming, string token) => (naming ?? "").IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool UsesAnyToken(string naming) => Tokens.Any(t => Uses(naming, t));

        /// <summary>Invoice numbers from a CSV/text file: every comma/space/tab/line-separated whole number (anything else is ignored).</summary>
        public static List<int> ParseInvoiceNumbers(string text)
        {
            var result = new List<int>();
            foreach (var token in (text ?? "").Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                    result.Add(n);
            }
            return result.Distinct().ToList();
        }

        // ---------- selection ----------

        public List<BillRow> SelectBills(SelectionCriteria c)
        {
            var parameters = new List<SqlParameter>();
            var clauses = new List<string>();
            if (c.DateFrom.HasValue && c.DateTo.HasValue)
            {
                clauses.Add("(arbilldate >= @From and arbilldate < dateadd(day, 1, @To))");
                parameters.Add(new SqlParameter("@From", SqlDbType.DateTime) { Value = c.DateFrom.Value.Date });
                parameters.Add(new SqlParameter("@To", SqlDbType.DateTime) { Value = c.DateTo.Value.Date });
            }
            if (c.NumberFrom.HasValue && c.NumberTo.HasValue)
            {
                clauses.Add("(arbillnbr between @NumFrom and @NumTo)");
                parameters.Add(Sql.P("@NumFrom", c.NumberFrom.Value));
                parameters.Add(Sql.P("@NumTo", c.NumberTo.Value));
            }
            if (!string.IsNullOrEmpty(c.Client))
            {
                parameters.Add(Sql.P("@Cli", c.Client));
                if (!string.IsNullOrEmpty(c.Matter))
                {
                    clauses.Add("(dbo.jfn_FormatClientCode(clicode) = @Cli and dbo.jfn_FormatMatterCode(matcode) = @Mat)");
                    parameters.Add(Sql.P("@Mat", c.Matter));
                }
                else
                {
                    clauses.Add("(dbo.jfn_FormatClientCode(clicode) = @Cli)");
                }
            }
            if (c.CsvBills != null)
            {
                clauses.Add("(arbillnbr in " + Sql.IdSelect("@CsvBills") + ")");
                parameters.Add(Sql.IdList("@CsvBills", c.CsvBills));
            }

            var criteria = clauses.Count == 0 ? null : "(" + string.Join(c.UseOr ? " or " : " and ", clauses) + ")";
            const string balanceFrom = "from armatalloc inner join arbill on arbillnbr = armbillnbr inner join matter on matsysnbr = armmatter inner join client on clisysnbr = matclinbr";
            const string ledgerFrom = "from ledgerhistory inner join arbill on arbillnbr = lhbillnbr inner join matter on matsysnbr = lhmatter inner join client on clisysnbr = matclinbr";

            string sql;
            if (c.BalanceOnly && criteria == null)
                sql = "select distinct " + Cols + " " + balanceFrom + " where ARMBalDue <> 0 order by arbillnbr";
            else if (c.BalanceOnly && !c.UseOr)
                sql = "select distinct " + Cols + " " + balanceFrom + " where ARMBalDue <> 0 and " + criteria + " order by arbillnbr";
            else if (c.BalanceOnly)
                sql = "select distinct matsysnbr, clisysnbr, clicode, clireportingname, matcode, matreportingname, arbillnbr, BillDate from ("
                      + " select distinct " + Cols + " " + balanceFrom + " where ARMBalDue <> 0"
                      + " union all select distinct " + Cols + " " + ledgerFrom + " where " + criteria + " and " + BilledFilter
                      + ") ffc order by arbillnbr";
            else
                sql = "select distinct " + Cols + " " + ledgerFrom + " where " + BilledFilter
                      + (criteria == null ? "" : " and " + criteria) + " order by arbillnbr";

            return _session.Query(sql, parameters.ToArray()).Rows().Select(r => new BillRow
            {
                BillNo = Convert.ToInt32(r["arbillnbr"]),
                MatSys = Convert.ToInt32(r["matsysnbr"]),
                CliSys = Convert.ToInt32(r["clisysnbr"]),
                ClientCode = Sql.Text(r, "clicode"),
                ClientName = Sql.Text(r, "clireportingname"),
                MatterCode = Sql.Text(r, "matcode"),
                MatterName = Sql.Text(r, "matreportingname"),
                BillDate = Sql.Text(r, "BillDate"),
            }).ToList();
        }

        // ---------- planning ----------

        /// <summary>
        /// Works out what to write: drops invoices with no archive image, skips invoices whose files already exist (non-empty),
        /// and takes at most <see cref="MaxInvoicesPerRun"/> of the rest.
        /// </summary>
        public PrintPlan BuildPlan(List<BillRow> rows, string folder, string naming, bool attachments, DateTime now)
        {
            var plan = new PrintPlan { Folder = folder, Attachments = attachments };
            var billNumbers = rows.Select(r => r.BillNo).Distinct().ToList();
            plan.SelectedInvoices = billNumbers.Count;

            var archived = GetArchivedBills(billNumbers);
            var nicknames = Uses(naming, "[BilAdrNick]") ? GetNicknames(rows.Select(r => r.MatSys).Distinct().ToList()) : null;
            var attachmentNames = attachments ? GetAttachmentNames(billNumbers.Where(archived.Contains).ToList()) : new Dictionary<int, List<string>>();

            var waiting = new List<BillJob>();
            foreach (var bill in rows.GroupBy(r => r.BillNo).OrderBy(g => g.Key))
            {
                if (!archived.Contains(bill.Key))
                {
                    plan.NoArchive.Add(bill.First());
                    continue;
                }
                var job = new BillJob { BillNo = bill.Key, First = bill.First() };
                foreach (var row in bill)
                {
                    List<string> nicks = null;
                    if (nicknames != null && (!nicknames.TryGetValue(row.MatSys, out nicks) || nicks.Count == 0))
                        nicks = new List<string> { "" }; // FIX: no bill copies -> empty nickname instead of a crash
                    foreach (var nick in nicks ?? new List<string> { "" })
                    {
                        var path = Path.Combine(folder, BuildFileName(naming, row, nick, now));
                        if (!job.PdfPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            job.PdfPaths.Add(path);
                    }
                }
                if (attachmentNames.TryGetValue(bill.Key, out var names))
                    job.AttachmentNames.AddRange(names);

                if (NeedsWork(job))
                    waiting.Add(job);
                else
                    plan.AlreadyPrinted++;
            }

            plan.ThisRun.AddRange(waiting.Take(MaxInvoicesPerRun));
            plan.LeftForNextRun = waiting.Count - plan.ThisRun.Count;
            return plan;
        }

        private static bool NeedsWork(BillJob job) =>
            job.PdfPaths.Any(p => !IsWritten(p)) || job.PdfPaths.Any(p => job.AttachmentNames.Any(a => !IsWritten(AttachmentPath(p, a))));

        /// <summary>A file counts as already printed only if it exists and isn't empty (FIX: a 0-byte file from a failed run is reprinted).</summary>
        public static bool IsWritten(string path)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }

        public static string AttachmentPath(string pdfPath, string attachmentName) =>
            pdfPath.Substring(0, pdfPath.Length - 4) + "-" + Sanitize(attachmentName, "attachment");

        /// <summary>Invoice numbers that have an archive image in the JBills database (own connection; the shared one is never touched).</summary>
        private HashSet<int> GetArchivedBills(List<int> billNumbers)
        {
            var result = new HashSet<int>();
            if (billNumbers.Count == 0) return result;
            using (var connection = _session.CreateConnection(_session.JBillsDbName))
            {
                connection.Open();
                using (var command = new SqlCommand(
                           "select distinct BASInvoiceNbr from BillArchiveSegment where BASInvoiceNbr <> 0 and BASInvoiceNbr in " + Sql.IdSelect("@Bills"),
                           connection) { CommandTimeout = 0 })
                {
                    command.Parameters.Add(Sql.IdList("@Bills", billNumbers));
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            result.Add(Convert.ToInt32(reader[0]));
                }
            }
            return result;
        }

        private Dictionary<int, List<string>> GetNicknames(List<int> matters)
        {
            var table = _session.Query(
                @"select distinct Matter.MatSysNbr, BilAdrNickName from BillTo
                  inner join BillCopy on BillTo.BillToSysNbr = BillCopy.BilCpyBillTo
                  inner join BillingAddress on BillingAddress.BilAdrSysNbr = BillCopy.BilCpyBilAdr
                  inner join Matter on BillTo.BillToSysNbr = Matter.MatBillTo
                  where Matter.MatSysNbr in " + Sql.IdSelect("@Mats"),
                Sql.IdList("@Mats", matters));
            return table.Rows()
                .GroupBy(r => Convert.ToInt32(r["MatSysNbr"]))
                .ToDictionary(g => g.Key, g => g.Select(r => Sql.Text(r, "BilAdrNickName")).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());
        }

        /// <summary>Attachment names only (no file contents) so the plan can tell which ones are already saved.</summary>
        private Dictionary<int, List<string>> GetAttachmentNames(List<int> billNumbers)
        {
            if (billNumbers.Count == 0) return new Dictionary<int, List<string>>();
            var table = _session.Query(
                "select be.bebillnbr, aa.name " + ExpenseAttachmentJoins + " and be.bebillnbr in " + Sql.IdSelect("@Bills"),
                Sql.IdList("@Bills", billNumbers));
            return table.Rows()
                .GroupBy(r => Convert.ToInt32(r["bebillnbr"]))
                .ToDictionary(g => g.Key, g => g.Select(r => Sql.Text(r, "name")).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        /// <summary>Loads the attachment files for ONE invoice (FIX: the original loaded every attachment in the database at once).</summary>
        private List<KeyValuePair<string, byte[]>> LoadAttachments(int billNo)
        {
            return _session.Query("select aa.name, aa.attachmentobject " + ExpenseAttachmentJoins + " and be.bebillnbr = @Bill", Sql.P("@Bill", billNo))
                .Rows()
                .Where(r => r["attachmentobject"] is byte[])
                .Select(r => new KeyValuePair<string, byte[]>(Sql.Text(r, "name"), (byte[])r["attachmentobject"]))
                .ToList();
        }

        // ---------- file names ----------

        public static string BuildFileName(string naming, BillRow row, string nickname, DateTime now)
        {
            var name = naming.Trim();
            name = name.Substring(0, name.Length - 4); // strip only the ".pdf" extension (the original removed "pdf" anywhere)
            name = ReplaceToken(name, "[ClientNum]", row.ClientCode);
            name = ReplaceToken(name, "[MatterNum]", row.MatterCode);
            name = ReplaceToken(name, "[ClientName]", row.ClientName);
            name = ReplaceToken(name, "[MatterName]", row.MatterName);
            name = ReplaceToken(name, "[BillNum]", row.BillNo.ToString(CultureInfo.InvariantCulture));
            name = ReplaceToken(name, "[BillDate]", (row.BillDate ?? "").Replace("/", "-"));
            name = ReplaceToken(name, "[Clisys]", row.CliSys.ToString(CultureInfo.InvariantCulture));
            name = ReplaceToken(name, "[Matsys]", row.MatSys.ToString(CultureInfo.InvariantCulture));
            name = ReplaceToken(name, "[BilAdrNick]", nickname ?? "");
            name = ReplaceToken(name, "[NowDate]", now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));
            name = ReplaceToken(name, "[NowTime]", now.ToString("MM-dd-yyyy hh mm tt", CultureInfo.InvariantCulture));
            return Sanitize(name, "Invoice " + row.BillNo.ToString(CultureInfo.InvariantCulture)) + ".pdf";
        }

        private static string ReplaceToken(string text, string token, string value) =>
            Regex.Replace(text, Regex.Escape(token), _ => value ?? "", RegexOptions.IgnoreCase);

        private static readonly HashSet<char> Illegal = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("%*/\\#@"));

        /// <summary>Removes characters Windows (or the UI note) doesn't allow in file names; falls back when nothing is left.</summary>
        public static string Sanitize(string name, string fallback)
        {
            var clean = new string((name ?? "").Where(ch => !Illegal.Contains(ch)).ToArray()).Trim().TrimEnd('.').Trim();
            return clean.Length == 0 ? fallback : clean;
        }

        // ---------- printing ----------

        /// <summary>
        /// Writes the planned files, one invoice at a time. Each invoice is rendered once; extra copies (bill-to nicknames,
        /// other matters on the same invoice) are file copies. Stops between invoices when <paramref name="cancel"/> is set.
        /// Failures never stop the run and are never counted as printed.
        /// </summary>
        public PrintOutcome Print(PrintPlan plan, IArchiveBillRenderer renderer, Action<int, int> progress, CancellationToken cancel)
        {
            var outcome = new PrintOutcome { Problems = NewProblemTable() };
            foreach (var row in plan.NoArchive)
                AddProblem(outcome.Problems, row, "Juris has no archived copy of invoice " + row.BillNo + ", so it can't be printed");

            Directory.CreateDirectory(plan.Folder);
            if (plan.ThisRun.Any(j => j.PdfPaths.Any(p => !IsWritten(p))))
                renderer.Open(); // a logon failure stops the run before anything is written
            for (var i = 0; i < plan.ThisRun.Count; i++)
            {
                if (cancel.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    outcome.NotReached = plan.ThisRun.Count - i;
                    break;
                }
                progress?.Invoke(i + 1, plan.ThisRun.Count);
                var job = plan.ThisRun[i];
                try
                {
                    var missing = job.PdfPaths.Where(p => !IsWritten(p)).ToList();
                    if (missing.Count > 0)
                    {
                        var source = job.PdfPaths.FirstOrDefault(IsWritten);
                        if (source == null)
                        {
                            source = missing[0];
                            missing.RemoveAt(0);
                            RenderSafely(renderer, job.BillNo, source);
                            outcome.PdfFilesWritten++;
                        }
                        foreach (var copy in missing)
                        {
                            File.Copy(source, copy, true);
                            outcome.PdfFilesWritten++;
                        }
                        outcome.InvoicesPrinted++;
                    }

                    if (plan.Attachments && job.AttachmentNames.Count > 0)
                    {
                        var targets = job.PdfPaths.SelectMany(p => job.AttachmentNames.Select(a => AttachmentPath(p, a))).Where(p => !IsWritten(p)).ToList();
                        if (targets.Count > 0)
                        {
                            foreach (var file in LoadAttachments(job.BillNo))
                                foreach (var pdf in job.PdfPaths)
                                {
                                    var path = AttachmentPath(pdf, file.Key);
                                    if (IsWritten(path)) continue;
                                    File.WriteAllBytes(path, file.Value);
                                    outcome.AttachmentsWritten++;
                                }
                        }
                    }
                }
                catch (Exception ex) // one bad invoice (render, file or attachment problem) must not stop the rest; it is reported
                {
                    outcome.Failed++;
                    try
                    {
                        // Full details for support; the user sees the short message in the problems list.
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "JPS Tool Library.log"),
                            DateTime.Now + "  Archive Bill Print, invoice " + job.BillNo + ": " + ex + Environment.NewLine);
                    }
                    catch (IOException) { }
                    AddProblem(outcome.Problems, job.First, "Invoice " + job.BillNo + " couldn't be printed: " + ex.Message);
                }
            }
            return outcome;
        }

        /// <summary>Renders to a temporary file first, so an interrupted or failed render never leaves a file that looks printed.</summary>
        private static void RenderSafely(IArchiveBillRenderer renderer, int billNo, string path)
        {
            var temp = path + ".partial";
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                renderer.RenderToFile(billNo, temp);
                if (!IsWritten(temp))
                    throw new ArchiveRenderException("the archived copy is empty");
                if (File.Exists(path)) File.Delete(path); // only an empty leftover can be here
                File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public static DataTable NewProblemTable()
        {
            var table = new DataTable("Problems");
            table.Columns.Add("Invoice Number", typeof(int));
            table.Columns.Add("Client", typeof(string));
            table.Columns.Add("Matter", typeof(string));
            table.Columns.Add("Problem", typeof(string));
            return table;
        }

        private static void AddProblem(DataTable table, BillRow row, string problem) =>
            table.Rows.Add(row.BillNo, row.ClientCode + "  " + row.ClientName, row.MatterCode + "  " + row.MatterName, problem);
    }

    /// <summary>Runs work on a dedicated STA thread and waits for it (Juris's report engine is WinForms/ActiveReports based).</summary>
    internal static class StaThread
    {
        public static T Run<T>(Func<T> work)
        {
            var result = default(T);
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "Archive bill printing" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
    }
}
