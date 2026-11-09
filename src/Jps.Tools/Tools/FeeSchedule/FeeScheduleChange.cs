using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Tools.CodeReassignment;
using Jps.Tools.Tools.PracticeClass;

namespace Jps.Tools.Tools.FeeSchedule
{
    public enum MatterStatusFilter
    {
        OpenOnly,
        ClosedOnly,
        All,
    }

    /// <summary>One fee schedule reassignment: filters, report, delete checks, and the delete.</summary>
    public sealed class FeeScheduleChange : ClientMatterReassignment
    {
        public FeeScheduleChange(string from, string to, ReassignScope scope, MatterStatusFilter status,
                                 string practiceClass, string office, int? client) : base(from, to, scope)
        {
            Status = status;
            if (practiceClass != null)
            {
                AddValue("@PC", practiceClass);
                FilterClients("c.CliPracticeClass = @PC");
                FilterMatters("m.MatPracticeClass = @PC");
            }
            if (office != null)
            {
                AddValue("@Ofc", office);
                FilterClients("c.CliOfficeCode = @Ofc");
                FilterMatters("m.MatOfficeCode = @Ofc");
            }
            if (client != null)
            {
                AddValue("@Cli", client.Value);
                FilterClients("c.CliSysNbr = @Cli");
                FilterMatters("c.CliSysNbr = @Cli");
            }
            if (status == MatterStatusFilter.OpenOnly) FilterMatters("m.MatStatusFlag = 'O'");
            if (status == MatterStatusFilter.ClosedOnly) FilterMatters("m.MatStatusFlag = 'C'");
        }

        public MatterStatusFilter Status { get; }

        protected override string ClientColumn => "CliFeeSch";
        protected override string MatterColumn => "MatFeeSch";
        protected override string MatterSource => "Matter m inner join Client c on c.CliSysNbr = m.MatCliNbr";
        protected override string CodeTable => "FeeSchedule";
        protected override string CodeColumn => "FeeSchCode";

        /// <summary>Every fee schedule, inactive ones marked, so a client/matter can be moved off an inactive schedule.</summary>
        public const string FromListSql =
            @"select FeeSchCode, FeeSchCode as Code, FeeSchDesc + case when isnull(FeeSchActive, '') <> 'Y' then ' (inactive)' else '' end
              from FeeSchedule order by FeeSchCode";

        /// <summary>Only active schedules can be the target.</summary>
        public const string ToListSql = "select FeeSchCode, FeeSchCode as Code, FeeSchDesc from FeeSchedule where FeeSchActive = 'Y' order by FeeSchCode";

        public const string PracticeClassesSql = "select PrctClsCode, PrctClsCode as Code, PrctClsDesc from PracticeClass order by PrctClsCode";
        public const string OfficesSql = "select OfcOfficeCode, OfcOfficeCode as Code, OfcDesc from OfficeCode order by OfcOfficeCode";
        public const string ClientsSql =
            "select CliSysNbr, dbo.jfn_FormatClientCode(CliCode) as Code, CliReportingName from Client order by 2";

        public string StatusText => !ChangesMatters ? "" :
            Status == MatterStatusFilter.OpenOnly ? "Only open matters will be changed."
            : Status == MatterStatusFilter.ClosedOnly ? "Only closed matters will be changed."
            : "Both open and closed matters will be changed.";

        /// <summary>The rows that will change (pre-report, and captured inside the transaction for the post-report).</summary>
        public DataTable Report(JurisSession s)
        {
            string sql;
            if (Scope == ReassignScope.ClientsOnly)
                sql = @"select dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name], c.CliPracticeClass as [Practice Class],
                               c.CliOfficeCode as [Office], c.CliFeeSch as [Old Fee Schedule], @To as [New Fee Schedule]
                        from " + ClientSource + " where " + ClientWhere + " order by c.CliCode";
            else if (Scope == ReassignScope.MattersOnly)
                sql = @"select dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], dbo.jfn_FormatMatterCode(m.MatCode) as [Matter Code],
                               m.MatReportingName as [Matter Name], m.MatPracticeClass as [Practice Class], m.MatOfficeCode as [Office],
                               m.MatFeeSch as [Old Fee Schedule], @To as [New Fee Schedule]
                        from " + MatterSource + " where " + MatterWhere + " order by c.CliCode, m.MatCode";
            else
                sql = @"select [Client Code], [Client Name], [Client Practice Class], [Matter Code], [Matter Name], [Matter Practice Class], [Office],
                               [Old Client Fee Schedule], [Old Matter Fee Schedule], [New Fee Schedule], [Type]
                        from (
                          select c.CliCode as SortCli, '' as SortMat,
                                 dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name], c.CliPracticeClass as [Client Practice Class],
                                 '' as [Matter Code], '' as [Matter Name], '' as [Matter Practice Class], c.CliOfficeCode as [Office],
                                 c.CliFeeSch as [Old Client Fee Schedule], '' as [Old Matter Fee Schedule], @To as [New Fee Schedule], 'Client Change' as [Type]
                          from " + ClientSource + " where " + ClientWhere + @"
                          union all
                          select c.CliCode, m.MatCode,
                                 dbo.jfn_FormatClientCode(c.CliCode), c.CliReportingName, '', dbo.jfn_FormatMatterCode(m.MatCode), m.MatReportingName,
                                 m.MatPracticeClass, m.MatOfficeCode, '', m.MatFeeSch, @To, 'Matter Change'
                          from " + MatterSource + " where " + MatterWhere + @"
                        ) r
                        order by [Type], SortCli, SortMat";
            return s.Query(sql, Params());
        }

        /// <summary>The post-report: the captured rows without the "old" fee schedule columns.</summary>
        public static DataTable PostReport(DataTable captured) =>
            WithoutColumns(captured, "Old Fee Schedule", "Old Client Fee Schedule", "Old Matter Fee Schedule");

        /// <summary>
        /// What would stop the old fee schedule being deleted: clients/matters left on it (Client.CliFeeSch, Matter.MatFeeSch)
        /// and it being the firm's standard rate fee schedule (SysParam CfgTransOpts, 8th item). The other referencing tables
        /// (time and rate tables) are dealt with by <see cref="DeleteFrom"/> and listed by <see cref="DeleteImpact"/>.
        /// </summary>
        public List<DeleteBlocker> DeleteBlockers(JurisSession s, bool afterChange)
        {
            var list = ClientMatterBlockers(s, afterChange, "fee schedule");
            if (string.Equals(StandardRateSchedule(s), From, StringComparison.OrdinalIgnoreCase))
                list.Add(new DeleteBlocker("It is the firm's standard rate fee schedule", 0,
                    "Choose a different standard rate fee schedule in Juris system settings first."));
            return list;
        }

        /// <summary>The standard-rate fee schedule from SysParam CfgTransOpts (comma list, 8th item), or null if not set.</summary>
        private static string StandardRateSchedule(JurisSession s)
        {
            var value = Convert.ToString(s.Scalar("select SpTxtValue from SysParam where SpName = 'CfgTransOpts'"));
            if (string.IsNullOrEmpty(value)) return null;
            var items = value.Split(',');
            // Token 7 counting from 0 (e.g. "Y,N,N,N,Y,N,B,STDR,T,..." -> STDR); see JSysParams.cs "StdRateFee".
            return items.Length >= 8 ? items[7].Trim().Trim('"').Trim() : null;
        }

        /// <summary>Lines describing what the delete will also do to other tables (shown before the user confirms).</summary>
        public List<string> DeleteImpact(JurisSession s)
        {
            int N(string sql) => Count(s, sql);
            var lines = new List<string>();
            void Add(int n, string text)
            {
                if (n > 0) lines.Add(n.ToString("N0") + " " + text);
            }
            Add(N("select count(*) from UnbilledTime where UTFeeSched = @From"), "unbilled time entr(ies) changed to " + To);
            Add(N("select count(*) from BilledTime where BTFeeSched = @From"), "billed time entr(ies) changed to " + To + " (history)");
            Add(N("select count(*) from TimeBatchDetail where TBDFeeSched = @From"), "time batch entr(ies) changed to " + To);
            Add(N("select count(*) from TimeEntry where FeeScheduleCode = @From"), "Time Entry screen entr(ies) changed to " + To);
            Add(N("select count(*) from TkprRate where TKRFeeSch = @From"), "timekeeper rate(s) deleted");
            Add(N("select count(*) from PersTypRate where PTRFeeSch = @From"), "personnel type rate(s) deleted");
            Add(N("select count(*) from TaskCodeRate where TCRFeeSch = @From"), "task code rate(s) deleted");
            return lines;
        }

        /// <summary>
        /// Re-codes time to the new schedule, removes the old schedule's rates, then the schedule and its Tables-view entry.
        /// Covers every FK to FeeSchedule. Call inside the transaction after <see cref="DeleteBlockers"/> finds nothing.
        /// </summary>
        public void DeleteFrom(JurisSession s)
        {
            // TimeBatchDetail first, with the linked TimeEntry rows saved and put back: its MyJuris update trigger would
            // otherwise reset billed entries (status 9) to Posted and overwrite pre-bill edits with the batch values.
            TriggerSafeDml.SnapshotEntries(s, EntryMirror.TimeEntry, "d.TBDFeeSched = @From", Sql.P("@From", From));
            s.Execute("update TimeBatchDetail set TBDFeeSched = @To where TBDFeeSched = @From", Params());
            TriggerSafeDml.RestoreEntries(s, EntryMirror.TimeEntry);
            s.Execute("update UnbilledTime set UTFeeSched = @To where UTFeeSched = @From", Params());
            s.Execute("update BilledTime set BTFeeSched = @To where BTFeeSched = @From", Params());
            s.Execute("update TimeEntry set FeeScheduleCode = @To where FeeScheduleCode = @From", Params());
            s.Execute("delete from TkprRate where TKRFeeSch = @From", Params());
            s.Execute("delete from PersTypRate where PTRFeeSch = @From", Params());
            s.Execute("delete from TaskCodeRate where TCRFeeSch = @From", Params());
            // Counted without the FeeSchedule_Log / DeletedRows / JurisChangeLog rows its delete triggers write
            // (Execute includes them, so "!= 1" on it always failed). FeeSchedule_Log keeps its history on purpose.
            if (s.ExecuteRows("delete from FeeSchedule where FeeSchCode = @From", Params()) != 1)
                throw new InvalidOperationException("Fee schedule " + From + " no longer exists (someone may have deleted it already). Nothing was changed.");
            // Its leaf entry (class 2700) under the Fee Schedules folder, found by class rather than a fixed DTDocID.
            s.Execute(@"delete from DocumentTree
                        where DTDocClass = 2700 and DTDocType = 'R' and DTKeyT = @From
                          and DTParentID in (select p.DTDocID from DocumentTree p where p.DTDocClass = 2700 and p.DTDocType = 'F')
                          and not exists (select 1 from DocumentTree c where c.DTParentID = DocumentTree.DTDocID)", Params());
        }
    }
}
