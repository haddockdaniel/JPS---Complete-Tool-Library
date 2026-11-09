using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.PracticeClass
{
    /// <summary>One practice class reassignment: what it touches, its report, its delete checks and the delete.</summary>
    public sealed class PracticeClassChange : ClientMatterReassignment
    {
        public PracticeClassChange(string from, string to, ReassignScope scope, int? billingAtty) : base(from, to, scope)
        {
            if (billingAtty == null) return;
            AddValue("@Atty", billingAtty.Value);
            FilterClients("c.CliBillingAtty = @Atty");
            FilterMatters("b.BillToBillingAtty = @Atty");
        }

        protected override string ClientColumn => "CliPracticeClass";
        protected override string MatterColumn => "MatPracticeClass";
        protected override string MatterSource => "Matter m inner join BillTo b on m.MatBillTo = b.BillToSysNbr";
        protected override string CodeTable => "PracticeClass";
        protected override string CodeColumn => "PrctClsCode";

        public const string ClassesSql = "select PrctClsCode, PrctClsCode as Code, PrctClsDesc from PracticeClass order by PrctClsCode";

        // A hash join over one union: the OR'd IN version made SQL Server re-scan BillTo for every timekeeper
        // (600,000 page reads, about 6 s on a 45,000-client firm, every time the tool opened). Now about 40 ms.
        public const string AttorneysSql =
            @"select e.EmpSysNbr, e.EmpInitials, e.EmpName from Employee e
              inner join (select BillToBillingAtty as A from BillTo union select CliBillingAtty from Client) x on x.A = e.EmpSysNbr
              where e.EmpValidAsTkpr = 'Y'
              order by e.EmpInitials
              option (hash join, hash union)";

        /// <summary>
        /// The rows that will change. Used for the pre-report and, re-run inside the transaction just before the update,
        /// for the post-report. Employee is a left join so a client/matter with no billing timekeeper is still listed
        /// (the update changes it too).
        /// </summary>
        public DataTable Report(JurisSession s)
        {
            string sql;
            if (Scope == ReassignScope.ClientsOnly)
                sql = @"select dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name], e.EmpInitials as [Billing Timekeeper],
                               c.CliPracticeClass as [Old Practice Class], @To as [New Practice Class]
                        from " + ClientSource + @" left join Employee e on e.EmpSysNbr = c.CliBillingAtty
                        where " + ClientWhere + " order by c.CliCode";
            else if (Scope == ReassignScope.MattersOnly)
                sql = @"select dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name], dbo.jfn_FormatMatterCode(m.MatCode) as [Matter Code], m.MatReportingName as [Matter Name],
                               m.MatPracticeClass as [Old Practice Class], e.EmpInitials as [Billing Timekeeper], @To as [New Practice Class]
                        from " + MatterSource + @" inner join Client c on m.MatCliNbr = c.CliSysNbr
                        left join Employee e on e.EmpSysNbr = b.BillToBillingAtty
                        where " + MatterWhere + " order by c.CliCode, m.MatCode";
            else
                sql = @"select [Client Code], [Client Name], [Matter Code], [Matter Name], [Matter Billing Timekeeper], [Client Billing Timekeeper],
                               [Old Client Practice Class], [Old Matter Practice Class], [New Practice Class]
                        from (
                          select dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name], '' as [Matter Code], '' as [Matter Name],
                                 '' as [Matter Billing Timekeeper], isnull(ce.EmpInitials, '') as [Client Billing Timekeeper],
                                 c.CliPracticeClass as [Old Client Practice Class], '' as [Old Matter Practice Class], @To as [New Practice Class]
                          from " + ClientSource + @" left join Employee ce on ce.EmpSysNbr = c.CliBillingAtty
                          where " + ClientWhere + @"
                          union all
                          select dbo.jfn_FormatClientCode(c.CliCode), c.CliReportingName, dbo.jfn_FormatMatterCode(m.MatCode), m.MatReportingName,
                                 isnull(me.EmpInitials, ''), '', '', m.MatPracticeClass, @To
                          from " + MatterSource + @" inner join Client c on m.MatCliNbr = c.CliSysNbr
                          left join Employee me on me.EmpSysNbr = b.BillToBillingAtty
                          where " + MatterWhere + @"
                        ) t
                        order by [Client Code], [Matter Code]";
            return s.Query(sql, Params());
        }

        /// <summary>The post-report: the captured rows without the "old" practice class columns.</summary>
        public static DataTable PostReport(DataTable captured) =>
            WithoutColumns(captured, "Old Practice Class", "Old Client Practice Class", "Old Matter Practice Class");

        /// <summary>
        /// Everything that would stop the old practice class being deleted. Covers every column in the schema that stores a
        /// practice class (_Log audit tables excepted): Client, Matter, PractClassGLAcct, ClientFilterPracticeCodes.
        /// DocumentTree (class 3000 leaf) is removed with the code. Before the change (<paramref name="afterChange"/> false)
        /// the client/matter counts are predicted from what this run will move.
        /// </summary>
        public List<DeleteBlocker> DeleteBlockers(JurisSession s, bool afterChange)
        {
            var list = new List<DeleteBlocker>();

            var glTypes = s.Query("select PGAType from PractClassGLAcct where PGAPractClass = @From order by PGAType", Params())
                .Rows().Select(r => Sql.Text(r, "PGAType")).ToList();
            if (glTypes.Count > 0)
                list.Add(new DeleteBlocker("GL accounts set up for this practice class", glTypes.Count,
                    "Remove " + From + " from the practice class GL accounts in Juris, then try again."));

            var filters = s.Query("select distinct ClientFilterID from ClientFilterPracticeCodes where PrctClsCode = @From order by ClientFilterID", Params())
                .Rows().Select(r => Sql.Text(r, "ClientFilterID")).ToList();
            if (filters.Count > 0)
                list.Add(new DeleteBlocker("Saved client filters that include it", filters.Count,
                    "Remove " + From + " from those client filters in Juris, then try again."));

            list.AddRange(ClientMatterBlockers(s, afterChange, "practice class"));
            return list;
        }

        /// <summary>
        /// Deletes the old practice class and its Tables-view entry. Call inside the transaction after the checks pass.
        /// The count excludes the PracticeClass_Log / JurisChangeLog rows the delete triggers write (Execute includes
        /// them, so "!= 1" on it always failed). PracticeClass_Log keeps its history rows on purpose.
        /// </summary>
        public void DeleteFrom(JurisSession s)
        {
            var n = s.ExecuteRows("delete from PracticeClass where PrctClsCode = @From", Params());
            if (n != 1)
                throw new InvalidOperationException("Practice class " + From + " no longer exists (someone may have deleted it already). Nothing was changed.");
            // Its leaf entry (class 3000) under the Practice Classes folder, found by class rather than a fixed DTDocID.
            s.Execute(@"delete from DocumentTree
                        where DTDocClass = 3000 and DTDocType = 'R' and DTKeyT = @From
                          and DTParentID in (select p.DTDocID from DocumentTree p where p.DTDocClass = 3000 and p.DTDocType = 'F')
                          and not exists (select 1 from DocumentTree c where c.DTParentID = DocumentTree.DTDocID)", Params());
        }
    }
}
