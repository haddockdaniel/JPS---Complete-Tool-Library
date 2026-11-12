using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Common;
using Jps.Tools.Tools.MatterCloser;

namespace Jps.Tools.Tools.SplitManagement
{
    /// <summary>One line of the split table on screen: who the matter splits to and by how much.</summary>
    public sealed class SplitRow : Observable
    {
        private string _clientCode = "", _clientName = "", _matterCode = "", _matterName = "";
        private decimal _percent;

        public string ClientCode { get => _clientCode; set => Set(ref _clientCode, value ?? ""); }
        public string ClientName { get => _clientName; set => Set(ref _clientName, value ?? ""); }
        public string MatterCode { get => _matterCode; set => Set(ref _matterCode, value ?? ""); }
        public string MatterName { get => _matterName; set => Set(ref _matterName, value ?? ""); }
        public decimal Percent { get => _percent; set => Set(ref _percent, value); }

        /// <summary>
        /// The bill-to a NEW split-to matter copies (the original's "BillSys"): the copied split's own bill-to on a copy,
        /// otherwise 0 = the split-to client's latest bill-to (see <see cref="SplitManagementService.DefaultBillToSql"/>).
        /// </summary>
        public int BillToTemplate { get; set; }
    }

    /// <summary>A matter whose splits will be replaced.</summary>
    public sealed class SplitTarget
    {
        public int MatSysNbr;
        public string MatterCode, MatterName;
    }

    /// <summary>What a run will do, worked out before anything changes. Nothing runs while <see cref="Problems"/> has rows.</summary>
    public sealed class SplitPlan
    {
        public DataTable Preview { get; set; }
        public DataTable Problems { get; set; }
        public int MattersToCreate { get; set; }
        public int Added, Changed, Removed, Unchanged;
        internal List<TargetPlan> Targets { get; } = new List<TargetPlan>();
        internal Dictionary<string, NewMatter> NewMatters { get; } = new Dictionary<string, NewMatter>(StringComparer.OrdinalIgnoreCase);
        public bool Lock { get; set; }
    }

    internal sealed class TargetPlan
    {
        public SplitTarget Target;
        public string Method; // MatSplitMethod to store
        public List<PlannedSplit> Splits = new List<PlannedSplit>();
    }

    internal sealed class PlannedSplit
    {
        public int? ToMatter;        // existing matter
        public NewMatter Create;     // or one this run creates
        public decimal Percent;
        public bool IsSelf;
    }

    /// <summary>A split-to matter that doesn't exist yet: created by copying <see cref="CopyOf"/> into client <see cref="CliSysNbr"/>.</summary>
    internal sealed class NewMatter
    {
        public int CliSysNbr;
        public string ClientCode;
        public string StoredCode;    // MatCode as Juris stores it
        public string ShownCode;     // formatted, as people see it
        public int CopyOf;           // MatSysNbr of the matter being split
        public int BillToTemplate;   // BillTo whose settings the new bill-to copies
        public int MatSysNbr;        // filled in when created
    }

    /// <summary>
    /// All SQL for Split Management. Ported from "JPS - Split Management Utility" (UtilityBaseMain.cs): same screens and
    /// rules (one matter or every open unlocked matter of a client; edit the splits or copy another matter's; "XXXX" stands
    /// for each matter's own code; a split-to matter that doesn't exist is created as a copy of the matter being split;
    /// optional lock of the split-to matters; prebills block the change; report of splits not totalling 100). Fixed: codes
    /// are matched the way Juris stores them (the original compared stored codes with formatted ones, so on zero-filled
    /// firms it never found the existing matters and tried to create duplicates); every value is a parameter; the whole run
    /// is one transaction worked out and shown before it starts; Juris's own split rules are checked; percentages keep
    /// Juris's 4 decimals; a 0% total really removes the splits; created matters don't inherit the parent's balances and
    /// the firm-wide bill copy insert is gone; numbers come from the SysParam counters; a UtilityLog line is written.
    /// </summary>
    public sealed class SplitManagementService
    {
        public const string Token = "XXXX";

        private readonly JurisSession _session;

        public SplitManagementService(JurisSession session)
        {
            _session = session;
        }

        // ---------- lookups ----------

        /// <summary>Clients with at least one open matter that isn't locked against time and expenses.</summary>
        public const string ClientsSql =
            @"select c.CliSysNbr, dbo.jfn_FormatClientCode(c.CliCode) as Code, c.CliReportingName
              from Client c
              where c.CliSysNbr in (select MatCliNbr from Matter where MatStatusFlag = 'O' and MatLockFlag <> '3')
              order by 2";

        public const string MattersSql =
            @"select MatSysNbr, dbo.jfn_FormatMatterCode(MatCode) as Code, MatReportingName
              from Matter where MatCliNbr = @cli and MatStatusFlag = 'O' and MatLockFlag <> '3'
              order by 2";

        /// <summary>Clients with an open matter that has splits (copy source).</summary>
        public const string SourceClientsSql =
            @"select c.CliSysNbr, dbo.jfn_FormatClientCode(c.CliCode) as Code, c.CliReportingName
              from Client c
              where c.CliSysNbr in (select m.MatCliNbr from Matter m inner join SplitBill s on s.SplitFromMat = m.MatSysNbr where m.MatStatusFlag = 'O')
              order by 2";

        public const string SourceMattersSql =
            @"select m.MatSysNbr, dbo.jfn_FormatMatterCode(m.MatCode) as Code, m.MatReportingName
              from Matter m where m.MatCliNbr = @cli and m.MatSysNbr in (select SplitFromMat from SplitBill)
              order by 2";

        /// <summary>
        /// The original's bill-to for a new split-to matter in client @cli: the bill-to of the latest bill copy on the client's
        /// latest billing address, else the client's latest bill-to.
        /// </summary>
        public const string DefaultBillToSql =
            @"select coalesce(
                 (select max(bc.BilCpyBillTo) from BillCopy bc
                  where bc.BilCpyBilAdr = (select max(BilAdrSysNbr) from BillingAddress where BilAdrCliNbr = @cli)),
                 (select max(BillToSysNbr) from BillTo where BillToCliNbr = @cli))";

        // ---------- what's on screen ----------

        /// <summary>One matter's current splits.</summary>
        public List<SplitRow> CurrentSplits(int matSysNbr) =>
            _session.Query(
                    @"select dbo.jfn_FormatClientCode(c.CliCode) as ClientCode, c.CliReportingName, dbo.jfn_FormatMatterCode(m.MatCode) as MatterCode,
                             m.MatReportingName, s.SplitToPcnt
                      from SplitBill s inner join Matter m on m.MatSysNbr = s.SplitToMat inner join Client c on c.CliSysNbr = m.MatCliNbr
                      where s.SplitFromMat = @m
                      order by 1, 3", Sql.P("@m", matSysNbr))
                .Rows().Select(r => new SplitRow
                {
                    ClientCode = Sql.Text(r, "ClientCode"),
                    ClientName = Sql.Text(r, "CliReportingName"),
                    MatterCode = Sql.Text(r, "MatterCode"),
                    MatterName = Sql.Text(r, "MatReportingName"),
                    Percent = Convert.ToDecimal(r["SplitToPcnt"], CultureInfo.InvariantCulture),
                }).ToList();

        /// <summary>
        /// "All matters": the client's open unlocked matters' splits as XXXX rows, one per split-to client and percentage
        /// (the original's grouping): XXXX on the client itself = the matter keeps that share; [client code]XXXX on another
        /// client = the matter coded "client code + matter code" there.
        /// </summary>
        public List<SplitRow> AllMattersTemplate(int cliSysNbr, string clientCode) =>
            _session.Query(
                    @"select ToCli, ToCliName, IsSelf, Pcnt from (
                        select dbo.jfn_FormatClientCode(tc.CliCode) as ToCli, tc.CliReportingName as ToCliName,
                               case when s.SplitFromMat = s.SplitToMat then 1 else 0 end as IsSelf, s.SplitToPcnt as Pcnt
                        from SplitBill s
                        inner join Matter f on f.MatSysNbr = s.SplitFromMat
                        inner join Matter t on t.MatSysNbr = s.SplitToMat
                        inner join Client tc on tc.CliSysNbr = t.MatCliNbr
                        where f.MatCliNbr = @cli and f.MatStatusFlag = 'O' and f.MatLockFlag <> '3') x
                      group by ToCli, ToCliName, IsSelf, Pcnt
                      order by IsSelf desc, ToCli, Pcnt", Sql.P("@cli", cliSysNbr))
                .Rows().Select(r => new SplitRow
                {
                    ClientCode = Convert.ToInt32(r["IsSelf"]) == 1 ? clientCode : Sql.Text(r, "ToCli"),
                    ClientName = Sql.Text(r, "ToCliName"),
                    MatterCode = Convert.ToInt32(r["IsSelf"]) == 1 ? Token : clientCode + Token,
                    MatterName = "",
                    Percent = Convert.ToDecimal(r["Pcnt"], CultureInfo.InvariantCulture),
                }).ToList();

        /// <summary>
        /// The original's copy: the source matter's split to itself becomes the target itself; every other split becomes the
        /// matter coded "target client code + target matter code" (XXXX for all matters) in that split's client, a new one
        /// copying that split's bill-to settings if it doesn't exist.
        /// </summary>
        public List<SplitRow> CopyTemplate(int sourceMatter, string clientCode, string matterCode, string matterName)
        {
            var single = !string.IsNullOrEmpty(matterCode);
            return _session.Query(
                    @"select dbo.jfn_FormatClientCode(c.CliCode) as ClientCode, c.CliReportingName, t.MatBillTo,
                             case when s.SplitFromMat = s.SplitToMat then 1 else 0 end as IsSelf, s.SplitToPcnt
                      from SplitBill s inner join Matter t on t.MatSysNbr = s.SplitToMat inner join Client c on c.CliSysNbr = t.MatCliNbr
                      where s.SplitFromMat = @m
                      order by 4 desc, 1", Sql.P("@m", sourceMatter))
                .Rows().Select(r =>
                {
                    var self = Convert.ToInt32(r["IsSelf"]) == 1;
                    return new SplitRow
                    {
                        ClientCode = self ? clientCode : Sql.Text(r, "ClientCode"),
                        ClientName = self ? "" : Sql.Text(r, "CliReportingName"),
                        MatterCode = self ? (single ? matterCode : Token) : clientCode + (single ? matterCode : Token),
                        MatterName = single ? matterName : "",
                        Percent = Convert.ToDecimal(r["SplitToPcnt"], CultureInfo.InvariantCulture),
                        BillToTemplate = self ? 0 : Convert.ToInt32(r["MatBillTo"]),
                    };
                }).ToList();
        }

        /// <summary>The matters a run changes: the one picked, or every open unlocked matter of the client.</summary>
        public List<SplitTarget> Targets(int cliSysNbr, int? matSysNbr) =>
            _session.Query(
                    @"select MatSysNbr, dbo.jfn_FormatMatterCode(MatCode) as Code, MatReportingName from Matter
                      where MatCliNbr = @cli and MatStatusFlag = 'O' and MatLockFlag <> '3' and (@m is null or MatSysNbr = @m)
                      order by 2", Sql.P("@cli", cliSysNbr), Sql.P("@m", matSysNbr))
                .Rows().Select(r => new SplitTarget
                {
                    MatSysNbr = Convert.ToInt32(r["MatSysNbr"]),
                    MatterCode = Sql.Text(r, "Code"),
                    MatterName = Sql.Text(r, "MatReportingName"),
                }).ToList();

        // ---------- reports ----------

        /// <summary>Every split matter whose percentages don't add up to 100 (the original's "Check for Splits > 100%").</summary>
        public DataTable Not100Report() =>
            _session.Query(
                @"select dbo.jfn_FormatClientCode(c.CliCode) as [Client Code], c.CliReportingName as [Client Name],
                         dbo.jfn_FormatMatterCode(m.MatCode) as [Matter Code], m.MatReportingName as [Matter Name],
                         case m.MatStatusFlag when 'O' then 'Open' when 'C' then 'Closed' when 'F' then 'Final Bill Sent' when 'P' then 'Ready to Purge' else m.MatStatusFlag end as [Status],
                         x.Total as [Split Total %]
                  from (select SplitFromMat, sum(SplitToPcnt) as Total from SplitBill group by SplitFromMat having sum(SplitToPcnt) <> 100) x
                  inner join Matter m on m.MatSysNbr = x.SplitFromMat inner join Client c on c.CliSysNbr = m.MatCliNbr
                  order by 1, 3");

        /// <summary>The splits of the given matters after a run (the original's "review the splits").</summary>
        public DataTable SplitsReport(IEnumerable<int> matters) =>
            TempKeyTable.With(_session, "#JpsSplitMatters", "MatSysNbr", matters, () => _session.Query(
                @"select dbo.jfn_FormatClientCode(fc.CliCode) as [Client Code], fc.CliReportingName as [Client Name],
                         dbo.jfn_FormatMatterCode(f.MatCode) as [Matter Code], f.MatReportingName as [Matter Name],
                         dbo.jfn_FormatClientCode(tc.CliCode) as [Split to Client], tc.CliReportingName as [Split to Client Name],
                         dbo.jfn_FormatMatterCode(t.MatCode) as [Split to Matter], t.MatReportingName as [Split to Matter Name],
                         s.SplitToPcnt as [Split %]
                  from #JpsSplitMatters k
                  inner join SplitBill s on s.SplitFromMat = k.MatSysNbr
                  inner join Matter f on f.MatSysNbr = s.SplitFromMat inner join Client fc on fc.CliSysNbr = f.MatCliNbr
                  inner join Matter t on t.MatSysNbr = s.SplitToMat inner join Client tc on tc.CliSysNbr = t.MatCliNbr
                  order by 1, 3, 5, 7"));

        // ---------- planning ----------

        /// <summary>
        /// Works out every change for <paramref name="targets"/> from the rows on screen, checking what the original and
        /// Juris's Matter screen check. Reads only.
        /// </summary>
        public SplitPlan Plan(int cliSysNbr, string clientCode, IList<SplitTarget> targets, IList<SplitRow> rows, bool lockSplitTo)
        {
            var plan = new SplitPlan { Lock = lockSplitTo, Preview = NewPreviewTable(), Problems = NewProblemTable() };
            var format = CodeFormat.Load(_session);
            var live = rows.Where(r => r.Percent != 0m).ToList();
            var total = rows.Sum(r => r.Percent);

            void Problem(string matter, string splitTo, string why)
            {
                plan.Problems.Rows.Add(clientCode + "/" + matter, splitTo, why);
            }

            if (total != 100m && total != 0m)
                Problem("", "", "The split percentages add up to " + total.ToString("0.####", CultureInfo.InvariantCulture) + "%. They must add up to exactly 100 (or 0 to remove the splits).");
            if (rows.Any(r => r.Percent < 0m || r.Percent > 100m))
                Problem("", "", "A split percentage is below 0 or above 100.");
            if (rows.Any(r => decimal.Round(r.Percent, 4) != r.Percent))
                Problem("", "", "Juris keeps split percentages to 4 decimal places; one has more.");
            foreach (var r in live.Where(r => r.ClientCode.Trim().Length == 0 || r.MatterCode.Trim().Length == 0))
                Problem("", r.ClientCode + "/" + r.MatterCode, "A split needs both a client code and a matter code.");
            if (targets.Count == 0)
                Problem("", "", "This client has no open matter that isn't locked, so there's nothing to change.");
            if (plan.Problems.Rows.Count > 0) return plan;

            var targetIds = targets.Select(t => t.MatSysNbr).ToList();
            TempKeyTable.With(_session, "#JpsSplitTargets", "MatSysNbr", targetIds, () =>
            {
                // Juris won't let a matter's splits change while it has a prebill, nor split a consolidated bill.
                var info = _session.Query(
                    @"select m.MatSysNbr, m.MatSplitMethod, b.BillToUsageFlg,
                             (select count(*) from PreBillMatter p where p.PBMMatter = m.MatSysNbr) as Prebills,
                             (select count(*) from Matter o where o.MatBillTo = m.MatBillTo and o.MatSysNbr <> m.MatSysNbr) as Sharing
                      from #JpsSplitTargets k inner join Matter m on m.MatSysNbr = k.MatSysNbr
                      inner join BillTo b on b.BillToSysNbr = m.MatBillTo")
                    .Rows().ToDictionary(r => Convert.ToInt32(r["MatSysNbr"]));
                var existing = _session.Query(
                    @"select s.SplitFromMat, s.SplitToMat, s.SplitToPcnt, dbo.jfn_FormatClientCode(c.CliCode) as ClientCode,
                             dbo.jfn_FormatMatterCode(m.MatCode) as MatterCode, m.MatReportingName
                      from #JpsSplitTargets k inner join SplitBill s on s.SplitFromMat = k.MatSysNbr
                      inner join Matter m on m.MatSysNbr = s.SplitToMat inner join Client c on c.CliSysNbr = m.MatCliNbr")
                    .Rows().GroupBy(r => Convert.ToInt32(r["SplitFromMat"])).ToDictionary(g => g.Key, g => g.ToList());

                var codes = new CodeLookup(_session, format);
                var defaultBillTo = new Dictionary<int, int>();
                var hasAddress = new Dictionary<int, bool>();

                foreach (var target in targets)
                {
                    if (!info.TryGetValue(target.MatSysNbr, out var t))
                    {
                        Problem(target.MatterCode, "", "The matter no longer exists.");
                        continue;
                    }
                    if (Convert.ToInt32(t["Prebills"]) > 0)
                        Problem(target.MatterCode, "", "The matter has a prebill. Juris doesn't allow splits to change while a prebill exists: post or delete the prebill first.");
                    if (live.Count > 0 && Convert.ToInt32(t["Sharing"]) > 0)
                        Problem(target.MatterCode, "", "The matter shares its bill-to with other matters (a consolidated bill). Juris only allows splits on matters billed on their own.");

                    var method = Sql.Text(t, "MatSplitMethod");
                    var tp = new TargetPlan { Target = target, Method = live.Count == 0 ? "0" : method == "2" ? "2" : "1" };
                    var seen = new HashSet<string>();
                    foreach (var row in live)
                    {
                        var cliCode = row.ClientCode.Trim();
                        var typed = row.MatterCode.Trim();
                        string matCode;
                        if (typed.Equals(Token, StringComparison.OrdinalIgnoreCase))
                            matCode = target.MatterCode;
                        else if (typed.Equals(clientCode + Token, StringComparison.OrdinalIgnoreCase))
                            matCode = clientCode + target.MatterCode;
                        else
                            matCode = typed;
                        var label = cliCode + "/" + matCode;

                        var cli = codes.Client(cliCode);
                        if (cli == null)
                        {
                            Problem(target.MatterCode, label, "No client with this code exists.");
                            continue;
                        }
                        var mat = codes.Matter(cli.Item1, matCode);

                        var split = new PlannedSplit { Percent = row.Percent };
                        string shownCode, name;
                        if (mat != null)
                        {
                            var status = Sql.Text(mat, "MatStatusFlag");
                            shownCode = Sql.Text(mat, "Code");
                            name = Sql.Text(mat, "MatReportingName");
                            if (status == "C" || status == "P")
                            {
                                Problem(target.MatterCode, label, "The split-to matter is " + (status == "C" ? "closed" : "ready to purge") + ". Juris won't split to it.");
                                continue;
                            }
                            split.ToMatter = Convert.ToInt32(mat["MatSysNbr"]);
                            split.IsSelf = split.ToMatter == target.MatSysNbr;
                        }
                        else
                        {
                            var why = format.CheckNewMatterCode(matCode, out var stored, out shownCode);
                            if (why != null)
                            {
                                Problem(target.MatterCode, label, "This client has no such matter, and one can't be created: " + why);
                                continue;
                            }
                            var newKey = cli.Item1 + "|" + stored;
                            if (!plan.NewMatters.TryGetValue(newKey, out var created))
                            {
                                var billTo = row.BillToTemplate;
                                if (billTo <= 0)
                                {
                                    if (!defaultBillTo.TryGetValue(cli.Item1, out billTo))
                                    {
                                        var d = _session.Scalar(DefaultBillToSql, Sql.P("@cli", cli.Item1));
                                        billTo = defaultBillTo[cli.Item1] = d == null || d == DBNull.Value ? 0 : Convert.ToInt32(d);
                                    }
                                }
                                if (billTo <= 0)
                                {
                                    Problem(target.MatterCode, label, "The matter needs to be created, but client " + cli.Item2 + " has no bill-to to copy its billing settings from.");
                                    continue;
                                }
                                if (!hasAddress.TryGetValue(cli.Item1, out var address))
                                    address = hasAddress[cli.Item1] = Convert.ToInt32(_session.Scalar(
                                        "select count(*) from BillingAddress where BilAdrCliNbr = @cli", Sql.P("@cli", cli.Item1))) > 0;
                                if (!address)
                                {
                                    Problem(target.MatterCode, label, "The matter needs to be created, but client " + cli.Item2 + " has no billing address for its bills.");
                                    continue;
                                }
                                created = new NewMatter
                                {
                                    CliSysNbr = cli.Item1, ClientCode = cli.Item2, StoredCode = stored, ShownCode = shownCode,
                                    CopyOf = target.MatSysNbr, BillToTemplate = billTo,
                                };
                                plan.NewMatters[newKey] = created;
                            }
                            split.Create = created;
                            name = target.MatterName;
                        }

                        var identity = split.ToMatter.HasValue ? "m" + split.ToMatter : "n" + cli.Item1 + "|" + split.Create.StoredCode;
                        if (!seen.Add(identity))
                        {
                            Problem(target.MatterCode, label, "The same split-to matter is listed twice for this matter.");
                            continue;
                        }
                        if (split.IsSelf && tp.Method == "2")
                        {
                            Problem(target.MatterCode, label, "The matter splits only the amount over a threshold, and Juris doesn't allow such a matter to split to itself.");
                            continue;
                        }
                        tp.Splits.Add(split);

                        decimal? old = null;
                        if (split.ToMatter.HasValue && existing.TryGetValue(target.MatSysNbr, out var olds))
                        {
                            var o = olds.FirstOrDefault(x => Convert.ToInt32(x["SplitToMat"]) == split.ToMatter.Value);
                            if (o != null) old = Convert.ToDecimal(o["SplitToPcnt"], CultureInfo.InvariantCulture);
                        }
                        var action = split.Create != null ? "Added (new matter created)"
                                     : old == null ? "Added" : old.Value == split.Percent ? "Unchanged" : "Changed";
                        if (action.StartsWith("Added", StringComparison.Ordinal)) plan.Added++;
                        else if (action == "Changed") plan.Changed++;
                        else plan.Unchanged++;
                        plan.Preview.Rows.Add(target.MatterCode, target.MatterName, cli.Item2, shownCode, name,
                            old.HasValue ? (object)old.Value : DBNull.Value, split.Percent, action);
                    }

                    // Current splits not kept are removed.
                    if (existing.TryGetValue(target.MatSysNbr, out var current))
                        foreach (var o in current.Where(o => tp.Splits.All(s => s.ToMatter != Convert.ToInt32(o["SplitToMat"]))))
                        {
                            plan.Removed++;
                            plan.Preview.Rows.Add(target.MatterCode, target.MatterName, Sql.Text(o, "ClientCode"), Sql.Text(o, "MatterCode"),
                                Sql.Text(o, "MatReportingName"), Convert.ToDecimal(o["SplitToPcnt"], CultureInfo.InvariantCulture), 0m, "Removed");
                        }
                    plan.Targets.Add(tp);
                }
                return 0;
            });
            plan.MattersToCreate = plan.NewMatters.Count;
            return plan;
        }

        /// <summary>
        /// Code lookups for one plan, each table read once (a plan over hundreds of matters otherwise ran thousands of
        /// lookups). Codes match the formatted code (zero-filled first for a numeric format) or the stored code.
        /// </summary>
        private sealed class CodeLookup
        {
            private readonly JurisSession _session;
            private readonly CodeFormat _format;
            private Dictionary<string, Tuple<int, string>> _clients;
            private readonly Dictionary<int, Dictionary<string, DataRow>> _matters = new Dictionary<int, Dictionary<string, DataRow>>();

            public CodeLookup(JurisSession session, CodeFormat format)
            {
                _session = session;
                _format = format;
            }

            public Tuple<int, string> Client(string code)
            {
                if (_clients == null)
                {
                    _clients = new Dictionary<string, Tuple<int, string>>(StringComparer.OrdinalIgnoreCase);
                    var raw = new List<Tuple<string, Tuple<int, string>>>();
                    foreach (var r in _session.Query("select CliSysNbr, CliCode, dbo.jfn_FormatClientCode(CliCode) as Code from Client").Rows())
                    {
                        var c = Tuple.Create(Convert.ToInt32(r["CliSysNbr"]), Sql.Text(r, "Code"));
                        if (!_clients.ContainsKey(c.Item2)) _clients[c.Item2] = c;
                        raw.Add(Tuple.Create(Sql.Text(r, "CliCode"), c));
                    }
                    foreach (var r in raw.Where(r => !_clients.ContainsKey(r.Item1))) _clients[r.Item1] = r.Item2;
                }
                return _clients.TryGetValue(_format.PadClient(code), out var hit) || _clients.TryGetValue(code, out hit) ? hit : null;
            }

            public DataRow Matter(int cli, string code)
            {
                if (!_matters.TryGetValue(cli, out var map))
                {
                    map = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
                    var rows = _session.Query(
                        "select MatSysNbr, MatCode, dbo.jfn_FormatMatterCode(MatCode) as Code, MatReportingName, MatStatusFlag from Matter where MatCliNbr = @cli",
                        Sql.P("@cli", cli)).Rows().ToList();
                    foreach (var r in rows.Where(r => !map.ContainsKey(Sql.Text(r, "Code")))) map[Sql.Text(r, "Code")] = r;
                    foreach (var r in rows.Where(r => !map.ContainsKey(Sql.Text(r, "MatCode")))) map[Sql.Text(r, "MatCode")] = r;
                    _matters[cli] = map;
                }
                return map.TryGetValue(_format.PadMatter(code), out var hit) || map.TryGetValue(code, out hit) ? hit : null;
            }
        }

        private static DataTable NewPreviewTable()
        {
            var t = new DataTable("Split changes");
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Matter Name", typeof(string));
            t.Columns.Add("Split to Client", typeof(string));
            t.Columns.Add("Split to Matter", typeof(string));
            t.Columns.Add("Split to Matter Name", typeof(string));
            t.Columns.Add("Current %", typeof(decimal));
            t.Columns.Add("New %", typeof(decimal));
            t.Columns.Add("Change", typeof(string));
            return t;
        }

        private static DataTable NewProblemTable()
        {
            var t = new DataTable("Problems");
            t.Columns.Add("Matter", typeof(string));
            t.Columns.Add("Split To", typeof(string));
            t.Columns.Add("Problem", typeof(string));
            return t;
        }

        // ---------- the change ----------

        /// <summary>Applies a plan with no problems, all in one transaction. Returns the matters whose splits were replaced.</summary>
        public List<int> Apply(SplitPlan plan)
        {
            if (plan.Problems.Rows.Count > 0) throw new InvalidOperationException("The plan has problems, so nothing was changed.");
            var changed = new List<int>();
            _session.InTransaction(() =>
            {
                foreach (var tp in plan.Targets)
                {
                    // Re-checked under lock: still open, unlocked, and no prebill appeared since the review.
                    var still = _session.Query(
                        @"select MatStatusFlag, MatLockFlag, (select count(*) from PreBillMatter where PBMMatter = @m) as Prebills
                          from Matter with (updlock, holdlock) where MatSysNbr = @m", Sql.P("@m", tp.Target.MatSysNbr)).Rows().FirstOrDefault();
                    if (still == null || Sql.Text(still, "MatStatusFlag") != "O" || Sql.Text(still, "MatLockFlag") == "3" || Convert.ToInt32(still["Prebills"]) > 0)
                        throw new InvalidOperationException("Matter " + tp.Target.MatterCode + " changed since the review (closed, locked or given a prebill), so nothing was changed. Review again.");
                }

                foreach (var created in plan.NewMatters.Values)
                    CreateMatter(created, plan.Lock);

                foreach (var tp in plan.Targets)
                {
                    var m = tp.Target.MatSysNbr;
                    _session.Execute("delete from SplitBill where SplitFromMat = @m", Sql.P("@m", m));
                    foreach (var s in tp.Splits)
                        _session.Execute("insert into SplitBill (SplitFromMat, SplitToMat, SplitToPcnt) values (@f, @t, @p)",
                            Sql.P("@f", m), Sql.P("@t", s.ToMatter ?? s.Create.MatSysNbr), new SqlParameter("@p", SqlDbType.Decimal) { Precision = 7, Scale = 4, Value = s.Percent });
                    _session.Execute("update Matter set MatSplitMethod = @method where MatSysNbr = @m", Sql.P("@method", tp.Method), Sql.P("@m", m));
                    if (plan.Lock)
                        foreach (var s in tp.Splits.Where(s => !s.IsSelf && s.ToMatter.HasValue))
                            _session.Execute("update Matter set MatLockFlag = '3' where MatSysNbr = @t and MatLockFlag <> '3'", Sql.P("@t", s.ToMatter.Value));
                    changed.Add(m);
                }

                _session.WriteUtilityLog("JPS Split Management: replaced the splits of " + plan.Targets.Count + " matter(s); "
                                         + plan.Added + " added, " + plan.Changed + " changed, " + plan.Removed + " removed; "
                                         + plan.NewMatters.Count + " split-to matter(s) created" + (plan.Lock ? "; split-to matters locked." : "."));
            });
            return changed;
        }

        /// <summary>Matter columns a new split-to matter does NOT copy from the matter being split (balances and history).</summary>
        private static readonly Dictionary<string, string> NewMatterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "MatSysNbr", "@id" }, { "MatCliNbr", "@cli" }, { "MatBillTo", "@billTo" }, { "MatCode", "@code" },
            { "MatLockFlag", "@lock" }, { "MatSplitMethod", "'0'" }, { "MatSplitThreshold", "0" }, { "MatSplitPriorAmtBld", "0" },
            // Juris's "never" date for a new matter (ADB2InitColData: CVDate(2) = 1900-01-01) and zero balances.
            { "MatDateLastWork", "'19000101'" }, { "MatDateLastExp", "'19000101'" }, { "MatDateLastBill", "'19000101'" },
            { "MatDateLastStmt", "'19000101'" }, { "MatDateLastPaymt", "'19000101'" },
            { "MatLastPaymtAmt", "0" }, { "MatARLastBill", "0" }, { "MatPaySinceLastBill", "0" }, { "MatAdjSinceLastBill", "0" }, { "MatPPDBalance", "0" },
            // Online payment links belong to the parent matter.
            { "MatOnlinePaymentURL", "null" }, { "MatTrustPaymentURL", "null" }, { "MatLawPayLastModified", "null" }, { "MatTrustLawPayLastModified", "null" },
        };

        /// <summary>
        /// The original's CreateCM, done the way Juris saves a new matter (JW4300 WriteDataNew): a new matter-billed bill-to
        /// (nickname = its number) copying the template bill-to's billing timekeeper, formats and responsible timekeeper; bill
        /// copies for the template's addresses with the original's settings (1 copy, formats 0), else the client's latest
        /// address with its latest bill copy's settings (the original's firm-wide insert, limited to this bill-to); the
        /// matter copied from the matter being split except its code, client, bill-to, lock, split and balance/history
        /// columns; its originating timekeepers; and Juris's empty budget line when the matter requires phases. No Juris
        /// Explorer entry: Juris writes none for matters.
        /// </summary>
        private void CreateMatter(NewMatter m, bool lockIt)
        {
            if (Convert.ToInt32(_session.Scalar(
                    "select count(*) from Matter with (updlock, holdlock) where MatCliNbr = @cli and MatCode = @code",
                    Sql.P("@cli", m.CliSysNbr), Sql.P("@code", m.StoredCode))) > 0)
                throw new InvalidOperationException("Matter " + m.ClientCode + "/" + m.ShownCode + " was created by someone else since the review, so nothing was changed. Review again.");

            var billTo = JurisCounters.Next(_session, "LastSysNbrBillTo", "bill-to number", "BillTo.BillToSysNbr");
            var b = Sql.P("@billTo", billTo);
            if (_session.ExecuteRows(
                    @"insert into BillTo (BillToSysNbr, BillToCliNbr, BillToUsageFlg, BillToNickName, BillToBillingAtty, BillToBillFormat, BillToEditFormat, BillToRespAtty)
                      select @billTo, @cli, 'M', cast(@billTo as varchar(30)), BillToBillingAtty, BillToBillFormat, BillToEditFormat, BillToRespAtty
                      from BillTo where BillToSysNbr = @tpl",
                    b, Sql.P("@cli", m.CliSysNbr), Sql.P("@tpl", m.BillToTemplate)) != 1)
                throw new InvalidOperationException("The bill-to to copy for " + m.ClientCode + "/" + m.ShownCode + " no longer exists, so nothing was changed.");

            var copies = _session.ExecuteRows(
                @"insert into BillCopy (BilCpyBillTo, BilCpyBilAdr, BilCpyComment, BilCpyNbrOfCopies, BilCpyPrintFormat, BilCpyEmailFormat, BilCpyExportFormat, BilCpyARFormat)
                  select distinct @billTo, bc.BilCpyBilAdr, '', 1, 0, 0, 0, 0
                  from BillCopy bc inner join BillingAddress a on a.BilAdrSysNbr = bc.BilCpyBilAdr
                  where bc.BilCpyBillTo = @tpl and a.BilAdrCliNbr = @cli",
                Sql.P("@billTo", billTo), Sql.P("@tpl", m.BillToTemplate), Sql.P("@cli", m.CliSysNbr));
            if (copies == 0)
                copies = _session.ExecuteRows(
                    @"insert into BillCopy (BilCpyBillTo, BilCpyBilAdr, BilCpyComment, BilCpyNbrOfCopies, BilCpyPrintFormat, BilCpyEmailFormat, BilCpyExportFormat, BilCpyARFormat)
                      select top 1 @billTo, a.BilAdrSysNbr, bc.BilCpyComment, bc.BilCpyNbrOfCopies, bc.BilCpyPrintFormat, bc.BilCpyEmailFormat, bc.BilCpyExportFormat, bc.BilCpyARFormat
                      from BillingAddress a inner join BillCopy bc on bc.BilCpyBilAdr = a.BilAdrSysNbr
                      where a.BilAdrCliNbr = @cli
                      order by a.BilAdrSysNbr desc, bc.BilCpyBillTo desc",
                    Sql.P("@billTo", billTo), Sql.P("@cli", m.CliSysNbr));
            if (copies == 0)
                // No bill copy on any of the client's addresses: Juris's new-matter default (latest address, 1 copy, print format 1).
                copies = _session.ExecuteRows(
                    @"insert into BillCopy (BilCpyBillTo, BilCpyBilAdr, BilCpyComment, BilCpyNbrOfCopies, BilCpyPrintFormat, BilCpyEmailFormat, BilCpyExportFormat, BilCpyARFormat)
                      select top 1 @billTo, BilAdrSysNbr, '', 1, 1, 0, 0, 0 from BillingAddress where BilAdrCliNbr = @cli order by BilAdrSysNbr desc",
                    Sql.P("@billTo", billTo), Sql.P("@cli", m.CliSysNbr));
            if (copies == 0)
                throw new InvalidOperationException("Client " + m.ClientCode + " has no billing address, so matter " + m.ShownCode + " can't be created. Nothing was changed.");

            m.MatSysNbr = JurisCounters.Next(_session, "LastSysNbrMatter", "matter number", "Matter.MatSysNbr");
            var columns = _session.Query(
                    @"select c.name from sys.columns c inner join sys.types t on t.user_type_id = c.user_type_id
                      where c.object_id = object_id('Matter') and c.is_computed = 0 and t.name <> 'timestamp' order by c.column_id")
                .Rows().Select(r => Sql.Text(r, "name")).ToList();
            var values = columns.Select(c => NewMatterValues.TryGetValue(c, out var v) ? v : "[" + c + "]");
            if (_session.ExecuteRows(
                    "insert into Matter (" + string.Join(", ", columns.Select(c => "[" + c + "]")) + ")\nselect " + string.Join(", ", values) +
                    "\nfrom Matter where MatSysNbr = @parent",
                    Sql.P("@id", m.MatSysNbr), Sql.P("@cli", m.CliSysNbr), Sql.P("@billTo", billTo), Sql.P("@code", m.StoredCode),
                    Sql.P("@lock", lockIt ? "3" : "0"), Sql.P("@parent", m.CopyOf)) != 1)
                throw new InvalidOperationException("The matter to copy for " + m.ClientCode + "/" + m.ShownCode + " no longer exists, so nothing was changed.");

            // MOrigEffectiveDate (newer Juris): Juris stamps a new matter's originators with the time it was saved.
            var effective = HasColumn("MatOrigAtty", "MOrigEffectiveDate");
            _session.Execute(
                @"insert into MatOrigAtty (MOrigMat, MOrigAtty, MOrigPcnt" + (effective ? ", MOrigEffectiveDate" : "") + @")
                  select @id, MOrigAtty, MOrigPcnt" + (effective ? ", getdate()" : "") + @"
                  from MatOrigAtty where MOrigMat = @parent",
                Sql.P("@id", m.MatSysNbr), Sql.P("@parent", m.CopyOf));

            // Juris adds one empty budget line of each kind when a new matter requires phases (JW4300 WriteDateMFBAndMEB).
            _session.Execute(
                @"if exists (select 1 from Matter where MatSysNbr = @id and MatReqPhaseOnTrans = 'Y')
                  begin
                      insert into MatterFeeBudget (MFBMatter, MFBLine, MFBPhase, MFBTaskCode, MFBActivityCode, MFBPrsTyp, MFBTkpr,
                                                   MFBBudHrsWrk, MFBBudFeeWrk, MFBActualHrsWrk, MFBActualFeeWrk, MFBActualHrsBld, MFBActualFeeBld)
                      select MatSysNbr, 1, MatBudgetPhase, null, null, null, null, 0, 0, 0, 0, 0, 0 from Matter where MatSysNbr = @id
                      insert into MatterExpBudget (MEBMatter, MEBLine, MEBPhase, MEBTaskCode, MEBExpCode, MEBBudExp, MEBExpIncur, MEBExpBld)
                      select MatSysNbr, 1, MatBudgetPhase, null, null, 0, 0, 0 from Matter where MatSysNbr = @id
                  end", Sql.P("@id", m.MatSysNbr));
        }

        private bool HasColumn(string table, string column) =>
            Convert.ToInt32(_session.Scalar("select count(*) from sys.columns where object_id = object_id(@t) and name = @c",
                Sql.P("@t", table), Sql.P("@c", column))) > 0;
    }
}
