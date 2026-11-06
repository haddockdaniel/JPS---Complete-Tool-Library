using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Common
{
    /// <summary>
    /// A connection-scoped temp table (e.g. #JpsMatterIds) holding one int key column, filled with parameterised inserts
    /// (500 rows per statement), so any number of keys can be used in SQL without concatenating values or hitting SQL
    /// Server's 2,100-parameter limit. <see cref="With{T}"/> runs inside Session.InTransaction (joining an outer one), so
    /// nothing else uses the shared connection meanwhile; the table is dropped afterwards (or rolled away with the
    /// transaction). The table name must be a constant chosen by the tool, never user text.
    /// Don't nest two calls that use the same table name: the inner one would replace and then drop the outer's table.
    /// </summary>
    public static class TempKeyTable
    {
        public static T With<T>(JurisSession session, string table, string column, IEnumerable<int> keys, Func<T> work)
        {
            var ids = keys.Distinct().ToList();
            return session.InTransaction(() =>
            {
                // Created without parameters so it is a plain batch: a #table made inside sp_executesql vanishes when that call ends.
                session.Execute("if object_id('tempdb.." + table + "') is not null drop table " + table +
                                "; create table " + table + " (" + column + " int not null primary key)");
                for (var start = 0; start < ids.Count; start += 500)
                {
                    var parameters = new List<SqlParameter>();
                    var values = new List<string>();
                    foreach (var id in ids.Skip(start).Take(500))
                    {
                        var name = "@k" + parameters.Count;
                        parameters.Add(Sql.P(name, id));
                        values.Add("(" + name + ")");
                    }
                    session.Execute("insert into " + table + " (" + column + ") values " + string.Join(",", values), parameters.ToArray());
                }
                var result = work();
                session.Execute("drop table " + table);
                return result;
            });
        }
    }
}
