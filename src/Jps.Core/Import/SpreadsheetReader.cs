using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Jps.Core.Import
{
    /// <summary>
    /// Reads import files into a DataTable of strings: .csv natively, .xlsx natively (no Excel needed),
    /// and .xls through Excel itself when it is installed. The first row is the header row.
    /// </summary>
    public static class SpreadsheetReader
    {
        public static DataTable Read(string path, string sheetName = null)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".csv":
                case ".txt":
                    return ReadCsv(path);
                case ".xlsx":
                case ".xlsm":
                    return ReadXlsx(path, sheetName);
                case ".xls":
                    return ReadWithExcel(path, sheetName);
                default:
                    throw new InvalidDataException("Use a .csv, .xlsx or .xls file.");
            }
        }

        public static string FileFilter => "Spreadsheets (*.xlsx;*.xls;*.csv)|*.xlsx;*.xlsm;*.xls;*.csv|All files (*.*)|*.*";

        // ---------- CSV ----------

        public static DataTable ReadCsv(string path)
        {
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // readable while open in Excel
            using (var reader = new StreamReader(stream, Encoding.Default, true))
                text = reader.ReadToEnd();
            return ToTable(ParseCsv(text));
        }

        public static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"': quoted = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                    default: field.Append(c); break;
                }
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        // ---------- XLSX (Office Open XML, read directly) ----------

        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static DataTable ReadXlsx(string path, string sheetName = null)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var workbook = Load(zip, "xl/workbook.xml");
                var sheets = workbook.Root.Element(Main + "sheets").Elements(Main + "sheet").ToList();
                var sheet = sheetName == null
                    ? sheets.First()
                    : sheets.FirstOrDefault(s => string.Equals((string)s.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException("The workbook has no sheet named \"" + sheetName + "\".");
                var relId = (string)sheet.Attribute(Rel + "id");
                var target = (string)Load(zip, "xl/_rels/workbook.xml.rels").Root.Elements(PackageRel + "Relationship")
                    .First(r => (string)r.Attribute("Id") == relId).Attribute("Target");
                var sheetPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;

                var shared = zip.GetEntry("xl/sharedStrings.xml") == null
                    ? new List<string>()
                    : Load(zip, "xl/sharedStrings.xml").Root.Elements(Main + "si")
                        .Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList();
                var dateStyles = DateStyleIndexes(zip);

                var rows = new List<List<string>>();
                foreach (var r in Load(zip, sheetPath).Root.Element(Main + "sheetData").Elements(Main + "row"))
                {
                    var rowIndex = (int?)r.Attribute("r") ?? rows.Count + 1;
                    while (rows.Count < rowIndex - 1) rows.Add(new List<string>());
                    var row = new List<string>();
                    foreach (var cell in r.Elements(Main + "c"))
                    {
                        var col = ColumnIndex((string)cell.Attribute("r")) ?? row.Count;
                        while (row.Count < col) row.Add("");
                        row.Add(CellText(cell, shared, dateStyles));
                    }
                    rows.Add(row);
                }
                return ToTable(rows);
            }
        }

        private static XDocument Load(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("This isn't a valid .xlsx file.");
            using (var s = entry.Open())
                return XDocument.Load(s);
        }

        private static string CellText(XElement cell, List<string> shared, HashSet<int> dateStyles)
        {
            var type = (string)cell.Attribute("t");
            if (type == "inlineStr")
                return string.Concat(cell.Descendants(Main + "t").Select(t => t.Value));
            var value = (string)cell.Element(Main + "v");
            if (value == null)
                return "";
            if (type == "s")
                return shared[int.Parse(value, CultureInfo.InvariantCulture)];
            if (type == "b")
                return value == "1" ? "TRUE" : "FALSE";
            if (type == null || type == "n")
            {
                var style = (int?)cell.Attribute("s") ?? 0;
                if (dateStyles.Contains(style) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                {
                    var date = DateTime.FromOADate(serial);
                    return date.TimeOfDay == TimeSpan.Zero ? date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : date.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                }
                // Avoid "1E-05" style output and binary noise like 0.30000000000000004.
                if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return number.ToString(CultureInfo.InvariantCulture);
            }
            return value;
        }

        /// <summary>Cell style indexes whose number format is a date, so serial numbers come back as dates.</summary>
        private static HashSet<int> DateStyleIndexes(ZipArchive zip)
        {
            var result = new HashSet<int>();
            if (zip.GetEntry("xl/styles.xml") == null)
                return result;
            var styles = Load(zip, "xl/styles.xml").Root;
            var custom = styles.Element(Main + "numFmts")?.Elements(Main + "numFmt")
                .ToDictionary(f => (int)f.Attribute("numFmtId"), f => (string)f.Attribute("formatCode")) ?? new Dictionary<int, string>();
            var xfs = styles.Element(Main + "cellXfs")?.Elements(Main + "xf").ToList() ?? new List<XElement>();
            for (var i = 0; i < xfs.Count; i++)
            {
                var id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
                var isDate = (id >= 14 && id <= 22) || (id >= 45 && id <= 47)
                    || (custom.TryGetValue(id, out var code) && IsDateFormat(code));
                if (isDate) result.Add(i);
            }
            return result;
        }

        private static bool IsDateFormat(string code)
        {
            var bare = new StringBuilder();
            var inQuote = false;
            foreach (var c in code)
            {
                if (c == '"') inQuote = !inQuote;
                else if (!inQuote) bare.Append(c);
            }
            var s = System.Text.RegularExpressions.Regex.Replace(bare.ToString(), @"\[[^\]]*\]", "").ToLowerInvariant();
            return s.IndexOfAny(new[] { 'd', 'y', 'h', 's' }) >= 0;
        }

        private static int? ColumnIndex(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            var index = 0;
            foreach (var c in reference)
            {
                if (!char.IsLetter(c)) break;
                index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            }
            return index - 1;
        }

        // ---------- XLS (legacy binary, via the user's own Excel) ----------

        private static DataTable ReadWithExcel(string path, string sheetName)
        {
            var type = Type.GetTypeFromProgID("Excel.Application")
                       ?? throw new InvalidDataException("Opening .xls files needs Excel. Save the file as .xlsx or .csv and try again.");
            dynamic excel = Activator.CreateInstance(type);
            try
            {
                excel.DisplayAlerts = false;
                dynamic book = excel.Workbooks.Open(Path.GetFullPath(path), 0, true);
                try
                {
                    dynamic sheet = sheetName == null ? book.Worksheets[1] : book.Worksheets[sheetName];
                    object[,] values = sheet.UsedRange.Value2 is object[,] grid ? grid : new object[,] { { sheet.UsedRange.Value2 } };
                    var rows = new List<List<string>>();
                    for (var r = values.GetLowerBound(0); r <= values.GetUpperBound(0); r++)
                    {
                        var row = new List<string>();
                        for (var c = values.GetLowerBound(1); c <= values.GetUpperBound(1); c++)
                            row.Add(Convert.ToString(values[r, c], CultureInfo.InvariantCulture) ?? "");
                        rows.Add(row);
                    }
                    return ToTable(rows);
                }
                finally
                {
                    book.Close(false);
                }
            }
            finally
            {
                excel.Quit();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(excel);
            }
        }

        // ---------- shared ----------

        private const string SourceRowsKey = "Jps.SourceRows";
        private const string BlankHeadersKey = "Jps.BlankHeaders";

        /// <summary>
        /// The spreadsheet/file row number (1-based, header = row 1) of each row in a table returned by <see cref="Read"/>.
        /// Blank rows are dropped when reading, so use this — not the DataTable index — in messages like "row 12: ...".
        /// </summary>
        public static int SourceRow(DataTable table, int index) =>
            table.ExtendedProperties[SourceRowsKey] is List<int> rows && index >= 0 && index < rows.Count ? rows[index] : index + 2;

        /// <summary>
        /// The index of the first column that holds anything (a header or any value), like the left edge of Excel's used
        /// range; 0 when column A is used. For templates read "relative to the first used column".
        /// </summary>
        public static int FirstUsedColumn(DataTable table)
        {
            var blankHeaders = table.ExtendedProperties[BlankHeadersKey] as HashSet<int> ?? new HashSet<int>();
            for (var c = 0; c < table.Columns.Count; c++)
                if (!blankHeaders.Contains(c) || table.Rows.Cast<DataRow>().Any(r => !string.IsNullOrWhiteSpace(Convert.ToString(r[c]))))
                    return c;
            return 0;
        }

        /// <summary>First row becomes the column names (trimmed; blanks/duplicates made unique). Fully blank rows are dropped.</summary>
        private static DataTable ToTable(List<List<string>> rows)
        {
            var table = new DataTable();
            var sourceRows = new List<int>();
            table.ExtendedProperties[SourceRowsKey] = sourceRows;
            var blankHeaders = new HashSet<int>();
            table.ExtendedProperties[BlankHeadersKey] = blankHeaders;
            if (rows.Count == 0)
                return table;
            var width = rows.Max(r => r.Count);
            var header = rows[0];
            for (var c = 0; c < width; c++)
            {
                var name = c < header.Count ? header[c].Trim() : "";
                if (name.Length == 0)
                {
                    name = "Column" + (c + 1);
                    blankHeaders.Add(c);
                }
                var unique = name;
                for (var n = 2; table.Columns.Contains(unique); n++) unique = name + n;
                table.Columns.Add(unique, typeof(string));
            }
            for (var r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.All(string.IsNullOrWhiteSpace))
                    continue;
                var dataRow = table.NewRow();
                for (var c = 0; c < width; c++)
                    dataRow[c] = c < row.Count ? row[c].Trim() : "";
                table.Rows.Add(dataRow);
                sourceRows.Add(r + 1);
            }
            return table;
        }
    }
}
