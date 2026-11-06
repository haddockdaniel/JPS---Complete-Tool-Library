using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.MatterCloser
{
    /// <summary>What the user picked in "Close by criteria". Null = filter not used.</summary>
    public sealed class CloseCriteria
    {
        public int? Days { get; set; }
        public DateTime? Since { get; set; }
        public object BillTkpr { get; set; }
        public string BillTkprText { get; set; }
        public object Client { get; set; }
        public string ClientText { get; set; }
        public object PracticeClass { get; set; }
        public DateTime? OpenedOnOrBefore { get; set; }
        public object ExceptBillTkpr { get; set; }
        public string ExceptBillTkprText { get; set; }
        public object ExceptClient { get; set; }
        public string ExceptClientText { get; set; }

        /// <summary>The WHERE clause (fresh parameters each call: a SqlParameter can only belong to one command).</summary>
        public string Where(List<SqlParameter> p)
        {
            var w = "where Matter.MatStatusFlag in ('O','F')";
            if (Days.HasValue)
            {
                p.Add(Sql.P("@Days", Days.Value));
                foreach (var col in new[] { "MatDateLastWork", "MatDateLastExp", "MatDateLastBill", "MatDateLastPaymt", "MatDateOpened" })
                    w += "\n  and datediff(d, Matter." + col + ", getdate()) > @Days";
            }
            else
            {
                p.Add(new SqlParameter("@Since", SqlDbType.DateTime) { Value = Since.Value.Date });
                foreach (var col in new[] { "MatDateLastWork", "MatDateLastExp", "MatDateLastBill", "MatDateLastPaymt", "MatDateOpened" })
                    w += "\n  and Matter." + col + " < @Since";
            }
            if (BillTkpr != null) { w += "\n  and Employee.EmpSysNbr = @BT"; p.Add(Sql.P("@BT", BillTkpr)); }
            if (Client != null) { w += "\n  and Client.CliSysNbr = @Cli"; p.Add(Sql.P("@Cli", Client)); }
            if (PracticeClass != null) { w += "\n  and Matter.MatPracticeClass = @PC"; p.Add(Sql.P("@PC", PracticeClass)); }
            if (OpenedOnOrBefore.HasValue)
            {
                w += "\n  and Matter.MatDateOpened <= @OpenedBy";
                p.Add(new SqlParameter("@OpenedBy", SqlDbType.DateTime) { Value = OpenedOnOrBefore.Value.Date });
            }
            if (ExceptBillTkpr != null) { w += "\n  and Employee.EmpSysNbr <> @ExBT"; p.Add(Sql.P("@ExBT", ExceptBillTkpr)); }
            if (ExceptClient != null) { w += "\n  and Client.CliSysNbr <> @ExCli"; p.Add(Sql.P("@ExCli", ExceptClient)); }
            return w;
        }

        public string Describe()
        {
            var parts = new List<string>
            {
                Days.HasValue ? "no activity for more than " + Days.Value + " days"
                    : "no activity since " + Since.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            };
            if (BillTkpr != null) parts.Add("billing timekeeper " + BillTkprText);
            if (Client != null) parts.Add("client " + ClientText);
            if (PracticeClass != null) parts.Add("practice class " + PracticeClass);
            if (OpenedOnOrBefore.HasValue) parts.Add("opened on or before " + OpenedOnOrBefore.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
            if (ExceptBillTkpr != null) parts.Add("except billing timekeeper " + ExceptBillTkprText);
            if (ExceptClient != null) parts.Add("except client " + ExceptClientText);
            return string.Join(", ", parts);
        }
    }

    public sealed class CriteriaCounts
    {
        public int Matching { get; set; }
        public int Blocked { get; set; }
        public int Ready => Matching - Blocked;
    }

    /// <summary>The SQL behind both Matter Closer modes. All close rules come from <see cref="MatterCloseRules"/>.</summary>
    public sealed class MatterCloserService
    {
        private readonly JurisSession _session;

        public MatterCloserService(JurisSession session)
        {
            _session = session;
        }

        // ---------- Close by criteria ----------

        private const string CandidateFrom = @"from Matter
inner join Client on Matter.MatCliNbr = Client.CliSysNbr
inner join BillTo on Matter.MatBillTo = BillTo.BillToSysNbr
inner join Employee on Employee.EmpSysNbr = BillTo.BillToBillingAtty";

        // Originators feed only the OrigTkpr display column, so this is a LEFT join (an INNER join hid matters with no originator).
        private const string Originators = @"
left join (select MOrigMat,
                  max(case when OT = 1 then EmpInitials else '' end) + max(case when OT = 2 then ' ' + EmpInitials else '' end)
                + max(case when OT = 3 then ' ' + EmpInitials else '' end) + max(case when OT = 4 then ' ' + EmpInitials else '' end)
                + max(case when OT = 5 then ' ' + EmpInitials else '' end) as OrigTkpr
           from (select MOrigMat, E2.EmpInitials, rank() over (partition by MOrigMat order by E2.EmpInitials) as OT
                 from MatOrigAtty inner join Employee E2 on MOrigAtty = E2.EmpSysNbr) MO1
           group by MOrigMat) MO on Matter.MatSysNbr = MO.MOrigMat";

        private static string ReportFrom(bool hasSuite) =>
            CandidateFrom + Originators + "\ninner join " + MatterCloseRules.Balances(hasSuite) + " on B.MatSys = Matter.MatSysNbr";

        private static string LastDate(string col, string heading) =>
            "case when convert(varchar(10), Matter." + col + ", 101) = '01/01/1900' then 'Never' else convert(varchar(10), Matter." + col + ", 101) end as [" + heading + "]";

        // Report headings are plain words; MatterCloseRules.Reasons reads the balance columns by these headings too.
        private static string Columns =>
            "select case when " + MatterCloseRules.Unresolved + " then 'Can''t be closed yet - see the reasons' else 'Ready to close' end as [Result]," +
            @" dbo.jfn_FormatClientCode(Client.CliCode) as [Client Code], Client.CliReportingName as [Client Name],
 dbo.jfn_FormatMatterCode(Matter.MatCode) as [Matter Code], Matter.MatReportingName as [Matter Name], Matter.MatPracticeClass as [Practice Class],
 Employee.EmpInitials as [Billing Timekeeper], MO.OrigTkpr as [Originating Timekeepers], convert(varchar(10), Matter.MatDateOpened, 101) as [Date Opened],
 case when Matter.MatStatusFlag = 'O' then 'Open' when Matter.MatStatusFlag = 'F' then 'Final Bill Sent' else Matter.MatStatusFlag end as [Matter Status], " +
            LastDate("MatDateLastWork", "Last Work Date") + ", " + LastDate("MatDateLastBill", "Last Bill Date") + ", " +
            LastDate("MatDateLastExp", "Last Expense Date") + ", " + LastDate("MatDateLastPaymt", "Last Payment Date") + ", " +
            MatterCloseRules.HeadedColumns("UnbilledTime", "UnbilledExp", "WIP", "UnpostedTime", "UnpostedExp", "UnpostedVouchers", "OpenVouchers",
                "OpenBills", "AR", "PendingCash", "PPD", "Trust", "TrustBanks", "TrustLedger", "TrustLedgerBanks", "UnpostedTrustAdj", "Prebills", "Splits");

        /// <summary>R1 (unresolved = true: the exception report, with a reason column) or R2 (matters to be closed).</summary>
        public DataTable CriteriaReport(CloseCriteria criteria, bool unresolved)
        {
            var hasSuite = MatterCloseRules.HasSuite(_session);
            var p = new List<SqlParameter>();
            var sql = Columns + "\n" + ReportFrom(hasSuite) + "\n" + criteria.Where(p) + "\n  and " + (unresolved ? "" : "not ") +
                      MatterCloseRules.Unresolved + "\norder by Client.CliCode, Matter.MatCode";
            var table = _session.Query(sql, p.ToArray());
            if (unresolved)
            {
                var reasons = table.Columns.Add("Why It Can't Be Closed", typeof(string));
                reasons.SetOrdinal(1);
                foreach (var row in table.Rows())
                    row[reasons] = MatterCloseRules.Reasons(row);
            }
            return table;
        }

        public CriteriaCounts CountCriteria(CloseCriteria criteria)
        {
            var hasSuite = MatterCloseRules.HasSuite(_session);
            var p = new List<SqlParameter>();
            var row = _session.Query(
                "select count(*) as Matching, isnull(sum(case when " + MatterCloseRules.Unresolved + " then 1 else 0 end), 0) as Blocked\n" +
                ReportFrom(hasSuite) + "\n" + criteria.Where(p), p.ToArray()).Rows[0];
            return new CriteriaCounts
            {
                Matching = Convert.ToInt32(row["Matching"], CultureInfo.InvariantCulture),
                Blocked = Convert.ToInt32(row["Blocked"], CultureInfo.InvariantCulture),
            };
        }

        /// <summary>Closes every matching matter that passes the rules at update time. Returns the number closed.</summary>
        public int CloseByCriteria(CloseCriteria criteria) => _session.InTransaction(() =>
        {
            var where = criteria.Where(new List<SqlParameter>());
            SqlParameter[] Parameters()
            {
                var p = new List<SqlParameter>();
                criteria.Where(p);
                return p.ToArray();
            }
            var closed = MatterCloseRules.Close(_session, "select Matter.MatSysNbr " + CandidateFrom + "\n" + where, Parameters);
            _session.WriteUtilityLog("JPS Matter Closer: closed " + closed + " matters by criteria (" + criteria.Describe() + ").");
            return closed;
        });

        // ---------- Close from a spreadsheet ----------

        public const string WillClose = "Will be closed";

        // Spreadsheet check report headings (the view reads the report by these).
        public const string RowColumn = "Row";
        public const string ClientColumn = "Client Code";
        public const string MatterColumn = "Matter Code";
        public const string ResultColumn = "Result";
        public const string ReasonColumn = "Reason";

        /// <summary>Reads, resolves and checks the file. Result column is <see cref="WillClose"/> for closable rows.</summary>
        public SheetCheck CheckSpreadsheet(string path, bool pad)
        {
            var rows = SpreadsheetMatters.Read(path);
            if (rows.Count == 0)
                throw new System.IO.InvalidDataException("No matters were found in the file. Put the Client Code in column A and the Matter Code in column B of the first sheet, one matter per row starting on row 2.");
            SpreadsheetMatters.Resolve(_session, rows, pad);

            var candidates = rows.Where(r => r.IsUsable && r.Status != "C" && r.Status != "P").Select(r => r.MatSysNbr.Value).ToList();
            var checks = MatterCloseRules.Check(_session, MatterCloseRules.HasSuite(_session), candidates);

            var table = new DataTable();
            table.Columns.Add(RowColumn, typeof(int));
            table.Columns.Add(ClientColumn, typeof(string));
            table.Columns.Add(MatterColumn, typeof(string));
            table.Columns.Add("Matter Name", typeof(string));
            table.Columns.Add(ResultColumn, typeof(string));
            table.Columns.Add(ReasonColumn, typeof(string));
            var ready = new List<int>();
            foreach (var r in rows)
            {
                string result, error = "";
                if (r.Problem != null) { result = "Can't be closed"; error = r.Problem; }
                else if (r.DuplicateOfRow != null) { result = "Duplicate"; error = "The same matter is listed on row " + r.DuplicateOfRow + "; it is handled once"; }
                else if (r.Status == "C") { result = "Can't be closed"; error = "The matter is already closed"; }
                else if (r.Status == "P") { result = "Can't be closed"; error = "The matter's status is Ready to Purge, which this tool doesn't change"; }
                else if (!checks.TryGetValue(r.MatSysNbr.Value, out var check)) { result = "Can't be closed"; error = "The matter's balances could not be checked"; }
                else if (!check.Ready) { result = "Can't be closed"; error = check.Reasons; }
                else { result = WillClose; ready.Add(r.MatSysNbr.Value); }
                table.Rows.Add(r.RowNumber, r.ClientCode, r.MatterCode, r.MatterName ?? "", result, error);
            }
            return new SheetCheck(rows, table, ready);
        }

        /// <summary>
        /// Closes the checked-ready matters, re-checking the rules at update time. Returns the matter ids that were
        /// actually closed (anything that gained activity since the check stays open).
        /// </summary>
        public HashSet<int> CloseFromSpreadsheet(IList<int> readyIds, string fileName) => _session.InTransaction(() =>
            MatterIdTable.With(_session, readyIds, () =>
            {
                var closed = MatterCloseRules.Close(_session, "select MatSysNbr from " + MatterIdTable.Name, () => new SqlParameter[0]);
                var stillOpen = new HashSet<int>(_session.Query(
                        "select M.MatSysNbr from " + MatterIdTable.Name + " I inner join Matter M on M.MatSysNbr = I.MatSysNbr where M.MatStatusFlag <> 'C'")
                    .Rows().Select(r => Convert.ToInt32(r["MatSysNbr"], CultureInfo.InvariantCulture)));
                _session.WriteUtilityLog("JPS Matter Closer: closed " + closed + " matters from spreadsheet " + fileName + ".");
                return new HashSet<int>(readyIds.Where(id => !stillOpen.Contains(id)));
            }));
    }

    public sealed class SheetCheck
    {
        public SheetCheck(List<SheetMatterRow> rows, DataTable report, List<int> readyIds)
        {
            Rows = rows;
            Report = report;
            ReadyIds = readyIds;
        }

        public List<SheetMatterRow> Rows { get; }
        public DataTable Report { get; }
        public List<int> ReadyIds { get; }
        public int ProblemCount => Report.Rows().Count(r => (string)r[MatterCloserService.ResultColumn] != MatterCloserService.WillClose);
    }
}
