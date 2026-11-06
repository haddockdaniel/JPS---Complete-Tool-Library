using System.Collections.Generic;

namespace Jps.Tools.Tools.CodeReassignment
{
    /// <summary>How one column that holds a code is moved from the From code to the To code.</summary>
    public enum CodeRefKind
    {
        /// <summary>Plain <c>update T set C = @To where C = @From</c>.</summary>
        Update,

        /// <summary>
        /// Summary/allocation table: a From row whose key (all <see cref="CodeRef.Keys"/>) already exists with the To code
        /// is ADDED INTO that To row and deleted; every other From row is simply re-coded. No amount is ever lost.
        /// </summary>
        Merge,

        /// <summary>
        /// Table with a unique key on (Keys + code), e.g. favourites or rates: if the To row already exists for that key,
        /// the To row is kept and the From row deleted; otherwise the From row is re-coded.
        /// </summary>
        KeepTarget,

        /// <summary>Plain update, after appending "Updated code from X to Y on date" to <see cref="CodeRef.NoteColumn"/>.</summary>
        UpdateWithNote,
    }

    /// <summary>One table.column that stores a code, and how to reassign it.</summary>
    public sealed class CodeRef
    {
        private CodeRef() { }

        /// <summary>Status text shown while this step runs (consecutive refs with the same text share one status).</summary>
        public string Step { get; private set; }
        public string Table { get; private set; }
        public string Column { get; private set; }
        public CodeRefKind Kind { get; private set; }

        /// <summary>Key columns other than the code. A trailing "?" marks a nullable code column (NULL and '' compare equal).</summary>
        public IReadOnlyList<string> Keys { get; private set; } = new string[0];

        /// <summary>Hours/amount columns added together when rows merge.</summary>
        public IReadOnlyList<string> Sums { get; private set; } = new string[0];

        public string NoteColumn { get; private set; }

        /// <summary>
        /// Set on TimeBatchDetail / ExpBatchDetail: their MyJuris update trigger rewrites the linked TimeEntry / ExpenseEntry
        /// rows (status back to Posted, batch values over pre-bill edits), so the engine saves and restores those rows.
        /// </summary>
        public EntryMirror Mirror { get; private set; }

        /// <summary>What the user sees for this reference: the place in Juris in plain words (never the table/column name).</summary>
        public string Name => Places.TryGetValue(Table, out var place) ? place : Table;

        /// <summary>Plain-English names of the Juris tables a code can live in, as shown in counts, reports and messages.</summary>
        private static readonly Dictionary<string, string> Places = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "ARFTaskAlloc", "Billed fees on open and paid bills (accounts receivable)" },
            { "BilledTime", "Billed time entries" },
            { "BilledExpenses", "Billed expense entries" },
            { "ActivityCodeMostRecent", "Recently used Activity Code lists" },
            { "ActivityCodeFavorite", "Favorite Activity Code lists" },
            { "TaskCodeMostRecent", "Recently used Task Code lists" },
            { "TaskCodeFavorite", "Favorite Task Code lists" },
            { "MobilityTemplates", "Mobile time entry templates" },
            { "CRFeeAlloc", "Cash receipt fee allocations" },
            { "FeeSumByPrd", "Fee totals by accounting period" },
            { "MatterFeeBudget", "Matter fee budgets" },
            { "MatterExpBudget", "Matter expense budgets" },
            { "TimeBatchDetail", "Time entry batches" },
            { "ExpBatchDetail", "Expense entry batches" },
            { "TimeEntry", "Time entries (Time Entry screen)" },
            { "TimeEntryTemplate", "Time entry templates" },
            { "ExpenseEntry", "Expense entries (Expense Entry screen)" },
            { "ExpenseEntryTemplate", "Expense entry templates" },
            { "VoucherBatchMatDist", "Voucher batches (matter distributions)" },
            { "VoucherMatDist", "Posted vouchers (matter distributions)" },
            { "UnbilledExpense", "Unbilled expenses" },
            { "UnbilledTime", "Unbilled time" },
            { "TaskCodeRate", "Task Code rates on fee schedules" },
            { "TaskCodeXref", "Task Code cross-reference lists" },
        };

        /// <summary>The same reference, with the linked MyJuris entry rows protected from the table's update trigger.</summary>
        public CodeRef Protecting(EntryMirror mirror)
        {
            var copy = (CodeRef)MemberwiseClone();
            copy.Mirror = mirror;
            return copy;
        }

        public static CodeRef Update(string step, string table, string column) =>
            new CodeRef { Step = step, Table = table, Column = column, Kind = CodeRefKind.Update };

        public static CodeRef Merge(string step, string table, string column, string[] keys, string[] sums) =>
            new CodeRef { Step = step, Table = table, Column = column, Kind = CodeRefKind.Merge, Keys = keys, Sums = sums };

        public static CodeRef KeepTarget(string step, string table, string column, params string[] keys) =>
            new CodeRef { Step = step, Table = table, Column = column, Kind = CodeRefKind.KeepTarget, Keys = keys };

        public static CodeRef UpdateWithNote(string step, string table, string column, string noteColumn) =>
            new CodeRef { Step = step, Table = table, Column = column, Kind = CodeRefKind.UpdateWithNote, NoteColumn = noteColumn };
    }

    /// <summary>
    /// Everything that differs between the code-reassignment tools: labels, the code table, every column that
    /// references a code, the clean-up after deleting the From code, and an optional report.
    /// All identifiers here are compile-time constants, never user input.
    /// </summary>
    public sealed class CodeReassignmentMap
    {
        /// <summary>"Activity Code", "Task Code"...</summary>
        public string Noun { get; set; }

        /// <summary>UtilityLog prefix, e.g. "Activity Code Reassignment".</summary>
        public string ToolName { get; set; }

        public string CodeTable { get; set; }
        public string CodeColumn { get; set; }
        public string DescColumn { get; set; }

        public IReadOnlyList<CodeRef> References { get; set; }

        /// <summary>Extra statements run after the code row is deleted (e.g. DocumentTree clean-up). May use @From.</summary>
        public IReadOnlyList<string> AfterDeleteSql { get; set; } = new string[0];

        /// <summary>
        /// Places that hold the code but are NOT reassigned (free text such as saved filter definitions): if any count
        /// (a query returning one number, using @From) is above zero the delete is blocked and the label shown.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> DeleteChecks { get; set; } = new KeyValuePair<string, string>[0];

        /// <summary>Optional report shown from a button left of the action (null = no report).</summary>
        public string ReportButtonText { get; set; }
        public string ReportTitle { get; set; }
        public string ReportSubtitle { get; set; }
        public string ReportSql { get; set; }
    }
}
