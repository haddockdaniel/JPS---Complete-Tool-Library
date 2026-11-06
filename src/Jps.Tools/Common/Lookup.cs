using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Common
{
    /// <summary>One pickable thing (client, matter, timekeeper, code...). Key is what the SQL uses; Code/Name are what people see.</summary>
    public sealed class LookupItem : Observable
    {
        private bool _isChecked;

        public LookupItem(object key, string code, string name)
        {
            Key = key;
            Code = code ?? "";
            Name = name ?? "";
        }

        public object Key { get; }
        public string Code { get; }
        public string Name { get; }
        public string Display => Name.Length == 0 ? Code : Code.Length == 0 ? Name : Code + "  ·  " + Name;
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
        public override string ToString() => Display;
    }

    public static class Lookup
    {
        /// <summary>
        /// Runs a lookup query. Column 0 = key; column 1 = code; column 2 (optional) = name.
        /// With only two columns, column 0 is both key and code and column 1 is the name.
        /// </summary>
        public static List<LookupItem> Load(JurisSession session, string sql, params SqlParameter[] parameters)
        {
            var table = session.Query(sql, parameters);
            var three = table.Columns.Count >= 3;
            return table.Rows.Cast<DataRow>().Select(r => three
                    ? new LookupItem(r[0], Sql.Text(r, table.Columns[1].ColumnName), Sql.Text(r, table.Columns[2].ColumnName))
                    : new LookupItem(r[0], Sql.Text(r, table.Columns[0].ColumnName), table.Columns.Count > 1 ? Sql.Text(r, table.Columns[1].ColumnName) : ""))
                .ToList();
        }
    }
}
