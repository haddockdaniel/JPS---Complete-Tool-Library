using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Jps.Core.Data;
using Jps.Core.Reports;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.HideInactiveClients
{
    public sealed class HideResult
    {
        public int NodesHidden { get; set; }
        public int ClientsHidden { get; set; }
    }

    /// <summary>Database work for Hide Inactive Clients. Every method must be called through ToolFrame.RunAsync.</summary>
    public sealed class HideInactiveClientsService
    {
        public const string WillHide = "Will be hidden";
        public const string Kept = "Kept: items are filed under its Juris explorer entry";

        // A client's explorer node is a "reference" row (DTDocType 'R', class 4200) directly under the Clients folder (22).
        // Only nodes with nothing filed under them are removed, so FK1_DocumentTree can never fire.
        private const string HideableNode =
            @"d.DTParentID = 22 and d.DTDocClass = 4200 and d.DTDocType = 'R' and d.DTKeyL = c.CliSysNbr
              and not exists (select 1 from DocumentTree ch where ch.DTParentID = d.DTDocID)";

        private const string KeyTable = "#JpsHideInactiveClients";

        private readonly JurisSession _session;

        public HideInactiveClientsService(JurisSession session) => _session = session;

        public List<LookupItem> LoadOriginators() =>
            Lookup.Load(_session,
                @"select distinct EmpSysNbr, EmpID, EmpName from Employee inner join CliOrigAtty on COrigAtty = EmpSysNbr
                  order by EmpID");

        /// <summary>
        /// Clients opened on or before <paramref name="openedOnOrBefore"/> that have matters, none of them open (O) or
        /// final-bill-sent (F), and still show in the explorer's Clients folder. Column CliSysNbr is the key; Result says
        /// whether the node can be removed.
        /// </summary>
        public DataTable Select(DateTime openedOnOrBefore, int? originator)
        {
            var sql =
                @"select c.CliSysNbr, dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name],
                         c.CliDateOpened as [Date Opened],
                         case when exists (select 1 from DocumentTree d where " + HideableNode + @") then @willHide else @kept end as Result
                  from Client c
                  where c.CliDateOpened <= @opened
                    and exists (select 1 from Matter m where m.MatCliNbr = c.CliSysNbr)
                    and not exists (select 1 from Matter m where m.MatCliNbr = c.CliSysNbr and m.MatStatusFlag in ('O','F'))"
                + (originator == null ? "" : " and exists (select 1 from CliOrigAtty o where o.COrigCli = c.CliSysNbr and o.COrigAtty = @orig)")
                + @" and exists (select 1 from DocumentTree d where d.DTParentID = 22 and d.DTDocClass = 4200 and d.DTKeyL = c.CliSysNbr)
                  order by [Client Code]";
            return _session.Query(sql,
                Sql.P("@opened", openedOnOrBefore.Date), Sql.P("@orig", originator),
                Sql.P("@willHide", WillHide), Sql.P("@kept", Kept));
        }

        /// <summary>
        /// Removes the hideable explorer nodes of the given clients in one transaction. (The pre-report the user approved
        /// can be opened in Excel or saved from the report window if they want a record.)
        /// </summary>
        public HideResult Hide(IReadOnlyCollection<int> clientSysNbrs, string logDetail)
        {
            // TempKeyTable runs the work in one transaction and drops the key table afterwards.
            return TempKeyTable.With(_session, KeyTable, "CliSysNbr", clientSysNbrs, () =>
            {
                // Re-check, under lock, that each client is still inactive: someone may have opened a matter (or reopened a
                // closed one) since the pre-report was run. Such clients are silently skipped and keep their node.
                _session.Execute("select m.MatSysNbr from Matter m with (updlock, holdlock) inner join " + KeyTable + " k on k.CliSysNbr = m.MatCliNbr");
                var where = "from DocumentTree d inner join " + KeyTable + @" k on k.CliSysNbr = d.DTKeyL
                             inner join Client c on c.CliSysNbr = k.CliSysNbr where " + HideableNode + @"
                               and exists (select 1 from Matter m where m.MatCliNbr = c.CliSysNbr)
                               and not exists (select 1 from Matter m where m.MatCliNbr = c.CliSysNbr and m.MatStatusFlag in ('O','F'))";
                var clients = Convert.ToInt32(_session.Scalar("select count(distinct d.DTKeyL) " + where));
                var nodes = _session.ExecuteRows("delete d " + where);

                _session.WriteUtilityLog("JPS Hide Inactive Clients: hid " + clients + " client(s) (" + nodes + " explorer node(s)) " + logDetail + ".");
                return new HideResult { NodesHidden = nodes, ClientsHidden = clients };
            });
        }
    }
}
