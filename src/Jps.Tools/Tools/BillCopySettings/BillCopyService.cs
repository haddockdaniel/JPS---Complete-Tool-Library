using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using Jps.Core.Data;
using Jps.Core.Import;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.BillCopySettings
{
    public enum BillCopyMode { BillingTimekeeper, OriginatingTimekeeper, Client, Template }

    /// <summary>The settings to write. Null = leave that column unchanged.</summary>
    public sealed class BillCopyValues
    {
        public static readonly string[] FormatNames = { "None", "XLS", "HTML", "Text", "RTF", "PDF", "TIFF", "LEDES" };
        /// <summary>Email accepts None..PDF only (TIFF and LEDES aren't email formats; the original template refused them too).</summary>
        public const int MaxEmailFormat = 5;

        public short? Copies { get; set; }
        public int? PrintBill { get; set; }
        public int? PrintAR { get; set; }
        public int? Email { get; set; }
        public int? Export { get; set; }
        public string Comment { get; set; }

        public bool IsEmpty => Copies == null && PrintBill == null && PrintAR == null && Email == null && Export == null && Comment == null;

        /// <summary>"col = @p, ..." in the original's order, adding the parameters to <paramref name="parameters"/>.</summary>
        public string SetList(List<SqlParameter> parameters)
        {
            var set = new List<string>();
            void Add(string column, string name, SqlDbType type, object value, int size = 0)
            {
                set.Add(column + " = " + name);
                var p = new SqlParameter(name, type) { Value = value };
                if (size > 0) p.Size = size;
                parameters.Add(p);
            }
            if (Copies != null) Add("BilCpyNbrOfCopies", "@Copies", SqlDbType.SmallInt, Copies.Value);
            if (PrintBill != null) Add("BilCpyPrintFormat", "@Print", SqlDbType.Int, PrintBill.Value);
            if (PrintAR != null) Add("BilCpyARFormat", "@PrintAR", SqlDbType.Int, PrintAR.Value);
            if (Email != null) Add("BilCpyEmailFormat", "@Email", SqlDbType.Int, Email.Value);
            if (Export != null) Add("BilCpyExportFormat", "@Export", SqlDbType.Int, Export.Value);
            if (Comment != null) Add("BilCpyComment", "@Comment", SqlDbType.VarChar, Comment, 30);
            return string.Join(", ", set);
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (Copies != null) parts.Add("copies " + Copies);
            if (PrintBill != null) parts.Add("print bill " + YesNo(PrintBill));
            if (PrintAR != null) parts.Add("print A/R statement " + YesNo(PrintAR));
            if (Email != null) parts.Add("email " + FormatNames[Email.Value]);
            if (Export != null) parts.Add("export " + FormatNames[Export.Value]);
            if (Comment != null) parts.Add("comment \"" + Comment + "\"");
            return string.Join(", ", parts);
        }

        public static string YesNo(int? value) => value == null ? "" : value == 1 ? "Yes" : "No";
        public static string Format(int? value) => value == null ? "" : FormatNames[value.Value];
    }

    /// <summary>Which matters a manual (non-template) run covers. TimekeeperId/ClientIds null = "all".</summary>
    public sealed class MatterSelection
    {
        public BillCopyMode Mode { get; set; }
        public int? TimekeeperId { get; set; }
        public IReadOnlyList<int> ClientIds { get; set; }
        public string Description { get; set; }
    }

    /// <summary>One validated template row.</summary>
    public sealed class TemplateRow
    {
        public int Line { get; set; }
        public string ClientCode { get; set; }
        public string MatterCode { get; set; }
        public string Type { get; set; }
        public List<int> BillTos { get; set; } = new List<int>();
        public BillCopyValues Values { get; set; } = new BillCopyValues();
    }

    public sealed class TemplateCheck
    {
        public List<TemplateRow> Rows { get; } = new List<TemplateRow>();
        public DataTable Errors { get; } = NewErrorTable();
        public int ErrorCount => Errors.Rows.Count;

        private static DataTable NewErrorTable()
        {
            var t = new DataTable();
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Row", typeof(int));
            t.Columns.Add("Problem", typeof(string));
            return t;
        }
    }

    public sealed class BillCopyService
    {
        private readonly JurisSession _session;

        public BillCopyService(JurisSession session)
        {
            _session = session;
        }

        // ---------- lookups ----------

        public List<LookupItem> LoadTimekeepers() =>
            Lookup.Load(_session, "select EmpSysNbr, EmpID, EmpName from Employee order by EmpID");

        public List<LookupItem> LoadClients() =>
            Lookup.Load(_session, "select CliSysNbr, dbo.jfn_FormatClientCode(CliCode), CliNickName from Client order by CliCode");

        // ---------- manual modes ----------

        /// <summary>
        /// "where" text over Matter M for the selection. Closed matters are always excluded (every mode, so the timekeeper modes
        /// now match the client mode). Clients are passed as one XML parameter, so any number of clients works.
        /// </summary>
        private static string MatterFilter(MatterSelection selection, List<SqlParameter> parameters)
        {
            const string open = "M.MatStatusFlag <> 'C'";
            switch (selection.Mode)
            {
                case BillCopyMode.BillingTimekeeper:
                    if (selection.TimekeeperId == null)
                        return open + " and M.MatBillTo in (select BillToSysNbr from BillTo where BillToBillingAtty in (select EmpSysNbr from Employee))";
                    parameters.Add(Sql.P("@Tkpr", selection.TimekeeperId.Value));
                    return open + " and M.MatBillTo in (select BillToSysNbr from BillTo where BillToBillingAtty = @Tkpr)";
                case BillCopyMode.OriginatingTimekeeper:
                    if (selection.TimekeeperId == null)
                        return open + " and M.MatSysNbr in (select MOrigMat from MatOrigAtty where MOrigAtty in (select EmpSysNbr from Employee))";
                    parameters.Add(Sql.P("@Tkpr", selection.TimekeeperId.Value));
                    return open + " and M.MatSysNbr in (select MOrigMat from MatOrigAtty where MOrigAtty = @Tkpr)";
                case BillCopyMode.Client:
                    if (selection.ClientIds == null)
                        return open;
                    parameters.Add(Sql.IdList("@Clients", selection.ClientIds));
                    return open + " and M.MatCliNbr in " + Sql.IdSelect("@Clients");
                default:
                    throw new InvalidOperationException("Template rows are not a matter selection.");
            }
        }

        /// <summary>Pre-report: the matters selected, with how many other matters share each bill-to (they change too).</summary>
        public DataTable MattersToChange(MatterSelection selection)
        {
            var parameters = new List<SqlParameter>();
            var where = MatterFilter(selection, parameters);
            return _session.Query(
                @"select dbo.jfn_FormatClientCode(C.CliCode) as [Client Code], C.CliReportingName as [Client Name],
                         dbo.jfn_FormatMatterCode(M.MatCode) as [Matter Code], M.MatReportingName as [Matter Name],
                         (select BT.BillToNickName from BillTo BT where BT.BillToSysNbr = M.MatBillTo) as [Bill-To],
                         (select count(*) from Matter M2 where M2.MatBillTo = M.MatBillTo) - count(*) over (partition by M.MatBillTo)
                             as [Other Matters Sharing This Bill-To]
                  from Matter M inner join Client C on M.MatCliNbr = C.CliSysNbr
                  where " + where + @"
                  order by C.CliCode, M.MatCode", parameters.ToArray());
        }

        /// <summary>Updates every BillCopy row of every bill-to of the selected matters, in one transaction. Returns rows changed.</summary>
        public int ApplySelection(MatterSelection selection, BillCopyValues values, int matterCount)
        {
            return _session.InTransaction(() =>
            {
                var parameters = new List<SqlParameter>();
                var set = values.SetList(parameters);
                var where = MatterFilter(selection, parameters);
                var changed = _session.ExecuteRows(
                    "update BC set " + set + " from BillCopy BC where BC.BilCpyBillTo in (select M.MatBillTo from Matter M where " + where + ")",
                    parameters.ToArray());
                _session.WriteUtilityLog(Truncate("JPS Update Bill Copy Settings: " + selection.Description + ", " + matterCount
                    + " matter(s), " + changed + " bill copy row(s): " + values.Describe()));
                return changed;
            });
        }

        // ---------- template ----------

        private static readonly Dictionary<string, int> YesNoValues = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Yes"] = 1, ["Y"] = 1, ["1"] = 1, ["No"] = 0, ["N"] = 0, ["0"] = 0,
        };

        /// <summary>Reads and validates every row of the template. Nothing is written.</summary>
        public TemplateCheck CheckTemplate(string path)
        {
            var fileRows = ReadTemplateFile(path);
            var check = new TemplateCheck();
            if (fileRows.Count == 0)
                return check;

            // Load the lookups once instead of querying per row; compare the way SQL Server does (case-insensitive, trimmed).
            var clients = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _session.Query("select CliSysNbr, dbo.jfn_FormatClientCode(CliCode) as Code from Client").Rows())
                clients[Sql.Text(r, "Code")] = Convert.ToInt32(r["CliSysNbr"]);

            var matters = new Dictionary<int, List<Tuple<string, int, bool>>>(); // client -> (formatted code, bill-to, closed)
            foreach (var r in _session.Query(
                         "select MatCliNbr, dbo.jfn_FormatMatterCode(MatCode) as Code, MatBillTo, MatStatusFlag from Matter").Rows())
            {
                var cli = Convert.ToInt32(r["MatCliNbr"]);
                if (!matters.TryGetValue(cli, out var list)) matters[cli] = list = new List<Tuple<string, int, bool>>();
                list.Add(Tuple.Create(Sql.Text(r, "Code"), Convert.ToInt32(r["MatBillTo"]), Sql.Text(r, "MatStatusFlag") == "C"));
            }

            var clientBillTos = _session.Query("select BillToCliNbr, BillToSysNbr from BillTo where BillToUsageFlg = 'C'").Rows()
                .GroupBy(r => Convert.ToInt32(r["BillToCliNbr"]))
                .ToDictionary(g => g.Key, g => g.Select(r => Convert.ToInt32(r["BillToSysNbr"])).Distinct().ToList());

            foreach (var fileRow in fileRows)
            {
                var errors = new List<string>();
                var row = new TemplateRow
                {
                    Line = fileRow.Line,
                    ClientCode = fileRow[0],
                    MatterCode = fileRow[1],
                    Type = ApplyToCode(fileRow[2]),
                };

                // A, B, C: which bill-tos.
                int? cliId = null;
                if (row.ClientCode.Length == 0)
                    errors.Add("Client Code is required");
                else if (clients.TryGetValue(row.ClientCode, out var id))
                    cliId = id;
                else if (clients.Where(c => SameCode(c.Key, row.ClientCode)).Select(c => (int?)c.Value).ToList() is var near && near.Count == 1)
                    cliId = near[0];   // e.g. "1234" typed for 001234: Excel drops leading zeros from numeric cells
                else
                    errors.Add("No client with this Client Code was found in Juris");

                var clientMatters = cliId != null && matters.TryGetValue(cliId.Value, out var ml) ? ml : new List<Tuple<string, int, bool>>();
                if (row.MatterCode.Length > 0)
                {
                    if (cliId != null)
                    {
                        var matter = clientMatters.FirstOrDefault(m => string.Equals(m.Item1, row.MatterCode, StringComparison.OrdinalIgnoreCase))
                                     ?? clientMatters.FirstOrDefault(m => SameCode(m.Item1, row.MatterCode));
                        if (matter == null)
                            errors.Add("This client has no matter with this Matter Code");
                        else if (matter.Item3)
                            errors.Add("This matter is closed, so its bill copy settings can't be changed");
                        else
                            row.BillTos.Add(matter.Item2);
                    }
                }
                else if (row.Type != "C" && row.Type != "A")
                    errors.Add("Apply To must be " + ClientBillTos + " or " + AllMatters + " when Matter Code is blank");
                else if (cliId != null)
                {
                    row.BillTos = row.Type == "C"
                        ? (clientBillTos.TryGetValue(cliId.Value, out var bt) ? bt : new List<int>())
                        : clientMatters.Where(m => !m.Item3).Select(m => m.Item2).Distinct().ToList();
                    if (row.BillTos.Count == 0)
                        errors.Add(row.Type == "C" ? "This client has no client bill-tos" : "This client has no open matters");
                }

                // D..I: the settings. Blank always means "leave unchanged".
                var copies = fileRow[3];
                if (copies.Length > 0)
                {
                    if (short.TryParse(copies, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                        row.Values.Copies = n;
                    else
                        errors.Add("Number of Copies must be a whole number from 0 to 32767, or blank");
                }
                row.Values.PrintBill = ParseChoice(fileRow[4], YesNoValues, "Print Bill must be Yes, No or blank", errors);
                row.Values.PrintAR = ParseChoice(fileRow[5], YesNoValues, "Print A/R Statement must be Yes, No or blank", errors);
                row.Values.Email = ParseFormat(fileRow[6], BillCopyValues.MaxEmailFormat, "Email Format must be None, XLS, HTML, Text, RTF, PDF or blank", errors);
                row.Values.Export = ParseFormat(fileRow[7], BillCopyValues.FormatNames.Length - 1, "Export Format must be None, XLS, HTML, Text, RTF, PDF, TIFF, LEDES or blank", errors);
                var comment = fileRow[8];
                if (comment.Length > 30)
                    errors.Add("Comment can't be longer than 30 characters");
                else if (comment.Length > 0)
                    row.Values.Comment = comment;

                if (copies.Length == 0 && fileRow[4].Length == 0 && fileRow[5].Length == 0 && fileRow[6].Length == 0
                    && fileRow[7].Length == 0 && comment.Length == 0)
                    errors.Add("Nothing to change: fill in at least one of Number of Copies, Print Bill, Print A/R Statement, Email Format, Export Format or Comment");

                if (errors.Count > 0)
                    check.Errors.Rows.Add(row.ClientCode, row.Line, string.Join("; ", errors));
                else
                    check.Rows.Add(row);
            }
            return check;
        }

        // "Apply To" (column C, formerly "Type"): the plain words, or the original C / A codes.
        public const string ClientBillTos = "Client Bill-Tos";
        public const string AllMatters = "All Matters";

        private static string ApplyToCode(string text)
        {
            var t = text.Trim();
            if (t.Equals("C", StringComparison.OrdinalIgnoreCase) || t.Equals(ClientBillTos, StringComparison.OrdinalIgnoreCase)
                || t.Equals("Client Bill-To", StringComparison.OrdinalIgnoreCase) || t.Equals("Client", StringComparison.OrdinalIgnoreCase))
                return "C";
            if (t.Equals("A", StringComparison.OrdinalIgnoreCase) || t.Equals(AllMatters, StringComparison.OrdinalIgnoreCase)
                || t.Equals("All Open Matters", StringComparison.OrdinalIgnoreCase))
                return "A";
            return t.ToUpperInvariant();
        }

        private static int? ParseChoice(string text, Dictionary<string, int> allowed, string error, List<string> errors)
        {
            if (text.Length == 0) return null;
            if (allowed.TryGetValue(text, out var value)) return value;
            errors.Add(error);
            return null;
        }

        private static int? ParseFormat(string text, int maxIndex, string error, List<string> errors)
        {
            if (text.Length == 0) return null;
            for (var i = 0; i <= maxIndex; i++)
                if (string.Equals(BillCopyValues.FormatNames[i], text, StringComparison.OrdinalIgnoreCase))
                    return i;
            errors.Add(error);
            return null;
        }

        public static DataTable TemplatePreReport(IEnumerable<TemplateRow> rows)
        {
            var t = new DataTable();
            t.Columns.Add("Row", typeof(int));
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Apply To", typeof(string));
            t.Columns.Add("Bill-Tos Changed", typeof(int));
            t.Columns.Add("Number of Copies", typeof(string));
            t.Columns.Add("Print Bill", typeof(string));
            t.Columns.Add("Print A/R Statement", typeof(string));
            t.Columns.Add("Email Format", typeof(string));
            t.Columns.Add("Export Format", typeof(string));
            t.Columns.Add("Comment", typeof(string));
            foreach (var r in rows)
                t.Rows.Add(r.Line, r.ClientCode, r.MatterCode, r.MatterCode.Length > 0 ? "This matter's bill-to" : r.Type == "C" ? ClientBillTos : AllMatters, r.BillTos.Count,
                    r.Values.Copies?.ToString(CultureInfo.InvariantCulture) ?? "", BillCopyValues.YesNo(r.Values.PrintBill),
                    BillCopyValues.YesNo(r.Values.PrintAR), BillCopyValues.Format(r.Values.Email), BillCopyValues.Format(r.Values.Export),
                    r.Values.Comment ?? "");
            return t;
        }

        /// <summary>Applies every template row, in file order, in one transaction. Returns BillCopy rows changed.</summary>
        public int ApplyTemplate(IReadOnlyList<TemplateRow> rows, string fileName)
        {
            return _session.InTransaction(() =>
            {
                var changed = 0;
                foreach (var row in rows)
                {
                    var parameters = new List<SqlParameter>();
                    var set = row.Values.SetList(parameters);
                    parameters.Add(Sql.IdList("@BillTos", row.BillTos));
                    changed += _session.ExecuteRows("update BillCopy set " + set + " where BilCpyBillTo in " + Sql.IdSelect("@BillTos"), parameters.ToArray());
                }
                _session.WriteUtilityLog(Truncate("JPS Update Bill Copy Settings: imported " + rows.Count + " template row(s) from "
                    + fileName + ", " + changed + " bill copy row(s) updated."));
                return changed;
            });
        }

        // ---------- helpers ----------

        /// <summary>One data row of the template file with the row number the user sees (sheet row or CSV line; header = 1).</summary>
        private sealed class FileRow
        {
            private readonly DataRow _row;
            private readonly int _firstColumn;

            public FileRow(int line, DataRow row, int firstColumn)
            {
                Line = line;
                _row = row;
                _firstColumn = firstColumn;
            }

            public int Line { get; }

            /// <summary>Trimmed cell text by position from the first used column; missing cells are blank.</summary>
            public string this[int index] =>
                _firstColumn + index < _row.Table.Columns.Count ? Convert.ToString(_row[_firstColumn + index]).Trim() : "";
        }

        /// <summary>
        /// The template's data rows: first sheet by position, columns counted from the first used column (Excel's used
        /// range, as the original read it; CSV columns are taken as they are), blank rows skipped, and reading stops once
        /// more than 20 blank rows follow each other.
        /// </summary>
        private static List<FileRow> ReadTemplateFile(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".csv" && ext != ".xlsx")
                throw new System.IO.InvalidDataException("This kind of file can't be read. Save it as an Excel workbook (.xlsx) or a .csv file and choose it again.");
            var table = SpreadsheetReader.Read(path);
            var firstColumn = ext == ".csv" ? 0 : SpreadsheetReader.FirstUsedColumn(table);
            var rows = new List<FileRow>();
            var previousLine = 1; // the header
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var line = SpreadsheetReader.SourceRow(table, i);
                if (line - previousLine - 1 > MaxConsecutiveBlankRows)
                    break;
                previousLine = line;
                rows.Add(new FileRow(line, table.Rows[i], firstColumn));
            }
            return rows;
        }

        /// <summary>Numeric codes match without their leading zeros ("110" = "00110"), since Excel drops them.</summary>
        private static bool SameCode(string juris, string typed) =>
            juris.Length > 0 && typed.Length > 0 && juris.All(char.IsDigit) && typed.All(char.IsDigit)
            && juris.TrimStart('0') == typed.TrimStart('0');

        private const int MaxConsecutiveBlankRows = 20;
        private static string Truncate(string text) => text.Length <= 7900 ? text : text.Substring(0, 7900) + "…";
    }
}
