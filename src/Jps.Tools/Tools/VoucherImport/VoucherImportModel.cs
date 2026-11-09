using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace Jps.Tools.Tools.VoucherImport
{
    /// <summary>How invoices that already exist in Juris (same vendor, invoice number and invoice date) are handled.</summary>
    public enum DuplicateMode
    {
        Reject,
        Increment,
    }

    /// <summary>One payment voucher from the file: an A (AP) or T (trust) row plus the G/E rows below it.</summary>
    public sealed class ImportVoucher
    {
        public int Sequence { get; set; }          // voucher number in the file (1, 2, 3 ...)
        public int Row { get; set; }               // spreadsheet row of the A/T row
        public string Type { get; set; }          // "A" or "T"
        public string VendorText { get; set; }
        public string FileInvoice { get; set; }   // invoice number exactly as in the file (G/E rows must match it)
        public string Invoice { get; set; }       // final invoice number written to Juris (after any _n suffix)
        public string PONbr { get; set; }
        public DateTime VoucherDate { get; set; }
        public DateTime InvoiceDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? DiscDate { get; set; }
        public decimal InvoiceAmt { get; set; }
        public decimal NonDiscAmt { get; set; }
        public string Memo { get; set; }
        public string SeparateCheck { get; set; } = "N";
        public string APAcctText { get; set; }
        public string TrustBankText { get; set; }
        public string ClientText { get; set; }
        public string MatterText { get; set; }

        // Resolved from Juris during the checks.
        public int VendorSysNbr { get; set; }
        public string VendorCode { get; set; }
        /// <summary>The vendor code as Juris shows it (formatted), for messages and reports.</summary>
        public string VendorDisplay { get; set; }
        public string VendorName { get; set; }
        public int? VendorDiscAcct { get; set; }
        public int? VendorDefaultDistAcct { get; set; }
        public string APAcct { get; set; }         // A: resolved AP account; T: null
        public int? TrustMatter { get; set; }      // T only
        public string TrustBank { get; set; }      // T only

        public List<GlLine> GlLines { get; } = new List<GlLine>();
        public List<ExpenseLine> ExpenseLines { get; } = new List<ExpenseLine>();
        public List<string> Notes { get; } = new List<string>();
        public bool HeaderParsed { get; set; }
        public bool Rejected { get; set; }

        public decimal ExpenseTotal => ExpenseLines.Sum(e => e.EffectiveAmount);
        public decimal GlTotal => GlLines.Sum(g => g.Amount);
        public string Describe => (Type == "T" ? "Trust" : "AP") + " voucher, vendor " + VendorText + ", invoice " + FileInvoice;
    }

    public sealed class GlLine
    {
        public int Row { get; set; }               // 0 for the automatic expense line
        public string AccountText { get; set; }
        public int ChtSysNbr { get; set; }
        public decimal Amount { get; set; }
        public bool IsAutomatic { get; set; }
    }

    public sealed class ExpenseLine
    {
        public int Row { get; set; }
        public string ClientText { get; set; }
        public string MatterText { get; set; }
        public string ExpCodeText { get; set; }
        public string TaskText { get; set; }
        public string IncurredByText { get; set; }
        public decimal Units { get; set; }
        public decimal Amount { get; set; }
        public string Narrative { get; set; }
        public string BillNote { get; set; }

        // Resolved from Juris.
        public int MatterSysNbr { get; set; }
        public string ExpCode { get; set; }
        public string TaskCode { get; set; }       // null when none given
        public string IncurredBy { get; set; }     // EmpID, null when none given
        public decimal EffectiveAmount { get; set; } // what VBMAmount will be: Amount, or Units x rate
    }

    /// <summary>Everything the read-only checks produced. Only <see cref="Accepted"/> vouchers are ever written.</summary>
    public sealed class ImportCheck
    {
        public string FilePath { get; set; }
        public DuplicateMode Mode { get; set; }
        public string Suffix { get; set; }
        public List<ImportVoucher> Vouchers { get; } = new List<ImportVoucher>();
        public DataTable Problems { get; } = NewProblemTable();
        public List<ImportVoucher> Accepted => Vouchers.Where(v => !v.Rejected).ToList();
        public int RejectedCount => Vouchers.Count(v => v.Rejected);
        public int SkippedRowCount { get; set; }

        public void AddProblem(int row, string voucher, string problem) => Problems.Rows.Add(row == 0 ? (object)DBNull.Value : row, voucher, problem);

        /// <summary>Puts the problems in file-row order (file-level problems without a row first), keeping the order within a row.</summary>
        public void SortProblems()
        {
            var rows = Problems.Rows.Cast<DataRow>()
                .Select((r, i) => new { Values = r.ItemArray, Row = r["Row"] == DBNull.Value ? 0 : (int)r["Row"], Index = i })
                .OrderBy(x => x.Row).ThenBy(x => x.Index).Select(x => x.Values).ToList();
            Problems.Rows.Clear();
            foreach (var values in rows) Problems.Rows.Add(values);
        }

        private static DataTable NewProblemTable()
        {
            var table = new DataTable();
            table.Columns.Add("Row", typeof(int));
            table.Columns.Add("Voucher", typeof(string));
            table.Columns.Add("Problem", typeof(string));
            return table;
        }
    }

    public sealed class ImportResult
    {
        public int BatchNumber { get; set; }
        public int VoucherCount { get; set; }
        public int FirstVoucherNumber { get; set; }
        public int LastVoucherNumber { get; set; }
        public decimal Total { get; set; }
    }
}
