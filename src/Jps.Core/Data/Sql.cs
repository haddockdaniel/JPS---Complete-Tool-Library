using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace Jps.Core.Data
{
    /// <summary>Small helpers so tools never build SQL from user or spreadsheet text.</summary>
    public static class Sql
    {
        public static SqlParameter P(string name, object value) => new SqlParameter(name, value ?? DBNull.Value);

        /// <summary>
        /// Builds "(@x0,@x1,...)" plus the matching parameters, for "where Code in ..." with a user's selection.
        /// An empty selection yields "(NULL)", which matches nothing.
        /// </summary>
        public static string InList(string prefix, IEnumerable<object> values, List<SqlParameter> parameters)
        {
            var names = new List<string>();
            foreach (var value in values)
            {
                var name = "@" + prefix + names.Count;
                names.Add(name);
                parameters.Add(P(name, value));
            }
            return names.Count == 0 ? "(NULL)" : "(" + string.Join(",", names) + ")";
        }

        /// <summary>
        /// Passes any number of integer ids as ONE xml parameter ("&lt;i&gt;1&lt;/i&gt;&lt;i&gt;2&lt;/i&gt;"), so there is no
        /// 2,100-parameter limit and nothing is concatenated into the SQL. Duplicates are removed; an empty list matches nothing.
        /// Use it with <see cref="IdSelect"/>: <c>where MatSysNbr in " + Sql.IdSelect("@Mats")</c>.
        /// </summary>
        public static SqlParameter IdList(string name, IEnumerable<int> ids) =>
            new SqlParameter(name, SqlDbType.Xml)
            {
                Value = string.Concat((ids ?? Enumerable.Empty<int>()).Distinct()
                    .Select(i => "<i>" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</i>")),
            };

        /// <summary>
        /// "(select ... as Id from @name.nodes(...))": the ids of an <see cref="IdList"/> parameter, for "in" lists and joins.
        /// .nodes() needs a nested-loop apply, so a statement with <c>option (hash join)</c> must first copy the ids into a
        /// table variable (<c>insert @k select ... from</c> this) and use that instead.
        /// </summary>
        public static string IdSelect(string name) => "(select x.i.value('.', 'int') as Id from " + name + ".nodes('/i') x(i))";

        public static string Text(DataRow row, string column) => row[column] == DBNull.Value ? "" : Convert.ToString(row[column]).Trim();

        public static IEnumerable<DataRow> Rows(this DataTable table) => table.Rows.Cast<DataRow>();
    }
}
