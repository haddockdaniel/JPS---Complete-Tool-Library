using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using Jps.Core.Data;

namespace Jps.Tools.Tools.TimekeeperReassignment
{
    public enum TkprRole { All, Billing, Originating, Responsible }

    public enum TkprScope { ClientAndMatter, ClientOnly, MatterOnly }

    /// <summary>Everything the user chose, captured on the UI thread before any database work starts.</summary>
    public sealed class ReassignRequest
    {
        public int From { get; set; }
        public string FromId { get; set; }
        public int To { get; set; }
        public string ToId { get; set; }
        public TkprRole Role { get; set; }
        public TkprScope Scope { get; set; }
        public bool AddNoteCards { get; set; }

        private bool Clients => Scope != TkprScope.MatterOnly;
        private bool Matters => Scope != TkprScope.ClientOnly;
        private bool Billing => Role == TkprRole.All || Role == TkprRole.Billing;
        private bool Originating => Role == TkprRole.All || Role == TkprRole.Originating;
        private bool Responsible => Role == TkprRole.All || Role == TkprRole.Responsible;

        public bool ClientOriginating => Clients && Originating;
        public bool MatterOriginating => Matters && Originating;
        public bool ClientBilling => Clients && Billing;
        public bool MatterBilling => Matters && Billing;
        public bool ClientResponsible => Clients && Responsible;
        public bool MatterResponsible => Matters && Responsible;

        /// <summary>The wording the original put in note cards (trailing space included).</summary>
        public string RoleText =>
            Role == TkprRole.All ? "Billing, Originating and Responsible "
            : Role == TkprRole.Billing ? "Billing "
            : Role == TkprRole.Originating ? "Originating " : "Responsible ";

        public string ScopeText =>
            Scope == TkprScope.ClientAndMatter ? "clients and matters" : Scope == TkprScope.ClientOnly ? "clients" : "matters";
    }

    public sealed class StepCount
    {
        public string Label { get; set; }
        public int Count { get; set; }
    }

    public sealed class ReassignResult
    {
        public List<StepCount> Changed { get; } = new List<StepCount>();
        public HashSet<int> Clients { get; } = new HashSet<int>();
        public HashSet<int> Matters { get; } = new HashSet<int>();
        public int NoteCards { get; set; }
        public DataTable PostReport { get; set; }
    }

    /// <summary>All SQL for the timekeeper reassignment. Every value is a parameter; id lists travel as one XML parameter.</summary>
    public sealed class TimekeeperReassignmentService
    {
        private const string NoteBase = "TkprChg";
        private readonly JurisSession _session;

        public TimekeeperReassignmentService(JurisSession session) => _session = session;

        // ---------------------------------------------------------------- lists

        /// <summary>
        /// Everyone currently assigned anywhere as billing, originating or responsible timekeeper (the original missed
        /// responsible-only timekeepers). Includes timekeepers already made invalid, marked "(inactive)", so their
        /// assignments can still be moved away.
        /// </summary>
        public const string FromListSql = @"
select EmpSysNbr, EmpID, EmpName + case when EmpValidAsTkpr = 'Y' then '' else ' (inactive)' end
from Employee
inner join (
      -- one pass over each assignment table, hashed (the OR'd IN-list version took ~65 s on a 285k-matter firm; this, < 1 s)
      select BillToBillingAtty as x from BillTo
      union select MOrigAtty from MatOrigAtty
      union select CliBillingAtty from Client where CliBillingAtty is not null
      union select COrigAtty from CliOrigAtty
      union select MRTEmployeeID from MatterResponsibleTimekeeper
      union select CRTEmployeeID from ClientResponsibleTimekeeper) assigned on assigned.x = EmpSysNbr
order by EmpID
option (hash join, hash union)";

        public const string ToListSql = "select EmpSysNbr, EmpID, EmpName from Employee where EmpValidAsTkpr = 'Y' order by EmpID";

        // ---------------------------------------------------------------- counts

        /// <summary>How many assignments each selected step would change (the original's status-bar counts, corrected).</summary>
        public List<StepCount> Count(ReassignRequest r)
        {
            var list = new List<StepCount>();
            if (r.ClientOriginating) list.Add(Step("client originating timekeeper", "select count(*) from CliOrigAtty where COrigAtty = @From", r.From));
            if (r.MatterOriginating) list.Add(Step("matter originating timekeeper", "select count(*) from MatOrigAtty where MOrigAtty = @From", r.From));
            if (r.ClientBilling) list.Add(Step("client billing timekeeper", "select count(*) from Client where CliBillingAtty = @From", r.From));
            if (r.MatterBilling) list.Add(Step("matter billing timekeeper", "select count(distinct MatSysNbr) from Matter inner join BillTo on MatBillTo = BillToSysNbr where BillToBillingAtty = @From", r.From));
            if (r.MatterResponsible) list.Add(Step("matter responsible timekeeper", "select count(*) from MatterResponsibleTimekeeper where MRTEmployeeID = @From", r.From));
            if (r.ClientResponsible) list.Add(Step("client responsible timekeeper", "select count(*) from ClientResponsibleTimekeeper where CRTEmployeeID = @From", r.From));
            return list;
        }

        private StepCount Step(string label, string sql, int from) =>
            new StepCount { Label = label, Count = Convert.ToInt32(_session.Scalar(sql, Sql.P("@From", from)) ?? 0) };

        public static string Describe(IEnumerable<StepCount> counts) =>
            string.Join(", ", counts.Select(c => c.Count.ToString("N0") + " " + c.Label + " assignment(s)"));

        // ---------------------------------------------------------------- reports

        /// <summary>Pre-report: what's assigned to the From timekeeper now, with the new timekeeper alongside.</summary>
        public DataTable PreReport(ReassignRequest r) =>
            _session.Query(ReportSql(r, post: false), Sql.P("@From", r.From), Sql.P("@ToID", r.ToId));

        /// <summary>Post-report: the clients/matters that were changed, as they are now.</summary>
        public DataTable PostReport(ReassignRequest r, ICollection<int> clients, ICollection<int> matters) =>
            _session.Query(ReportSql(r, post: true),
                Sql.IdList("@Clients", clients), Sql.IdList("@Matters", matters));

        private static string ReportSql(ReassignRequest r, bool post)
        {
            var matterLevel = r.Scope != TkprScope.ClientOnly;
            var clientLevel = r.Scope != TkprScope.MatterOnly;
            var cols = new List<string> { "[Client Code] = dbo.jfn_FormatClientCode(CliCode)", "[Client Name] = CliReportingName" };
            if (matterLevel) cols.AddRange(new[] { "[Matter Code] = dbo.jfn_FormatMatterCode(MatCode)", "[Matter Name] = MatReportingName" });
            var joins = new StringBuilder(matterLevel ? "from Matter inner join Client on MatCliNbr = CliSysNbr\n" : "from Client\n");
            var match = new List<string>();

            void Add(bool on, string column, string alias, string join)
            {
                if (!on) return;
                cols.Add("[" + column + "] = " + alias + ".EmpID");
                joins.AppendLine(join);
                match.Add(alias + ".EmpSysNbr = @From");
            }

            // Left joins throughout, so a client/matter shows even when only one of its levels had the From timekeeper.
            Add(matterLevel && r.MatterOriginating, "Matter Originating Timekeeper", "matOrig",
                "left outer join MatOrigAtty on MOrigMat = MatSysNbr left outer join Employee matOrig on matOrig.EmpSysNbr = MOrigAtty");
            Add(clientLevel && r.ClientOriginating, "Client Originating Timekeeper", "cliOrig",
                "left outer join CliOrigAtty on COrigCli = CliSysNbr left outer join Employee cliOrig on cliOrig.EmpSysNbr = COrigAtty");
            Add(matterLevel && r.MatterBilling, "Matter Billing Timekeeper", "matBill",
                "left outer join BillTo on MatBillTo = BillToSysNbr left outer join Employee matBill on matBill.EmpSysNbr = BillToBillingAtty");
            Add(clientLevel && r.ClientBilling, "Client Billing Timekeeper", "cliBill",
                "left outer join Employee cliBill on cliBill.EmpSysNbr = CliBillingAtty");
            Add(matterLevel && r.MatterResponsible, "Matter Responsible Timekeeper", "matResp",
                "left outer join MatterResponsibleTimekeeper on MRTMatterID = MatSysNbr left outer join Employee matResp on matResp.EmpSysNbr = MRTEmployeeID");
            Add(clientLevel && r.ClientResponsible, "Client Responsible Timekeeper", "cliResp",
                "left outer join ClientResponsibleTimekeeper on CRTClientID = CliSysNbr left outer join Employee cliResp on cliResp.EmpSysNbr = CRTEmployeeID");

            string where;
            if (!post)
            {
                cols.Add("[New Timekeeper] = @ToID");
                where = string.Join(" or ", match);
            }
            else
            {
                var mats = "MatSysNbr in " + Sql.IdSelect("@Matters");
                var clis = "CliSysNbr in " + Sql.IdSelect("@Clients");
                where = r.Scope == TkprScope.MatterOnly ? mats : r.Scope == TkprScope.ClientOnly ? clis : mats + " or " + clis;
            }

            return "select " + string.Join(", ", cols) + "\n" + joins + "where " + where + "\norder by CliCode" + (matterLevel ? ", MatCode" : "");
        }

        // ---------------------------------------------------------------- the change

        /// <summary>
        /// Runs every selected step, the note cards and the UtilityLog line in ONE transaction. Each percentage table is
        /// merged in a single MERGE statement, so a client/matter that had both timekeepers ends with one row carrying the
        /// combined percentage and the total never moves off 100. The totals are re-checked before commit.
        /// </summary>
        public ReassignResult Run(ReassignRequest r)
        {
            if (r.From == r.To) throw new InvalidOperationException("The From and To timekeepers are the same, so nothing was changed.");
            var result = new ReassignResult();

            _session.InTransaction(() =>
            {
                var from = r.From;

                // The To timekeeper must still be a valid timekeeper; lock the row so it can't be made invalid mid-change.
                var toValid = Convert.ToString(_session.Scalar(
                    "select EmpValidAsTkpr from Employee with (updlock, holdlock) where EmpSysNbr = @To", Sql.P("@To", r.To)));
                if (toValid != "Y")
                    throw new InvalidOperationException("Timekeeper " + r.ToId + " is no longer a valid timekeeper, so nothing was changed. Reload the lists and try again.");

                if (r.ClientOriginating)
                    result.Changed.Add(MergePercent("client originating timekeeper", "CliOrigAtty", "COrigCli", "COrigAtty", "COrigPcnt", null, from, r.To, result.Clients));
                if (r.MatterOriginating)
                    result.Changed.Add(MergePercent("matter originating timekeeper", "MatOrigAtty", "MOrigMat", "MOrigAtty", "MOrigPcnt", null, from, r.To, result.Matters));
                if (r.ClientBilling)
                {
                    Collect("select CliSysNbr from Client where CliBillingAtty = @From", from, result.Clients);
                    result.Changed.Add(new StepCount { Label = "client billing timekeeper", Count = _session.ExecuteRows(
                        "update Client set CliBillingAtty = @To where CliBillingAtty = @From", Sql.P("@From", from), Sql.P("@To", r.To)) });
                }
                if (r.MatterBilling)
                {
                    var mats = new HashSet<int>();
                    Collect("select distinct MatSysNbr from BillTo inner join Matter on MatBillTo = BillToSysNbr where BillToBillingAtty = @From", from, mats);
                    result.Matters.UnionWith(mats);
                    _session.Execute("update BillTo set BillToBillingAtty = @To where BillToBillingAtty = @From", Sql.P("@From", from), Sql.P("@To", r.To));
                    result.Changed.Add(new StepCount { Label = "matter billing timekeeper", Count = mats.Count });
                }
                if (r.MatterResponsible)
                    result.Changed.Add(MergePercent("matter responsible timekeeper", "MatterResponsibleTimekeeper", "MRTMatterID", "MRTEmployeeID", "MRTPercent", "MRTID", from, r.To, result.Matters));
                if (r.ClientResponsible)
                    result.Changed.Add(MergePercent("client responsible timekeeper", "ClientResponsibleTimekeeper", "CRTClientID", "CRTEmployeeID", "CRTPercent", "CRTID", from, r.To, result.Clients));

                if (r.AddNoteCards)
                {
                    var text = "Updated Timekeeper: " + r.RoleText.Trim() + " changed from " + r.FromId + " to " + r.ToId + " on "
                               + DateTime.Today.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    result.NoteCards += AddNotes("MatterNote", "MNMatter", "MNNoteIndex", "MNObject", "MNNoteText", "MNNoteObject", result.Matters, text);
                    result.NoteCards += AddNotes("ClientNote", "CNClient", "CNNoteIndex", "CNObject", "CNNoteText", "CNNoteObject", result.Clients, text);
                }

                _session.WriteUtilityLog("Timekeeper Assignment - " + r.FromId + " to " + r.ToId + " (" + r.RoleText.Trim() + ", " + r.ScopeText + ": "
                    + Describe(result.Changed) + (r.AddNoteCards ? ", " + result.NoteCards + " note card(s)" : "") + ")");
            });

            return result;
        }

        private void Collect(string sql, int from, HashSet<int> into)
        {
            foreach (DataRow row in _session.Query(sql, Sql.P("@From", from)).Rows)
                into.Add(Convert.ToInt32(row[0]));
        }

        /// <summary>
        /// Moves the From timekeeper's share to the To timekeeper on one percentage table in a single MERGE: per owner
        /// (client/matter), all From and To rows collapse into one To row holding their summed percentage. Table and
        /// column names are fixed strings from this class, never user input.
        /// </summary>
        private StepCount MergePercent(string label, string table, string owner, string emp, string pct, string idCol,
                                       int from, int to, HashSet<int> affected)
        {
            var p = new Func<SqlParameter[]>(() => new[] { Sql.P("@From", from), Sql.P("@To", to) });
            var owners = new HashSet<int>();
            Collect("select distinct " + owner + " from " + table + " where " + emp + " = @From", from, owners);
            if (owners.Count == 0) return new StepCount { Label = label, Count = 0 };
            affected.UnionWith(owners);

            var totalSql = "select isnull(sum(" + pct + "), 0) from " + table;
            var totalBefore = Convert.ToDecimal(_session.Scalar(totalSql));

            // The row kept per owner: the To row if there is one, else the From row (for tables with an identity key, the
            // lowest id, which also tidies up any duplicate rows for the same timekeeper).
            var keepKey = idCol ?? emp;
            var keepExpr = idCol == null
                ? "case when max(case when " + emp + " = @To then 1 else 0 end) = 1 then @To else @From end"
                : "isnull(min(case when " + emp + " = @To then " + idCol + " end), min(" + idCol + "))";

            var merge =
                "merge " + table + " as t\n" +
                "using (select " + owner + " as Owner, sum(" + pct + ") as Pct, " + keepExpr + " as KeepKey\n" +
                "       from " + table + " where " + emp + " in (@From, @To) group by " + owner + "\n" +
                "       having max(case when " + emp + " = @From then 1 else 0 end) = 1) as s\n" +
                "on t." + owner + " = s.Owner and t." + emp + " in (@From, @To)\n" +
                "when matched and t." + keepKey + " = s.KeepKey then update set t." + emp + " = @To, t." + pct + " = s.Pct\n" +
                "when matched then delete;";
            _session.Execute(merge, p());

            var totalAfter = Convert.ToDecimal(_session.Scalar(totalSql));
            var left = Convert.ToInt32(_session.Scalar("select count(*) from " + table + " where " + emp + " = @From", Sql.P("@From", from)));
            if (totalAfter != totalBefore || left != 0)
                throw new InvalidOperationException("The " + label + " percentages didn't add up after the change, so nothing was saved. Please contact JPS support.");

            return new StepCount { Label = label, Count = owners.Count };
        }

        /// <summary>
        /// One note card per client/matter. The index is "TkprChg" for the first, then "TkprChg" + (highest existing
        /// number + 1), so it can never collide with an existing note (the original used the count, which collided once
        /// any note had been deleted).
        /// </summary>
        private int AddNotes(string table, string ownerCol, string indexCol, string objCol, string textCol, string blobCol,
                             ICollection<int> owners, string text)
        {
            if (owners.Count == 0) return 0;
            var existing = _session.Query(
                "select " + ownerCol + " as Owner, " + indexCol + " as Idx from " + table +
                " where " + indexCol + " like 'TkprChg%' and " + ownerCol + " in " + Sql.IdSelect("@Ids"),
                Sql.IdList("@Ids", owners));

            var highest = new Dictionary<int, int>();
            foreach (DataRow row in existing.Rows)
            {
                var owner = Convert.ToInt32(row["Owner"]);
                var suffix = Sql.Text(row, "Idx").Substring(NoteBase.Length);
                int n;
                if (suffix.Length == 0) n = 0;
                else if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out n)) continue;
                highest[owner] = highest.TryGetValue(owner, out var h) ? Math.Max(h, n) : n;
            }

            var insert = "insert into " + table + " (" + ownerCol + ", " + indexCol + ", " + objCol + ", " + textCol + ", " + blobCol + ") " +
                         "values (@Owner, @Idx, '', @Text, null)";
            var added = 0;
            foreach (var owner in owners.OrderBy(o => o))
            {
                var index = highest.TryGetValue(owner, out var h) ? NoteBase + (h + 1).ToString(CultureInfo.InvariantCulture) : NoteBase;
                if (index.Length > 15) throw new InvalidOperationException("A note card name would be longer than the 15 characters Juris allows, so nothing was saved. Please contact JPS support.");
                added += _session.ExecuteRows(insert, Sql.P("@Owner", owner), Sql.P("@Idx", index),
                    new SqlParameter("@Text", SqlDbType.Text) { Value = text });
            }
            return added;
        }
    }
}
