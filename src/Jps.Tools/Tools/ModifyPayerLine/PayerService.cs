using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.ModifyPayerLine
{
    /// <summary>Reads cash receipts and rewrites their payer text on the receipt and on every A/R allocation line.</summary>
    public sealed class PayerService
    {
        private readonly JurisSession _session;

        public PayerService(JurisSession session)
        {
            _session = session;
        }

        /// <summary>The longest payer both CashReceipt.CRPayor and CRARAlloc.CRAPayor can hold (50 in current Juris).</summary>
        public int MaxPayerLength()
        {
            var value = _session.Scalar(
                @"select min(CHARACTER_MAXIMUM_LENGTH) from INFORMATION_SCHEMA.COLUMNS
                  where (TABLE_NAME = 'CashReceipt' and COLUMN_NAME = 'CRPayor') or (TABLE_NAME = 'CRARAlloc' and COLUMN_NAME = 'CRAPayor')");
            var length = value == null ? 50 : Convert.ToInt32(value);
            return length > 0 ? length : 50;
        }

        public List<int> Batches(DateTime date) =>
            _session.Query("select distinct CRBatch from CashReceipt where CRDate = @Date order by CRBatch", Sql.P("@Date", date.Date))
                .Rows().Select(r => Convert.ToInt32(r["CRBatch"])).ToList();

        public List<int> Records(int batch) =>
            _session.Query("select distinct CRRecNbr from CashReceipt where CRBatch = @Batch order by CRRecNbr", Sql.P("@Batch", batch))
                .Rows().Select(r => Convert.ToInt32(r["CRRecNbr"])).ToList();

        public string CurrentPayer(int batch, int record) =>
            Convert.ToString(_session.Scalar("select CRPayor from CashReceipt where CRBatch = @Batch and CRRecNbr = @Rec",
                Sql.P("@Batch", batch), Sql.P("@Rec", record)))?.Trim() ?? "";

        /// <summary>Pre-report: the receipts that will change. <paramref name="record"/> null = every receipt in the batch.</summary>
        public DataTable Receipts(int batch, int? record, string newPayer) =>
            _session.Query(
                @"select CR.CRBatch as [Batch], CR.CRRecNbr as [Record], CR.CRDate as [Date], CR.CRCheckNbr as [Check Number],
                         CR.CRCheckAmt as [Check Amount], CR.CRPayor as [Current Payer], @NewPayor as [New Payer],
                         (select count(*) from CRARAlloc A where A.CRABatch = CR.CRBatch and A.CRARecNbr = CR.CRRecNbr) as [A/R Allocation Lines],
                         case when CR.CRPosted = 'Y' then 'Yes' else 'No' end as [Posted]
                  from CashReceipt CR
                  where CR.CRBatch = @Batch and (@All = 1 or CR.CRRecNbr = @Rec)
                  order by CR.CRRecNbr",
                Parameters(batch, record, newPayer));

        /// <summary>Writes the new payer to the receipt(s) and their A/R allocation lines in one transaction.</summary>
        public Tuple<int, int> Apply(int batch, int? record, string newPayer, string oldPayer)
        {
            return _session.InTransaction(() =>
            {
                var receipts = _session.ExecuteRows(
                    "update CashReceipt set CRPayor = @NewPayor where CRBatch = @Batch and (@All = 1 or CRRecNbr = @Rec)",
                    Parameters(batch, record, newPayer));
                var lines = _session.ExecuteRows(
                    "update CRARAlloc set CRAPayor = @NewPayor where CRABatch = @Batch and (@All = 1 or CRARecNbr = @Rec)",
                    Parameters(batch, record, newPayer));
                if (receipts > 0)
                    _session.WriteUtilityLog("JPS Modify Payer Line in Cash Receipts: batch " + batch + " record "
                        + (record == null ? "All" : record.ToString()) + " payer changed from '" + oldPayer + "' to '" + newPayer + "' ("
                        + receipts + " receipt(s), " + lines + " A/R line(s)).");
                return Tuple.Create(receipts, lines);
            });
        }

        private static SqlParameter[] Parameters(int batch, int? record, string newPayer) => new[]
        {
            new SqlParameter("@NewPayor", SqlDbType.VarChar, Math.Max(1, newPayer.Length)) { Value = newPayer },
            Sql.P("@Batch", batch),
            Sql.P("@Rec", record ?? 0),
            Sql.P("@All", record == null ? 1 : 0),
        };
    }
}
