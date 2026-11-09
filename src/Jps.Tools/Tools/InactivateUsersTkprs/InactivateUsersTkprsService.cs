using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.InactivateUsersTkprs
{
    public enum PeopleFilter { UsersOnly, TimekeepersOnly, All }

    /// <summary>One employee with no activity since the cut-off. Kind is "User", "Timekeeper" or "Both".</summary>
    public sealed class Candidate
    {
        public int EmpSysNbr { get; set; }
        public string EmpId { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public DateTime? LastLog { get; set; }
        public string ExcludedReason { get; set; }
    }

    public sealed class SearchResult
    {
        public List<Candidate> Offered { get; } = new List<Candidate>();
        public List<Candidate> Excluded { get; } = new List<Candidate>();
        public List<string> SkippedSources { get; } = new List<string>();
    }

    /// <summary>All SQL for "Hide Inactive Users/Tkprs". Every value is a parameter.</summary>
    public sealed class InactivateUsersTkprsService
    {
        private readonly JurisSession _session;

        public InactivateUsersTkprsService(JurisSession session) => _session = session;

        /// <summary>
        /// Activity sources: anything here on or after the cut-off date (or still unposted) means the person is active.
        /// Each needs the listed table.column pairs; a source whose columns aren't in this Juris version is skipped and
        /// reported rather than failing the whole search. Fixed text only — never user input.
        /// </summary>
        private static readonly (string Label, string[] Columns, string Sql)[] ActivitySources =
        {
            ("Unbilled time", new[] { "UnbilledTime.UTTkpr", "UnbilledTime.UTDate", "UnbilledTime.UTPostDate" },
                "select UTTkpr from UnbilledTime where UTDate >= @Since or UTPostDate >= @Since"),
            ("Billed time", new[] { "BilledTime.BTTkpr", "BilledTime.BTWrkTkpr", "BilledTime.BTDate", "BilledTime.BTPostDate" },
                "select v.t from BilledTime cross apply (values (BTTkpr), (BTWrkTkpr)) v(t) where BTDate >= @Since or BTPostDate >= @Since"),
            ("Time batch entries", new[] { "TimeBatchDetail.TBDTkpr", "TimeBatchDetail.TBDDate", "TimeBatchDetail.TBDPosted" },
                "select TBDTkpr from TimeBatchDetail where TBDDate >= @Since or TBDPosted <> 'Y'"),
            ("Time entries", new[] { "TimeEntry.TimekeeperSysNbr", "TimeEntry.EntryDate", "TimeEntry.PostDate" },
                "select TimekeeperSysNbr from TimeEntry where EntryDate >= @Since or PostDate is null"),
            ("Time entries created", new[] { "TimeEntry.CreatorSysNbr", "TimeEntry.CreationDate" },
                "select CreatorSysNbr from TimeEntry where CreationDate >= @Since"),
            ("Time entries edited", new[] { "TimeEntry.LastModifiedUserSysNbr", "TimeEntry.LastModifiedDate" },
                "select LastModifiedUserSysNbr from TimeEntry where LastModifiedDate >= @Since"),
            ("Expense entries created", new[] { "ExpenseEntry.CreatorSysNbr", "ExpenseEntry.CreationDate" },
                "select CreatorSysNbr from ExpenseEntry where CreationDate >= @Since"),
            ("Expense entries edited", new[] { "ExpenseEntry.LastModifiedUserSysNbr", "ExpenseEntry.LastModifiedDate" },
                "select LastModifiedUserSysNbr from ExpenseEntry where LastModifiedDate >= @Since"),
            ("Time batches", new[] { "TimeBatch.TBEnteredBy", "TimeBatch.TBDateEntered", "TimeBatch.TBLastOpenedDate" },
                "select TBEnteredBy from TimeBatch where TBDateEntered >= @Since or TBLastOpenedDate >= @Since"),
            ("Expense batches", new[] { "ExpenseBatch.EBEnteredBy", "ExpenseBatch.EBDateEntered", "ExpenseBatch.EBLastOpenedDate" },
                "select EBEnteredBy from ExpenseBatch where EBDateEntered >= @Since or EBLastOpenedDate >= @Since"),
            ("Cash receipt batches", new[] { "CashReceiptsBatch.CRBEnteredBy", "CashReceiptsBatch.CRBDateEntered" },
                "select CRBEnteredBy from CashReceiptsBatch where CRBDateEntered >= @Since"),
            ("Credit memo batches", new[] { "CreditMemoBatch.CMBEnteredBy", "CreditMemoBatch.CMBDateEntered", "CreditMemoBatch.CMBLastOpenedDate" },
                "select CMBEnteredBy from CreditMemoBatch where CMBDateEntered >= @Since or CMBLastOpenedDate >= @Since"),
            ("Journal entry batches", new[] { "JEBatch.JEBEnteredBy", "JEBatch.JEBEnteredDate", "JEBatch.JEBLastOpenedDate" },
                "select JEBEnteredBy from JEBatch where JEBEnteredDate >= @Since or JEBLastOpenedDate >= @Since"),
            ("Manual bill batches", new[] { "ManualBillBatch.MBBEnteredBy", "ManualBillBatch.MBBDateEntered", "ManualBillBatch.MBBLastOpenedDate" },
                "select MBBEnteredBy from ManualBillBatch where MBBDateEntered >= @Since or MBBLastOpenedDate >= @Since"),
            ("Voucher batches", new[] { "VoucherBatch.VBUser", "VoucherBatch.VBDate" },
                "select VBUser from VoucherBatch where VBDate >= @Since"),
            ("Check batches", new[] { "CheckBatch.CBUser", "CheckBatch.CBDate" },
                "select CBUser from CheckBatch where CBDate >= @Since"),
            ("Trust adjustment batches", new[] { "TrAdjBatch.TABUser", "TrAdjBatch.TABDate" },
                "select TABUser from TrAdjBatch where TABDate >= @Since"),
        };

        private static string TypeFilter(PeopleFilter filter, string alias)
        {
            switch (filter)
            {
                case PeopleFilter.UsersOnly: return alias + "EmpValidAsUser = 'Y' and " + alias + "EmpValidAsTkpr = 'N'";
                case PeopleFilter.TimekeepersOnly: return alias + "EmpValidAsTkpr = 'Y' and " + alias + "EmpValidAsUser = 'N'";
                default: return "(" + alias + "EmpValidAsTkpr = 'Y' or " + alias + "EmpValidAsUser = 'Y')";
            }
        }

        /// <summary>
        /// Finds everyone of the chosen kind whose last Employee_Log change is before <paramref name="since"/> and who has
        /// no time, expense or batch activity on or after it. Of those, timekeepers still assigned on open matters (or on
        /// clients that have open matters), and people with no Employee_Log history at all, are excluded with a reason.
        /// </summary>
        public SearchResult Search(DateTime since, PeopleFilter filter)
        {
            var result = new SearchResult();

            var existing = new HashSet<string>(_session.Query(
                    "select TABLE_NAME + '.' + COLUMN_NAME as Col from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = 'dbo'")
                .Rows().Select(r => Sql.Text(r, "Col")), StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains("Employee_Log.DateTimeStamp") || !existing.Contains("Employee_Log.EmpSysNbr"))
                throw new InvalidOperationException("This Juris database doesn't keep a change history for employees, so the last sign-in of users can't be checked. Nothing was listed.");

            var sources = new List<string>();
            foreach (var source in ActivitySources)
            {
                if (source.Columns.All(existing.Contains)) sources.Add(source.Sql);
                else result.SkippedSources.Add(source.Label);
            }
            var activity = sources.Count == 0 ? "select cast(null as int) where 1 = 0" : string.Join("\n union ", sources);

            // Collect everyone with activity ONCE into @active, then compare. (Written as "not in (union ...)" SQL Server
            // re-ran the whole union per employee: 2–3 minutes per search on the test database; collected first, ~3 seconds.)
            var sql = @"
set nocount on;
declare @active table (x int primary key);
insert @active (x) select distinct x from (" + activity + @") act(x) where x is not null;
-- Employee_Log can hold millions of rows with no index on EmpSysNbr: read it ONCE (one scan, ~2 s) instead of once per
-- employee (~3 minutes on the test database).
declare @log table (e int primary key, last datetime);
insert @log (e, last) select EmpSysNbr, max(DateTimeStamp) from Employee_Log where EmpSysNbr is not null group by EmpSysNbr option (hash group);
set nocount off;
select e.EmpSysNbr, e.EmpID, e.EmpName, e.EmpValidAsUser, e.EmpValidAsTkpr,
       (select l.last from @log l where l.e = e.EmpSysNbr) as LastLog,
       (select count(distinct m.MatSysNbr) from BillTo b inner join Matter m on m.MatBillTo = b.BillToSysNbr
         where m.MatStatusFlag <> 'C' and b.BillToBillingAtty = e.EmpSysNbr) as OpenBilling,
       (select count(distinct m.MatSysNbr) from MatOrigAtty o inner join Matter m on o.MOrigMat = m.MatSysNbr
         where m.MatStatusFlag <> 'C' and o.MOrigAtty = e.EmpSysNbr) as OpenOriginating,
       (select count(distinct m.MatSysNbr) from MatterResponsibleTimekeeper r inner join Matter m on r.MRTMatterID = m.MatSysNbr
         where m.MatStatusFlag <> 'C' and r.MRTEmployeeID = e.EmpSysNbr) as OpenResponsible,
       (select count(*) from Client c
         where (c.CliBillingAtty = e.EmpSysNbr
                or exists (select 1 from CliOrigAtty co where co.COrigCli = c.CliSysNbr and co.COrigAtty = e.EmpSysNbr)
                or exists (select 1 from ClientResponsibleTimekeeper cr where cr.CRTClientID = c.CliSysNbr and cr.CRTEmployeeID = e.EmpSysNbr))
           and exists (select 1 from Matter m where m.MatCliNbr = c.CliSysNbr and m.MatStatusFlag <> 'C')) as OpenClients
from Employee e
where e.EmpID <> 'SMGR'
  and " + TypeFilter(filter, "e.") + @"
  and not exists (select 1 from @log l where l.e = e.EmpSysNbr and l.last >= @Since)
  and not exists (select 1 from @active a where a.x = e.EmpSysNbr)
order by e.EmpID";

            foreach (var row in _session.Query(sql, Sql.P("@Since", since.Date)).Rows())
            {
                var isUser = Sql.Text(row, "EmpValidAsUser") == "Y";
                var isTkpr = Sql.Text(row, "EmpValidAsTkpr") == "Y";
                var c = new Candidate
                {
                    EmpSysNbr = Convert.ToInt32(row["EmpSysNbr"]),
                    EmpId = Sql.Text(row, "EmpID"),
                    Name = Sql.Text(row, "EmpName"),
                    Kind = isUser && isTkpr ? "Both" : isUser ? "User" : "Timekeeper",
                    LastLog = row["LastLog"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(row["LastLog"]),
                };

                var reasons = new List<string>();
                if (c.LastLog == null)
                    reasons.Add("Juris has no sign-in or change history for them, so their last sign-in can't be confirmed");
                if (isTkpr)
                {
                    var parts = new List<string>();
                    void Part(string column, string what)
                    {
                        var n = Convert.ToInt32(row[column]);
                        if (n > 0) parts.Add(what + " on " + n.ToString("N0") + (column == "OpenClients" ? " client(s) with open matters" : " open matter(s)"));
                    }
                    Part("OpenBilling", "billing timekeeper");
                    Part("OpenOriginating", "originating timekeeper");
                    Part("OpenResponsible", "responsible timekeeper");
                    Part("OpenClients", "assigned");
                    if (parts.Count > 0) reasons.Add("still " + string.Join(", ", parts) + " — reassign them first");
                }

                if (reasons.Count == 0) result.Offered.Add(c);
                else
                {
                    c.ExcludedReason = char.ToUpperInvariant(reasons[0][0]) + string.Join("; ", reasons).Substring(1) + ".";
                    result.Excluded.Add(c);
                }
            }
            return result;
        }

        public static DataTable ListTable(IEnumerable<Candidate> people, bool withReason)
        {
            var table = new DataTable();
            table.Columns.Add("Employee ID");
            table.Columns.Add("Name");
            table.Columns.Add(withReason ? "User or Timekeeper" : "Will Be Inactivated As");
            table.Columns.Add("Last Sign-in or Employee Change", typeof(DateTime));
            if (withReason) table.Columns.Add("Why It Was Left Out");
            foreach (var p in people)
            {
                var row = table.NewRow();
                row[0] = p.EmpId;
                row[1] = p.Name;
                row[2] = KindText(p.Kind);
                row[3] = (object)p.LastLog ?? DBNull.Value;
                if (withReason) row[4] = p.ExcludedReason;
                table.Rows.Add(row);
            }
            return table;
        }

        /// <summary>Open matters (and clients with open matters) the timekeeper @Emp is still assigned to; same rules as the search.</summary>
        private const string OpenAssignmentsSql = @"
select (select count(distinct m.MatSysNbr) from BillTo b inner join Matter m on m.MatBillTo = b.BillToSysNbr
         where m.MatStatusFlag <> 'C' and b.BillToBillingAtty = @Emp)
     + (select count(distinct m.MatSysNbr) from MatOrigAtty o inner join Matter m on o.MOrigMat = m.MatSysNbr
         where m.MatStatusFlag <> 'C' and o.MOrigAtty = @Emp)
     + (select count(distinct m.MatSysNbr) from MatterResponsibleTimekeeper r inner join Matter m on r.MRTMatterID = m.MatSysNbr
         where m.MatStatusFlag <> 'C' and r.MRTEmployeeID = @Emp)
     + (select count(*) from Client c
         where (c.CliBillingAtty = @Emp
                or exists (select 1 from CliOrigAtty co where co.COrigCli = c.CliSysNbr and co.COrigAtty = @Emp)
                or exists (select 1 from ClientResponsibleTimekeeper cr where cr.CRTClientID = c.CliSysNbr and cr.CRTEmployeeID = @Emp))
           and exists (select 1 from Matter m where m.MatCliNbr = c.CliSysNbr and m.MatStatusFlag <> 'C'))";

        public static string KindText(string kind) =>
            kind == "Both" ? "User and timekeeper" : kind == "User" ? "User" : "Timekeeper";

        /// <summary>
        /// Turns off the user and/or timekeeper flag of each chosen employee and removes them from the Juris Tables view
        /// (DocumentTree class 2500 = users, 2600 = timekeepers), all in one transaction with a UtilityLog line.
        /// </summary>
        public int Inactivate(IReadOnlyList<Candidate> people, DateTime since)
        {
            return _session.InTransaction(() =>
            {
                foreach (var p in people)
                {
                    var id = Sql.P("@Emp", p.EmpSysNbr);
                    var user = p.Kind == "User" || p.Kind == "Both";
                    var tkpr = p.Kind == "Timekeeper" || p.Kind == "Both";
                    var set = user && tkpr ? "EmpValidAsUser = 'N', EmpValidAsTkpr = 'N'" : user ? "EmpValidAsUser = 'N'" : "EmpValidAsTkpr = 'N'";
                    if (_session.ExecuteRows("update Employee set " + set + " where EmpSysNbr = @Emp", id) != 1)
                        throw new InvalidOperationException("Employee " + p.EmpId + " couldn't be found any more, so nothing was saved. Search again and retry.");

                    // Re-check inside the transaction (the Employee row is now locked by the update): someone may have made
                    // this timekeeper billing/originating/responsible on an open matter since the search ran.
                    if (tkpr)
                    {
                        var open = Convert.ToInt32(_session.Scalar(OpenAssignmentsSql, Sql.P("@Emp", p.EmpSysNbr)));
                        if (open > 0)
                            throw new InvalidOperationException("Timekeeper " + p.EmpId + " has been assigned to " + open.ToString("N0")
                                + " open matter(s) or client(s) since the search, so nothing was saved. Reassign them first, then search again.");
                    }

                    // Only the employee's own leaf entry in the Tables view (never a folder or a node with items under it).
                    const string deleteNode = "delete from DocumentTree where DTDocClass = @Class and DTKeyL = @Emp and DTDocType = 'R'"
                                              + " and not exists (select 1 from DocumentTree c where c.DTParentID = DocumentTree.DTDocID)";
                    if (user) _session.Execute(deleteNode, Sql.P("@Class", 2500), Sql.P("@Emp", p.EmpSysNbr));
                    if (tkpr) _session.Execute(deleteNode, Sql.P("@Class", 2600), Sql.P("@Emp", p.EmpSysNbr));
                }
                _session.WriteUtilityLog("Inactivate Users and Tkprs with no Activity since " + since.ToShortDateString() + " - "
                    + people.Count + " employee(s) inactivated: " + string.Join(", ", people.Select(p => p.EmpId + " (" + p.Kind + ")")));
                return people.Count;
            });
        }
    }
}
