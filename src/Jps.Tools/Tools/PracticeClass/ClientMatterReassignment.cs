using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.PracticeClass
{
    /// <summary>Which records a reassignment touches.</summary>
    public enum ReassignScope
    {
        ClientsAndMatters,
        ClientsOnly,
        MattersOnly,
    }

    /// <summary>Something that still uses a code and so stops it being deleted.</summary>
    public sealed class DeleteBlocker
    {
        public DeleteBlocker(string what, int count, string fix)
        {
            What = what;
            Count = count;
            Fix = fix;
        }

        public string What { get; }
        public int Count { get; }
        public string Fix { get; }

        public override string ToString() => "•  " + What + (Count > 0 ? " (" + Count.ToString("N0") + ")" : "") + ". " + Fix;

        /// <summary>A heading plus one bullet per blocker.</summary>
        public static string Message(string heading, IEnumerable<DeleteBlocker> blockers) =>
            heading + Environment.NewLine + string.Join(Environment.NewLine, blockers.Select(b => b.ToString()));
    }

    /// <summary>
    /// Shared engine for the tools that move clients and/or matters from one code to another (practice class, fee schedule).
    /// A subclass names the Client/Matter columns, the matter joins and adds its own filters; this class builds the WHERE
    /// clauses once so the pre-report, the update and the "what would be left on the old code" count all use exactly the
    /// same rows. Client rows are aliased <c>c</c>, matter rows <c>m</c>. All values are SQL parameters.
    /// </summary>
    public abstract class ClientMatterReassignment
    {
        private readonly List<KeyValuePair<string, object>> _values = new List<KeyValuePair<string, object>>();
        private readonly List<string> _clientFilters = new List<string>();
        private readonly List<string> _matterFilters = new List<string>();

        protected ClientMatterReassignment(string from, string to, ReassignScope scope)
        {
            From = from;
            To = to;
            Scope = scope;
            AddValue("@From", from);
            AddValue("@To", to);
        }

        public string From { get; }
        public string To { get; }
        public ReassignScope Scope { get; }
        public bool ChangesClients => Scope != ReassignScope.MattersOnly;
        public bool ChangesMatters => Scope != ReassignScope.ClientsOnly;

        /// <summary>"clients and matters", "clients" or "matters".</summary>
        public string ScopeText => Scope == ReassignScope.ClientsOnly ? "clients" : Scope == ReassignScope.MattersOnly ? "matters" : "clients and matters";

        /// <summary>Client column holding the code (unqualified, e.g. CliPracticeClass).</summary>
        protected abstract string ClientColumn { get; }

        /// <summary>Matter column holding the code (unqualified, e.g. MatPracticeClass).</summary>
        protected abstract string MatterColumn { get; }

        /// <summary>FROM clause for client rows: Client aliased c, plus any joins the filters need.</summary>
        protected virtual string ClientSource => "Client c";

        /// <summary>FROM clause for matter rows: Matter aliased m, plus any joins the filters need.</summary>
        protected abstract string MatterSource { get; }

        protected void AddValue(string name, object value) => _values.Add(new KeyValuePair<string, object>(name, value));
        protected void FilterClients(string clause) => _clientFilters.Add(clause);
        protected void FilterMatters(string clause) => _matterFilters.Add(clause);

        /// <summary>A fresh parameter set (a SqlParameter can only belong to one command).</summary>
        public SqlParameter[] Params() => _values.Select(v => Sql.P(v.Key, v.Value)).ToArray();

        public string ClientWhere => "c." + ClientColumn + " = @From" + string.Concat(_clientFilters.Select(f => " and " + f));
        public string MatterWhere => "m." + MatterColumn + " = @From" + string.Concat(_matterFilters.Select(f => " and " + f));

        public int CountClientsToChange(JurisSession s) => ChangesClients ? Count(s, "select count(*) from " + ClientSource + " where " + ClientWhere) : 0;
        public int CountMattersToChange(JurisSession s) => ChangesMatters ? Count(s, "select count(*) from " + MatterSource + " where " + MatterWhere) : 0;
        public int CountClientsOnFrom(JurisSession s) => Count(s, "select count(*) from Client c where c." + ClientColumn + " = @From");
        public int CountMattersOnFrom(JurisSession s) => Count(s, "select count(*) from Matter m where m." + MatterColumn + " = @From");

        /// <summary>The code table and its key column (e.g. PracticeClass / PrctClsCode).</summary>
        protected abstract string CodeTable { get; }
        protected abstract string CodeColumn { get; }

        /// <summary>
        /// Locks the From and To code rows until the transaction ends (so nobody deletes or renames either one between the
        /// checks and the delete) and confirms both still exist. Call first inside Session.InTransaction.
        /// </summary>
        public void LockCodes(JurisSession s, string noun)
        {
            foreach (var code in new[] { From, To })
            {
                var n = Convert.ToInt32(s.Scalar("select count(*) from " + CodeTable + " with (updlock, holdlock) where " + CodeColumn + " = @c",
                    Sql.P("@c", code)));
                if (n == 0)
                    throw new InvalidOperationException(noun + " " + code + " no longer exists (someone may have deleted it). Nothing was changed.");
            }
        }

        /// <summary>
        /// Moves the matters, then the clients. Call inside Session.InTransaction. Counts exclude rows written by Juris's
        /// audit triggers (Client_Log, Matter_Log...), which Execute would include.
        /// </summary>
        public void Apply(JurisSession s, out int clients, out int matters)
        {
            matters = ChangesMatters
                ? s.ExecuteRows("update m set " + MatterColumn + " = @To from " + MatterSource + " where " + MatterWhere, Params())
                : 0;
            clients = ChangesClients
                ? s.ExecuteRows("update c set " + ClientColumn + " = @To from " + ClientSource + " where " + ClientWhere, Params())
                : 0;
        }

        /// <summary>
        /// Clients/matters that would still be on the old code. Before the change it predicts (all on the code minus the
        /// ones this run moves); after the change it simply counts.
        /// </summary>
        public List<DeleteBlocker> ClientMatterBlockers(JurisSession s, bool afterChange, string codeName)
        {
            var list = new List<DeleteBlocker>();
            var clients = CountClientsOnFrom(s) - (afterChange ? 0 : CountClientsToChange(s));
            var matters = CountMattersOnFrom(s) - (afterChange ? 0 : CountMattersToChange(s));
            if (clients > 0)
                list.Add(new DeleteBlocker("Clients still on " + codeName + " " + From + " after this change", clients,
                    ChangesClients ? "Your filters leave them out; widen the filters or move them first."
                                   : "You chose \"Matters only\"; choose \"Clients and matters\" to move them too."));
            if (matters > 0)
                list.Add(new DeleteBlocker("Matters still on " + codeName + " " + From + " after this change", matters,
                    ChangesMatters ? "Your filters leave them out; widen the filters or move them first."
                                   : "You chose \"Clients only\"; choose \"Clients and matters\" to move them too."));
            return list;
        }

        protected int Count(JurisSession s, string sql) => Convert.ToInt32(s.Scalar(sql, Params()) ?? 0);

        /// <summary>A copy of the table without the named columns (ones that aren't there are ignored).</summary>
        public static DataTable WithoutColumns(DataTable table, params string[] columns)
        {
            var copy = table.Copy();
            foreach (var name in columns)
                if (copy.Columns.Contains(name))
                    copy.Columns.Remove(name);
            return copy;
        }
    }
}
