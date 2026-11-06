using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.DeleteZeroBatches
{
    /// <summary>
    /// One kind of Juris batch as it appears in the batch tree (DocumentTree). Shared by "Delete Zero Batches" and
    /// "Hide Old Batches by Date". All names below are fixed Juris table/column names and class numbers from this file,
    /// never user input, so they are safe to place in SQL text.
    /// </summary>
    public sealed class BatchType
    {
        private BatchType(int index, string label, string table, string prefix, string nbrCol, string dateCol,
            string detailTable, string detailCol, bool postsAsSaved, params int[] docClasses)
        {
            Index = index;
            Label = label;
            Table = table;
            NumberColumn = nbrCol;
            CountColumn = prefix + "RecCount";
            CommentColumn = prefix + "Comment";
            StatusColumn = prefix + "Status";
            DateColumn = dateCol;
            DetailTable = detailTable;
            DetailColumn = detailCol;
            PostsAsSaved = postsAsSaved;
            DocClasses = docClasses;
        }

        public int Index { get; }
        public string Label { get; }
        public string ReportLabel => Label + " Batch";
        public string Table { get; }
        public string NumberColumn { get; }
        public string CountColumn { get; }
        public string CommentColumn { get; }
        public string StatusColumn { get; }
        public string DateColumn { get; }
        public string DetailTable { get; }
        public string DetailColumn { get; }

        /// <summary>
        /// Credit memo and manual bill batches have no "posted" status: each record posts as it is saved, and the batch
        /// only ever shows U (unlocked) or L (open in Juris right now).
        /// </summary>
        public bool PostsAsSaved { get; }

        /// <summary>DocumentTree.DTDocClass values whose 'R' leaves point at this table (DTKeyL = batch number).</summary>
        public IReadOnlyList<int> DocClasses { get; }

        public string DocClassList => string.Join(",", DocClasses);

        /// <summary>
        /// SQL condition, on batch alias <paramref name="b"/>, for "this batch has finished and can leave the tree":
        /// posted or deleted, or for credit memo / manual bill batches, simply not open in Juris right now.
        /// </summary>
        public string FinishedCondition(string b) => PostsAsSaved
            ? b + "." + StatusColumn + " <> 'L'"
            : b + "." + StatusColumn + " in ('P','D')";

        /// <summary>The batch status letter in words, as a user sees it in Juris.</summary>
        public string StatusText(string code)
        {
            switch ((code ?? "").Trim().ToUpperInvariant())
            {
                case "P": return "Posted";
                case "D": return "Deleted";
                case "L": return "Open in Juris now";
                case "U": return PostsAsSaved ? "Saved (not open)" : "Unposted";
                case "": return "";
                default: return code.Trim();
            }
        }

        /// <summary>SQL expression, on batch alias <paramref name="b"/>, giving the batch status in words (see <see cref="StatusText"/>).</summary>
        public string StatusSql(string b) =>
            "case " + b + "." + StatusColumn + " when 'P' then 'Posted' when 'D' then 'Deleted' when 'L' then 'Open in Juris now' when 'U' then '"
            + (PostsAsSaved ? "Saved (not open)" : "Unposted") + "' else " + b + "." + StatusColumn + " end";

        public LookupItem ToLookupItem() => new LookupItem(Index, Label, "") { IsChecked = true };

        /// <summary>
        /// Every other "Table.Column" in Juris that holds this type's batch number (found in the live schema: FKs and
        /// naming convention). A batch is only "empty" when none of them has a row for it — the header's record count
        /// and the main detail table alone miss posted history (e.g. BilledTime, JournalEntry, CBCheck for quick-check
        /// voucher batches) and links from other batches (xxJEBatchNbr, VBExpBatchNbr).
        /// </summary>
        public IReadOnlyList<string> OtherReferences => References[Index];

        private static readonly string[][] References =
        {
            /* Cash Receipt */ new[] { "CRARAlloc.CRABatch", "CRExpAlloc.CREBatch", "CRFeeAlloc.CRFBatch", "CRNonCliAlloc.CRNBatch", "CRPPDAlloc.CRPBatch",
                                       "CRTrustAlloc.CRTBatch", "CashReceiptImportError.CRIEBatchNbr" },
            /* Check */        new[] { "CBCheckDetail.CBCDBatch" },
            /* Credit Memo */  new[] { "CMExpAlloc.CMEBatch", "CMFeeAlloc.CMFBatch" },
            /* Expense */      new[] { "UnbilledExpense.UEBatch", "BilledExpenses.BEBatch", "ExpDetailDist.EDDBatch", "ExpenseBatchImportError.EBIEBatchNbr",
                                       "PreBillExpDetailItem.PBEDUEBatch", "PreBillExpSumDetail.PBESDUEBatch", "VoucherBatch.VBExpBatchNbr",
                                       "VoucherBatchMatDist.VBMExpBatch", "VoucherMatDist.VMExpBatch" },
            /* Journal Entry */new[] { "JournalEntry.JEBatchNbr", "JEBatchImportError.JEBIEBatchNbr", "ARPostBatch.ARPJEBatchNbr",
                                       "TimeBatch.TBJEBatchNbr", "ExpenseBatch.EBJEBatchNbr", "CashReceiptsBatch.CRBJEBatchNbr", "CheckBatch.CBJEBatchNbr",
                                       "CreditMemoBatch.CMBJEBatchNbr", "ManualBillBatch.MBBJEBatchNbr", "TrAdjBatch.TABJEBatchNbr", "VoucherBatch.VBJEBatchNbr",
                                       "TimeDetailDist.TDDJEBatch", "ExpDetailDist.EDDJEBatch", "VoucherBatchGLDist.VBGJEBatch", "VoucherGLDist.VGLJEBatch" },
            /* Manual Bill */  new string[0],
            /* Time */         new[] { "UnbilledTime.UTBatch", "BilledTime.BTBatch", "TimeDetailDist.TDDBatch", "TimeBatchImportError.TBIEBatchNbr",
                                       "TkprDiary.TDBatch", "PreBillFeeItem.PBFUTBatch" },
            /* Trust Adj. */   new string[0],
            /* Voucher */      new[] { "VoucherBatchGLDist.VBGBatch", "VoucherBatchMatDist.VBMBatch", "VoucherBatchMatDistAttachment.VBMDBatch", "CBCheck.CBCVchBatchNbr" },
        };

        /// <summary>
        /// SQL condition, on batch alias <paramref name="b"/>: nothing in <see cref="OtherReferences"/> points at the batch,
        /// and no Juris user has it saved as the batch Juris reopens for them (EmpParam: "Open Last Batch" LASTB= for journal
        /// entries and cash receipts; JW7200/LastVchBatch, which bill unposting reuses the same day, for vouchers). Juris
        /// would open such a batch with no tree node, letting entries go into a batch nobody can see.
        /// </summary>
        public string NotReferenced(string b)
        {
            var key = b + "." + NumberColumn;
            var sql = string.Concat(OtherReferences.Select(r =>
            {
                var parts = r.Split('.');
                return " and not exists (select 1 from " + parts[0] + " x where x." + parts[1] + " = " + key + ")";
            }));
            // The batch number is cut out of the EmpParam text so the test is an equality (callers run with a hash-join hint,
            // which a LIKE join can't use). Values look like "...;CUSTOM=,LASTB=76774,;..." and "8938:08/10/2010".
            if (Table == "JEBatch" || Table == "CashReceiptsBatch")
                sql += " and not exists (select 1 from (select substring(v, p + 6, charindex(',', v + ',', p + 6) - p - 6) as n"
                       + " from (select cast(ep.EPValue as varchar(max)) as v, charindex('LASTB=', cast(ep.EPValue as varchar(max))) as p from EmpParam ep"
                       + " where ep.EPKey = '" + (Table == "JEBatch" ? "JW4700" : "JW5300") + "' and ep.EPSubKey = 'WinPos') lb where lb.p > 0) lbn"
                       + " where lbn.n = cast(" + key + " as varchar(12)))";
            if (Table == "VoucherBatch")
                sql += " and not exists (select 1 from (select left(v, charindex(':', v + ':') - 1) as n"
                       + " from (select cast(ep.EPValue as varchar(max)) as v from EmpParam ep where ep.EPKey = 'JW7200' and ep.EPSubKey = 'LastVchBatch') lb) lbn"
                       + " where lbn.n = cast(" + key + " as varchar(12)))";
            return sql;
        }

        /// <summary>The nine types, in the order the original tools listed them.</summary>
        public static readonly IReadOnlyList<BatchType> All = new[]
        {
            new BatchType(0, "Cash Receipt", "CashReceiptsBatch", "CRB", "CRBBatchNbr", "CRBDateEntered", "CashReceipt", "CRBatch", false, 5300),
            new BatchType(1, "Check", "CheckBatch", "CB", "CBBatchNbr", "CBDate", "CBCheck", "CBCBatch", false, 7300, 7400),
            new BatchType(2, "Credit Memo", "CreditMemoBatch", "CMB", "CMBBatchNbr", "CMBDateEntered", "CreditMemo", "CMBatchNbr", true, 5200),
            new BatchType(3, "Expense", "ExpenseBatch", "EB", "EBBatchNbr", "EBDateEntered", "ExpBatchDetail", "EBDBatch", false, 5000),
            new BatchType(4, "Journal Entry", "JEBatch", "JEB", "JEBBatchNbr", "JEBEnteredDate", "JEBatchDetail", "JEBDBatchNbr", false, 4700),
            new BatchType(5, "Manual Bill", "ManualBillBatch", "MBB", "MBBBatchNbr", "MBBDateEntered", "ManualBill", "MBBatch", true, 5100),
            new BatchType(6, "Time", "TimeBatch", "TB", "TBBatchNbr", "TBDateEntered", "TimeBatchDetail", "TBDBatch", false, 4900),
            new BatchType(7, "Trust Adjustment", "TrAdjBatch", "TAB", "TABBatchNbr", "TABDate", "TrAdjBatchDetail", "TABDBatch", false, 7500),
            new BatchType(8, "Voucher", "VoucherBatch", "VB", "VBBatchNbr", "VBDate", "VoucherBatchDetail", "VBDBatch", false, 7200),
        };

        public static List<LookupItem> LookupItems() => All.Select(t => t.ToLookupItem()).ToList();

        public static BatchType FromKey(object key) => All[(int)key];

        public static BatchType ForDocClass(int docClass) => All.First(t => t.DocClasses.Contains(docClass));
    }

    /// <summary>Small SQL helpers for set-based work on the Juris batch tree (DocumentTree).</summary>
    public static class BatchTree
    {
        /// <summary>SQL condition: DocumentTree node <paramref name="dt"/> has no children (FK1_DocumentTree).</summary>
        public static string NoChildren(string dt) => "not exists (select 1 from DocumentTree ch where ch.DTParentID = " + dt + ".DTDocID)";
    }
}
