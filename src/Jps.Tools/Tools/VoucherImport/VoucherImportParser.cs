using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jps.Core.Import;

namespace Jps.Tools.Tools.VoucherImport
{
    /// <summary>
    /// Turns the import file into vouchers (no database access). A G/E row belongs to the nearest A/T row above it.
    /// Every value problem is recorded against its spreadsheet row; a problem on any row rejects the whole voucher.
    /// </summary>
    public static class VoucherImportParser
    {
        /// <summary>
        /// The 25 columns, by their original header names. Third-party exports (Chrome River, Concur...) are mapped to these
        /// names, so they always keep working; the code below reads every value by these keys.
        /// </summary>
        public static readonly string[] Headers =
        {
            "Type", "VoucherDate", "VendorCode", "PONbr", "InvoiceNbr", "DueDate", "InvoiceDate", "DiscountDate",
            "InvoiceAmt", "NonDiscAmt", "VchReference", "SeparateCheck", "APAcct", "GLDistAcct", "GLAmt", "TrustBank",
            "ExpClient", "ExpMatter", "ExpCode", "ExpTaskCode", "IncurredBy", "ExpUnits", "ExpAmount", "ExpNarrative", "ExpBillNote",
        };

        /// <summary>The plain header the template uses for each original header (same order). Either name is accepted.</summary>
        public static readonly string[] FriendlyHeaders =
        {
            "Row Type", "Voucher Date", "Vendor Code", "PO Number", "Invoice Number", "Due Date", "Invoice Date", "Discount Date",
            "Invoice Amount", "Non-Discountable Amount", "Voucher Reference", "Separate Check (Yes/No)", "A/P Account", "GL Distribution Account", "GL Amount", "Trust Bank",
            "Client Code", "Matter Code", "Expense Code", "Task Code", "Incurred By", "Expense Units", "Expense Amount", "Expense Narrative", "Expense Bill Note",
        };

        /// <summary>The plain name of a column, for messages.</summary>
        private static string Col(string header) => FriendlyHeaders[Array.IndexOf(Headers, header)];

        // Headers are matched ignoring case, spaces and punctuation, so "Invoice Number", "InvoiceNbr" and "invoice_number" all match.
        private static string Normalize(string header) => new string((header ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>Normalized header text -> original header name: the original names, the plain names, and the plain names without their "(...)" hint.</summary>
        private static readonly Dictionary<string, string> Aliases = BuildAliases();

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i < Headers.Length; i++)
            {
                var friendly = FriendlyHeaders[i];
                var paren = friendly.IndexOf('(');
                foreach (var name in new[] { Headers[i], friendly, paren > 0 ? friendly.Substring(0, paren) : null })
                    if (name != null && !map.ContainsKey(Normalize(name))) map[Normalize(name)] = Headers[i];
            }
            return map;
        }

        public static string FileFilter => "Voucher import files (*.csv;*.xlsx;*.xls)|*.csv;*.xlsx;*.xls|All files (*.*)|*.*";

        private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

        private sealed class SourceRow
        {
            public int Row;
            public Dictionary<string, string> Values;
            public string this[string header] => Values.TryGetValue(header, out var v) ? (v ?? "").Trim() : "";
        }

        /// <summary>Reads and parses the file into <paramref name="check"/>. Throws InvalidDataException when the file itself can't be used.</summary>
        public static void Parse(string path, ImportCheck check)
        {
            var excel = !string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
            var rows = excel ? ReadExcel(path) : ReadCsv(path);

            ImportVoucher current = null;
            var lastHeaderRow = 0;
            var sequence = 0;
            foreach (var r in rows)
            {
                var type = r["Type"].ToUpperInvariant();
                switch (type)
                {
                    case "A":
                    case "T":
                        current = ParseHeader(r, type, ++sequence, excel, check);
                        lastHeaderRow = r.Row;
                        check.Vouchers.Add(current);
                        break;
                    case "G":
                    case "E":
                        if (current == null)
                        {
                            check.AddProblem(r.Row, "", "This " + (type == "G" ? "GL line (G)" : "expense line (E)") + " has no voucher row (A or T) above it, so it was not imported.");
                            check.SkippedRowCount++;
                            break;
                        }
                        if (type == "G") ParseGl(r, current, check);
                        else ParseExpense(r, current, excel, check);
                        break;
                    default:
                        check.AddProblem(r.Row, "", type.Length == 0
                            ? "Row Type is blank, so this row was skipped. Use A (A/P voucher), T (trust voucher), G (GL line) or E (expense line)."
                            : "Row Type \"" + r["Type"] + "\" isn't A (A/P voucher), T (trust voucher), G (GL line) or E (expense line), so this row was skipped.");
                        check.SkippedRowCount++;
                        break;
                }
            }
            if (check.Vouchers.Count == 0 && check.Problems.Rows.Count == 0)
                throw new InvalidDataException("The file has no voucher rows (Row Type A for an A/P voucher or T for a trust voucher).");

            // A file that repeats vendor + invoice: 2nd, 3rd ... occurrence become INV_2, INV_3 ... (original behaviour, in file order).
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in check.Vouchers.Where(v => v.HeaderParsed))
            {
                var key = Pad12(v.VendorText) + "|" + v.FileInvoice;
                seen[key] = seen.TryGetValue(key, out var n) ? n + 1 : 1;
                if (seen[key] > 1)
                {
                    v.Invoice = v.FileInvoice + "_" + seen[key];
                    v.Notes.Add("Invoice number changed to " + v.Invoice + " because vendor " + v.VendorText + " invoice " + v.FileInvoice + " appears more than once in the file.");
                }
            }
        }

        // ------------------------------------------------------------------ rows

        private static ImportVoucher ParseHeader(SourceRow r, string type, int sequence, bool excel, ImportCheck check)
        {
            var v = new ImportVoucher
            {
                Sequence = sequence,
                Row = r.Row,
                Type = type,
                VendorText = r["VendorCode"],
                FileInvoice = r["InvoiceNbr"],
                Invoice = r["InvoiceNbr"],
                PONbr = r["PONbr"],
                Memo = r["VchReference"],
                APAcctText = r["APAcct"],
                TrustBankText = r["TrustBank"],
                ClientText = r["ExpClient"],
                MatterText = r["ExpMatter"],
            };
            var errors = new List<string>();

            if (v.VendorText.Length == 0) errors.Add("Vendor Code is blank.");
            else if (v.VendorText.Length > 12) errors.Add("Vendor Code \"" + v.VendorText + "\" is longer than 12 characters.");
            if (v.FileInvoice.Length == 0) errors.Add("Invoice Number is blank.");
            else if (v.FileInvoice.Length > 20) errors.Add("Invoice number \"" + v.FileInvoice + "\" is longer than 20 characters.");
            if (v.PONbr.Length > 20) errors.Add("PO number \"" + v.PONbr + "\" is longer than 20 characters.");
            if (v.Memo.Length == 0) errors.Add("Voucher Reference is required.");

            v.VoucherDate = RequiredDate(r["VoucherDate"], "Voucher Date", excel, errors) ?? DateTime.MinValue;
            v.InvoiceDate = RequiredDate(r["InvoiceDate"], "Invoice Date", excel, errors) ?? DateTime.MinValue;
            v.DueDate = OptionalDate(r["DueDate"], "Due Date", excel, errors);
            v.DiscDate = OptionalDate(r["DiscountDate"], "Discount Date", excel, errors);

            var amount = Amount(r["InvoiceAmt"], "Invoice Amount", errors);
            if (amount == null) { if (r["InvoiceAmt"].Length == 0) errors.Add("Invoice Amount is blank."); }
            else if (amount.Value == 0) errors.Add("Invoice Amount is zero.");
            v.InvoiceAmt = amount ?? 0;
            v.NonDiscAmt = Amount(r["NonDiscAmt"], "Non-Discountable Amount", errors) ?? 0;

            var sep = r["SeparateCheck"].ToUpperInvariant();
            if (sep.Length == 0 || sep == "NO") sep = "N";
            if (sep == "YES") sep = "Y";
            if (sep != "Y" && sep != "N") errors.Add("Separate Check \"" + r["SeparateCheck"] + "\" must be Yes, No (or Y, N) or blank.");
            v.SeparateCheck = sep;

            if (type == "T")
            {
                if (v.TrustBankText.Length == 0) errors.Add("Trust Bank is blank. A trust voucher needs the trust bank account it is paid from.");
                if (v.ClientText.Length == 0 || v.MatterText.Length == 0) errors.Add("A trust voucher needs the trust matter in Client Code and Matter Code.");
            }

            v.HeaderParsed = errors.Count == 0;
            Reject(v, r.Row, errors, check);
            return v;
        }

        private static void ParseGl(SourceRow r, ImportVoucher v, ImportCheck check)
        {
            var errors = new List<string>();
            MatchHeader(r, v, errors);
            var line = new GlLine { Row = r.Row, AccountText = r["GLDistAcct"] };
            if (line.AccountText.Length == 0) errors.Add("GL Distribution Account is blank.");
            var amount = Amount(r["GLAmt"], "GL Amount", errors);
            if (amount == null && r["GLAmt"].Length == 0) errors.Add("GL Amount is blank.");
            line.Amount = amount ?? 0;
            v.GlLines.Add(line);
            Reject(v, r.Row, errors, check);
        }

        private static void ParseExpense(SourceRow r, ImportVoucher v, bool excel, ImportCheck check)
        {
            var errors = new List<string>();
            MatchHeader(r, v, errors);
            var line = new ExpenseLine
            {
                Row = r.Row,
                ClientText = r["ExpClient"],
                MatterText = r["ExpMatter"],
                ExpCodeText = r["ExpCode"],
                TaskText = r["ExpTaskCode"],
                IncurredByText = r["IncurredBy"],
                Narrative = r["ExpNarrative"],
                BillNote = r["ExpBillNote"],
            };
            if (line.ClientText.Length == 0 || line.MatterText.Length == 0) errors.Add("Client Code and Matter Code are required on an expense line (E).");
            if (line.ExpCodeText.Length == 0) errors.Add("Expense Code is blank.");
            line.Units = Amount(r["ExpUnits"], "Expense Units", errors) ?? 0;
            line.Amount = Amount(r["ExpAmount"], "Expense Amount", errors) ?? 0;
            v.ExpenseLines.Add(line);
            Reject(v, r.Row, errors, check);
        }

        /// <summary>G/E rows must carry their own voucher's vendor and invoice number (the original attached mismatches to the wrong voucher).</summary>
        private static void MatchHeader(SourceRow r, ImportVoucher v, List<string> errors)
        {
            if (Pad12(r["VendorCode"]) != Pad12(v.VendorText))
                errors.Add("Vendor Code \"" + r["VendorCode"] + "\" doesn't match its voucher row (row " + v.Row + ", vendor " + v.VendorText + ").");
            if (!string.Equals(r["InvoiceNbr"], v.FileInvoice, StringComparison.OrdinalIgnoreCase))
                errors.Add("Invoice Number \"" + r["InvoiceNbr"] + "\" doesn't match its voucher row (row " + v.Row + ", invoice " + v.FileInvoice + ").");
            if (v.Type == "T")
                errors.Add("Trust vouchers (T) can't have GL lines (G) or expense lines (E).");
        }

        private static void Reject(ImportVoucher v, int row, List<string> errors, ImportCheck check)
        {
            foreach (var e in errors)
                check.AddProblem(row, v.Describe, e);
            if (errors.Count > 0) v.Rejected = true;
        }

        // ------------------------------------------------------------------ values

        public static string Pad12(string code)
        {
            var padded = "000000000000" + (code ?? "").Trim();
            return padded.Substring(padded.Length - 12).ToUpperInvariant();
        }

        private static DateTime? RequiredDate(string text, string label, bool excel, List<string> errors)
        {
            if (text.Length == 0)
            {
                errors.Add(label + " is blank.");
                return null;
            }
            return OptionalDate(text, label, excel, errors);
        }

        private static DateTime? OptionalDate(string text, string label, bool excel, List<string> errors)
        {
            if (text.Length == 0) return null;
            var date = ParseDate(text, excel);
            if (date == null) errors.Add(label + " " + text + " is not a valid date (use mm/dd/yyyy).");
            return date;
        }

        private static readonly string[] DateFormats =
        {
            "M/d/yyyy", "M/d/yy", "M-d-yyyy", "M-d-yy", "yyyy-M-d", "M/d/yyyy H:mm", "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy h:mm tt", "yyyy-M-d H:mm:ss", "yyyy-M-dTH:mm:ss",
        };

        public static DateTime? ParseDate(string text, bool excel)
        {
            DateTime date;
            if (DateTime.TryParseExact(text, DateFormats, Us, DateTimeStyles.AllowWhiteSpaces, out date))
                return InRange(date.Date);
            // .xls through Excel returns date cells as serial numbers.
            if (excel && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial >= 1 && serial < 2958466)
                return InRange(DateTime.FromOADate(serial).Date);
            return null;
        }

        private static DateTime? InRange(DateTime date) => date.Year >= 1900 && date.Year <= 2199 ? date : (DateTime?)null;

        /// <summary>Blank -> null (no error). Not a number, or more than 2 decimal places -> error.</summary>
        private static decimal? Amount(string text, string label, List<string> errors)
        {
            if (text.Length == 0) return null;
            if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses
                                        | NumberStyles.AllowExponent, Us, out var value))
            {
                errors.Add(label + " " + text + " is not a number.");
                return null;
            }
            var rounded = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded - value) > 0.000000001m)
            {
                errors.Add(label + " " + text + " has more than 2 decimal places.");
                return null;
            }
            if (Math.Abs(rounded) >= 10000000000m)
            {
                errors.Add(label + " " + text + " is too large.");
                return null;
            }
            return rounded;
        }

        // ------------------------------------------------------------------ files

        private static List<SourceRow> ReadCsv(string path)
        {
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.Default, true))
                text = reader.ReadToEnd();
            var records = SpreadsheetReader.ParseCsv(text);
            if (records.Count == 0) throw new InvalidDataException("The file is empty.");
            var map = HeaderMap(records[0]);
            var rows = new List<SourceRow>();
            for (var i = 1; i < records.Count; i++)
            {
                if (records[i].All(string.IsNullOrWhiteSpace)) continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in map)
                    values[h.Key] = h.Value < records[i].Count ? records[i][h.Value] : "";
                rows.Add(new SourceRow { Row = i + 1, Values = values });
            }
            return rows;
        }

        private static List<SourceRow> ReadExcel(string path)
        {
            DataTable table;
            try
            {
                table = SpreadsheetReader.Read(path, "Sheet1");
            }
            catch (Exception)
            {
                table = SpreadsheetReader.Read(path); // no "Sheet1": use the first sheet
            }
            var map = HeaderMap(table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList());
            var rows = new List<SourceRow>();
            for (var index = 0; index < table.Rows.Count; index++)
            {
                var dr = table.Rows[index];
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in map)
                    values[h.Key] = Convert.ToString(dr[h.Value]);
                rows.Add(new SourceRow { Row = SpreadsheetReader.SourceRow(table, index), Values = values });
            }
            return rows;
        }

        /// <summary>
        /// Original header name -> column index, from row 1 holding either the original or the plain header names (any order,
        /// case, spacing). Throws when any of the 25 columns is missing.
        /// </summary>
        private static Dictionary<string, int> HeaderMap(List<string> header)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Count; c++)
            {
                if (Aliases.TryGetValue(Normalize(header[c]), out var name) && !map.ContainsKey(name)) map[name] = c;
            }
            var missing = Headers.Where(h => !map.ContainsKey(h)).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException("The file is missing these columns: " + string.Join(", ", missing.Select(Col))
                    + ". Row 1 must hold all 25 column headings from the template, in any order. The older headings (Type, VoucherDate, VendorCode, ... ExpBillNote) are accepted too.");
            return map;
        }
    }
}
