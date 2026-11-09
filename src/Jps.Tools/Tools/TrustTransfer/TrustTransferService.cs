using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.TrustTransfer
{
    /// <summary>One line in the "Check it can be moved" list.</summary>
    public sealed class TransferCheck
    {
        public bool Passed { get; set; }
        public string Text { get; set; }
    }

    /// <summary>Everything the read-only checks found, plus what the user approves before the batch is created.</summary>
    public sealed class TransferReview
    {
        public string FromBank { get; set; }
        public string ToBank { get; set; }
        public List<TransferCheck> Checks { get; } = new List<TransferCheck>();
        public DataTable Issues { get; set; }
        public DataTable NegativeMatters { get; set; }
        public DataTable Matters { get; set; }
        public decimal Total { get; set; }
        public int MatterCount { get; set; }
        public short PeriodYear { get; set; }
        public short PeriodNumber { get; set; }
        public bool CanTransfer => Checks.Count > 0 && Checks.All(c => c.Passed);
    }

    /// <summary>Result of a successful transfer.</summary>
    public sealed class TransferResult
    {
        public int BatchNumber { get; set; }
        public int MatterCount { get; set; }
        public int LineCount { get; set; }
        public decimal Total { get; set; }
    }

    /// <summary>
    /// Read-only pre-checks and the one-transaction batch creation for Trust Transfer.
    /// Nothing here posts: it writes an unposted Trust Adjustment batch that the user posts in Juris.
    /// </summary>
    public sealed class TrustTransferService
    {
        private const int TrustAdjustmentDocClass = 7500;
        private const string BatchFolderUser = "SMGR"; // original hard-coded user folder (the tool has no Juris login)

        private readonly JurisSession _session;

        public TrustTransferService(JurisSession session)
        {
            _session = session;
        }

        // ---------------------------------------------------------------- checks (read-only)

        public TransferReview Review(string from, string to)
        {
            var review = new TransferReview { FromBank = from, ToBank = to };
            var checks = review.Checks;

            var balance = _session.Scalar("select sum(TLAmount) from TrustLedger where TLBank = @from", Sql.P("@from", from));
            if (balance == null || Convert.ToDecimal(balance) == 0m)
            {
                checks.Add(new TransferCheck { Passed = false, Text = "Bank " + from + " doesn't hold any matter balances, so there's nothing to transfer." });
                return review;
            }

            review.Issues = FindIssues(from);
            var issueCount = review.Issues.Rows.Count;
            checks.Add(new TransferCheck
            {
                Passed = issueCount == 0,
                Text = issueCount == 0
                    ? "No open or unposted vouchers, trust adjustments, prebill trust allocations, cash receipt allocations or journal entries use bank " + from + "."
                    : issueCount + " open or unposted item(s) still use bank " + from + ". Open the issues list, post or remove them in Juris, then run the checks again.",
            });

            review.NegativeMatters = _session.Query(
                @"select dbo.jfn_FormatClientCode(CliCode) as [Client Code], CliReportingName as [Client Name],
                         dbo.jfn_FormatMatterCode(MatCode) as [Matter Code], MatReportingName as [Matter Name], sum(TLAmount) as [Trust Balance]
                  from TrustLedger
                  inner join Matter on MatSysNbr = TLMatter
                  inner join Client on CliSysNbr = MatCliNbr
                  where TLBank = @from
                  group by dbo.jfn_FormatClientCode(CliCode), CliReportingName, dbo.jfn_FormatMatterCode(MatCode), MatReportingName
                  having sum(TLAmount) < 0
                  order by 1, 3", Sql.P("@from", from));
            var negatives = review.NegativeMatters.Rows.Count;
            checks.Add(new TransferCheck
            {
                Passed = negatives == 0,
                Text = negatives == 0
                    ? "No matter has a negative balance in bank " + from + "."
                    : negatives + " matter(s) have a negative balance in bank " + from + ". Fix them in Juris first (see the negative balances list).",
            });

            var period = FindPeriod();
            if (period == null)
            {
                checks.Add(new TransferCheck { Passed = false, Text = "No Juris accounting period contains today's date, so the batch can't be dated. Set up the period in Juris first." });
            }
            else
            {
                review.PeriodYear = period.Item1;
                review.PeriodNumber = period.Item2;
                checks.Add(new TransferCheck { Passed = true, Text = "The batch will be dated today in accounting period " + period.Item1 + "-" + period.Item2.ToString("00") + "." });
            }

            review.Matters = LoadMatterList(from);
            review.MatterCount = review.Matters.Rows.Count;
            review.Total = review.Matters.Rows().Sum(r => Convert.ToDecimal(r["Trust Balance"]));
            return review;
        }

        private DataTable FindIssues(string from)
        {
            var issues = new DataTable();
            issues.Columns.Add("Problem", typeof(string));
            issues.Columns.Add("Details", typeof(string));
            var prefix = "Bank " + from + " ";
            var p = new Func<System.Data.SqlClient.SqlParameter>(() => Sql.P("@from", from));

            foreach (var r in _session.Query(
                         @"select dbo.jfn_FormatVendorCode(Vendor.VenCode) as Vendor, Vendor.VenName, VchVoucherNbr
                           from Voucher inner join Vendor on VchVendor = VenSysNbr
                           where VchType in ('T','2','4') and VchStatus = 'O' and VchBank = @from order by VchVoucherNbr", p()).Rows())
                issues.Rows.Add(prefix + "has open trust vouchers. Pay or void them first", VoucherDetail(r));

            foreach (var r in _session.Query(
                         @"select distinct dbo.jfn_FormatVendorCode(Vendor.VenCode) as Vendor, Vendor.VenName, VchVoucherNbr
                           from VoucherBatchDetail
                           inner join VoucherBatch on VBBatchNbr = VBDBatch
                           inner join Voucher on Voucher.VchVoucherNbr = VBDVoucherNbr
                           inner join Vendor on VchVendor = VenSysNbr
                           where VBStatus not in ('P','D') and VBDType in ('T','2','4') and VBDBank = @from order by VchVoucherNbr", p()).Rows())
                issues.Rows.Add(prefix + "has trust vouchers in unposted voucher batches. Post or delete those batches first", VoucherDetail(r));

            AddJoined(issues, prefix + "has unposted trust adjustment batches (batch numbers shown). Post or delete them first", _session.Query(
                @"select distinct TABDBatch from TrAdjBatchDetail inner join TrAdjBatch on TABDBatch = TABBatchNbr
                  where TABStatus not in ('P','D') and TABDBank = @from order by TABDBatch", p()));

            AddJoined(issues, prefix + "has prebills with trust applied (prebill numbers shown). Bill or delete those prebills first", _session.Query(
                @"select PBFTAPreBill from PrebillFeeTrustApplied where PBFTABank = @from
                  union select PBETAPrebill from PrebillExpenseTrustApplied where PBETABank = @from
                  union select PBMTAPrebill from PrebillMatterTrustApplied where PBMTABank = @from
                  order by 1", p()));

            AddJoined(issues, prefix + "has unposted cash receipt batches with trust receipts (batch numbers shown). Post or delete them first", _session.Query(
                @"select distinct CRTBatch from CRTrustAlloc inner join CashReceiptsBatch on CRTBatch = CRBBatchNbr
                  where CRBStatus not in ('P','D') and CRTBank = @from order by CRTBatch", p()));

            var glAsset = BankGl(from, "A");
            var glLiability = BankGl(from, "L");
            if (glAsset != 0 || glLiability != 0)
                AddJoined(issues, prefix + "has unposted journal entry batches that use its GL accounts (batch numbers shown). Post or delete them first", _session.Query(
                    @"select distinct JEBDBatchNbr from JEBatchDetail inner join JEBatch on JEBDBatchNbr = JEBBatchNbr
                      where JEBStatus not in ('P','D') and JEBDAccount in (@glA, @glL) order by JEBDBatchNbr",
                    Sql.P("@glA", glAsset), Sql.P("@glL", glLiability)));

            return issues;
        }

        private static string VoucherDetail(DataRow r) =>
            "Voucher " + Sql.Text(r, "VchVoucherNbr") + " - vendor " + Sql.Text(r, "Vendor") + " " + Sql.Text(r, "VenName");

        private static void AddJoined(DataTable issues, string error, DataTable numbers)
        {
            if (numbers.Rows.Count == 0) return;
            issues.Rows.Add(error, string.Join(", ", numbers.Rows().Select(r => Convert.ToString(r[0]).Trim())));
        }

        /// <summary>The bank's GL account of the given type (A = asset, L = liability), or 0 when there's no mapping (e.g. GL not installed).</summary>
        private int BankGl(string bank, string type)
        {
            var value = _session.Scalar("select top 1 BGAAcct from BkAcctGLAcct where BGAType = @type and BGABkAcct = @bank",
                Sql.P("@type", type), Sql.P("@bank", bank));
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private Tuple<short, short> FindPeriod()
        {
            var table = _session.Query(
                // Period 0 is the firm's whole-year period on some books (1/1–12/31): never post to it.
                @"select top 1 PrdYear, PrdNbr from ActngPeriod
                  where PrdNbr > 0
                    and convert(date, PrdEndDate) = convert(date, dateadd(d, -1, dateadd(m, datediff(m, 0, getdate()) + 1, 0)))
                  order by PrdYear desc, PrdNbr");
            if (table.Rows.Count == 0)
                table = _session.Query(
                    @"select top 1 PrdYear, PrdNbr from ActngPeriod
                      where PrdNbr > 0 and convert(date, getdate()) between convert(date, PrdStartDate) and convert(date, PrdEndDate)
                      order by PrdEndDate, PrdYear, PrdNbr");
            if (table.Rows.Count == 0) return null;
            return Tuple.Create(Convert.ToInt16(table.Rows[0]["PrdYear"]), Convert.ToInt16(table.Rows[0]["PrdNbr"]));
        }

        /// <summary>The pre-report: every matter whose balance in the FROM bank will be moved.</summary>
        private DataTable LoadMatterList(string from) => _session.Query(
            @"select dbo.jfn_FormatClientCode(CliCode) as [Client Code], CliReportingName as [Client Name],
                     dbo.jfn_FormatMatterCode(MatCode) as [Matter Code], MatReportingName as [Matter Name],
                     case MatStatusFlag when 'O' then 'Open' when 'C' then 'Closed' when 'F' then 'Final Bill Sent' when 'P' then 'Ready to purge' else MatStatusFlag end as [Matter Status],
                     b.Balance as [Trust Balance]
              from (select TLMatter, sum(TLAmount) as Balance from TrustLedger where TLBank = @from
                    group by TLMatter having sum(TLAmount) <> 0) b
              inner join Matter on MatSysNbr = b.TLMatter
              inner join Client on CliSysNbr = MatCliNbr
              order by 1, 3", Sql.P("@from", from));

        // ---------------------------------------------------------------- the change (one transaction)

        /// <summary>
        /// Creates the unposted Trust Adjustment batch: TO-bank TrustAccount rows, batch header, detail pairs and the
        /// batch-tree entry, all in one transaction. Any failure rolls everything back (nothing is ever deleted).
        /// </summary>
        public TransferResult CreateBatch(TransferReview review)
        {
            var from = review.FromBank;
            var to = review.ToBank;
            return _session.InTransaction(() =>
            {
                // Re-read the balances inside the transaction; refuse if they no longer match what the user approved.
                var matters = _session.Query(
                    @"select TLMatter, sum(TLAmount) as Balance from TrustLedger with (holdlock) where TLBank = @from
                      group by TLMatter having sum(TLAmount) <> 0 order by TLMatter", Sql.P("@from", from));
                var total = matters.Rows().Sum(r => Convert.ToDecimal(r["Balance"]));
                if (matters.Rows.Count != review.MatterCount || total != review.Total)
                    throw new InvalidOperationException("Trust balances in bank " + from + " changed after you reviewed them, so no batch was created. Run the checks again.");

                // TO-bank TrustAccount rows (FK4_TrAdjBatchDetail needs (matter, bank) to exist) — only for the matters
                // actually being moved. (The original added a zero row on the TO bank for every matter that ever used the
                // FROM bank: ~1,500 empty rows for a one-matter transfer on the test database.)
                _session.Execute(
                    @"insert into TrustAccount (TAMatter, TABank, TADateLastActy, TABalance)
                      select m.TLMatter, @to, '19000101', 0.00
                      from (select TLMatter from TrustLedger where TLBank = @from group by TLMatter having sum(TLAmount) <> 0) m
                      where not exists (select 1 from TrustAccount t where t.TAMatter = m.TLMatter and t.TABank = @to)",
                    Sql.P("@from", from), Sql.P("@to", to));

                var batch = ClaimTrustBatchNumber();

                _session.Execute(
                    @"insert into TrAdjBatch (TABBatchNbr, TABComment, TABBatchType, TABStatus, TABRecCount, TABUser, TABDate, TABJEBatchNbr)
                      values (@batch, @comment, '1', 'U', 0, 1, dateadd(d, datediff(d, 0, getdate()), 0), null)",
                    Sql.P("@batch", batch), Sql.P("@comment", Left("Trust transfer from bank " + from + " into bank " + to, 50)));

                const string detailSql =
                    @"insert into TrAdjBatchDetail (TABDBatch, TABDRecNbr, TABDPosted, TABDMatter, TABDBank, TABDLedgerLink, TABDDate,
                                                    TABDPrdYear, TABDPrdNbr, TABDAmount, TABDMemo)
                      values (@batch, @rec, 'N', @mat, @bank, null, dateadd(d, datediff(d, 0, getdate()), 0), @prdYear, @prdNbr, @amt, @memo)";
                var memo = "Trust utility transfer from bank " + from + " into bank " + to;
                var rec = 1;
                foreach (var row in matters.Rows())
                {
                    var matter = Convert.ToInt32(row["TLMatter"]);
                    var amount = Convert.ToDecimal(row["Balance"]);
                    foreach (var line in new[] { Tuple.Create(from, -amount), Tuple.Create(to, amount) })
                    {
                        _session.Execute(detailSql,
                            Sql.P("@batch", batch), Sql.P("@rec", rec++), Sql.P("@mat", matter), Sql.P("@bank", line.Item1),
                            Sql.P("@prdYear", review.PeriodYear), Sql.P("@prdNbr", review.PeriodNumber),
                            new System.Data.SqlClient.SqlParameter("@amt", SqlDbType.Money) { Value = line.Item2 },
                            Sql.P("@memo", memo));
                    }
                }

                var net = Convert.ToDecimal(_session.Scalar("select isnull(sum(TABDAmount), 0) from TrAdjBatchDetail where TABDBatch = @batch", Sql.P("@batch", batch)));
                if (net != 0m)
                    throw new InvalidOperationException("The new batch didn't balance to zero, so it was not created.");

                var lines = _session.Scalar(
                    @"update TrAdjBatch set TABRecCount = (select count(TABDID) from TrAdjBatchDetail where TABDBatch = @batch) where TABBatchNbr = @batch;
                      select TABRecCount from TrAdjBatch where TABBatchNbr = @batch", Sql.P("@batch", batch));

                AddToBatchTree(batch, Left("Xfer " + from + " to " + to + " Batch:" + batch, 30));

                _session.WriteUtilityLog("JPS Trust Transfer: created unposted trust adjustment batch " + batch + " moving "
                    + total.ToString("N2", System.Globalization.CultureInfo.InvariantCulture) + " for " + matters.Rows.Count
                    + " matter(s) from bank " + from + " into bank " + to + ".");

                return new TransferResult { BatchNumber = batch, MatterCount = matters.Rows.Count, LineCount = Convert.ToInt32(lines), Total = total };
            });
        }

        /// <summary>
        /// Takes the next trust adjustment batch number from Juris' own counter (SysParam LastBatchTrust) and advances it,
        /// in the caller's transaction (see <see cref="JurisCounters"/>: locked until commit, above every existing batch,
        /// never lowers the counter).
        /// </summary>
        private int ClaimTrustBatchNumber() =>
            JurisCounters.Next(_session, "LastBatchTrust", "trust adjustment batch number", "TrAdjBatch.TABBatchNbr");

        /// <summary>Puts the batch in the Juris batch tree (class 7500), creating the user / period folders the firm option asks for.</summary>
        private void AddToBatchTree(int batch, string title) =>
            JurisBatchTree.AddBatch(_session, TrustAdjustmentDocClass, "Trust Adjustment", batch, title, BatchFolderUser);

        private static string Left(string text, int length) => text.Length <= length ? text : text.Substring(0, length);
    }
}
