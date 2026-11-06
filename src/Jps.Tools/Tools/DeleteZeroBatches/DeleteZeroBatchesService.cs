using System.Collections.Generic;
using System.Data;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.DeleteZeroBatches
{
    /// <summary>
    /// Finds empty batches and removes their leaves from the Juris batch tree. Batch header and detail rows are never
    /// touched; only DocumentTree 'R' leaves with no children are deleted.
    /// </summary>
    public sealed class DeleteZeroBatchesService
    {
        private readonly JurisSession _session;

        public DeleteZeroBatchesService(JurisSession session) => _session = session;

        /// <summary>
        /// An empty batch: record count 0, no detail rows (guards a stale count), nothing else in Juris refers to it
        /// (posted history, allocations, import errors, links from other batches such as A/R post batches, a user's
        /// "last batch"), not open in Juris right now, and — unless already posted or deleted — not created today, so a
        /// batch someone has just made and may reopen this session is never pulled out from under them.
        /// </summary>
        private static string Empty(BatchType t, string b)
        {
            return b + "." + t.CountColumn + " = 0"
                + " and " + b + "." + t.StatusColumn + " <> 'L'"
                + " and (" + b + "." + t.StatusColumn + " in ('P','D') or " + b + "." + t.DateColumn + " < cast(getdate() as date))"
                + " and not exists (select 1 from " + t.DetailTable + " d where d." + t.DetailColumn + " = " + b + "." + t.NumberColumn + ")"
                + t.NotReferenced(b);
        }

        /// <summary>
        /// Several of the reference columns in <see cref="BatchType.OtherReferences"/> have no index (BilledTime.BTBatch,
        /// JournalEntry.JEBatchNbr, TimeDetailDist.TDDJEBatch, ... up to 4 million rows). Without this hint SQL Server picks
        /// nested loops and rescans those tables per batch (measured: over 2 minutes per type on the dev DB); with it each
        /// table is scanned once (under 1.5 s per type).
        /// </summary>
        private const string HashJoins = " option (hash join)";

        /// <summary>The batch still has a leaf in the tree that can be removed.</summary>
        private static string HasLeaf(BatchType t, string b) =>
            "exists (select 1 from DocumentTree dt where dt.DTDocClass in (" + t.DocClassList + ") and dt.DTDocType = 'R'"
            + " and dt.DTKeyL = " + b + "." + t.NumberColumn + " and " + BatchTree.NoChildren("dt") + ")";

        /// <summary>Report column names the view reads back to know which batches were approved.</summary>
        public const string BatchNumberColumn = "Batch Number";
        public const string BatchTypeColumn = "Batch Type";

        /// <summary>The pre-report: one row per batch Run would hide, in type order.</summary>
        public DataTable Preview(IEnumerable<BatchType> types)
        {
            DataTable result = null;
            foreach (var t in types.OrderBy(x => x.Index))
            {
                var table = _session.Query(
                    "select b." + t.NumberColumn + " as [" + BatchNumberColumn + "], b." + t.CommentColumn + " as [Comment], " + t.StatusSql("b") + " as [Status],"
                    + " b." + t.DateColumn + " as [Date Entered], @label as [" + BatchTypeColumn + "]"
                    + " from " + t.Table + " b where " + Empty(t, "b") + " and " + HasLeaf(t, "b")
                    + " order by b." + t.NumberColumn + HashJoins,
                    Sql.P("@label", t.ReportLabel));
                if (result == null) result = table;
                else result.Merge(table);
            }
            return result ?? new DataTable();
        }

        /// <summary>
        /// Removes the tree leaves of the approved batches, re-checking that each is still empty. Runs in one transaction
        /// and writes a UtilityLog line. Returns the number of tree leaves removed.
        /// </summary>
        public int Hide(IReadOnlyDictionary<BatchType, List<int>> approved)
        {
            return _session.InTransaction(() =>
            {
                var total = 0;
                var done = new List<string>();
                foreach (var pair in approved.OrderBy(p => p.Key.Index))
                {
                    var t = pair.Key;
                    if (pair.Value.Count == 0) continue;
                    // Lock the approved batch headers first so Juris can't open one (status L) or add to it between the
                    // re-check below and the delete; the delete then re-tests "empty" under that lock.
                    // The ids go into a table variable first: XML .nodes() needs a nested-loop apply, which the hash-join
                    // hint forbids. The lock select assigns to a variable so the batch returns no result set; ExecuteRows then returns
                    // the delete's own @@ROWCOUNT (not the rows its DocumentTree triggers write).
                    var n = _session.ExecuteRows(
                        "set nocount on;"
                        + " declare @k table (n int primary key);"
                        + " insert @k (n) select distinct ids.Id from " + Sql.IdSelect("@ids") + " ids;"
                        + " declare @locked int;"
                        + " select @locked = b." + t.NumberColumn + " from " + t.Table + " b with (updlock, holdlock) where b." + t.NumberColumn + " in (select n from @k);"
                        + " set nocount off;"
                        + " delete dt from DocumentTree dt"
                        + " where dt.DTDocClass in (" + t.DocClassList + ") and dt.DTDocType = 'R' and " + BatchTree.NoChildren("dt")
                        + " and dt.DTKeyL in (select b." + t.NumberColumn + " from " + t.Table + " b where " + Empty(t, "b")
                        + " and b." + t.NumberColumn + " in (select n from @k))" + HashJoins + ";",
                        Sql.IdList("@ids", pair.Value));
                    if (n > 0) done.Add(t.Label + " " + n);
                    total += n;
                }
                if (total > 0)
                    _session.WriteUtilityLog("JPS Delete Zero Batches: hid " + total + " empty batch(es) from the batch tree (" + string.Join(", ", done) + ").");
                return total;
            });
        }
    }
}
