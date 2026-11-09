using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.MatterCloser
{
    /// <summary>
    /// The ONE set of close rules shared by Matter Closer (criteria and spreadsheet modes) and the Matter Status and Lock
    /// Updater. Where the original tools disagreed, the stricter rule wins:
    /// a matter can be closed only when it has no WIP (amount or entries), no unposted time/expense (Suite entries too when
    /// the company has Juris Suite, otherwise batches only), no unposted or open vouchers (distribution or header matter),
    /// no open bills/A/R, no unposted/pending cash receipts, no prepaid balance, no trust balance in ANY bank in EITHER
    /// TrustAccount or TrustLedger, no unposted trust adjustments, is on no open prebill and is not part of a split-billing
    /// arrangement. This is stricter than the matter checks Juris keeps in the database (qsTrustAccountMatterNoBalance_COUNT
    /// = per-bank trust, qsARMatAllocByMatterIfBal = open A/R, qsPPDBalanceByCliMat = prepaid). Credit memos and manual
    /// bills update A/R (LedgerHistory) as soon as they are entered, so the A/R check covers them.
    /// </summary>
    public static class MatterCloseRules
    {
        /// <summary>
        /// True when the company has the Juris Suite entry tables. Checked on every use so a company switch can never reuse
        /// another company's answer. Uses object_id rather than a failing query so it is safe inside a transaction.
        /// </summary>
        public static bool HasSuite(JurisSession session) =>
            Convert.ToInt32(session.Scalar(
                @"select case when object_id('TimeEntryLink') is not null and object_id('TimeEntry') is not null
                               and object_id('ExpenseEntryLink') is not null and object_id('ExpenseEntry') is not null
                          then 1 else 0 end"), CultureInfo.InvariantCulture) == 1;

        // Every column of the derived table B, in order; money columns are decimal(19,2), the rest are counts (int).
        private static readonly string[] Columns =
        {
            "UnbilledTime", "UnbilledTimeAmt", "UnbilledExp", "UnbilledExpAmt", "UnpostedTime", "UnpostedExp",
            "UnpostedVouchers", "OpenVouchers", "OpenBills", "AR", "PendingCash", "PPD", "Trust", "TrustBanks",
            "TrustLedger", "TrustLedgerBanks", "UnpostedTrustAdj", "Prebills", "Splits",
        };

        private static readonly HashSet<string> MoneyColumns =
            new HashSet<string> { "UnbilledTimeAmt", "UnbilledExpAmt", "AR", "PPD", "Trust", "TrustLedger" };

        /// <summary>
        /// One "union all" branch of B: <paramref name="matter"/> is the matter-key column, <paramref name="from"/> the
        /// FROM part, <paramref name="where"/> its filter (or null), <paramref name="values"/> pairs of column name and
        /// aggregate expression. Every branch is limited to the matters in <paramref name="scope"/> (a temp table with a
        /// MatSysNbr column) BEFORE it aggregates, so only the matters being checked are read, never the whole firm.
        /// Columns not named are zero, so a branch can never put a value in the wrong position.
        /// </summary>
        private static string Branch(string scope, string matter, string from, string where, params string[] values)
        {
            from += "\n          where " + matter + " in (select MatSysNbr from " + scope + ")" + (where == null ? "" : " and (" + where + ")") +
                    "\n          group by " + matter;
            var map = new Dictionary<string, string>();
            for (var i = 0; i < values.Length; i += 2)
            {
                if (Array.IndexOf(Columns, values[i]) < 0)
                    throw new ArgumentException("Unknown close-rule column " + values[i]);
                map[values[i]] = values[i + 1];
            }
            var cols = Columns.Select(c =>
            {
                var expr = map.TryGetValue(c, out var e) ? e : "0";
                return (MoneyColumns.Contains(c) ? "cast(" + expr + " as decimal(19,2))" : "cast(" + expr + " as int)") + " as " + c;
            });
            return "\n        select " + matter + " as MatSys, " + string.Join(", ", cols) + "\n          " + from;
        }

        /// <summary>
        /// Derived table "B" with one row per matter (MatSys) holding everything that stops a matter being closed:
        /// WIP (UnbilledTime/UnbilledExp entry counts and amounts, WIP = their total), unposted time/expense (batch lines and,
        /// with Juris Suite, entries not yet posted), unposted voucher batch lines (matter distribution or header matter),
        /// open vouchers (matter distribution or header matter, e.g. trust vouchers), open bills / A/R, pending cash (unposted
        /// cash receipt allocations and A/R lines flagged pending), prepaid balance, trust balance per bank in TrustAccount and
        /// in TrustLedger (TrustBanks / TrustLedgerBanks = banks with a non-zero balance, so balances in two banks that
        /// cancel out still block), unposted trust adjustments, open prebills (as billed or split-to matter) and split billing.
        /// Only the matters in <paramref name="scope"/> (a temp table with a MatSysNbr column) are read.
        /// </summary>
        public static string Balances(bool hasSuite, string scope)
        {
            string B(string matter, string from, string where, params string[] values) => Branch(scope, matter, from, where, values);
            var branches = new List<string>
            {
                B("MatSysNbr", "from Matter", null, "PPD", "sum(MatPPDBalance)"),
                B("ARMMatter", "from ARMatAlloc", "ARMBalDue <> 0", "OpenBills", "count(*)", "AR", "sum(ARMBalDue)"),
                B("ARMMatter", "from ARMatAlloc", "ARMPendingFlag = 'Y'", "PendingCash", "count(*)"),
                B("UTMatter", "from UnbilledTime", null, "UnbilledTime", "count(*)", "UnbilledTimeAmt", "sum(UTAmount)"),
                B("UEMatter", "from UnbilledExpense", null, "UnbilledExp", "count(*)", "UnbilledExpAmt", "sum(UEAmount)"),
            };
            if (hasSuite)
            {
                // Suite: a batch line created from an entry is counted through the entry (EntryStatus 0-6 = not yet posted).
                branches.Add(B("TBDMatter", "from TimeBatchDetail",
                    "TBDPosted <> 'Y' and not exists (select 1 from TimeEntryLink L where L.TBDID = TimeBatchDetail.TBDID)", "UnpostedTime", "count(*)"));
                branches.Add(B("MatterSysNbr", "from TimeEntry", "EntryStatus <= 6", "UnpostedTime", "count(*)"));
                branches.Add(B("EBDMatter", "from ExpBatchDetail",
                    "EBDPosted <> 'Y' and not exists (select 1 from ExpenseEntryLink L where L.EBDID = ExpBatchDetail.EBDID)", "UnpostedExp", "count(*)"));
                branches.Add(B("MatterSysNbr", "from ExpenseEntry", "EntryStatus <= 6", "UnpostedExp", "count(*)"));
            }
            else
            {
                branches.Add(B("TBDMatter", "from TimeBatchDetail", "TBDPosted <> 'Y'", "UnpostedTime", "count(*)"));
                branches.Add(B("EBDMatter", "from ExpBatchDetail", "EBDPosted <> 'Y'", "UnpostedExp", "count(*)"));
            }
            branches.AddRange(new[]
            {
                B("VBMMatter", "from VoucherBatchMatDist inner join VoucherBatchDetail on VBDBatch = VBMBatch and VBDRecNbr = VBMRecNbr",
                    "VBDPosted <> 'Y'", "UnpostedVouchers", "count(*)"),
                // Trust/refund vouchers carry the matter on the voucher itself (no matter distribution lines).
                B("VBDMatter", "from VoucherBatchDetail", "VBDPosted <> 'Y'", "UnpostedVouchers", "count(*)"),
                B("VMMatter", "from Voucher inner join VoucherMatDist on VMVoucher = VchVoucherNbr",
                    "VchStatus = 'O' and VMAmount - VMAmountPaid <> 0", "OpenVouchers", "count(*)"),
                B("VchMatter", "from Voucher", "VchStatus = 'O'", "OpenVouchers", "count(*)"),
                // Unposted cash receipts allocated to the matter (A/R, fee, expense, prepaid or trust).
                B("CRAMatter", "from CRARAlloc inner join CashReceipt on CRBatch = CRABatch and CRRecNbr = CRARecNbr", "CRPosted <> 'Y'", "PendingCash", "count(*)"),
                B("CRFMatter", "from CRFeeAlloc inner join CashReceipt on CRBatch = CRFBatch and CRRecNbr = CRFRecNbr", "CRPosted <> 'Y'", "PendingCash", "count(*)"),
                B("CREMatter", "from CRExpAlloc inner join CashReceipt on CRBatch = CREBatch and CRRecNbr = CRERecNbr", "CRPosted <> 'Y'", "PendingCash", "count(*)"),
                B("CRPMatter", "from CRPPDAlloc inner join CashReceipt on CRBatch = CRPBatch and CRRecNbr = CRPRecNbr", "CRPosted <> 'Y'", "PendingCash", "count(*)"),
                B("CRTMatter", "from CRTrustAlloc inner join CashReceipt on CRBatch = CRTBatch and CRRecNbr = CRTRecNbr", "CRPosted <> 'Y'", "PendingCash", "count(*)"),
                B("TAMatter", "from TrustAccount", null, "Trust", "sum(TABalance)", "TrustBanks", "sum(case when TABalance <> 0 then 1 else 0 end)"),
                B("TLMatter", "from (select TLMatter, sum(TLAmount) as Amt from TrustLedger where TLMatter in (select MatSysNbr from " + scope + ") group by TLMatter, TLBank) TL",
                    null, "TrustLedger", "sum(Amt)", "TrustLedgerBanks", "sum(case when Amt <> 0 then 1 else 0 end)"),
                B("TABDMatter", "from TrAdjBatchDetail", "TABDPosted <> 'Y'", "UnpostedTrustAdj", "count(*)"),
                B("PBMMatter", "from PreBillMatter inner join PreBill on PBSysNbr = PBMPreBill", null, "Prebills", "count(*)"),
                B("PBSTFromMatter", "from PreBillSplitTo inner join PreBill on PBSysNbr = PBSTPreBill", null, "Prebills", "count(*)"),
                B("PBSTToMatter", "from PreBillSplitTo inner join PreBill on PBSysNbr = PBSTPreBill", null, "Prebills", "count(*)"),
                B("SplitFromMat", "from SplitBill", null, "Splits", "count(*)"),
                B("SplitToMat", "from SplitBill", null, "Splits", "count(*)"),
            });

            return "(select MatSys, " + string.Join(", ", Columns.Select(c => "sum(" + c + ") as " + c)) +
                   ", sum(UnbilledTimeAmt) + sum(UnbilledExpAmt) as WIP\n    from (" +
                   string.Join("\n        union all", branches) + ") X\n    group by MatSys) B";
        }

        /// <summary>True (SQL predicate on alias B) when the matter still has something that stops it being closed.</summary>
        public const string Unresolved =
            "(B.WIP <> 0 or B.UnbilledTime <> 0 or B.UnbilledExp <> 0 or B.UnpostedTime <> 0 or B.UnpostedExp <> 0" +
            " or B.UnpostedVouchers <> 0 or B.OpenVouchers <> 0 or B.OpenBills <> 0 or B.AR <> 0 or B.PendingCash <> 0 or B.PPD <> 0" +
            " or B.Trust <> 0 or B.TrustBanks <> 0 or B.TrustLedger <> 0 or B.TrustLedgerBanks <> 0 or B.UnpostedTrustAdj <> 0" +
            " or B.Prebills <> 0 or B.Splits <> 0)";

        /// <summary>The B columns <see cref="Reasons"/> needs, for SELECT lists.</summary>
        public const string CheckColumns =
            "B.UnbilledTime, B.UnbilledTimeAmt, B.UnbilledExp, B.UnbilledExpAmt, B.WIP, B.UnpostedTime, B.UnpostedExp," +
            " B.UnpostedVouchers, B.OpenVouchers, B.OpenBills, B.AR, B.PendingCash, B.PPD, B.Trust, B.TrustBanks, B.TrustLedger," +
            " B.TrustLedgerBanks, B.UnpostedTrustAdj, B.Prebills, B.Splits";

        /// <summary>
        /// The UPDATE that closes (status C, lock 3 "Disallow Time and Expense", close date = today) the matters returned by
        /// <paramref name="candidateSubquery"/> (a "select MatSysNbr ..." text) that STILL pass the rules at update time,
        /// so a matter that gained activity after the review is not closed. Closed and 'P' matters are never touched.
        /// Run it through <see cref="Close"/>, which locks the matter rows first.
        /// </summary>
        private const string CloseScope = "#JpsCloseScope";

        /// <summary>
        /// Closes the candidate matters (<paramref name="candidateSubquery"/>, a "select MatSysNbr ..." text) that still pass
        /// the rules, in one transaction (joining an outer one). The open candidates are first copied to a scope table while
        /// their Matter rows are locked (updlock, holdlock), so a Juris posting that is updating one of them (posting time,
        /// expenses and bills updates the matter's last-activity dates) finishes first and its rows are seen by the re-check,
        /// and nothing can change a candidate's status between the check and the update. Only those matters' balances are
        /// read. Closed matters get status C, lock 3 "Disallow Time and Expense" and today's close date; closed and 'P'
        /// matters are never touched. <paramref name="parameters"/> is called once per statement, because a SqlParameter
        /// can belong to only one command. Returns the number closed.
        /// </summary>
        public static int Close(JurisSession session, string candidateSubquery, Func<SqlParameter[]> parameters) =>
            session.InTransaction(() =>
            {
                session.Execute(DropScope(CloseScope) + "create table " + CloseScope + " (MatSysNbr int primary key);\n" +
                                "insert " + CloseScope + " select MatSysNbr from Matter with (updlock, holdlock) where MatStatusFlag not in ('C', 'P') and MatSysNbr in (" +
                                candidateSubquery + ")", parameters());
                try
                {
                    return session.ExecuteRows(
                        @"update Matter set MatStatusFlag = 'C', MatLockFlag = '3', MatDateClosed = cast(getdate() as date)
where MatStatusFlag not in ('C', 'P')
  and MatSysNbr in (select B.MatSys from " + Balances(HasSuite(session), CloseScope) + " where not " + Unresolved + ")");
                }
                finally
                {
                    session.Execute(DropScope(CloseScope));
                }
            });

        /// <summary>SQL that drops a session temp table if it exists (left behind if an earlier statement failed).</summary>
        public static string DropScope(string table) => "if object_id('tempdb.." + table + "') is not null drop table " + table + ";\n";

        /// <summary>
        /// The column headings reports show for the B columns (and WIP). Reports select B.X as [heading]; <see cref="Reasons"/>
        /// reads a column by either name.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Headings = new Dictionary<string, string>
        {
            ["UnbilledTime"] = "Unbilled Time Entries",
            ["UnbilledTimeAmt"] = "Unbilled Time Amount",
            ["UnbilledExp"] = "Unbilled Expense Entries",
            ["UnbilledExpAmt"] = "Unbilled Expense Amount",
            ["WIP"] = "Total Unbilled (WIP)",
            ["UnpostedTime"] = "Unposted Time Entries",
            ["UnpostedExp"] = "Unposted Expense Entries",
            ["UnpostedVouchers"] = "Unposted Vouchers",
            ["OpenVouchers"] = "Open Vouchers",
            ["OpenBills"] = "Open Bills",
            ["AR"] = "A/R Balance",
            ["PendingCash"] = "Unposted Cash Receipts",
            ["PPD"] = "Prepaid Balance",
            ["Trust"] = "Trust Balance",
            ["TrustBanks"] = "Trust Banks With a Balance",
            ["TrustLedger"] = "Trust Ledger Balance",
            ["TrustLedgerBanks"] = "Trust Ledger Banks With a Balance",
            ["UnpostedTrustAdj"] = "Unposted Trust Adjustments",
            ["Prebills"] = "Open Prebills",
            ["Splits"] = "Split Billing Arrangements",
        };

        /// <summary>"B.UnbilledTime as [Unbilled Time Entries], ..." for the given B columns, for report SELECT lists.</summary>
        public static string HeadedColumns(params string[] columns) =>
            string.Join(", ", columns.Select(c => "B." + c + " as [" + Headings[c] + "]"));

        /// <summary>Plain-English list of what stops a matter closing, from a row holding <see cref="CheckColumns"/> (or their <see cref="Headings"/>). Empty when ready.</summary>
        public static string Reasons(DataRow row)
        {
            var reasons = new List<string>();
            string Col(string c) => row.Table.Columns.Contains(c) ? c : Headings.TryGetValue(c, out var h) && row.Table.Columns.Contains(h) ? h : null;
            int Count(string c) => Col(c) is string n && row[n] != DBNull.Value ? Convert.ToInt32(row[n], CultureInfo.InvariantCulture) : 0;
            decimal Amount(string c) => Col(c) is string n && row[n] != DBNull.Value ? Convert.ToDecimal(row[n], CultureInfo.InvariantCulture) : 0m;
            string Money(decimal d) => d.ToString("$#,##0.00;-$#,##0.00", CultureInfo.InvariantCulture);

            int ut = Count("UnbilledTime"), ue = Count("UnbilledExp");
            var wip = Amount("WIP");
            if (ut != 0 || ue != 0 || wip != 0)
                reasons.Add(ut + " unbilled time and " + ue + " unbilled expense entries (WIP " + Money(wip) + ")");
            int pt = Count("UnpostedTime"), pe = Count("UnpostedExp");
            if (pt != 0 || pe != 0)
                reasons.Add(pt + " unposted time and " + pe + " unposted expense entries");
            int uv = Count("UnpostedVouchers"), ov = Count("OpenVouchers");
            if (uv != 0 || ov != 0)
                reasons.Add(uv + " unposted and " + ov + " open vouchers");
            var bills = Count("OpenBills");
            var ar = Amount("AR");
            if (bills != 0 || ar != 0)
                reasons.Add(bills + " open bills with A/R of " + Money(ar));
            var cash = Count("PendingCash");
            if (cash != 0)
                reasons.Add(cash + " unposted or pending cash receipt lines");
            var ppd = Amount("PPD");
            if (ppd != 0)
                reasons.Add("prepaid balance of " + Money(ppd));
            var trust = Amount("Trust");
            var ledger = Amount("TrustLedger");
            int trustBanks = Count("TrustBanks"), ledgerBanks = Count("TrustLedgerBanks");
            if (trust != 0 || trustBanks != 0)
                reasons.Add("trust balance of " + Money(trust) + (trustBanks > 1 ? " across " + trustBanks + " banks" : ""));
            if ((ledger != 0 || ledgerBanks != 0) && (ledger != trust || ledgerBanks != trustBanks))
                reasons.Add("trust ledger balance of " + Money(ledger) + (ledgerBanks > 1 ? " across " + ledgerBanks + " banks" : ""));
            var trustAdj = Count("UnpostedTrustAdj");
            if (trustAdj != 0)
                reasons.Add(trustAdj + " unposted trust adjustments");
            var prebills = Count("Prebills");
            if (prebills != 0)
                reasons.Add("on " + prebills + " open prebill(s) (post or delete the prebill first)");
            if (Count("Splits") != 0)
                reasons.Add("part of a split-billing arrangement (remove the split first)");
            return string.Join("; ", reasons);
        }

        /// <summary>Checks the given matters against the rules. Key = MatSysNbr.</summary>
        public static Dictionary<int, MatterCheck> Check(JurisSession session, bool hasSuite, IEnumerable<int> matterIds)
        {
            var ids = matterIds.Distinct().ToList();
            var result = new Dictionary<int, MatterCheck>();
            if (ids.Count == 0)
                return result;
            var table = MatterIdTable.With(session, ids, () => session.Query(
                "select B.MatSys, " + CheckColumns + ", case when " + Unresolved + " then 0 else 1 end as Ready from " + Balances(hasSuite, MatterIdTable.Name)));
            foreach (var row in table.Rows())
            {
                var id = Convert.ToInt32(row["MatSys"], CultureInfo.InvariantCulture);
                result[id] = new MatterCheck(Convert.ToInt32(row["Ready"], CultureInfo.InvariantCulture) == 1, Reasons(row), row);
            }
            return result;
        }
    }

    public sealed class MatterCheck
    {
        public MatterCheck(bool ready, string reasons, DataRow row)
        {
            Ready = ready;
            Reasons = reasons;
            Row = row;
        }

        public bool Ready { get; }
        public string Reasons { get; }
        public DataRow Row { get; }
    }

    /// <summary>
    /// The #JpsMatterIds temp table of matter sys numbers (see <see cref="TempKeyTable"/>) that the close rules join to.
    /// </summary>
    public static class MatterIdTable
    {
        public const string Name = "#JpsMatterIds";

        public static T With<T>(JurisSession session, IEnumerable<int> matterIds, Func<T> work) =>
            TempKeyTable.With(session, Name, "MatSysNbr", matterIds, work);
    }
}
