using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Jps.Core.Data;
using Jps.Tools.Tools.DeleteZeroBatches;

namespace Jps.Tools.Tools.HideOldBatches
{
    /// <summary>
    /// Works out which batch-tree leaves sit in accounting-period folders that ended before a cut-off date, and removes
    /// the finished ones (plus folders left empty). Only DocumentTree is changed.
    /// </summary>
    public sealed class HideOldBatchesService
    {
        public const string WillHide = "Will be hidden";

        /// <summary>Report column saying what happens to each batch (the view filters on it).</summary>
        public const string ResultColumn = "Result";

        /// <summary>What a run would do. Built by <see cref="BuildPlan"/>, approved by the user, then passed to <see cref="Hide"/>.</summary>
        public sealed class Plan
        {
            public string FolderOption { get; set; }
            public DataTable Report { get; set; }
            public Dictionary<int, List<int>> LeavesByClass { get; } = new Dictionary<int, List<int>>();
            public Dictionary<int, List<int>> PeriodsByClass { get; } = new Dictionary<int, List<int>>();
            public int HideCount => LeavesByClass.Values.Sum(l => l.Count);
            public int KeepCount => Report.Rows.Count - HideCount;
        }

        private static readonly Regex PeriodTitle = new Regex(@"^\s*(\d{4})-(\d{1,2})\s*$");

        private readonly JurisSession _session;

        public HideOldBatchesService(JurisSession session) => _session = session;

        public Plan BuildPlan(IEnumerable<BatchType> types, DateTime cutoff)
        {
            var plan = new Plan { FolderOption = ReadFolderOption() };

            var periodEnds = new Dictionary<Tuple<int, int>, DateTime>();
            foreach (var r in _session.Query("select PrdYear, PrdNbr, PrdEndDate from ActngPeriod where PrdNbr > 0").Rows())
                periodEnds[Tuple.Create(Convert.ToInt32(r["PrdYear"]), Convert.ToInt32(r["PrdNbr"]))] = Convert.ToDateTime(r["PrdEndDate"]);

            var report = new DataTable();
            report.Columns.Add("Batch Type", typeof(string));
            report.Columns.Add("Period", typeof(string));
            report.Columns.Add("Period Ended", typeof(DateTime));
            report.Columns.Add("Batch Number", typeof(int));
            report.Columns.Add("Batch Title", typeof(string));
            report.Columns.Add("Status", typeof(string));
            report.Columns.Add(ResultColumn, typeof(string));
            plan.Report = report;

            foreach (var t in types.OrderBy(x => x.Index))
                foreach (var cls in t.DocClasses)
                {
                    // Period folders whose whole accounting period ended before the cut-off date.
                    var periods = new Dictionary<int, Tuple<string, DateTime>>();
                    foreach (var r in _session.Query("select DTDocID, DTTitle from DocumentTree where DTDocClass = @cls and DTDocType = 'F'",
                                 Sql.P("@cls", cls)).Rows())
                    {
                        var m = PeriodTitle.Match(Sql.Text(r, "DTTitle"));
                        if (!m.Success) continue;
                        var key = Tuple.Create(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                        if (periodEnds.TryGetValue(key, out var end) && end < cutoff)
                            periods[Convert.ToInt32(r["DTDocID"])] = Tuple.Create(Sql.Text(r, "DTTitle"), end);
                    }
                    if (periods.Count == 0) continue;
                    plan.PeriodsByClass[cls] = periods.Keys.ToList();

                    var ids = Sql.IdList("@periods", periods.Keys);
                    var leaves = _session.Query(
                        "select dt.DTDocID, dt.DTKeyL, dt.DTTitle, dt.DTParentID, u.DTParentID as GrandParentID,"
                        + " b." + t.StatusColumn + " as BatchStatus,"
                        + " case when b." + t.NumberColumn + " is null then 'Stays: batch not found in Juris'"
                        + "      when " + t.FinishedCondition("b") + " and " + BatchTree.NoChildren("dt") + " then @hide"
                        + "      when b." + t.StatusColumn + " = 'L' then 'Stays: open in Juris'"
                        + "      else 'Stays: not posted' end as Result"
                        + " from DocumentTree dt"
                        + " left join DocumentTree u on u.DTDocID = dt.DTParentID"
                        + " left join " + t.Table + " b on b." + t.NumberColumn + " = dt.DTKeyL"
                        + " where dt.DTDocClass = @cls and dt.DTDocType = 'R' and "
                        + (plan.FolderOption == "1"
                            ? "dt.DTParentID in " + Sql.IdSelect("@periods")
                            : "u.DTDocType = 'F' and u.DTDocClass = @cls and u.DTParentID in " + Sql.IdSelect("@periods"))
                        + " order by dt.DTKeyL",
                        Sql.P("@cls", cls), Sql.P("@hide", WillHide), ids);

                    var hide = new List<int>();
                    foreach (var r in leaves.Rows())
                    {
                        var periodId = Convert.ToInt32(plan.FolderOption == "1" ? r["DTParentID"] : r["GrandParentID"]);
                        var period = periods[periodId];
                        var result = Sql.Text(r, "Result");
                        if (result == WillHide) hide.Add(Convert.ToInt32(r["DTDocID"]));
                        report.Rows.Add(cls == 7400 ? "Quick Check Batch" : t.ReportLabel, period.Item1, period.Item2,
                            r["DTKeyL"] == DBNull.Value ? (object)DBNull.Value : Convert.ToInt32(r["DTKeyL"]),
                            Sql.Text(r, "DTTitle"), t.StatusText(Sql.Text(r, "BatchStatus")), result);
                    }
                    if (hide.Count > 0) plan.LeavesByClass[cls] = hide;
                }
            return plan;
        }

        /// <summary>
        /// Juris's batch folder layout: '1' = user folder then period folder, '2' = period folder then user folder.
        /// Anything else means there are no period folders to work with, so the tool refuses to run.
        /// </summary>
        private string ReadFolderOption()
        {
            // CfgMiscOpts is a comma list; Juris (JPARAMS) reads the folder option from the 7th field and treats a blank
            // field as 1. A fixed character position breaks when an earlier field isn't exactly one/two characters long
            // (e.g. a firm with fewer than 10 accounting periods).
            var text = _session.Scalar("select SpTxtValue from SysParam where SpName = 'CfgMiscOpts'");
            if (text == null)
                throw new InvalidOperationException("Juris's batch folder setting (in the firm options) is missing, so the period folders can't be found. Nothing was changed.");
            var fields = Convert.ToString(text).Split(',');
            if (fields.Length < 7)
                throw new InvalidOperationException("Juris's batch folder setting (in the firm options) isn't in the expected format ("
                    + Convert.ToString(text) + "). Nothing was changed.");
            var value = fields[6].Trim();
            if (value.Length == 0) value = "1";
            if (value == "1" || value == "2") return value;
            if (string.IsNullOrWhiteSpace(value) || !char.IsDigit(value[0]))
                throw new InvalidOperationException("Juris's batch folder setting (in the firm options) is blank or isn't in the expected format ("
                    + (value ?? "blank") + "). Nothing was changed.");
            throw new InvalidOperationException("Juris is set to file batches without period folders (batch folder setting " + value
                + "), so there are no period folders to hide by date. Nothing was changed.");
        }

        /// <summary>
        /// Removes the approved leaves (re-checking each batch is still finished), then any period and user folders of those
        /// periods that are now empty. One transaction; writes a UtilityLog line. Returns (leaves, folders) removed.
        /// </summary>
        public Tuple<int, int> Hide(Plan plan, IEnumerable<BatchType> types, DateTime cutoff)
        {
            return _session.InTransaction(() =>
            {
                int leaves = 0, folders = 0;
                foreach (var pair in plan.LeavesByClass)
                {
                    var t = BatchType.ForDocClass(pair.Key);
                    leaves += _session.ExecuteRows(
                        "delete dt from DocumentTree dt"
                        + " where dt.DTDocClass = @cls and dt.DTDocType = 'R' and " + BatchTree.NoChildren("dt")
                        + " and dt.DTDocID in " + Sql.IdSelect("@ids")
                        + " and exists (select 1 from " + t.Table + " b with (updlock, holdlock) where b." + t.NumberColumn + " = dt.DTKeyL and " + t.FinishedCondition("b") + ")",
                        Sql.P("@cls", pair.Key), Sql.IdList("@ids", pair.Value));
                }

                foreach (var pair in plan.LeavesByClass)
                {
                    var periods = plan.PeriodsByClass[pair.Key];
                    // Layout '1' (user folder → period folder): remember each period folder's user folder, so it can go too
                    // once its last period folder is removed (the confirmation promises empty user folders are removed).
                    var userFolders = plan.FolderOption == "1"
                        ? _session.Query("select distinct DTParentID from DocumentTree where DTDocID in " + Sql.IdSelect("@ids") + " and DTParentID is not null",
                                Sql.IdList("@ids", periods)).Rows().Select(r => Convert.ToInt32(r[0])).ToList()
                        : new List<int>();
                    if (plan.FolderOption == "2")
                        folders += _session.ExecuteRows(
                            "delete u from DocumentTree u where u.DTDocClass = @cls and u.DTDocType = 'F' and " + BatchTree.NoChildren("u")
                            + " and u.DTParentID in " + Sql.IdSelect("@ids"),
                            Sql.P("@cls", pair.Key), Sql.IdList("@ids", periods));
                    folders += _session.ExecuteRows(
                        "delete p from DocumentTree p where p.DTDocClass = @cls and p.DTDocType = 'F' and " + BatchTree.NoChildren("p")
                        + " and p.DTDocID in " + Sql.IdSelect("@ids"),
                        Sql.P("@cls", pair.Key), Sql.IdList("@ids", periods));
                    if (userFolders.Count > 0)
                        // Only real user folders: same class AND their parent is the class's own folder (never the class root).
                        folders += _session.ExecuteRows(
                            "delete u from DocumentTree u where u.DTDocClass = @cls and u.DTDocType = 'F' and " + BatchTree.NoChildren("u")
                            + " and u.DTDocID in " + Sql.IdSelect("@ids")
                            + " and exists (select 1 from DocumentTree root where root.DTDocID = u.DTParentID and root.DTDocClass = @cls)",
                            Sql.P("@cls", pair.Key), Sql.IdList("@ids", userFolders));
                }

                if (leaves + folders > 0)
                    _session.WriteUtilityLog("JPS Hide Old Batches by Date: periods ended before " + cutoff.ToString("MM/dd/yyyy")
                        + ", types " + string.Join(", ", types.Select(x => x.Label)) + "; hid " + leaves + " batch(es) and " + folders + " empty folder(s).");
                return Tuple.Create(leaves, folders);
            });
        }
    }
}
