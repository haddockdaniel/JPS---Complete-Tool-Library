using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Jps.Core.Data;
using Jps.Core.Import;

namespace Jps.Tools.Tools.MatterCloser
{
    /// <summary>One data row of a client/matter spreadsheet (column A = client code, column B = matter code).</summary>
    public sealed class SheetMatterRow
    {
        public int RowNumber { get; set; }
        public string ClientCode { get; set; }
        public string MatterCode { get; set; }
        public int? CliSysNbr { get; set; }
        public int? MatSysNbr { get; set; }
        public string MatterName { get; set; }
        public string Status { get; set; }
        public string Lock { get; set; }
        /// <summary>Set when the client or matter couldn't be found. The row is then unusable.</summary>
        public string Problem { get; set; }
        /// <summary>Set when the same matter is listed on an earlier row; the earlier row is the one acted on.</summary>
        public int? DuplicateOfRow { get; set; }

        public bool IsUsable => Problem == null && MatSysNbr.HasValue && DuplicateOfRow == null;
    }

    /// <summary>The firm's client/matter code format from SysParam (FldClient / FldMatter: "x,type,length,...").</summary>
    public sealed class CodeFormat
    {
        public bool ClientNumeric { get; private set; }
        public int ClientLength { get; private set; }
        public bool MatterNumeric { get; private set; }
        public int MatterLength { get; private set; }

        public static CodeFormat Load(JurisSession session)
        {
            var format = new CodeFormat();
            Parse(Convert.ToString(session.Scalar("select SpTxtValue from SysParam where SpName = 'FldClient'"), CultureInfo.InvariantCulture),
                out var cn, out var cl);
            Parse(Convert.ToString(session.Scalar("select SpTxtValue from SysParam where SpName = 'FldMatter'"), CultureInfo.InvariantCulture),
                out var mn, out var ml);
            format.ClientNumeric = cn;
            format.ClientLength = cl;
            format.MatterNumeric = mn;
            format.MatterLength = ml;
            return format;
        }

        // Element [1] = type ('C' = character/alphanumeric, anything else numeric), element [2] = length.
        // An unreadable setting is treated as alphanumeric, so nothing is ever padded on a guess.
        private static void Parse(string value, out bool numeric, out int length)
        {
            numeric = false;
            length = 0;
            var parts = (value ?? "").Split(',');
            if (parts.Length < 3 || !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                return;
            numeric = !parts[1].Trim().Equals("C", StringComparison.OrdinalIgnoreCase);
        }

        public string PadClient(string code) => ClientNumeric && ClientLength > 0 ? code.PadLeft(ClientLength, '0') : code;
        public string PadMatter(string code) => MatterNumeric && MatterLength > 0 ? code.PadLeft(MatterLength, '0') : code;
    }

    /// <summary>
    /// Reads a client/matter spreadsheet and resolves each row to a Juris matter. Shared by Matter Closer and the Matter
    /// Status and Lock Updater. The FIRST sheet is read by position; row 1 is a header (any text); column A = client code,
    /// column B = matter code (the matter number within the client); other columns are ignored; rows with A or B blank
    /// are skipped. Codes are matched to the FORMATTED Juris codes, zero-padded first only when the firm's code format is
    /// numeric.
    /// </summary>
    public static class SpreadsheetMatters
    {
        public static List<SheetMatterRow> Read(string path)
        {
            var table = SpreadsheetReader.Read(path);
            if (table.Columns.Count < 2)
                throw new InvalidDataException("The first sheet needs the Client Code in column A and the Matter Code in column B, one matter per row starting on row 2. Use the template if you're not sure.");
            var rows = new List<SheetMatterRow>();
            for (var index = 0; index < table.Rows.Count; index++)
            {
                var row = table.Rows[index];
                var client = Convert.ToString(row[0], CultureInfo.InvariantCulture)?.Trim() ?? "";
                var matter = Convert.ToString(row[1], CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (client.Length == 0 || matter.Length == 0)
                    continue;
                rows.Add(new SheetMatterRow { RowNumber = SpreadsheetReader.SourceRow(table, index), ClientCode = client, MatterCode = matter });
            }
            return rows;
        }

        /// <summary>Looks up every row's client and matter. <paramref name="pad"/> = apply zero-padding for numeric code formats.</summary>
        public static void Resolve(JurisSession session, IList<SheetMatterRow> rows, bool pad)
        {
            var format = CodeFormat.Load(session);

            var clients = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in session.Query("select CliSysNbr, dbo.jfn_FormatClientCode(CliCode) as Code from Client").Rows())
            {
                var code = Sql.Text(r, "Code");
                if (!clients.ContainsKey(code))
                    clients[code] = Convert.ToInt32(r["CliSysNbr"], CultureInfo.InvariantCulture);
            }

            var matters = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in session.Query(
                         "select MatSysNbr, MatCliNbr, dbo.jfn_FormatMatterCode(MatCode) as Code, MatReportingName, MatStatusFlag, MatLockFlag from Matter").Rows())
            {
                var key = Convert.ToInt32(r["MatCliNbr"], CultureInfo.InvariantCulture) + "|" + Sql.Text(r, "Code");
                if (!matters.ContainsKey(key))
                    matters[key] = r;
            }

            var seen = new Dictionary<int, int>();
            foreach (var row in rows)
            {
                var cli = Find(clients, row.ClientCode, pad ? format.PadClient(row.ClientCode) : row.ClientCode, out var clientCode);
                row.ClientCode = clientCode;
                if (cli == null)
                {
                    row.Problem = "No client with this Client Code was found in Juris";
                    continue;
                }
                row.CliSysNbr = cli;

                var paddedMatter = pad ? format.PadMatter(row.MatterCode) : row.MatterCode;
                var usedMatter = paddedMatter;
                if (!matters.TryGetValue(cli + "|" + paddedMatter, out var mat) && matters.TryGetValue(cli + "|" + row.MatterCode, out mat))
                    usedMatter = row.MatterCode;
                row.MatterCode = usedMatter;
                if (mat == null)
                {
                    row.Problem = "This client has no matter with this Matter Code in Juris";
                    continue;
                }
                var id = Convert.ToInt32(mat["MatSysNbr"], CultureInfo.InvariantCulture);
                row.MatSysNbr = id;
                row.MatterName = Sql.Text(mat, "MatReportingName");
                row.Status = Sql.Text(mat, "MatStatusFlag");
                row.Lock = Sql.Text(mat, "MatLockFlag");
                if (seen.TryGetValue(id, out var first))
                    row.DuplicateOfRow = first;
                else
                    seen[id] = row.RowNumber;
            }
        }

        // Tries the padded code first, then the code as typed (covers firms whose formatted codes aren't zero-filled).
        private static int? Find(Dictionary<string, int> clients, string raw, string padded, out string used)
        {
            used = padded;
            if (clients.TryGetValue(padded, out var id))
                return id;
            if (clients.TryGetValue(raw, out id))
            {
                used = raw;
                return id;
            }
            return null;
        }
    }
}
