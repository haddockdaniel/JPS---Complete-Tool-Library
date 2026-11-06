using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.VoucherImport
{
    /// <summary>
    /// Read-only checks of every parsed voucher against Juris, and the one-transaction creation of the unposted
    /// Payment Voucher batch. Nothing here posts.
    /// </summary>
    public sealed class VoucherImportService
    {
        private const int VoucherBatchDocClass = 7200;

        private readonly JurisSession _session;

        public VoucherImportService(JurisSession session)
        {
            _session = session;
        }

        // ================================================================== checks (read-only)

        private sealed class VendorInfo
        {
            public int SysNbr; public string Code; public string Name; public int? DiscAcct; public string DefaultAP; public int? DefaultDist; public bool NoVouchers;
        }

        private sealed class MatterInfo
        {
            public int SysNbr; public string Status; public string Lock; public string ExpSch; public bool RequiresTask;
        }

        private sealed class TrustInfo
        {
            public int Matter; public string Bank; public decimal Balance; public decimal Committed;
        }

        // Per-check caches (a file usually repeats the same vendors, matters and codes).
        private Dictionary<string, List<VendorInfo>> _vendors;
        private Dictionary<string, bool> _clients;
        private Dictionary<string, List<MatterInfo>> _matters;
        private Dictionary<string, string> _expCodes;
        private Dictionary<string, decimal?> _rates;
        private Dictionary<string, string> _taskCodes;
        private Dictionary<string, string> _employees;
        private Dictionary<string, string> _apAccounts;
        private Dictionary<string, int> _glAccounts;
        private Dictionary<string, TrustInfo> _trust;

        /// <summary>Parses the file and checks every voucher completely; every problem is listed with its row.</summary>
        public ImportCheck Check(string path, DuplicateMode mode, string suffix)
        {
            var check = new ImportCheck { FilePath = path, Mode = mode, Suffix = suffix };
            VoucherImportParser.Parse(path, check);

            _vendors = new Dictionary<string, List<VendorInfo>>();
            _clients = new Dictionary<string, bool>();
            _matters = new Dictionary<string, List<MatterInfo>>();
            _expCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _rates = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
            _taskCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _employees = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _trust = new Dictionary<string, TrustInfo>(StringComparer.OrdinalIgnoreCase);
            _apAccounts = _session.Query("select APACode from APAccount").Rows()
                .Select(r => Sql.Text(r, "APACode")).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
            _glAccounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _session.Query("select ChtSysNbr, dbo.jfn_FormatChartOfAccount(ChtSysNbr) as Acct from ChartOfAccounts").Rows())
            {
                var acct = Sql.Text(r, "Acct");
                if (acct.Length > 0 && !_glAccounts.ContainsKey(acct)) _glAccounts[acct] = Convert.ToInt32(r["ChtSysNbr"]);
            }

            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in check.Vouchers)
            {
                var errors = new List<Tuple<int, string>>();
                CheckVoucher(v, check, errors);

                // The same vendor + invoice number must not be created twice by this file (e.g. INV_1 from a suffix and INV_1 in the file).
                if (errors.Count == 0 && !v.Rejected && !created.Add(v.VendorSysNbr + "|" + v.Invoice))
                    errors.Add(Tuple.Create(v.Row, "Invoice " + v.Invoice + " for vendor " + v.VendorText + " would be created twice by this file."));

                // Trust funds last, so only vouchers that pass everything else use up the balance.
                if (errors.Count == 0 && !v.Rejected && v.Type == "T")
                    CheckTrustFunds(v, errors);

                foreach (var e in errors)
                    check.AddProblem(e.Item1, v.Describe, e.Item2);
                if (errors.Count > 0) v.Rejected = true;
            }
            check.SortProblems();
            return check;
        }

        private void CheckVoucher(ImportVoucher v, ImportCheck check, List<Tuple<int, string>> errors)
        {
            Action<string> err = m => errors.Add(Tuple.Create(v.Row, m));

            // Vendor
            VendorInfo vendor = null;
            if (v.VendorText.Length > 0 && v.VendorText.Length <= 12)
            {
                var found = Vendor(v.VendorText);
                if (found.Count == 0) err("No vendor with Vendor Code " + v.VendorText + " was found in Juris.");
                else if (found.Count > 1) err("Vendor " + v.VendorText + " matches more than one Juris vendor (" + string.Join(", ", found.Select(f => f.Code)) + "). Use the exact vendor code.");
                else
                {
                    vendor = found[0];
                    v.VendorSysNbr = vendor.SysNbr;
                    v.VendorCode = vendor.Code;
                    v.VendorName = vendor.Name;
                    v.VendorDiscAcct = vendor.DiscAcct;
                    v.VendorDefaultDistAcct = vendor.DefaultDist;
                    if (vendor.NoVouchers) err("Vendor " + vendor.Code + " is set in Juris to not accept new vouchers.");
                }
            }

            if (v.Invoice.Length > 20)
                err("Invoice Number " + v.Invoice + " is longer than 20 characters.");

            // Duplicates in Juris (posted vouchers and unposted batches).
            if (vendor != null && v.HeaderParsed && v.Invoice.Length > 0 && v.Invoice.Length <= 20)
            {
                if (IsDuplicate(vendor.SysNbr, v.Invoice, v.InvoiceDate))
                {
                    if (check.Mode == DuplicateMode.Reject)
                        err("Invoice " + v.Invoice + " for vendor " + vendor.Code + " dated " + v.InvoiceDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
                            + " already exists in Juris (as a voucher or in an unposted batch) and you chose to reject duplicate invoices.");
                    else
                    {
                        var renamed = v.Invoice + "_" + check.Suffix;
                        if (renamed.Length > 20)
                            err("Invoice " + v.Invoice + " already exists in Juris, and " + renamed + " would be longer than 20 characters.");
                        else if (IsDuplicate(vendor.SysNbr, renamed, v.InvoiceDate))
                            err("Invoice " + v.Invoice + " already exists in Juris, and so does " + renamed + ".");
                        else
                        {
                            v.Notes.Add("Invoice number changed from " + v.Invoice + " to " + renamed + " because it already exists in Juris.");
                            v.Invoice = renamed;
                        }
                    }
                }
            }

            if (v.Type == "A")
            {
                // AP account: as given, else the vendor's default, else the first AP account.
                if (v.APAcctText.Length > 0)
                {
                    if (_apAccounts.TryGetValue(v.APAcctText, out var ap)) v.APAcct = ap;
                    else err("A/P Account " + v.APAcctText + " doesn't exist in Juris. Leave A/P Account blank to use the vendor's default A/P account.");
                }
                else
                {
                    if (vendor?.DefaultAP != null && _apAccounts.TryGetValue(vendor.DefaultAP, out var ap)) v.APAcct = ap;
                    else v.APAcct = _apAccounts.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (v.APAcct == null) err("Juris has no A/P accounts set up.");
                }
            }
            else if (v.ClientText.Length > 0 && v.MatterText.Length > 0 && v.TrustBankText.Length > 0)
            {
                var matter = Matter(v.ClientText, v.MatterText);
                if (matter.Count != 1)
                    err(matter.Count == 0 ? "No matter " + v.ClientText + "/" + v.MatterText + " (Client Code/Matter Code) was found in Juris for this trust voucher."
                                          : "Client/Matter " + v.ClientText + "/" + v.MatterText + " matches more than one Juris matter.");
                else
                {
                    var trust = Trust(matter[0].SysNbr, v.TrustBankText);
                    if (trust == null) err("Trust Bank " + v.TrustBankText + " isn't a trust bank account of matter " + v.ClientText + "/" + v.MatterText + ".");
                    else
                    {
                        v.TrustMatter = trust.Matter;
                        v.TrustBank = trust.Bank;
                    }
                }
            }

            foreach (var g in v.GlLines)
            {
                if (g.AccountText.Length == 0) continue; // already reported
                if (_glAccounts.TryGetValue(g.AccountText, out var cht)) g.ChtSysNbr = cht;
                else errors.Add(Tuple.Create(g.Row, "GL Distribution Account " + g.AccountText + " doesn't exist in Juris (enter the account exactly as Juris shows it, with its separators)."));
            }

            foreach (var e in v.ExpenseLines)
                CheckExpense(e, errors);

            if (v.Type == "A" && errors.Count == 0 && !v.Rejected)
                Balance(v, errors);
        }

        private void CheckExpense(ExpenseLine e, List<Tuple<int, string>> errors)
        {
            Action<string> err = m => errors.Add(Tuple.Create(e.Row, m));
            MatterInfo matter = null;
            if (e.ClientText.Length > 0 && e.MatterText.Length > 0)
            {
                if (!ClientExists(e.ClientText)) err("No client with Client Code " + e.ClientText + " was found in Juris.");
                else
                {
                    var found = Matter(e.ClientText, e.MatterText);
                    if (found.Count == 0) err("Client " + e.ClientText + " has no matter with Matter Code " + e.MatterText + ".");
                    else if (found.Count > 1) err("Client/Matter " + e.ClientText + "/" + e.MatterText + " matches more than one Juris matter.");
                    else
                    {
                        matter = found[0];
                        e.MatterSysNbr = matter.SysNbr;
                        if (matter.Status != "O" || matter.Lock == "2" || matter.Lock == "3")
                            err("Client/Matter " + e.ClientText + "/" + e.MatterText + " doesn't accept expenses (it isn't Open, or its lock disallows expenses).");
                    }
                }
            }

            decimal? stdRate = null;
            if (e.ExpCodeText.Length > 0)
            {
                e.ExpCode = ExpCode(e.ExpCodeText);
                if (e.ExpCode == null) err("Expense Code " + e.ExpCodeText + " doesn't exist in Juris.");
                else
                {
                    stdRate = Rate("STDR", e.ExpCode);
                    if (stdRate == null) err("Expense Code " + e.ExpCode + " has no rate in the standard expense schedule (STDR). Add one in Juris first.");
                }
            }

            if (e.TaskText.Length > 0)
            {
                e.TaskCode = TaskCode(e.TaskText);
                if (e.TaskCode == null) err("Task Code " + e.TaskText + " doesn't exist in Juris.");
            }
            else if (matter != null && matter.RequiresTask)
                err("Client/Matter " + e.ClientText + "/" + e.MatterText + " requires a task code on expenses, but Task Code is blank.");

            if (e.IncurredByText.Length > 0)
            {
                e.IncurredBy = Employee(e.IncurredByText);
                if (e.IncurredBy == null) err("Incurred By " + e.IncurredByText + " isn't a Juris employee ID.");
            }

            if (e.Units == 0 && e.Amount == 0)
            {
                err("The expense line has no Expense Amount and no Expense Units.");
                return;
            }
            if (e.Units != 0 && e.Amount != 0 && !(e.Units == 1 && e.Amount >= 1000) && Math.Abs(e.Amount / e.Units) >= 1000m)
                err("Expense Amount ÷ Expense Units is 1,000 or more, which Juris can't store as an expense rate.");

            if (e.Amount != 0)
                e.EffectiveAmount = e.Amount;
            else if (matter != null && stdRate != null)
            {
                var rate = Rate(matter.ExpSch, e.ExpCode) ?? stdRate.Value;
                var value = e.Units * rate;
                if (decimal.Round(value, 2) != value)
                    err("Expense Units × rate (" + e.Units.ToString(CultureInfo.InvariantCulture) + " × " + rate.ToString(CultureInfo.InvariantCulture)
                        + ") isn't a whole number of cents. Put the amount in Expense Amount.");
                e.EffectiveAmount = decimal.Round(value, 2);
            }
        }

        /// <summary>
        /// The automatic expense GL line (vendor's default distribution account, for the expense total) is added when the
        /// voucher has expenses and its own G lines don't already total the invoice; then GL must equal the invoice amount.
        /// </summary>
        private static void Balance(ImportVoucher v, List<Tuple<int, string>> errors)
        {
            var expenses = v.ExpenseTotal;
            if (expenses != 0 && v.GlTotal != v.InvoiceAmt)
            {
                if (v.VendorDefaultDistAcct == null)
                {
                    errors.Add(Tuple.Create(v.Row, "Vendor " + v.VendorCode + " has no default distribution account in Juris, which expense lines need."));
                    return;
                }
                v.GlLines.Add(new GlLine { Row = 0, IsAutomatic = true, ChtSysNbr = v.VendorDefaultDistAcct.Value, Amount = expenses, AccountText = "(vendor's default distribution account)" });
            }
            if (v.GlLines.Count == 0)
                errors.Add(Tuple.Create(v.Row, "The voucher has no GL lines (G) or expense lines (E) below it."));
            else if (v.GlTotal != v.InvoiceAmt)
                errors.Add(Tuple.Create(v.Row, "The voucher doesn't balance: the GL lines total " + Money(v.GlTotal) + ", not the Invoice Amount of " + Money(v.InvoiceAmt) + "."));
        }

        private void CheckTrustFunds(ImportVoucher v, List<Tuple<int, string>> errors)
        {
            var trust = _trust[v.TrustMatter + "|" + v.TrustBank];
            var remaining = trust.Balance - trust.Committed - v.InvoiceAmt;
            if (remaining <= 0)
            {
                errors.Add(Tuple.Create(v.Row, "Trust Bank " + v.TrustBank + " doesn't have enough funds for this invoice: the matter's trust balance less open and unposted trust vouchers"
                    + (trust.Committed > 0 ? " and earlier vouchers in this file" : "") + " is " + Money(trust.Balance - trust.Committed) + "."));
                return;
            }
            trust.Committed += v.InvoiceAmt;
        }

        // ------------------------------------------------------------------ lookups

        private List<VendorInfo> Vendor(string text)
        {
            var key = VoucherImportParser.Pad12(text);
            if (!_vendors.TryGetValue(key, out var list))
            {
                list = _session.Query(
                        @"select VenSysNbr, VenCode, VenName, VenDiscAcct, VenDefaultAPAcct, VenDefaultDistAcct, VenNoVouchers from Vendor
                          where right('000000000000' + VenCode, 12) = right('000000000000' + @ven, 12)", Sql.P("@ven", text)).Rows()
                    .Select(r => new VendorInfo
                    {
                        SysNbr = Convert.ToInt32(r["VenSysNbr"]),
                        Code = Sql.Text(r, "VenCode"),
                        Name = Sql.Text(r, "VenName"),
                        DiscAcct = r["VenDiscAcct"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["VenDiscAcct"]),
                        DefaultAP = r["VenDefaultAPAcct"] == DBNull.Value ? null : Sql.Text(r, "VenDefaultAPAcct"),
                        DefaultDist = r["VenDefaultDistAcct"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["VenDefaultDistAcct"]),
                        NoVouchers = Sql.Text(r, "VenNoVouchers").ToUpperInvariant() == "Y",
                    }).ToList();
                _vendors[key] = list;
            }
            return list;
        }

        private bool ClientExists(string client)
        {
            var key = VoucherImportParser.Pad12(client);
            if (!_clients.TryGetValue(key, out var exists))
            {
                exists = _session.Scalar("select top 1 1 from Client where right('000000000000' + CliCode, 12) = right('000000000000' + @cli, 12)",
                    Sql.P("@cli", client)) != null;
                _clients[key] = exists;
            }
            return exists;
        }

        private List<MatterInfo> Matter(string client, string matter)
        {
            var key = VoucherImportParser.Pad12(client) + "|" + VoucherImportParser.Pad12(matter);
            if (!_matters.TryGetValue(key, out var list))
            {
                list = _session.Query(
                        @"select MatSysNbr, MatStatusFlag, MatLockFlag, MatExpSch, MatReqTaskCdOnExp
                          from Client inner join Matter on MatCliNbr = CliSysNbr
                          where right('000000000000' + CliCode, 12) = right('000000000000' + @cli, 12)
                            and right('000000000000' + MatCode, 12) = right('000000000000' + @mat, 12)",
                        Sql.P("@cli", client), Sql.P("@mat", matter)).Rows()
                    .Select(r => new MatterInfo
                    {
                        SysNbr = Convert.ToInt32(r["MatSysNbr"]),
                        Status = Sql.Text(r, "MatStatusFlag"),
                        Lock = Sql.Text(r, "MatLockFlag"),
                        ExpSch = Sql.Text(r, "MatExpSch"),
                        RequiresTask = Sql.Text(r, "MatReqTaskCdOnExp").ToUpperInvariant() == "Y",
                    }).ToList();
                _matters[key] = list;
            }
            return list;
        }

        private string ExpCode(string code) => Cached(_expCodes, code, () =>
            Convert.ToString(_session.Scalar("select ExpCdCode from ExpenseCode where ExpCdCode = @c", Sql.P("@c", code))));

        private string TaskCode(string code) => Cached(_taskCodes, code, () =>
            Convert.ToString(_session.Scalar("select TaskCdCode from TaskCode where TaskCdCode = @c", Sql.P("@c", code))));

        private string Employee(string empId) => Cached(_employees, empId, () =>
            Convert.ToString(_session.Scalar("select EmpID from Employee where EmpID = @id", Sql.P("@id", empId))));

        private static string Cached(Dictionary<string, string> cache, string key, Func<string> load)
        {
            if (!cache.TryGetValue(key, out var value))
            {
                value = load();
                if (string.IsNullOrEmpty(value)) value = null; else value = value.Trim();
                cache[key] = value;
            }
            return value;
        }

        private decimal? Rate(string schedule, string code)
        {
            var key = schedule + "|" + code;
            if (!_rates.TryGetValue(key, out var rate))
            {
                var value = _session.Scalar("select ESRMult from ExpSchRate where ESRSch = @s and ESRCode = @c", Sql.P("@s", schedule), Sql.P("@c", code));
                rate = value == null ? (decimal?)null : Convert.ToDecimal(value);
                _rates[key] = rate;
            }
            return rate;
        }

        private TrustInfo Trust(int matter, string bank)
        {
            var key = matter + "|" + bank;
            if (_trust.TryGetValue(key, out var info)) return info;
            var t = _session.Query(TrustSql, Sql.P("@mat", matter), Sql.P("@bank", bank));
            info = t.Rows.Count == 0 ? null : new TrustInfo
            {
                Matter = matter,
                Bank = Sql.Text(t.Rows[0], "TABank"),
                Balance = Convert.ToDecimal(t.Rows[0]["TABalance"]),
                Committed = Convert.ToDecimal(t.Rows[0]["Committed"]),
            };
            if (info != null) _trust[matter + "|" + info.Bank] = info;
            _trust[key] = info;
            return info;
        }

        /// <summary>Trust balance, and what open trust vouchers and unposted trust batch vouchers already commit against it.</summary>
        private const string TrustSql =
            @"select TABank, TABalance,
                     isnull((select sum(VchInvoiceBal) from Voucher where VchMatter = TAMatter and VchBank = TABank and VchStatus = 'O'), 0)
                   + isnull((select sum(VBDInvoiceAmt) from VoucherBatchDetail inner join VoucherBatch on VBBatchNbr = VBDBatch
                             where VBDMatter = TAMatter and VBDBank = TABank and VBDPosted = 'N' and VBStatus not in ('P','D')
                               and not exists (select 1 from Voucher where VchVoucherNbr = VBDVoucherNbr)), 0) as Committed
              from TrustAccount where TAMatter = @mat and TABank = @bank";

        private bool IsDuplicate(int vendor, string invoice, DateTime invoiceDate) => _session.Scalar(DuplicateSql,
            Sql.P("@ven", vendor), Sql.P("@inv", invoice), new SqlParameter("@date", SqlDbType.Date) { Value = invoiceDate }) != null;

        private const string DuplicateSql =
            @"select top 1 1 from (
                  select 1 as x from Voucher where VchVendor = @ven and VchInvoiceNbr = @inv and convert(date, VchInvoiceDate) = @date
                  union all
                  select 1 from VoucherBatchDetail where VBDVendor = @ven and VBDInvoiceNbr = @inv and convert(date, VBDInvoiceDate) = @date and VBDPosted = 'N') d";

        // ================================================================== reports

        /// <summary>The pre-report: one row per voucher that will be created.</summary>
        public static DataTable VoucherReport(IEnumerable<ImportVoucher> vouchers)
        {
            var t = new DataTable();
            t.Columns.Add("#", typeof(int));
            t.Columns.Add("Row", typeof(int));
            t.Columns.Add("Voucher Type", typeof(string));
            t.Columns.Add("Vendor Code", typeof(string));
            t.Columns.Add("Vendor Name", typeof(string));
            t.Columns.Add("Invoice Number", typeof(string));
            t.Columns.Add("Invoice Date", typeof(DateTime));
            t.Columns.Add("Voucher Date", typeof(DateTime));
            t.Columns.Add("Due Date", typeof(DateTime));
            t.Columns.Add("Invoice Amount", typeof(decimal));
            t.Columns.Add("Non-Discountable Amount", typeof(decimal));
            t.Columns.Add("A/P Account or Trust Bank", typeof(string));
            t.Columns.Add("GL Lines", typeof(int));
            t.Columns.Add("Expense Lines", typeof(int));
            t.Columns.Add("Voucher Reference", typeof(string));
            t.Columns.Add("Note", typeof(string));
            var n = 0;
            foreach (var v in vouchers)
                t.Rows.Add(++n, v.Row, v.Type == "T" ? "Trust" : "A/P", v.VendorCode, v.VendorName, v.Invoice, v.InvoiceDate, v.VoucherDate,
                    v.DueDate ?? v.InvoiceDate, v.InvoiceAmt, v.NonDiscAmt, v.Type == "T" ? "Trust Bank " + v.TrustBank + " (" + v.ClientText + "/" + v.MatterText + ")" : v.APAcct,
                    v.GlLines.Count, v.ExpenseLines.Count, v.Memo, string.Join(" ", v.Notes));
            return t;
        }

        /// <summary>Every distribution line of the vouchers, including the automatic expense GL line.</summary>
        public static DataTable LinesReport(IEnumerable<ImportVoucher> vouchers)
        {
            var t = new DataTable();
            t.Columns.Add("#", typeof(int));
            t.Columns.Add("Row", typeof(int));
            t.Columns.Add("Vendor Code", typeof(string));
            t.Columns.Add("Invoice Number", typeof(string));
            t.Columns.Add("Line Type", typeof(string));
            t.Columns.Add("GL Account", typeof(string));
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Expense Code", typeof(string));
            t.Columns.Add("Task Code", typeof(string));
            t.Columns.Add("Incurred By", typeof(string));
            t.Columns.Add("Units", typeof(decimal));
            t.Columns.Add("Amount", typeof(decimal));
            t.Columns.Add("Narrative", typeof(string));
            t.Columns.Add("Bill Note", typeof(string));
            var n = 0;
            foreach (var v in vouchers)
            {
                n++;
                foreach (var g in v.GlLines)
                    t.Rows.Add(n, g.Row == 0 ? (object)DBNull.Value : g.Row, v.VendorCode ?? v.VendorText, v.Invoice,
                        g.IsAutomatic ? "GL line (added automatically for the expenses)" : "GL line", g.AccountText, null, null, null, null, null, DBNull.Value, g.Amount, null, null);
                foreach (var e in v.ExpenseLines)
                    t.Rows.Add(n, e.Row, v.VendorCode ?? v.VendorText, v.Invoice, "Expense line", null, e.ClientText, e.MatterText, e.ExpCode ?? e.ExpCodeText,
                        e.TaskCode ?? e.TaskText, e.IncurredBy ?? e.IncurredByText, e.Units, e.EffectiveAmount, e.Narrative, e.BillNote);
            }
            return t;
        }

        // ================================================================== the change (one transaction)

        /// <summary>
        /// Creates the unposted Payment Voucher batch with every accepted voucher and its distributions, claims the batch and
        /// voucher numbers from Juris' counters and adds the batch to the batch tree, all in one transaction.
        /// </summary>
        public ImportResult CreateBatch(ImportCheck check, string empId, string batchTitle)
        {
            var vouchers = check.Accepted;
            if (vouchers.Count == 0) throw new InvalidOperationException("There are no valid vouchers to import, so no batch was created.");

            return _session.InTransaction(() =>
            {
                var empSys = _session.Scalar("select EmpSysNbr from Employee where EmpID = @id and EmpValidAsUser = 'Y'", Sql.P("@id", empId));
                if (empSys == null) throw new InvalidOperationException(empId + " isn't a Juris user, so no batch was created. Choose another batch owner.");

                // Re-check what other people could have changed since the file was checked.
                var committed = new Dictionary<string, decimal>();
                foreach (var v in vouchers)
                {
                    if (IsDuplicate(v.VendorSysNbr, v.Invoice, v.InvoiceDate))
                        throw new InvalidOperationException("Invoice " + v.Invoice + " for vendor " + v.VendorCode + " was added to Juris after the file was checked. "
                            + "No batch was created. Check the file again.");
                    if (v.Type == "T")
                    {
                        var key = v.TrustMatter + "|" + v.TrustBank;
                        var t = _session.Query(TrustSql, Sql.P("@mat", v.TrustMatter), Sql.P("@bank", v.TrustBank));
                        var used = committed.TryGetValue(key, out var c) ? c : 0m;
                        if (t.Rows.Count == 0 || Convert.ToDecimal(t.Rows[0]["TABalance"]) - Convert.ToDecimal(t.Rows[0]["Committed"]) - used - v.InvoiceAmt <= 0)
                            throw new InvalidOperationException("The trust balance for bank " + v.TrustBank + " (" + v.ClientText + "/" + v.MatterText + ") changed after the file was checked and no longer covers invoice "
                                + v.Invoice + ". No batch was created. Check the file again.");
                        committed[key] = used + v.InvoiceAmt;
                    }
                }

                var batch = ClaimBatchNumber();
                var baseNumber = ClaimVoucherNumbers(vouchers.Count);
                var comment = Left(batchTitle + batch.ToString(CultureInfo.InvariantCulture), 50);

                _session.Execute(
                    @"insert into VoucherBatch (VBBatchNbr, VBComment, VBBatchType, VBStatus, VBRecCount, VBUser, VBDate)
                      values (@batch, @comment, '1', 'U', @count, @user, dateadd(d, datediff(d, 0, getdate()), 0))",
                    Sql.P("@batch", batch), Sql.P("@comment", comment), Sql.P("@count", vouchers.Count), Sql.P("@user", Convert.ToInt32(empSys)));

                var rec = 0;
                foreach (var v in vouchers)
                {
                    rec++;
                    InsertVoucher(batch, rec, baseNumber + rec, v);
                }

                AddToBatchTree(batch, Left(comment, 30), empId);

                var total = vouchers.Sum(v => v.InvoiceAmt);
                _session.WriteUtilityLog("JPS Voucher Import: created unposted payment voucher batch " + batch
                    + " with " + vouchers.Count + " voucher(s) totalling " + total.ToString("N2", CultureInfo.InvariantCulture)
                    + " from " + System.IO.Path.GetFileName(check.FilePath) + ".");

                return new ImportResult
                {
                    BatchNumber = batch,
                    VoucherCount = vouchers.Count,
                    FirstVoucherNumber = baseNumber + 1,
                    LastVoucherNumber = baseNumber + vouchers.Count,
                    Total = total,
                };
            });
        }

        private void InsertVoucher(int batch, int rec, int voucherNumber, ImportVoucher v)
        {
            _session.Execute(
                @"insert into VoucherBatchDetail (VBDBatch, VBDRecNbr, VBDType, VBDPosted, VBDVoucherNbr, VBDVoucherDate, VBDVendor, VBDMatter,
                      VBDBank, VBDPONbr, VBDInvoiceNbr, VBDInvoiceDate, VBDDueDate, VBDDiscDate, VBDInvoiceAmt, VBDNonDiscountAmt, VBDDiscAmt,
                      VBDOn1099, VBDSeparateCheck, VBDDiscAcct, VBDAPAcct, VBDMemo)
                  values (@batch, @rec, @type, 'N', @vnbr, @vdate, @ven, @mat, @bank, @po, @inv, @idate, @due, @disc, @amt, @nondisc, 0,
                      '', @sep, @discAcct, @ap, @memo)",
                Sql.P("@batch", batch), Sql.P("@rec", rec), Sql.P("@type", v.Type), Sql.P("@vnbr", voucherNumber),
                Date("@vdate", v.VoucherDate), Sql.P("@ven", v.VendorSysNbr),
                Sql.P("@mat", v.Type == "T" ? (object)v.TrustMatter : null), Sql.P("@bank", v.Type == "T" ? v.TrustBank : null),
                Sql.P("@po", v.PONbr ?? ""), Sql.P("@inv", v.Invoice), Date("@idate", v.InvoiceDate),
                Date("@due", v.DueDate ?? v.InvoiceDate), Date("@disc", v.DiscDate ?? v.InvoiceDate),
                Money("@amt", v.InvoiceAmt), Money("@nondisc", v.NonDiscAmt), Sql.P("@sep", v.SeparateCheck),
                Sql.P("@discAcct", v.Type == "A" ? (object)v.VendorDiscAcct : null), Sql.P("@ap", v.Type == "A" ? v.APAcct : null),
                new SqlParameter("@memo", SqlDbType.Text) { Value = v.Memo });

            var seq = 0;
            foreach (var g in v.GlLines)
                _session.Execute(
                    "insert into VoucherBatchGLDist (VBGBatch, VBGRecNbr, VBGSeqNbr, VBGGLAcct, VBGAmount) values (@batch, @rec, @seq, @acct, @amt)",
                    Sql.P("@batch", batch), Sql.P("@rec", rec), Sql.P("@seq", ++seq), Sql.P("@acct", g.ChtSysNbr), Money("@amt", g.Amount));

            seq = 0;
            foreach (var e in v.ExpenseLines)
            {
                var rows = _session.ExecuteRows(MatDistSql,
                    Sql.P("@batch", batch), Sql.P("@rec", rec), Sql.P("@seq", ++seq), Sql.P("@mat", e.MatterSysNbr), Sql.P("@ExpCode", e.ExpCode),
                    Sql.P("@Task", e.TaskCode), Sql.P("@IncurredBy", e.IncurredBy ?? ""),
                    new SqlParameter("@Units", SqlDbType.Decimal) { Precision = 12, Scale = 2, Value = e.Units },
                    new SqlParameter("@Amt", SqlDbType.Decimal) { Precision = 12, Scale = 2, Value = e.Amount },
                    new SqlParameter("@Narr", SqlDbType.VarChar, -1) { Value = e.Narrative ?? "" },
                    new SqlParameter("@Note", SqlDbType.VarChar, -1) { Value = e.BillNote ?? "" },
                    Sql.P("@VenCode", v.VendorCode), Sql.P("@VenName", v.VendorName), Sql.P("@InvNbr", v.Invoice));
                if (rows != 1)
                    throw new InvalidOperationException("Expense line on row " + e.Row + " (invoice " + v.Invoice + ") couldn't be written. No batch was created.");
            }

            // Safety net: what was written must be exactly what the checks balanced.
            var check = _session.Query(
                @"select (select isnull(sum(VBGAmount), 0) from VoucherBatchGLDist where VBGBatch = @batch and VBGRecNbr = @rec) as GL,
                         (select isnull(sum(VBMAmount), 0) from VoucherBatchMatDist where VBMBatch = @batch and VBMRecNbr = @rec) as Exp",
                Sql.P("@batch", batch), Sql.P("@rec", rec)).Rows[0];
            if (v.Type == "A" && Convert.ToDecimal(check["GL"]) != v.InvoiceAmt)
                throw new InvalidOperationException("Invoice " + v.Invoice + " didn't balance when written (GL " + Money(Convert.ToDecimal(check["GL"])) + ", invoice " + Money(v.InvoiceAmt) + "). No batch was created.");
            if (Convert.ToDecimal(check["Exp"]) != v.ExpenseTotal)
                throw new InvalidOperationException("Invoice " + v.Invoice + "'s expense lines came to " + Money(Convert.ToDecimal(check["Exp"])) + " when written, not "
                    + Money(v.ExpenseTotal) + ". No batch was created.");
        }

        /// <summary>
        /// Original formula, with these fixes: matter by MatSysNbr (already validated), a blank task stays NULL (no dummy "ZZZ"),
        /// IncurredBy not given stays NULL, and VBMMult is the rate (not 0) when only units are given and never NULL.
        /// </summary>
        private const string MatDistSql =
            @"insert into VoucherBatchMatDist (VBMBatch, VBMRecNbr, VBMSeqNbr, VBMMatter, VBMExpCd, VBMExpSched, VBMUnits, VBMMult,
                  VBMAmountSource, VBMAmount, VBMSummarize, VBMAuthBy, VBMBudgPhase, VBMBudgTaskCd, VBMBillNote, VBMNarrative, VBMCode1, VBMCode2, VBMCode3)
              select @batch, @rec, @seq, MatSysNbr, @ExpCode, MatExpSch,
                cast(case when @Units <> 0 then @Units
                     else case when isnull(esrmult, stdmult) = 1 then @Amt
                               when isnull(esrmult, stdmult) <> 1 and @Amt <> 0 and isnull(esrmult, stdmult) <> 0 then @Amt / isnull(esrmult, stdmult)
                               else @Units end end as decimal(12,2)),
                cast(case when esrmult is null and @Units = 0 then stdmult
                          when @Units = 1 and @Amt >= 1000 then 1
                          when @Units <> 0 and @Amt <> 0 then @Amt / @Units
                          else isnull(esrmult, stdmult) end as decimal(7,4)),
                case when MatExpSch = 'STDR' then '1' when cast(@Amt as money) <> 0 then 'U' else '2' end,
                case when cast(@Amt as money) = 0 then cast(@Units * isnull(esrmult, stdmult) as money) else cast(@Amt as money) end,
                isnull(esrsumry, stdsum),
                EmpSysNbr,
                0,
                @Task,
                left((case when VenNote = 'N' or @Note like '%' + @VenCode + '%' then '' else @VenCode + '; ' end) +
                     (case when NameNote = 'N' or @Note like '%' + @VenName + '%' then '' else @VenName + '; ' end) +
                     (case when InvNote = 'N' or @Note like '%' + @InvNbr + '%' then '' else @InvNbr + '; ' end) + @Note, 60),
                case when len((case when VenNarr = 'N' or @Narr like '%' + @VenCode + '%' then '' else @VenCode + '; ' end) +
                              (case when NameVarr = 'N' or @Narr like '%' + @VenName + '%' then '' else @VenName + '; ' end) +
                              (case when InvNarr = 'N' or @Narr like '%' + @InvNbr + '%' then '' else @InvNbr + '; ' end)) > 1
                     then (case when VenNarr = 'N' or @Narr like '%' + @VenCode + '%' then '' else @VenCode + '; ' end) +
                          (case when NameVarr = 'N' or @Narr like '%' + @VenName + '%' then '' else @VenName + '; ' end) +
                          (case when InvNarr = 'N' or @Narr like '%' + @InvNbr + '%' then '' else @InvNbr + '; ' end) + char(13) + char(10) + @Narr
                     else @Narr end,
                '', '', ''
              from Matter
              left outer join (select esrcode, esrsch, esrsumry, esrmult from ExpSchRate) ESR on esrsch = MatExpSch and esrcode = @ExpCode
              left outer join (select EmpSysNbr, EmpID from Employee) EMP on EmpID = nullif(@IncurredBy, '')
              inner join (select esrcode as stdcode, esrsumry as stdsum, esrmult as stdmult from ExpSchRate where esrsch = 'STDR' and esrcode = @ExpCode) STD on stdcode = @ExpCode
              cross join (select substring(SpTxtValue, 7, 1) as VenNarr, substring(SpTxtValue, 9, 1) as NameVarr, substring(SpTxtValue, 11, 1) as InvNarr,
                                 substring(SpTxtValue, 13, 1) as VenNote, substring(SpTxtValue, 15, 1) as NameNote, substring(SpTxtValue, 17, 1) as InvNote
                          from SysParam where SpName = 'CfgVouchOpts') CFG
              where MatSysNbr = @mat";

        /// <summary>
        /// Next payment voucher batch number from Juris' counter (SysParam LastBatchVoucher), advanced in the caller's
        /// transaction (see <see cref="JurisCounters"/>: locked until commit, above every existing batch, never lowers the counter).
        /// </summary>
        private int ClaimBatchNumber() =>
            JurisCounters.Next(_session, "LastBatchVoucher", "voucher batch number", "VoucherBatch.VBBatchNbr");

        /// <summary>
        /// Reserves exactly <paramref name="count"/> voucher numbers from SysParam LastSysNbrVoucher and returns the base
        /// (numbers are base+1 .. base+count). The base is above the counter, every Voucher and every batch voucher; the
        /// counter only moves forward and stays locked until commit.
        /// </summary>
        private int ClaimVoucherNumbers(int count) =>
            JurisCounters.Claim(_session, "LastSysNbrVoucher", count, "voucher numbers", "Voucher.VchVoucherNbr", "VoucherBatchDetail.VBDVoucherNbr");

        /// <summary>Puts the new batch (only this batch) in the Juris batch tree (class 7200), creating the user / period folders the firm option asks for.</summary>
        private void AddToBatchTree(int batch, string title, string userFolder) =>
            JurisBatchTree.AddBatch(_session, VoucherBatchDocClass, "Payment Voucher", batch, title, userFolder);

        // ------------------------------------------------------------------ helpers

        private static SqlParameter Money(string name, decimal value) => new SqlParameter(name, SqlDbType.Money) { Value = value };

        private static SqlParameter Date(string name, DateTime value) => new SqlParameter(name, SqlDbType.DateTime) { Value = value.Date };

        private static string Money(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture);

        private static string Left(string text, int length) => text.Length <= length ? text : text.Substring(0, length);
    }
}
