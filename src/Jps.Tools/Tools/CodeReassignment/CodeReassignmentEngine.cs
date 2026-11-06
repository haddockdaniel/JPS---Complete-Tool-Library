using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using Jps.Core.Data;

namespace Jps.Tools.Tools.CodeReassignment
{
    /// <summary>What a reassignment did.</summary>
    public sealed class CodeReassignmentResult
    {
        /// <summary>One row per table.column touched: Table, Column, Rows reassigned, Rows merged.</summary>
        public DataTable Changes { get; set; }
        public int TotalRows { get; set; }
        public bool DeleteRequested { get; set; }
        public bool Deleted { get; set; }

        /// <summary>Why the delete was blocked (remaining references, or the database's refusal). Empty when not blocked.</summary>
        public List<string> DeleteBlockers { get; } = new List<string>();

        /// <summary>Mapped columns that don't exist in this database version, so were skipped.</summary>
        public List<string> Skipped { get; } = new List<string>();
    }

    /// <summary>
    /// The shared, set-based engine behind every "reassign code X to Y" tool. Each tool supplies a
    /// <see cref="CodeReassignmentMap"/>; the engine generates the SQL from it. Identifiers come only from the map
    /// (constants); the codes, dates and notes are always parameters.
    /// </summary>
    public static class CodeReassignmentEngine
    {
        /// <summary>The map's columns that exist in the connected database (older Juris versions may lack some tables).</summary>
        public static List<CodeRef> PresentReferences(JurisSession session, CodeReassignmentMap map, List<string> skipped = null)
        {
            var present = new List<CodeRef>();
            foreach (var r in map.References)
            {
                var exists = session.Scalar("select col_length(@t, @c)", Sql.P("@t", r.Table), Sql.P("@c", r.Column)) != null
                             && (r.NoteColumn == null || session.Scalar("select col_length(@t, @c)", Sql.P("@t", r.Table), Sql.P("@c", r.NoteColumn)) != null);
                if (exists) present.Add(r);
                else skipped?.Add(r.Name);
            }
            return present;
        }

        /// <summary>Rows per table.column that still hold <paramref name="code"/> (only those with at least one row).</summary>
        public static DataTable CountReferences(JurisSession session, IReadOnlyList<CodeRef> refs, string code)
        {
            var parts = new List<string>();
            var parameters = new List<SqlParameter> { Sql.P("@Code", code) };
            for (var i = 0; i < refs.Count; i++)
            {
                parts.Add("select @n" + i + " as [Where], count(*) as [Rows] from " + Q(refs[i].Table) + " where " + Q(refs[i].Column) + " = @Code");
                parameters.Add(Sql.P("@n" + i, refs[i].Name));
            }
            if (parts.Count == 0)
                return new DataTable();
            var sql = "select [Where], [Rows] from (" + string.Join(" union all ", parts) + ") x where [Rows] > 0 order by [Where]";
            return session.Query(sql, parameters.ToArray());
        }

        /// <summary>
        /// Reassigns every reference from <paramref name="from"/> to <paramref name="to"/> and, when asked and nothing
        /// still references it, deletes the From code - all in one transaction. A blocked delete leaves the
        /// reassignment in place and reports why.
        /// </summary>
        public static CodeReassignmentResult Run(JurisSession session, CodeReassignmentMap map, string from, string to,
            bool deleteFrom, IProgress<string> progress)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                throw new InvalidOperationException("Please choose both a From and a To " + map.Noun + ".");
            if (string.Equals(from.Trim(), to.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The From and To " + map.Noun + "s must be different.");

            var result = new CodeReassignmentResult { DeleteRequested = deleteFrom };
            var changes = new DataTable();
            changes.Columns.Add("Where in Juris");
            changes.Columns.Add("Entries changed to " + to, typeof(int));
            changes.Columns.Add("Entries combined with an existing " + to + " entry", typeof(int));
            result.Changes = changes;

            session.InTransaction(() =>
            {
                // Lock both code rows until commit, so another user (or another run of this tool) can't delete or reuse them
                // between the reassignment, the remaining-reference check and the delete.
                foreach (var code in new[] { from, to })
                {
                    var n = Convert.ToInt32(session.Scalar(
                        "select count(*) from " + Q(map.CodeTable) + " with (updlock, holdlock) where " + Q(map.CodeColumn) + " = @c", Sql.P("@c", code)));
                    if (n == 0)
                        throw new InvalidOperationException(map.Noun + " " + code + " no longer exists. Reload the page and choose again.");
                }

                var refs = PresentReferences(session, map, result.Skipped);
                string lastStep = null;
                var stepNames = refs.Select(r => r.Step).Distinct().ToList();
                foreach (var r in refs)
                {
                    if (r.Step != lastStep)
                    {
                        lastStep = r.Step;
                        progress?.Report(r.Step + " (" + (stepNames.IndexOf(r.Step) + 1) + " of " + stepNames.Count + ")");
                    }
                    var (recoded, merged) = Reassign(session, r, from, to);
                    if (recoded + merged > 0)
                        changes.Rows.Add(r.Name, recoded, merged);
                    result.TotalRows += recoded + merged;
                }

                if (deleteFrom)
                {
                    progress?.Report("Deleting " + map.Noun + " " + from + "…");
                    var remaining = CountReferences(session, refs, from);
                    foreach (DataRow row in remaining.Rows)
                        result.DeleteBlockers.Add(Sql.Text(row, "Where") + " (" + Convert.ToInt32(row["Rows"]).ToString("N0") + ")");
                    foreach (var check in map.DeleteChecks)
                    {
                        var n = Convert.ToInt32(session.Scalar(check.Value, Sql.P("@From", from)) ?? 0);
                        if (n > 0)
                            result.DeleteBlockers.Add(check.Key + " (" + n.ToString("N0") + ")");
                    }
                    if (result.DeleteBlockers.Count == 0)
                    {
                        result.Deleted = TryDelete(session, map, from, result.DeleteBlockers);
                    }
                }

                session.WriteUtilityLog(map.ToolName + " - " + from + " to " + to
                    + (result.Deleted ? " (deleted)" : deleteFrom ? " (delete blocked: still referenced)" : ""));
            });
            return result;
        }

        private static (int recoded, int merged) Reassign(JurisSession session, CodeRef r, string from, string to)
        {
            var t = Q(r.Table);
            var c = Q(r.Column);
            Func<SqlParameter[]> codes = () => new[] { Sql.P("@From", from), Sql.P("@To", to) };
            var merged = 0;

            // Counts use ExecuteRows: Juris's audit triggers would otherwise be counted too (NOCOUNT is off).
            TriggerSafeDml.SnapshotEntries(session, r.Mirror, "d." + c + " = @From", Sql.P("@From", from));
            switch (r.Kind)
            {
                case CodeRefKind.Merge:
                {
                    var join = KeyJoin(r.Keys);
                    var sums = string.Join(", ", r.Sums.Select(s => Q(s) + " = isnull(t." + Q(s) + ", 0) + isnull(f." + Q(s) + ", 0)"));
                    session.Execute("update t set " + sums + " from " + t + " t inner join " + t + " f on " + join
                                    + " where t." + c + " = @To and f." + c + " = @From", codes());
                    merged = session.ExecuteRows("delete f from " + t + " f where f." + c + " = @From and exists (select 1 from " + t
                                                 + " t where t." + c + " = @To and " + join + ")", codes());
                    break;
                }
                case CodeRefKind.KeepTarget:
                {
                    var join = KeyJoin(r.Keys);
                    merged = session.ExecuteRows("delete f from " + t + " f where f." + c + " = @From and exists (select 1 from " + t
                                                 + " t where t." + c + " = @To and " + join + ")", codes());
                    break;
                }
                case CodeRefKind.UpdateWithNote:
                {
                    var n = Q(r.NoteColumn);
                    var note = "Updated code from " + from + " to " + to + " on " + DateTime.Today.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    var p = codes().ToList();
                    p.Add(Sql.P("@Note", note));
                    session.Execute("update " + t + " set " + n + " = isnull(cast(" + n + " as nvarchar(max)), N'') + nchar(13) + nchar(10) + cast(@Note as nvarchar(max))"
                                    + " where " + c + " = @From", p.ToArray());
                    break;
                }
            }

            var recoded = session.ExecuteRows("update " + t + " set " + c + " = @To where " + c + " = @From", codes());
            TriggerSafeDml.RestoreEntries(session, r.Mirror);
            return (recoded, merged);
        }

        private static bool TryDelete(JurisSession session, CodeReassignmentMap map, string from, List<string> blockers)
        {
            // A savepoint lets us undo just the delete if some table outside the map still references the code
            // (FK violation), while keeping the reassignment the user asked for.
            session.Execute("save transaction JpsCodeDelete");
            try
            {
                var n = session.ExecuteRows("delete from " + Q(map.CodeTable) + " where " + Q(map.CodeColumn) + " = @From",
                    Sql.P("@From", from));
                if (n != 1)
                    throw new InvalidOperationException(map.Noun + " " + from + " no longer exists (someone may have deleted it already). Nothing was changed.");
                foreach (var sql in map.AfterDeleteSql)
                    session.Execute(sql, Sql.P("@From", from));
                return true;
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                session.Execute("if xact_state() = 1 rollback transaction JpsCodeDelete");
                blockers.Add("other parts of Juris that this tool doesn't change (details: " + ex.Message + ")");
                return false;
            }
        }

        private static string KeyJoin(IEnumerable<string> keys)
        {
            var parts = keys.Select(k =>
            {
                var nullable = k.EndsWith("?", StringComparison.Ordinal);
                var col = Q(nullable ? k.TrimEnd('?') : k);
                return nullable ? "isnull(f." + col + ", '') = isnull(t." + col + ", '')" : "f." + col + " = t." + col;
            });
            return string.Join(" and ", parts);
        }

        /// <summary>Brackets an identifier from the map (constants only).</summary>
        private static string Q(string identifier)
        {
            var sb = new StringBuilder("[");
            sb.Append(identifier.Replace("]", "]]"));
            return sb.Append(']').ToString();
        }
    }
}
