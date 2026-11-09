using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using Jps.Core.Data;
using Jps.Tools.Tools.MatterCloser;

namespace Jps.Tools.Tools.SplitMatterCloser
{
    public enum SplitSelectMode { ClientAll, ClientMatters, BillingTimekeeper, LastBillDate }

    /// <summary>What the user picked on screen, captured before any database work starts.</summary>
    public sealed class SplitCriteria
    {
        public SplitSelectMode Mode { get; set; }
        public object ClientKey { get; set; }
        public List<object> MatterKeys { get; set; } = new List<object>();
        public object TimekeeperKey { get; set; }
        /// <summary>Billing timekeeper mode: only matters opened on or before this date (null = any).</summary>
        public DateTime? OpenedOnOrBefore { get; set; }
        public DateTime LastBillOnOrBefore { get; set; }
        /// <summary>Last bill date mode: also take never-billed matters opened before this date (null = leave them out).</summary>
        public DateTime? NeverBilledOpenedBefore { get; set; }
    }

    public sealed class SplitCloseResult
    {
        public int FamiliesClosed { get; set; }
        public int MattersClosed { get; set; }
        public int FamiliesSkipped { get; set; }
        public DataTable NotClosed { get; set; }
    }

    /// <summary>All SQL for the Split Matter Closer. Every value is a parameter; no global temp tables.</summary>
    public sealed class SplitMatterCloserService
    {
        private const string NeverBilled = "'19020101'"; // Juris stores "never billed" as 12/31/1899

        private readonly JurisSession _session;

        public SplitMatterCloserService(JurisSession session)
        {
            _session = session;
        }

        // ---------- lookups ----------

        public const string ClientsSql =
            @"select distinct c.CliSysNbr, dbo.jfn_FormatClientCode(c.CliCode) as Code, c.CliReportingName as Name
              from Client c inner join Matter m on m.MatCliNbr = c.CliSysNbr
              where m.MatSysNbr in (select SplitFromMat from SplitBill) or m.MatSysNbr in (select SplitToMat from SplitBill)
              order by 2";

        public const string TimekeepersSql =
            @"select distinct e.EmpSysNbr, e.EmpID as Code, e.EmpName as Name
              from Employee e inner join BillTo b on b.BillToBillingAtty = e.EmpSysNbr
              inner join Matter m on m.MatBillTo = b.BillToSysNbr
              where m.MatSysNbr in (select SplitFromMat from SplitBill) or m.MatSysNbr in (select SplitToMat from SplitBill)
              order by 2";

        public const string ClientMattersSql =
            @"select m.MatSysNbr, dbo.jfn_FormatMatterCode(m.MatCode) as Code,
                     m.MatReportingName + case when m.MatSysNbr in (select SplitFromMat from SplitBill) then ' (parent)' else ' (child)' end as Name
              from Matter m
              where m.MatCliNbr = @cli
                and (m.MatSysNbr in (select SplitFromMat from SplitBill) or m.MatSysNbr in (select SplitToMat from SplitBill))
              order by 2";

        // ---------- candidate families ----------

        /// <summary>
        /// Parents of every split family where the parent OR any child matches the criteria (as the published tool does).
        /// </summary>
        private static string CandidateSql(SplitCriteria criteria, List<SqlParameter> ps)
        {
            Func<string, string> pred;
            switch (criteria.Mode)
            {
                case SplitSelectMode.ClientAll:
                    ps.Add(Sql.P("@cli", criteria.ClientKey));
                    pred = a => a + ".MatCliNbr = @cli";
                    break;
                case SplitSelectMode.ClientMatters:
                    var list = Sql.InList("mat", criteria.MatterKeys, ps);
                    pred = a => a + ".MatSysNbr in " + list;
                    break;
                case SplitSelectMode.BillingTimekeeper:
                    ps.Add(Sql.P("@emp", criteria.TimekeeperKey));
                    var opened = "";
                    if (criteria.OpenedOnOrBefore.HasValue)
                    {
                        ps.Add(Sql.P("@openedTo", criteria.OpenedOnOrBefore.Value.Date.AddDays(1)));
                        opened = " and {0}.MatDateOpened < @openedTo";
                    }
                    pred = a => "(" + a + ".MatBillTo in (select BillToSysNbr from BillTo where BillToBillingAtty = @emp)" + string.Format(opened, a) + ")";
                    break;
                default:
                    ps.Add(Sql.P("@billTo", criteria.LastBillOnOrBefore.Date.AddDays(1)));
                    var never = "";
                    if (criteria.NeverBilledOpenedBefore.HasValue)
                    {
                        ps.Add(Sql.P("@neverOpened", criteria.NeverBilledOpenedBefore.Value.Date));
                        never = " or ({0}.MatDateLastBill < " + NeverBilled + " and {0}.MatDateOpened < @neverOpened)";
                    }
                    pred = a => "((" + a + ".MatDateLastBill >= " + NeverBilled + " and " + a + ".MatDateLastBill < @billTo)" + string.Format(never, a) + ")";
                    break;
            }
            return @"select distinct sb.SplitFromMat as ParentID
                     from SplitBill sb
                     inner join Matter p on p.MatSysNbr = sb.SplitFromMat
                     inner join Matter c on c.MatSysNbr = sb.SplitToMat
                     where " + pred("p") + " or " + pred("c");
        }

        /// <summary>The pre-report: every matching family, parent row first then its children.</summary>
        public DataTable FamiliesReport(SplitCriteria criteria)
        {
            var ps = new List<SqlParameter>();
            var sql = ";with fam as (" + CandidateSql(criteria, ps) + @")
                select [Parent ID], [Split Family], [Client Code], [Client Name], [Matter Code], [Matter Name], [Role], [Split %] from (
                    select f.ParentID as [Parent ID], 0 as SortKey, dbo.jfn_FormatClientCode(cl.CliCode) + '/' + dbo.jfn_FormatMatterCode(m.MatCode) as [Split Family],
                           dbo.jfn_FormatClientCode(cl.CliCode) as [Client Code], cl.CliReportingName as [Client Name],
                           dbo.jfn_FormatMatterCode(m.MatCode) as [Matter Code], m.MatReportingName as [Matter Name], 'Parent' as [Role], '' as [Split %]
                    from fam f inner join Matter m on m.MatSysNbr = f.ParentID
                    inner join Client cl on cl.CliSysNbr = m.MatCliNbr
                    union all
                    select f.ParentID, 1, dbo.jfn_FormatClientCode(pc.CliCode) + '/' + dbo.jfn_FormatMatterCode(pm.MatCode),
                           dbo.jfn_FormatClientCode(cl.CliCode), cl.CliReportingName,
                           dbo.jfn_FormatMatterCode(m.MatCode), m.MatReportingName, 'Child',cast(cast(sb.SplitToPcnt as money) as varchar(20)) + '%'
                    from fam f inner join SplitBill sb on sb.SplitFromMat = f.ParentID
                    inner join Matter m on m.MatSysNbr = sb.SplitToMat
                    inner join Client cl on cl.CliSysNbr = m.MatCliNbr
                    inner join Matter pm on pm.MatSysNbr = f.ParentID
                    inner join Client pc on pc.CliSysNbr = pm.MatCliNbr
                ) r
                order by [Parent ID], SortKey, [Client Code], [Matter Code]";
            return _session.Query(sql, ps.ToArray());
        }

        /// <summary>The internal parent-matter number column of <see cref="FamiliesReport"/>: read by <see cref="ParentsIn"/>, removed before the report is shown.</summary>
        public const string ParentIdColumn = "Parent ID";

        public static List<int> ParentsIn(DataTable familiesReport) =>
            familiesReport.Rows().Select(r => Convert.ToInt32(r[ParentIdColumn])).Distinct().ToList();

        // ---------- closing ----------

        private sealed class Member
        {
            public int MatSysNbr;
            public bool IsChild;
            public string ClientCode, ClientName, MatterCode, MatterName;
            public decimal Percent;
            public DataRow Balances;
            public string Reasons;

            public string Line => (IsChild ? "Child" : "Parent") + ": Client - " + ClientCode + " / " + ClientName + " Matter - " + MatterCode + " / " + MatterName
                                  // The parent's own share shows too when it splits to itself (e.g. Parent 50% / Child 50%).
                                  + (IsChild || Percent > 0 ? " Percent: " + Percent.ToString("0.####", CultureInfo.InvariantCulture) + "%" : "");
        }

        /// <summary>
        /// Closes each family in its own transaction, all or nothing: parent and every child must be clear, otherwise nothing
        /// in that family is touched and every member is listed in the returned report.
        /// </summary>
        public SplitCloseResult Close(IList<int> parents, DateTime today)
        {
            var result = new SplitCloseResult { NotClosed = NewNotClosedTable() };
            // Families that share a matter (e.g. two matters split to each other, each the parent of the other) can only be
            // closed together: they are grouped and each group is checked and closed in one transaction, all or nothing.
            foreach (var group in GroupLinkedFamilies(parents))
            {
                try
                {
                    var closedCount = 0;
                    List<Member> blocked = null;
                    _session.InTransaction(() =>
                    {
                        var families = group.ToDictionary(p => p, LoadFamily);
                        var all = families.Values.SelectMany(f => f).GroupBy(m => m.MatSysNbr).Select(g => g.First()).ToList();
                        if (families.Values.Any(f => f.Count(m => m.IsChild) == 0))
                        {
                            blocked = all;
                            foreach (var m in all) m.Reasons = "The split no longer exists (it was removed after the list was shown)";
                            return;
                        }
                        LoadBalances(group, all);
                        if (all.Any(m => m.Reasons.Length > 0))
                        {
                            blocked = all;
                            foreach (var m in all.Where(m => m.Reasons.Length == 0))
                                m.Reasons = "Clear, but left open because another matter in this split family isn't";
                            return;
                        }
                        foreach (var family in families)
                            CloseFamily(family.Key, family.Value, today);
                        closedCount = all.Count;
                    });
                    if (blocked != null)
                    {
                        result.FamiliesSkipped += group.Count;
                        AddNotClosed(result.NotClosed, blocked);
                    }
                    else
                    {
                        result.FamiliesClosed += group.Count;
                        result.MattersClosed += closedCount;
                    }
                }
                catch (Exception ex) when (ex is SqlException || ex is InvalidOperationException)
                {
                    // That group was rolled back; record it and carry on with the others.
                    result.FamiliesSkipped += group.Count;
                    foreach (var parent in group)
                    {
                        var row = result.NotClosed.NewRow();
                        var label = ParentLabel(parent);
                        row["Parent Client Code"] = label.Item1;
                        row["Parent Matter Code"] = label.Item2;
                        row["Client Code"] = label.Item1;
                        row["Matter Code"] = label.Item2;
                        row["Role"] = "Parent";
                        row["Why It Wasn't Closed"] = "Could not be closed: " + ex.Message;
                        result.NotClosed.Rows.Add(row);
                    }
                }
            }

            _session.WriteUtilityLog("JPS Split Matter Closer: closed " + result.FamiliesClosed + " split famil" + (result.FamiliesClosed == 1 ? "y" : "ies")
                                     + " (" + result.MattersClosed + " matters); " + result.FamiliesSkipped + " not closed.");
            return result;
        }

        /// <summary>Groups the chosen parents whose families share any matter (union-find over each family's matters).</summary>
        private List<List<int>> GroupLinkedFamilies(IList<int> parents)
        {
            var owner = new Dictionary<int, int>();   // parent -> group root
            int Root(int p) { while (owner[p] != p) p = owner[p] = owner[owner[p]]; return p; }
            var firstParentOfMatter = new Dictionary<int, int>();
            foreach (var parent in parents.Distinct())
            {
                owner[parent] = parent;
                var matters = _session.Query("select SplitToMat from SplitBill where SplitFromMat = @p", Sql.P("@p", parent))
                    .Rows().Select(r => Convert.ToInt32(r[0])).Concat(new[] { parent });
                foreach (var m in matters)
                {
                    if (firstParentOfMatter.TryGetValue(m, out var other)) owner[Root(parent)] = Root(other);
                    else firstParentOfMatter[m] = parent;
                }
            }
            return parents.Distinct().GroupBy(Root).Select(g => g.ToList()).ToList();
        }

        /// <summary>The parent matter's client and matter code for the report (blank if it can't be read).</summary>
        private Tuple<string, string> ParentLabel(int parent)
        {
            try
            {
                var row = _session.Query(
                    @"select dbo.jfn_FormatClientCode(c.CliCode) as ClientCode, dbo.jfn_FormatMatterCode(m.MatCode) as MatterCode
                      from Matter m inner join Client c on c.CliSysNbr = m.MatCliNbr where m.MatSysNbr = @p", Sql.P("@p", parent)).Rows().FirstOrDefault();
                return row == null ? Tuple.Create("", "") : Tuple.Create(Sql.Text(row, "ClientCode"), Sql.Text(row, "MatterCode"));
            }
            catch (SqlException)
            {
                return Tuple.Create("", "");
            }
        }

        private List<Member> LoadFamily(int parent)
        {
            // updlock/holdlock: a second user running the tool waits rather than closing the same family twice.
            var table = _session.Query(
                @"select m.MatSysNbr, case when m.MatSysNbr = @p then 0 else 1 end as IsChild,
                         dbo.jfn_FormatClientCode(c.CliCode) as ClientCode, c.CliReportingName as ClientName,
                         dbo.jfn_FormatMatterCode(m.MatCode) as MatterCode, m.MatReportingName as MatterName,
                         isnull(sb.SplitToPcnt, 0) as Pcnt
                  from Matter m
                  inner join Client c on c.CliSysNbr = m.MatCliNbr
                  left join SplitBill sb with (updlock, holdlock) on sb.SplitFromMat = @p and sb.SplitToMat = m.MatSysNbr
                  where m.MatSysNbr = @p
                     or m.MatSysNbr in (select SplitToMat from SplitBill with (updlock, holdlock) where SplitFromMat = @p)
                  order by 2, 3, 5", Sql.P("@p", parent));
            return table.Rows().Select(r => new Member
            {
                MatSysNbr = Convert.ToInt32(r["MatSysNbr"]),
                IsChild = Convert.ToInt32(r["IsChild"]) == 1,
                ClientCode = Sql.Text(r, "ClientCode"),
                ClientName = Sql.Text(r, "ClientName"),
                MatterCode = Sql.Text(r, "MatterCode"),
                MatterName = Sql.Text(r, "MatterName"),
                Percent = Convert.ToDecimal(r["Pcnt"]),
                Reasons = "",
            }).ToList();
        }

        /// <summary>
        /// Checks every member against the shared close rules (<see cref="MatterCloseRules"/>, the same rules Matter Closer
        /// uses: WIP, unposted time/expense/vouchers/trust adjustments, open vouchers/bills/A/R, unposted or pending cash,
        /// prepaid, trust in any bank, open prebills). The rules' "Splits" count is replaced by the member's splits OUTSIDE
        /// this family, because the family's own SplitBill rows are removed when it closes.
        /// </summary>
        private void LoadBalances(IList<int> groupParents, List<Member> members)
        {
            var ids = members.Select(m => m.MatSysNbr).ToList();
            var ps = new List<SqlParameter>();
            var mats = Sql.InList("m", ids.Cast<object>(), ps);
            var groupList = Sql.InList("g", groupParents.Cast<object>(), ps);
            // updlock/holdlock on the member matter rows (MatPPDBalance is only in the clustered index, so the real rows are
            // locked): nothing can change their status, and a posting already updating one finishes first, until commit.
            var otherSplits = _session.Query(
                @"select m.MatSysNbr, m.MatPPDBalance,
                         (select count(*) from SplitBill x where (x.SplitFromMat = m.MatSysNbr or x.SplitToMat = m.MatSysNbr)
                             and x.SplitFromMat not in " + groupList + @") as OtherSplits
                  from Matter m with (updlock, holdlock) where m.MatSysNbr in " + mats, ps.ToArray())
                .Rows().ToDictionary(r => Convert.ToInt32(r["MatSysNbr"]), r => Convert.ToInt32(r["OtherSplits"]));

            var checks = MatterCloseRules.Check(_session, MatterCloseRules.HasSuite(_session), ids);
            foreach (var member in members)
            {
                if (!checks.TryGetValue(member.MatSysNbr, out var check))
                {
                    member.Reasons = "The matter no longer exists";
                    continue;
                }
                var row = check.Row;
                row["Splits"] = otherSplits.TryGetValue(member.MatSysNbr, out var n) ? n : 0;
                member.Balances = row;
                // Here the "split" reason means a split family that wasn't chosen in this run, so say that plainly.
                member.Reasons = MatterCloseRules.Reasons(row).Replace(
                    "part of a split-billing arrangement (remove the split first)",
                    "also in another split family that wasn't chosen (choose that family too, so they close together)");
            }
        }

        private void CloseFamily(int parent, List<Member> members, DateTime today)
        {
            var children = members.Where(m => m.IsChild).ToList();
            var parentMember = members.First(m => !m.IsChild);

            if (_session.ExecuteRows(
                    "update Matter set MatStatusFlag = 'C', MatLockFlag = '3', MatDateClosed = case when MatStatusFlag = 'C' then MatDateClosed else @today end, MatSplitMethod = '0' where MatSysNbr = @p",
                    Sql.P("@p", parent), Sql.P("@today", today)) != 1)
                throw new InvalidOperationException("Parent matter " + parentMember.ClientCode + "/" + parentMember.MatterCode + " could not be updated, so nothing in this family was changed.");

            var ps = new List<SqlParameter> { Sql.P("@today", today) };
            var list = Sql.InList("c", children.Select(c => (object)c.MatSysNbr), ps);
            if (_session.ExecuteRows("update Matter set MatStatusFlag = 'C', MatLockFlag = '3', MatDateClosed = case when MatStatusFlag = 'C' then MatDateClosed else @today end where MatSysNbr in " + list, ps.ToArray()) != children.Count)
                throw new InvalidOperationException("Not every child matter of " + parentMember.ClientCode + "/" + parentMember.MatterCode + " could be updated, so nothing in this family was changed.");

            var header = "Split Removed via JPS - Split Matter Closer\r\nPerformed on " + today.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) + "\r\n" + parentMember.Line;
            AddNote(parent, header + string.Concat(children.Select(c => "\r\n" + c.Line)));
            foreach (var child in children)
                AddNote(child.MatSysNbr, header + "\r\n" + child.Line);

            _session.Execute("delete from SplitBill where SplitFromMat = @p", Sql.P("@p", parent));
        }

        private void AddNote(int matSysNbr, string text)
        {
            // Next free "ToolNoteN" index (MNNoteIndex is varchar(15)); only all-digit suffixes are counted.
            var next = Convert.ToInt32(_session.Scalar(
                @"select isnull(max(case when len(MNNoteIndex) between 9 and 15 and substring(MNNoteIndex, 9, 7) not like '%[^0-9]%'
                                         then cast(substring(MNNoteIndex, 9, 7) as int) end), 0) + 1
                  from MatterNote where MNMatter = @m and MNNoteIndex like 'ToolNote%'", Sql.P("@m", matSysNbr)));
            _session.Execute(
                "insert into MatterNote (MNMatter, MNNoteIndex, MNObject, MNNoteText, MNNoteObject) values (@m, @idx, '', cast(@txt as varchar(max)), null)",
                Sql.P("@m", matSysNbr), Sql.P("@idx", "ToolNote" + next.ToString(CultureInfo.InvariantCulture)), Sql.P("@txt", text));
        }

        // ---------- post-report ----------

        private static readonly string[] AmountColumns = { "Prepaid Balance", "Unbilled Fees", "Unbilled Expenses", "A/R Balance", "Trust Balance" };
        private static readonly string[] CountColumns = { "Unposted Time Entries", "Unposted Expense Entries", "Unposted Vouchers", "Open Vouchers", "Unposted Cash Receipts", "Unposted Trust Adjustments", "Open Bills", "Open Prebills" };

        private static DataTable NewNotClosedTable()
        {
            var t = new DataTable("Matters not closed");
            foreach (var name in new[] { "Parent Client Code", "Parent Matter Code", "Client Code", "Matter Code", "Matter Name", "Role", "Why It Wasn't Closed" })
                t.Columns.Add(name, typeof(string));
            t.Columns.Add("Prepaid Balance", typeof(decimal));
            t.Columns.Add("Unbilled Fees", typeof(decimal));
            t.Columns.Add("Unbilled Expenses", typeof(decimal));
            t.Columns.Add("Unposted Time Entries", typeof(int));
            t.Columns.Add("Unposted Expense Entries", typeof(int));
            t.Columns.Add("Unposted Vouchers", typeof(int));
            t.Columns.Add("Open Vouchers", typeof(int));
            t.Columns.Add("Unposted Cash Receipts", typeof(int));
            t.Columns.Add("Unposted Trust Adjustments", typeof(int));
            t.Columns.Add("Open Bills", typeof(int));
            t.Columns.Add("A/R Balance", typeof(decimal));
            t.Columns.Add("Trust Balance", typeof(decimal));
            t.Columns.Add("Open Prebills", typeof(int));
            return t;
        }

        private static readonly Dictionary<string, string> SourceColumn = new Dictionary<string, string>
        {
            // Post-report column -> MatterCloseRules column (B).
            { "Prepaid Balance", "PPD" }, { "Unbilled Fees", "UnbilledTimeAmt" }, { "Unbilled Expenses", "UnbilledExpAmt" },
            { "Unposted Time Entries", "UnpostedTime" }, { "Unposted Expense Entries", "UnpostedExp" }, { "Unposted Vouchers", "UnpostedVouchers" },
            { "Open Vouchers", "OpenVouchers" }, { "Unposted Cash Receipts", "PendingCash" }, { "Unposted Trust Adjustments", "UnpostedTrustAdj" },
            { "Open Bills", "OpenBills" }, { "A/R Balance", "AR" },
            { "Trust Balance", "TrustLedger" }, { "Open Prebills", "Prebills" },
        };

        private static void AddNotClosed(DataTable table, List<Member> members)
        {
            var parent = members.FirstOrDefault(m => !m.IsChild);
            foreach (var m in members)
            {
                var row = table.NewRow();
                row["Parent Client Code"] = parent?.ClientCode ?? "";
                row["Parent Matter Code"] = parent?.MatterCode ?? "";
                row["Client Code"] = m.ClientCode;
                row["Matter Code"] = m.MatterCode;
                row["Matter Name"] = m.MatterName;
                row["Role"] = m.IsChild ? "Child" : "Parent";
                row["Why It Wasn't Closed"] = m.Reasons;
                if (m.Balances != null)
                {
                    foreach (var col in AmountColumns)
                        row[col] = m.Balances[SourceColumn[col]] == DBNull.Value ? 0m : Convert.ToDecimal(m.Balances[SourceColumn[col]]);
                    foreach (var col in CountColumns)
                        row[col] = m.Balances[SourceColumn[col]] == DBNull.Value ? 0 : Convert.ToInt32(m.Balances[SourceColumn[col]]);
                }
                table.Rows.Add(row);
            }
        }
    }
}
