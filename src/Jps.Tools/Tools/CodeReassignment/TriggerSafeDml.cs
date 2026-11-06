using System;
using System.Data.SqlClient;
using Jps.Core.Data;

namespace Jps.Tools.Tools.CodeReassignment
{
    /// <summary>Which MyJuris entry table a batch-detail table's update trigger rewrites.</summary>
    public enum EntryMirror
    {
        None,

        /// <summary>TimeBatchDetail: MyJuris_TimeBatchDetail_Update rewrites the linked TimeEntry rows.</summary>
        TimeEntry,

        /// <summary>ExpBatchDetail: MyJuris_ExpenseBatchDetail_Update rewrites the linked ExpenseEntry rows.</summary>
        ExpenseEntry,
    }

    /// <summary>
    /// SQL helpers for writing to Juris tables, whose triggers have side effects:
    /// <list type="bullet">
    /// <item>Almost every table has audit triggers (insert into X_Log, JurisChangeLog, DeletedRows) and the server runs with
    /// NOCOUNT off, so Session.Execute's count includes the trigger's rows (a one-row delete reports 2-3).
    /// Use <see cref="JurisSession.ExecuteRows"/> to count only the rows the statement itself touched.</item>
    /// <item>Updating ANY column of TimeBatchDetail / ExpBatchDetail fires MyJuris_*BatchDetail_Update, which copies every batch
    /// value onto the linked TimeEntry / ExpenseEntry rows and sets EntryStatus to 7 (Posted) - so billed entries (status 9)
    /// and entries edited on a pre-bill lose their real state. <see cref="SnapshotEntries"/> / <see cref="RestoreEntries"/>
    /// put the entry rows back exactly as they were (the caller re-codes the entry table itself).</item>
    /// </list>
    /// </summary>
    public static class TriggerSafeDml
    {
        private const string TimeColumns =
            "ClientSysNbr, MatterSysNbr, BudgetPhase, EntryDate, PeriodYear, PeriodNumber, TimekeeperSysNbr, FeeScheduleCode, TaskCode, "
            + "ActivityCode, BillableFlag, ActualHoursWork, HoursSource, HoursToBill, RateSource, Rate, AmountSource, Amount, "
            + "Code1, Code2, Code3, BillNote, Narrative, EntryState, EntryStatus";

        private const string ExpenseColumns =
            "ClientSysNbr, MatterSysNbr, BudgetPhase, TaskCode, EntryDate, PeriodYear, PeriodNbr, ExpenseScheduleCode, ExpenseCode, "
            + "Summarize, Units, Multiplier, AmountSource, Amount, IncurredBySysNbr, Code1, Code2, Code3, BillNote, Narrative, "
            + "EntryState, EntryStatus";

        private static string Table(EntryMirror m) => m == EntryMirror.TimeEntry ? "#JpsTimeEntryMirror" : "#JpsExpenseEntryMirror";
        private static string Columns(EntryMirror m) => m == EntryMirror.TimeEntry ? TimeColumns : ExpenseColumns;
        private static string EntryTable(EntryMirror m) => m == EntryMirror.TimeEntry ? "TimeEntry" : "ExpenseEntry";

        /// <summary>
        /// Saves the entry rows linked to the batch-detail rows matching <paramref name="detailWhere"/> (batch detail aliased
        /// <c>d</c>, e.g. <c>d.TBDTaskCd = @From</c>). Call inside the transaction, just before updating the batch detail.
        /// </summary>
        public static void SnapshotEntries(JurisSession session, EntryMirror mirror, string detailWhere, params SqlParameter[] parameters)
        {
            if (mirror == EntryMirror.None) return;
            var temp = Table(mirror);
            var entry = EntryTable(mirror);
            // Created without parameters so it is a plain batch: a #table made inside sp_executesql vanishes when that call ends.
            // cast() so the copy doesn't inherit EntryID's IDENTITY (the insert below supplies the ids).
            session.Execute("if object_id('tempdb.." + temp + "') is not null drop table " + temp + ";\n"
                            + "select top 0 cast(EntryID as int) as EntryID, " + Columns(mirror) + " into " + temp + " from " + entry + ";");
            var link = mirror == EntryMirror.TimeEntry
                ? "TimeEntryLink l inner join TimeBatchDetail d on d.TBDID = l.TBDID"
                : "ExpenseEntryLink l inner join ExpBatchDetail d on d.EBDID = l.EBDID";
            session.Execute("insert into " + temp + " (EntryID, " + Columns(mirror) + ")\n"
                            + "select e.EntryID, " + Prefixed("e.", Columns(mirror)) + " from " + entry + " e\n"
                            + "where e.EntryID in (select l.EntryID from " + link + " where " + detailWhere + ")", parameters);
        }

        /// <summary>Puts the saved entry rows back as they were and drops the snapshot.</summary>
        public static void RestoreEntries(JurisSession session, EntryMirror mirror)
        {
            if (mirror == EntryMirror.None) return;
            var temp = Table(mirror);
            var sets = string.Join(", ", Array.ConvertAll(Columns(mirror).Split(','), c => "e." + c.Trim() + " = s." + c.Trim()));
            session.Execute("update e set " + sets + " from " + EntryTable(mirror) + " e inner join " + temp + " s on s.EntryID = e.EntryID;\n"
                            + "drop table " + temp + ";");
        }

        private static string Prefixed(string prefix, string columns) =>
            string.Join(", ", Array.ConvertAll(columns.Split(','), c => prefix + c.Trim()));
    }
}
