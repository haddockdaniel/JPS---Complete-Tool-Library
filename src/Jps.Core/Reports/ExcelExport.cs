using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Jps.Core.Reports
{
    /// <summary>
    /// The one export every report uses (replaces each utility's own ReportDisplay export).
    /// Opens the rows in a new, visible Excel workbook: headers bold, text columns kept as text so codes like
    /// "0012" keep their zeros, dates as real dates, columns auto-fit. Talks to the user's installed Excel late-bound,
    /// so no Office interop assembly ships with the app. Falls back to CSV when Excel isn't installed.
    /// </summary>
    public static class ExcelExport
    {
        public static bool IsExcelInstalled => Type.GetTypeFromProgID("Excel.Application") != null;

        public static void Open(DataTable table, string sheetTitle)
        {
            var type = Type.GetTypeFromProgID("Excel.Application")
                       ?? throw new InvalidOperationException("Excel isn't installed on this PC. Use Save as CSV instead.");
            var columns = table.Columns.Count;
            var rows = table.Rows.Count;

            var values = new object[rows + 1, Math.Max(columns, 1)];
            for (var c = 0; c < columns; c++)
                values[0, c] = table.Columns[c].ColumnName;
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                {
                    var value = table.Rows[r][c];
                    values[r + 1, c] = value == DBNull.Value ? null
                        : value is DateTime date ? date.ToOADate()
                        : value is bool flag ? (flag ? "Y" : "N")
                        : value is Guid || value is byte[] ? Convert.ToString(value)
                        : value;
                }

            dynamic excel = Activator.CreateInstance(type);
            try
            {
                excel.ScreenUpdating = false;
                dynamic book = excel.Workbooks.Add();
                dynamic sheet = book.Worksheets[1];
                sheet.Name = SafeSheetName(sheetTitle);
                for (var c = 0; c < columns; c++)
                {
                    dynamic column = sheet.Columns[c + 1];
                    var dataType = table.Columns[c].DataType;
                    if (dataType == typeof(string) || dataType == typeof(Guid) || dataType == typeof(bool))
                        column.NumberFormat = "@";
                    else if (dataType == typeof(DateTime))
                        column.NumberFormat = "mm/dd/yyyy";
                    else if (dataType == typeof(decimal))
                        column.NumberFormat = "#,##0.00";
                }
                dynamic range = sheet.Range(sheet.Cells[1, 1], sheet.Cells[rows + 1, Math.Max(columns, 1)]);
                range.Value2 = values;
                sheet.Rows[1].Font.Bold = true;
                sheet.Columns.AutoFit();
                excel.ActiveWindow.SplitRow = 1;
                excel.ActiveWindow.FreezePanes = true;
                excel.ScreenUpdating = true;
                excel.Visible = true;
                excel.UserControl = true; // the user owns the workbook now; Excel stays open after we release it
            }
            catch
            {
                try { excel.DisplayAlerts = false; excel.Quit(); } catch (COMException) { }
                throw;
            }
            finally
            {
                Marshal.FinalReleaseComObject(excel);
            }
        }

        public static void SaveCsv(DataTable table, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(c => Quote(c.ColumnName))));
            foreach (DataRow row in table.Rows)
                sb.AppendLine(string.Join(",", row.ItemArray.Select(v =>
                    Quote(v == DBNull.Value ? "" : v is DateTime d ? d.ToString(d.TimeOfDay == TimeSpan.Zero ? "MM/dd/yyyy" : "MM/dd/yyyy HH:mm:ss") : Convert.ToString(v)))));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Quote(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || s.StartsWith(" ") || s.EndsWith(" ") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private static string SafeSheetName(string title)
        {
            var name = new string((title ?? "Report").Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
            if (name.Length == 0) name = "Report";
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }
    }
}
