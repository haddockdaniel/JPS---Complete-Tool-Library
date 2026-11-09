using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.BillFormatChange
{
    public enum FormatScope { ClientsOnly, MattersOnly, ClientsAndMatters }

    /// <summary>Everything the user picked, captured on the UI thread before any database work.</summary>
    public sealed class FormatChangeCriteria
    {
        public FormatScope Scope { get; set; }
        public int? BillingTkpr { get; set; }   // EmpSysNbr
        public int? Originator { get; set; }    // EmpSysNbr
        public string PracticeClass { get; set; }
        public string OldBill { get; set; }
        public string NewBill { get; set; }
        public string OldPre { get; set; }
        public string NewPre { get; set; }

        public bool ChangeBill => OldBill != null;
        public bool ChangePre => OldPre != null;
        public bool IncludesClients => Scope != FormatScope.MattersOnly;
        public bool IncludesMatters => Scope != FormatScope.ClientsOnly;

        public string Describe()
        {
            var parts = new List<string>();
            if (ChangeBill) parts.Add("bill format " + OldBill + " -> " + NewBill);
            if (ChangePre) parts.Add("prebill format " + OldPre + " -> " + NewPre);
            var scope = Scope == FormatScope.ClientsOnly ? "clients only" : Scope == FormatScope.MattersOnly ? "matters only" : "clients and matters";
            return string.Join(", ", parts) + "; " + scope
                   + (string.IsNullOrEmpty(FilterDescription) ? "" : "; filtered by " + FilterDescription);
        }

        /// <summary>Plain-English filter text for the log and report subtitle (e.g. "billing timekeeper JD").</summary>
        public string FilterDescription { get; set; }
    }

    public sealed class FormatChangeResult
    {
        public int Clients { get; set; }
        public int BillTos { get; set; }
    }

    /// <summary>Database work for Bill / PreBill Format Change. Every method must be called through ToolFrame.RunAsync.</summary>
    public sealed class BillFormatChangeService
    {
        public const string SharedBillToReason = "Shares a bill-to with a selected matter";

        /// <summary>Pre-report column saying why a row is listed (the view counts rows by it).</summary>
        public const string IncludedColumn = "Included Because";

        private readonly JurisSession _session;

        public BillFormatChangeService(JurisSession session) => _session = session;

        public List<LookupItem> LoadFormats() =>
            Lookup.Load(_session, "select BLCode from BillLayout where BLCode <> '{--}' order by BLCode");

        public List<LookupItem> LoadTimekeepers() =>
            Lookup.Load(_session, "select EmpSysNbr, EmpID, EmpName from Employee where EmpValidAsTkpr = 'Y' order by EmpInitials, EmpID");

        public List<LookupItem> LoadPracticeClasses() =>
            Lookup.Load(_session, "select PrctClsCode, PrctClsCode as Code, right(PrctClsDesc, 30) from PracticeClass order by PrctClsCode");

        // ---- predicates (fixed SQL text; every value is a parameter) ----

        private static string ClientWhere(FormatChangeCriteria c)
        {
            var w = new List<string>();
            if (c.ChangeBill) w.Add("Client.CliBillFormat = @OldBill");
            if (c.ChangePre) w.Add("Client.CliEditFormat = @OldPre");
            if (c.BillingTkpr != null) w.Add("Client.CliBillingAtty = @BillTkpr");
            if (c.Originator != null) w.Add("exists (select 1 from CliOrigAtty where COrigCli = Client.CliSysNbr and COrigAtty = @OrigTkpr)");
            if (c.PracticeClass != null) w.Add("Client.CliPracticeClass = @PracticeClass");
            return string.Join(" and ", w);
        }

        /// <summary>Applies to "Matter inner join Client on CliSysNbr = MatCliNbr inner join BillTo on BillToSysNbr = MatBillTo".</summary>
        private static string MatterWhere(FormatChangeCriteria c)
        {
            var w = new List<string>();
            if (c.ChangeBill) w.Add("BillTo.BillToBillFormat = @OldBill");
            if (c.ChangePre) w.Add("BillTo.BillToEditFormat = @OldPre");
            if (c.BillingTkpr != null) w.Add("BillTo.BillToBillingAtty = @BillTkpr");
            if (c.Originator != null) w.Add("exists (select 1 from MatOrigAtty where MOrigMat = Matter.MatSysNbr and MOrigAtty = @OrigTkpr)");
            if (c.PracticeClass != null) w.Add("Matter.MatPracticeClass = @PracticeClass");
            return string.Join(" and ", w);
        }

        private const string MatterJoin = "Matter inner join Client on Client.CliSysNbr = Matter.MatCliNbr inner join BillTo on BillTo.BillToSysNbr = Matter.MatBillTo";

        private static SqlParameter[] Params(FormatChangeCriteria c) => new[]
        {
            Sql.P("@OldBill", c.OldBill), Sql.P("@NewBill", c.NewBill),
            Sql.P("@OldPre", c.OldPre), Sql.P("@NewPre", c.NewPre),
            Sql.P("@BillTkpr", c.BillingTkpr), Sql.P("@OrigTkpr", c.Originator),
            Sql.P("@PracticeClass", c.PracticeClass),
            Sql.P("@ChangeBill", c.ChangeBill ? 1 : 0), Sql.P("@ChangePre", c.ChangePre ? 1 : 0),
        };

        private static string FormatColumns(FormatChangeCriteria c, string oldBillExpr, string oldPreExpr)
        {
            var cols = new List<string>();
            if (c.ChangeBill) cols.Add(oldBillExpr + " as [Current Bill Format]");
            if (c.ChangePre) cols.Add(oldPreExpr + " as [Current Prebill Format]");
            if (c.ChangeBill) cols.Add("@NewBill as [New Bill Format]");
            if (c.ChangePre) cols.Add("@NewPre as [New Prebill Format]");
            return string.Join(", ", cols);
        }

        /// <summary>
        /// The pre-report. For matters it lists every matter on each bill-to that will change, including matters the filters
        /// left out: they change too, because the formats live on the shared bill-to.
        /// </summary>
        public DataTable PreReport(FormatChangeCriteria c)
        {
            var clientSql =
                "select dbo.jfn_FormatClientCode(Client.CliCode) as [Client Code], Client.CliReportingName as [Client Name]"
                + (c.Scope == FormatScope.ClientsAndMatters ? ", '' as [Matter Code], '' as [Matter Name], '' as [Bill-To], 'Client' as [" + IncludedColumn + "]" : "")
                + ", " + FormatColumns(c, "Client.CliBillFormat", "Client.CliEditFormat")
                + " from Client where " + ClientWhere(c);

            var matterSql =
                "select dbo.jfn_FormatClientCode(Client.CliCode) as [Client Code]"
                + (c.Scope == FormatScope.ClientsAndMatters ? ", Client.CliReportingName as [Client Name]" : "")
                + ", dbo.jfn_FormatMatterCode(Matter.MatCode) as [Matter Code], Matter.MatReportingName as [Matter Name]"
                + ", BillTo.BillToNickName as [Bill-To]"
                + ", case when Matter.MatSysNbr in (select MatSysNbr from Sel) then 'Selected matter' else '" + SharedBillToReason + "' end as [" + IncludedColumn + "]"
                + ", " + FormatColumns(c, "BillTo.BillToBillFormat", "BillTo.BillToEditFormat")
                + " from " + MatterJoin + " where Matter.MatBillTo in (select MatBillTo from Sel)";

            var selCte = "with Sel as (select Matter.MatSysNbr, Matter.MatBillTo from " + MatterJoin + " where " + MatterWhere(c) + ") ";

            string sql;
            switch (c.Scope)
            {
                case FormatScope.ClientsOnly:
                    sql = clientSql + " order by [Client Code]";
                    break;
                case FormatScope.MattersOnly:
                    sql = selCte + matterSql + " order by [Client Code], [Matter Code]";
                    break;
                default:
                    sql = selCte + clientSql + " union all " + matterSql + " order by [Client Code], [Matter Code]";
                    break;
            }
            return _session.Query(sql, Params(c));
        }

        /// <summary>Set-based update of clients and/or bill-tos in one transaction, plus a UtilityLog line.</summary>
        public FormatChangeResult Apply(FormatChangeCriteria c)
        {
            return _session.InTransaction(() =>
            {
                var result = new FormatChangeResult();
                // Work out the bill-tos first so the client update can't influence the matter selection (it doesn't today,
                // because client formats aren't on BillTo, but this keeps the two selections independent by construction).
                if (c.IncludesMatters)
                {
                    result.BillTos = _session.ExecuteRows(
                        @"update BillTo set
                              BillToBillFormat = case when @ChangeBill = 1 then @NewBill else BillToBillFormat end,
                              BillToEditFormat = case when @ChangePre = 1 then @NewPre else BillToEditFormat end
                          where BillToSysNbr in (select Matter.MatBillTo from " + MatterJoin + " where " + MatterWhere(c) + ")",
                        Params(c));
                }
                if (c.IncludesClients)
                {
                    result.Clients = _session.ExecuteRows(
                        @"update Client set
                              CliBillFormat = case when @ChangeBill = 1 then @NewBill else CliBillFormat end,
                              CliEditFormat = case when @ChangePre = 1 then @NewPre else CliEditFormat end
                          where CliSysNbr in (select Client.CliSysNbr from Client where " + ClientWhere(c) + ")",
                        Params(c));
                }
                _session.WriteUtilityLog("JPS Bill-Prebill Format Change: updated " + result.Clients + " client(s) and "
                                         + result.BillTos + " matter bill-to(s) on " + DateTime.Today.ToString("MM/dd/yyyy",
                                             System.Globalization.CultureInfo.InvariantCulture) + " (" + c.Describe() + ").");
                return result;
            });
        }
    }
}
