using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.UndoBankRec
{
    /// <summary>One BankReconHistory row. Raw values are kept so the update keys on the exact primary key.</summary>
    public sealed class ReconRecord
    {
        public DateTime StmtDate { get; set; }
        public DateTime? RecordedDate { get; set; }
        public decimal OpenBal { get; set; }
        public decimal EndBal { get; set; }
        public DateTime? BookLastStmtDate { get; set; }
        public decimal BookLastStmtBal { get; set; }
        /// <summary>JE batch Juris created for check-register adjustments on this statement (0 = none).</summary>
        public int AdjustmentBatch { get; set; }
        /// <summary>Entries in that JE batch (JEBatch.JEBRecCount); Juris often creates the batch empty.</summary>
        public int AdjustmentEntries { get; set; }
        /// <summary>Deposits + checks Juris counted as cleared on this statement (BRHBookDepositCount + BRHBookCheckCount).</summary>
        public int BookItemCount { get; set; }
    }

    /// <summary>Everything the screen shows and the confirmation promises. The undo re-reads it and refuses if anything changed.</summary>
    public sealed class BankRecState
    {
        public string Bank { get; set; }
        public ReconRecord Latest { get; set; }
        public ReconRecord Previous { get; set; }
        public List<ReconRecord> InProgress { get; set; } = new List<ReconRecord>();
        /// <summary>Items cleared ('Y') by the latest rec: they go back to ticked ('P').</summary>
        public int ItemsToUnclear { get; set; }
        /// <summary>Items ticked ('P') in the in-progress rec: they go back to not cleared ('N').</summary>
        public int TickedInProgress { get; set; }

        /// <summary>BnkLastReconDate after the undo: the previous rec's statement date, or Juris's empty date when there is none.</summary>
        public DateTime NewLastReconDate => Previous?.StmtDate ?? UndoBankRecService.EmptyDate;
        public decimal NewLastReconBal => Previous?.EndBal ?? 0m;

        /// <summary>
        /// The undone rec also recorded what the prior statement was. Returns a description when that disagrees with the
        /// previous reconciliation on file (or there is none), otherwise null.
        /// </summary>
        public string PriorMismatch
        {
            get
            {
                if (Latest == null) return null;
                var bookDate = Latest.BookLastStmtDate;
                var bookIsEmpty = bookDate == null || bookDate.Value.Year < 1900;
                if (Previous == null)
                    return bookIsEmpty && Latest.BookLastStmtBal == 0m ? null
                        : "This reconciliation recorded its prior statement as " + Describe(bookDate, Latest.BookLastStmtBal)
                          + ", but no earlier recorded reconciliation exists for this bank.";
                if (!bookIsEmpty && bookDate.Value == Previous.StmtDate && Latest.BookLastStmtBal == Previous.EndBal) return null;
                return "This reconciliation recorded its prior statement as " + Describe(bookDate, Latest.BookLastStmtBal)
                       + ", but the previous recorded reconciliation is " + Describe(Previous.StmtDate, Previous.EndBal) + ".";
            }
        }

        /// <summary>
        /// Juris records how many items the rec cleared (BRHBookDepositCount + BRHBookCheckCount); on the live data that always equals
        /// the CheckRegister rows carrying the statement date. If fewer rows are left (cleared items purged with Juris's
        /// "purge cleared items", or edited by hand) the rec cannot be put back as it was, so the undo is refused. Null when they agree.
        /// </summary>
        public string ItemCountProblem =>
            Latest == null || ItemsToUnclear == Latest.BookItemCount ? null
                : "Juris recorded " + Latest.BookItemCount + " cleared item(s) for this statement, but " + ItemsToUnclear
                  + " are still in the check register with that reconciliation date (cleared items may have been purged or changed). "
                  + "The reconciliation can't be restored exactly, so this tool won't undo it.";

        public static string Describe(DateTime? date, decimal balance) =>
            (date == null || date.Value.Year < 1900 ? "no date" : date.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture))
            + " with ending balance " + balance.ToString("#,##0.00", CultureInfo.CurrentCulture);

        public bool SameAs(BankRecState other) =>
            other != null && other.Bank == Bank
            && Same(Latest, other.Latest) && Same(Previous, other.Previous)
            && InProgress.Count == other.InProgress.Count
            && InProgress.Zip(other.InProgress, Same).All(x => x)
            && ItemsToUnclear == other.ItemsToUnclear && TickedInProgress == other.TickedInProgress;

        private static bool Same(ReconRecord a, ReconRecord b) =>
            a == null ? b == null
                : b != null && a.StmtDate == b.StmtDate && a.RecordedDate == b.RecordedDate && a.EndBal == b.EndBal && a.AdjustmentBatch == b.AdjustmentBatch;
    }

    public sealed class UndoBankRecService
    {
        public static readonly DateTime EmptyDate = new DateTime(1899, 12, 31);

        private readonly JurisSession _session;

        public UndoBankRecService(JurisSession session)
        {
            _session = session;
        }

        public BankRecState Load(string bank) => Load(bank, false);

        private BankRecState Load(string bank, bool forUpdate)
        {
            var hint = forUpdate ? " with (updlock, holdlock)" : "";
            var state = new BankRecState { Bank = bank };

            // Lock the bank account row first so the history/register reads below and the BankAccount update are serialised.
            if (forUpdate)
                _session.Scalar("select BnkCode from BankAccount with (updlock, holdlock) where BnkCode = @bank", Sql.P("@bank", bank));

            const string columns = @"BRHStmtDate, BRHRecordedDate, BRHStmtOpenBal, BRHStmtEndBal, BRHBookLastStmtDate, BRHBookLastStmtBal, BRHLastCkRegBatchJE,
                         isnull(BRHBookDepositCount, 0) + isnull(BRHBookCheckCount, 0) as BookItemCount,
                         isnull((select JEBRecCount from JEBatch where JEBBatchNbr = BRHLastCkRegBatchJE), 0) as AdjustmentEntries";
            var recorded = _session.Query(
                @"select top 2 " + columns + @"
                  from BankReconHistory" + hint + @"
                  where BRHRecorded = 'Y' and BRHBank = @bank
                  order by BRHStmtDate desc, BRHRecordedDate desc", Sql.P("@bank", bank)).Rows().Select(ToRecord).ToList();
            state.Latest = recorded.ElementAtOrDefault(0);
            state.Previous = recorded.ElementAtOrDefault(1);

            state.InProgress = _session.Query(
                @"select " + columns + @"
                  from BankReconHistory" + hint + @"
                  where BRHRecorded = 'N' and BRHBank = @bank
                  order by BRHStmtDate", Sql.P("@bank", bank)).Rows().Select(ToRecord).ToList();

            state.TickedInProgress = Convert.ToInt32(_session.Scalar(
                "select count(*) from CheckRegister" + hint + " where CkRegBank = @bank and CkRegCleared = 'P'", Sql.P("@bank", bank)));
            if (state.Latest != null)
                state.ItemsToUnclear = Convert.ToInt32(_session.Scalar(
                    "select count(*) from CheckRegister" + hint + " where CkRegBank = @bank and CkRegCleared = 'Y' and CkRegReconDate = @stmt",
                    Sql.P("@bank", bank), Sql.P("@stmt", state.Latest.StmtDate)));
            return state;
        }

        private static ReconRecord ToRecord(DataRow r) => new ReconRecord
        {
            StmtDate = Convert.ToDateTime(r["BRHStmtDate"]),
            RecordedDate = r["BRHRecordedDate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["BRHRecordedDate"]),
            OpenBal = r["BRHStmtOpenBal"] == DBNull.Value ? 0m : Convert.ToDecimal(r["BRHStmtOpenBal"]),
            EndBal = r["BRHStmtEndBal"] == DBNull.Value ? 0m : Convert.ToDecimal(r["BRHStmtEndBal"]),
            BookLastStmtDate = r["BRHBookLastStmtDate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["BRHBookLastStmtDate"]),
            BookLastStmtBal = r["BRHBookLastStmtBal"] == DBNull.Value ? 0m : Convert.ToDecimal(r["BRHBookLastStmtBal"]),
            AdjustmentBatch = r["BRHLastCkRegBatchJE"] == DBNull.Value ? 0 : Convert.ToInt32(r["BRHLastCkRegBatchJE"]),
            AdjustmentEntries = r["AdjustmentEntries"] == DBNull.Value ? 0 : Convert.ToInt32(r["AdjustmentEntries"]),
            BookItemCount = Convert.ToInt32(r["BookItemCount"]),
        };

        /// <summary>
        /// Undoes <paramref name="confirmed"/>.Latest in one transaction. Refuses (nothing written) if the bank's reconciliation
        /// state is no longer exactly what the user confirmed.
        /// </summary>
        public void Undo(BankRecState confirmed)
        {
            _session.InTransaction(() =>
            {
                var bank = confirmed.Bank;
                var now = Load(bank, true);
                if (now.Latest == null || !confirmed.SameAs(now))
                    throw new InvalidOperationException("The reconciliation for bank " + bank + " changed since you loaded it (someone may be working on it in Juris). "
                                                        + "Nothing was changed. Pick the bank again to see the current state.");
                if (now.ItemCountProblem != null)
                    throw new InvalidOperationException(now.ItemCountProblem + " Nothing was changed.");
                var stmt = now.Latest.StmtDate;

                // a) discard the in-progress rec and un-tick its items
                if (now.InProgress.Count > 0)
                {
                    _session.Execute("delete from BankReconHistory where BRHBank = @bank and BRHRecorded = 'N'", Sql.P("@bank", bank));
                    _session.Execute("update CheckRegister set CkRegCleared = 'N', CkRegReconDate = null where CkRegBank = @bank and CkRegCleared = 'P'",
                        Sql.P("@bank", bank));
                }

                // b) the last recorded rec back to unrecorded, keyed on the primary key
                if (_session.ExecuteRows(
                        @"update BankReconHistory set BRHRecorded = 'N', BRHRecordedDate = @empty
                          where BRHBank = @bank and BRHStmtDate = @stmt and BRHRecorded = 'Y'",
                        Sql.P("@bank", bank), Sql.P("@stmt", stmt), Sql.P("@empty", EmptyDate)) != 1)
                    throw new InvalidOperationException("The " + stmt.ToString("MM/dd/yyyy") + " reconciliation for bank " + bank + " couldn't be found to unrecord. Nothing was changed.");

                // c) its cleared items back to ticked (exact inverse of Juris's record step)
                var items = _session.ExecuteRows(
                    "update CheckRegister set CkRegCleared = 'P', CkRegReconDate = null where CkRegBank = @bank and CkRegCleared = 'Y' and CkRegReconDate = @stmt",
                    Sql.P("@bank", bank), Sql.P("@stmt", stmt));
                if (items != now.ItemsToUnclear)
                    throw new InvalidOperationException("Expected to un-clear " + now.ItemsToUnclear + " items but found " + items + ". Nothing was changed.");

                // d) the bank account back to the previous recorded rec (or empty when there is none)
                if (_session.ExecuteRows("update BankAccount set BnkLastReconDate = @date, BnkLastReconBal = @bal where BnkCode = @bank",
                        Sql.P("@bank", bank), Sql.P("@date", now.NewLastReconDate), Sql.P("@bal", now.NewLastReconBal)) != 1)
                    throw new InvalidOperationException("Bank account " + bank + " couldn't be updated. Nothing was changed.");

                _session.WriteUtilityLog("JPS Undo Bank Rec: bank " + bank + ", statement " + stmt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
                                         + " set to unrecorded, " + items + " items un-cleared, in-progress rec deleted: " + (now.InProgress.Count > 0 ? "Y" : "N")
                                         + ", last rec now " + BankRecState.Describe(now.Previous?.StmtDate, now.NewLastReconBal)
                                         + (now.Latest.AdjustmentBatch != 0 ? ", its check-register JE batch " + now.Latest.AdjustmentBatch + " left posted" : "")
                                         + string.Concat(now.InProgress.Where(r => r.AdjustmentBatch != 0)
                                               .Select(r => ", deleted in-progress rec " + r.StmtDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
                                                            + " had check-register JE batch " + r.AdjustmentBatch))
                                         + ".");
            });
        }
    }
}
