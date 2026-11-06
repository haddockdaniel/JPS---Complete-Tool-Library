using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Tools.MatterCloser;

namespace Jps.Tools.Tools.MatterStatusLock
{
    public enum SelectionMode { ClientAll, ClientSpecific, BillingTimekeeper, LastBillDate, Spreadsheet }

    /// <summary>Everything the user chose, captured on the UI thread before any database work.</summary>
    public sealed class StatusLockRequest
    {
        /// <summary>"-" (leave as is), "O", "C" or "F".</summary>
        public string NewStatus { get; set; }
        /// <summary>"-" (leave as is), "0".."3". Forced to "3" when the new status is "C".</summary>
        public string NewLock { get; set; }
        public SelectionMode Mode { get; set; }
        public object CliSysNbr { get; set; }
        public List<int> MatterIds { get; set; } = new List<int>();
        public object BillTkprSysNbr { get; set; }
        public DateTime? OpenedOnOrBefore { get; set; }
        public DateTime LastBillOnOrBefore { get; set; }
        public DateTime? NeverBilledOpenedBefore { get; set; }
        public string SelectionText { get; set; }

        public string EffectiveLock => NewStatus == "C" ? "3" : NewLock;
    }

    public enum PlannedChange { None, Update, Close, Blocked }

    public sealed class PlannedMatter
    {
        public int MatSysNbr { get; set; }
        public string ClientCode { get; set; }
        public string ClientName { get; set; }
        public string MatterCode { get; set; }
        public string MatterName { get; set; }
        public string Status { get; set; }
        public string Lock { get; set; }
        public string LastBill { get; set; }
        public PlannedChange Change { get; set; }
        public string ChangeText { get; set; }
        public MatterCheck Check { get; set; }
    }

    public sealed class StatusLockPlan
    {
        public List<PlannedMatter> Matters { get; } = new List<PlannedMatter>();
        public List<SheetMatterRow> SheetProblems { get; } = new List<SheetMatterRow>();
        public int ChangeCount => Matters.Count(m => m.Change == PlannedChange.Update || m.Change == PlannedChange.Close);
        public int BlockedCount => Matters.Count(m => m.Change == PlannedChange.Blocked);
    }

    public sealed class StatusLockResult
    {
        public int Updated { get; set; }
        public int Closed { get; set; }
        /// <summary>Matters that passed the check but gained activity before the update, so were left open.</summary>
        public int LeftOpen { get; set; }
    }

    /// <summary>SQL for the Matter Status and Lock Updater. Close eligibility comes from the shared <see cref="MatterCloseRules"/>.</summary>
    public sealed class MatterStatusLockService
    {
        private readonly JurisSession _session;

        public MatterStatusLockService(JurisSession session)
        {
            _session = session;
        }

        public static string StatusName(string code)
        {
            switch (code)
            {
                case "O": return "Open";
                case "C": return "Closed";
                case "F": return "Final Bill Sent";
                case "P": return "Ready to Purge";
                default: return code;
            }
        }

        public static string LockName(string code)
        {
            switch (code)
            {
                case "0": return "Allow time and expenses";
                case "1": return "Disallow time";
                case "2": return "Disallow expenses";
                case "3": return "Disallow time and expenses";
                default: return code;
            }
        }

        private const string Select = @"select Matter.MatSysNbr, dbo.jfn_FormatClientCode(Client.CliCode) as ClientCode, Client.CliReportingName,
       dbo.jfn_FormatMatterCode(Matter.MatCode) as MatterCode, Matter.MatReportingName, Matter.MatStatusFlag, Matter.MatLockFlag,
       case when Matter.MatDateLastBill < '19020101' then 'Never' else convert(varchar(10), Matter.MatDateLastBill, 101) end as LastBill
from Matter inner join Client on Client.CliSysNbr = Matter.MatCliNbr";

        private const string Order = "\norder by dbo.jfn_FormatClientCode(Client.CliCode), dbo.jfn_FormatMatterCode(Matter.MatCode)";

        /// <summary>Spreadsheet mode, step 1: read and resolve. Returns the rows (check <see cref="SheetMatterRow.Problem"/>).</summary>
        public List<SheetMatterRow> ReadSpreadsheet(string path)
        {
            var rows = SpreadsheetMatters.Read(path);
            SpreadsheetMatters.Resolve(_session, rows, true);
            return rows;
        }

        /// <summary>Finds the candidate matters and decides, per matter, what will happen.</summary>
        public StatusLockPlan Plan(StatusLockRequest request)
        {
            var p = new List<SqlParameter>();
            DataTable table;
            switch (request.Mode)
            {
                case SelectionMode.ClientAll:
                    p.Add(Sql.P("@Cli", request.CliSysNbr));
                    table = _session.Query(Select + "\nwhere Client.CliSysNbr = @Cli" + Order, p.ToArray());
                    break;
                case SelectionMode.BillingTimekeeper:
                    p.Add(Sql.P("@BT", request.BillTkprSysNbr));
                    var opened = "";
                    if (request.OpenedOnOrBefore.HasValue)
                    {
                        opened = " and Matter.MatDateOpened <= @Opened";
                        p.Add(new SqlParameter("@Opened", SqlDbType.DateTime) { Value = request.OpenedOnOrBefore.Value.Date });
                    }
                    table = _session.Query(Select + @"
inner join BillTo on BillTo.BillToSysNbr = Matter.MatBillTo
inner join Employee on Employee.EmpSysNbr = BillTo.BillToBillingAtty
where Employee.EmpSysNbr = @BT" + opened + Order, p.ToArray());
                    break;
                case SelectionMode.LastBillDate:
                    p.Add(new SqlParameter("@LastBill", SqlDbType.DateTime) { Value = request.LastBillOnOrBefore.Date });
                    var never = "";
                    if (request.NeverBilledOpenedBefore.HasValue)
                    {
                        never = " or (Matter.MatDateLastBill < '19020101' and Matter.MatDateOpened < @NeverBilled)";
                        p.Add(new SqlParameter("@NeverBilled", SqlDbType.DateTime) { Value = request.NeverBilledOpenedBefore.Value.Date });
                    }
                    table = _session.Query(Select + "\nwhere (Matter.MatDateLastBill between '19020101' and @LastBill" + never + ")" + Order, p.ToArray());
                    break;
                default: // ClientSpecific, Spreadsheet
                    table = MatterIdTable.With(_session, request.MatterIds, () => _session.Query(
                        Select + "\nwhere Matter.MatSysNbr in (select MatSysNbr from " + MatterIdTable.Name + ")" + Order));
                    break;
            }

            var plan = new StatusLockPlan();
            var rows = table.Rows().ToList();
            Dictionary<int, MatterCheck> checks = new Dictionary<int, MatterCheck>();
            if (request.NewStatus == "C")
            {
                var toCheck = rows.Where(r => !IsStatus(r, "C") && !IsStatus(r, "P"))
                    .Select(r => Convert.ToInt32(r["MatSysNbr"], CultureInfo.InvariantCulture));
                checks = MatterCloseRules.Check(_session, MatterCloseRules.HasSuite(_session), toCheck);
            }

            foreach (var r in rows)
            {
                var m = new PlannedMatter
                {
                    MatSysNbr = Convert.ToInt32(r["MatSysNbr"], CultureInfo.InvariantCulture),
                    ClientCode = Sql.Text(r, "ClientCode"),
                    ClientName = Sql.Text(r, "CliReportingName"),
                    MatterCode = Sql.Text(r, "MatterCode"),
                    MatterName = Sql.Text(r, "MatReportingName"),
                    Status = Sql.Text(r, "MatStatusFlag"),
                    Lock = Sql.Text(r, "MatLockFlag"),
                    LastBill = Sql.Text(r, "LastBill"),
                };
                checks.TryGetValue(m.MatSysNbr, out var check);
                m.Check = check;
                Decide(m, request);
                // Selection modes list only matters that would change; spreadsheet mode lists every row, as the original did.
                if (m.Change != PlannedChange.None || request.Mode == SelectionMode.Spreadsheet)
                    plan.Matters.Add(m);
            }
            return plan;
        }

        private static bool IsStatus(DataRow r, string code) => Sql.Text(r, "MatStatusFlag") == code;

        private static void Decide(PlannedMatter m, StatusLockRequest request)
        {
            var newLock = request.EffectiveLock;
            if (m.Status == "P")
            {
                m.Change = PlannedChange.None;
                m.ChangeText = "Left as is: this tool doesn't change Ready to Purge matters";
                return;
            }
            switch (request.NewStatus)
            {
                case "-":
                    if (m.Status == "C") { m.Change = PlannedChange.None; m.ChangeText = "Left as is: closed matters keep their lock"; }
                    else if (m.Lock == newLock) { m.Change = PlannedChange.None; m.ChangeText = "Already set"; }
                    else { m.Change = PlannedChange.Update; m.ChangeText = "Lock: " + LockName(newLock); }
                    return;
                case "C":
                    if (m.Status == "C")
                    {
                        if (m.Lock == "3") { m.Change = PlannedChange.None; m.ChangeText = "Already set"; }
                        else { m.Change = PlannedChange.Update; m.ChangeText = "Already closed; lock: " + LockName("3"); }
                    }
                    else if (m.Check == null) { m.Change = PlannedChange.Blocked; m.ChangeText = "Can't be closed: its balances could not be checked"; }
                    else if (!m.Check.Ready) { m.Change = PlannedChange.Blocked; m.ChangeText = "Can't be closed: " + m.Check.Reasons; }
                    else { m.Change = PlannedChange.Close; m.ChangeText = "Close (lock: " + LockName("3") + ")"; }
                    return;
                default: // O or F
                    var statusChanges = m.Status != request.NewStatus;
                    var lockChanges = newLock != "-" && m.Lock != newLock;
                    if (!statusChanges && !lockChanges) { m.Change = PlannedChange.None; m.ChangeText = "Already set"; return; }
                    m.Change = PlannedChange.Update;
                    var parts = new List<string>();
                    if (statusChanges) parts.Add("Status: " + StatusName(request.NewStatus) + (m.Status == "C" ? " (reopened; close date cleared)" : ""));
                    if (lockChanges) parts.Add("Lock: " + LockName(newLock));
                    m.ChangeText = string.Join("; ", parts);
                    return;
            }
        }

        /// <summary>The pre-report the user approves.</summary>
        public static DataTable PreviewTable(StatusLockPlan plan)
        {
            var t = new DataTable();
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Client Name", typeof(string));
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Matter Name", typeof(string));
            t.Columns.Add("Current Status", typeof(string));
            t.Columns.Add("Current Lock", typeof(string));
            t.Columns.Add("Last Bill Date", typeof(string));
            t.Columns.Add("What will happen", typeof(string));
            foreach (var m in plan.Matters)
                t.Rows.Add(m.ClientCode, m.ClientName, m.MatterCode, m.MatterName, StatusName(m.Status), LockName(m.Lock), m.LastBill, m.ChangeText);
            return t;
        }

        /// <summary>Exception report for matters that can't be closed (layout of the original's ##TempBals, plus reasons).</summary>
        public static DataTable ExceptionTable(StatusLockPlan plan)
        {
            var t = new DataTable();
            foreach (var c in new[] { "Client Code", "Matter Code" }) t.Columns.Add(c, typeof(string));
            t.Columns.Add("Why It Can't Be Closed", typeof(string));
            foreach (var c in new[] { "Prepaid Balance", "Unbilled Time Amount", "Unbilled Expense Amount" }) t.Columns.Add(c, typeof(decimal));
            foreach (var c in new[] { "Unbilled Time Entries", "Unbilled Expense Entries", "Unposted Time Entries", "Unposted Expense Entries", "Unposted Vouchers", "Open Vouchers", "Open Bills" })
                t.Columns.Add(c, typeof(int));
            t.Columns.Add("A/R Balance", typeof(decimal));
            t.Columns.Add("Unposted Cash Receipts", typeof(int));
            foreach (var c in new[] { "Trust Balance", "Trust Ledger Balance" }) t.Columns.Add(c, typeof(decimal));
            foreach (var c in new[] { "Unposted Trust Adjustments", "Open Prebills" }) t.Columns.Add(c, typeof(int));
            t.Columns.Add("Split Billing", typeof(string));
            foreach (var m in plan.Matters.Where(x => x.Change == PlannedChange.Blocked))
            {
                var r = m.Check?.Row;
                decimal D(string c) => r == null || r[c] == DBNull.Value ? 0m : Convert.ToDecimal(r[c], CultureInfo.InvariantCulture);
                int I(string c) => r == null || r[c] == DBNull.Value ? 0 : Convert.ToInt32(r[c], CultureInfo.InvariantCulture);
                t.Rows.Add(m.ClientCode, m.MatterCode, m.Check?.Reasons ?? m.ChangeText, D("PPD"), D("UnbilledTimeAmt"), D("UnbilledExpAmt"),
                    I("UnbilledTime"), I("UnbilledExp"), I("UnpostedTime"), I("UnpostedExp"), I("UnpostedVouchers"), I("OpenVouchers"), I("OpenBills"),
                    D("AR"), I("PendingCash"), D("Trust"), D("TrustLedger"), I("UnpostedTrustAdj"), I("Prebills"), I("Splits") != 0 ? "Yes" : "No");
            }
            return t;
        }

        /// <summary>Applies the plan in one transaction, re-checking close rules at update time, and writes UtilityLog.</summary>
        public StatusLockResult Apply(StatusLockRequest request, StatusLockPlan plan)
        {
            var updateIds = plan.Matters.Where(m => m.Change == PlannedChange.Update).Select(m => m.MatSysNbr).ToList();
            var closeIds = plan.Matters.Where(m => m.Change == PlannedChange.Close).Select(m => m.MatSysNbr).ToList();
            var newLock = request.EffectiveLock;
            var result = new StatusLockResult();

            _session.InTransaction(() =>
            {
                if (updateIds.Count > 0)
                    result.Updated = MatterIdTable.With(_session, updateIds, () =>
                    {
                        var ids = "(select MatSysNbr from " + MatterIdTable.Name + ")";
                        switch (request.NewStatus)
                        {
                            case "-":
                                return _session.ExecuteRows("update Matter set MatLockFlag = @Lock where MatSysNbr in " + ids + " and MatStatusFlag not in ('C','P')",
                                    Sql.P("@Lock", newLock));
                            case "C": // already-closed matters: lock only
                                return _session.ExecuteRows("update Matter set MatLockFlag = '3' where MatSysNbr in " + ids + " and MatStatusFlag = 'C'");
                            default:
                                return _session.ExecuteRows(@"update Matter set MatStatusFlag = @Status,
       MatLockFlag = case when @Lock = '-' then MatLockFlag else @Lock end,
       MatDateClosed = case when MatStatusFlag = 'C' then '19000101' else MatDateClosed end
where MatSysNbr in " + ids + " and MatStatusFlag <> 'P'", Sql.P("@Status", request.NewStatus), Sql.P("@Lock", newLock));
                        }
                    });

                if (closeIds.Count > 0)
                    result.Closed = MatterIdTable.With(_session, closeIds, () =>
                        MatterCloseRules.Close(_session, "select MatSysNbr from " + MatterIdTable.Name, () => new SqlParameter[0]));
                result.LeftOpen = closeIds.Count - result.Closed;

                _session.WriteUtilityLog("JPS Matter Status and Lock Updater: status " +
                    (request.NewStatus == "-" ? "unchanged" : StatusName(request.NewStatus)) + ", lock " +
                    (newLock == "-" ? "unchanged" : LockName(newLock)) + "; " + request.SelectionText + "; updated " + result.Updated +
                    ", closed " + result.Closed + ", not closable " + (plan.BlockedCount + result.LeftOpen) + ".");
            });
            return result;
        }
    }
}
