using System;
using System.Collections.Generic;
using System.Data;
using Jps.Core.Data;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.VendorDiscountAccount
{
    /// <summary>
    /// Which vendor GL account column a swap tool works on, plus the wording it shows. Shared by
    /// Vendor Discount Account Updater (VenDiscAcct) and Vendor GL Default Dist Account Updater (VenDefaultDistAcct).
    /// The column name is a fixed constant from this class, never user text.
    /// </summary>
    public sealed class VendorAccountField
    {
        private VendorAccountField(string column, string toolName, string accountName)
        {
            Column = column;
            ToolName = toolName;
            AccountName = accountName;
        }

        /// <summary>Vendor table column, e.g. VenDiscAcct.</summary>
        public string Column { get; }

        /// <summary>Tool name for the UtilityLog line.</summary>
        public string ToolName { get; }

        /// <summary>Lower-case name used in sentences, e.g. "discount account".</summary>
        public string AccountName { get; }

        public static readonly VendorAccountField Discount =
            new VendorAccountField("VenDiscAcct", "Vendor Discount GL Updater", "discount account");

        public static readonly VendorAccountField DefaultDist =
            new VendorAccountField("VenDefaultDistAcct", "Vendor GL Default Dist Account Updater", "default distribution account");
    }

    /// <summary>The database side of a vendor GL account swap. All values are parameters; only the fixed column name is in the SQL text.</summary>
    public sealed class VendorAccountSwapService
    {
        // Vendors 1 and 2 are Juris system vendors; the Dist Account tool and the Inactivator never touch them, so neither tool does.
        private const string NotSystemVendor = "VenSysNbr not in (1,2)";

        private readonly JurisSession _session;
        private readonly VendorAccountField _field;

        public VendorAccountSwapService(JurisSession session, VendorAccountField field)
        {
            _session = session;
            _field = field;
        }

        /// <summary>
        /// Every chart account (key = ChtSysNbr). Main-account format is used only when the firm has no sub-accounts
        /// AND each main account is a single chart row; otherwise the full formatted account is shown so no two items look alike.
        /// </summary>
        public List<LookupItem> LoadAccounts()
        {
            var mainOnly = Convert.ToInt32(_session.Scalar(
                @"select case when count(*) = count(distinct ChtMainAcct)
                               and not exists (select 1 from ChartOfAccounts where isnull(ChtSubAcct1, 0) <> 0)
                          then 1 else 0 end
                  from ChartOfAccounts") ?? 0) == 1;
            var format = mainOnly ? "dbo.jfn_FormatMainAcct(ChtMainAcct)" : "dbo.jfn_FormatChartOfAccount(ChtSysNbr)";
            return Lookup.Load(_session,
                "select ChtSysNbr, " + format + " as Acct, ChtDesc from ChartOfAccounts order by " + format);
        }

        /// <summary>Vendors the change would touch. <paramref name="from"/> null = every vendor not already on <paramref name="to"/>.</summary>
        private string Where(int? from, string alias = "")
        {
            var col = alias + _field.Column;
            var notSystem = alias + NotSystemVendor;
            return from == null
                ? notSystem + " and (" + col + " is null or " + col + " <> @to)"
                : notSystem + " and " + col + " = @from";
        }

        public int CountAffected(int? from, int to) =>
            Convert.ToInt32(_session.Scalar("select count(*) from Vendor where " + Where(from), Sql.P("@from", from), Sql.P("@to", to)) ?? 0);

        /// <summary>The vendors the change would touch, for the "Show vendors" report.</summary>
        public DataTable AffectedVendors(int? from, int to) => _session.Query(
            @"select dbo.jfn_FormatVendorCode(v.VenCode) as [Vendor Code], v.VenName as [Vendor Name],
                     case v.VenActive when 'Y' then 'Yes' else 'No' end as [Active],
                     isnull(dbo.jfn_FormatChartOfAccount(c.ChtSysNbr) + '  ' + c.ChtDesc, '(none)') as [Current Account],
                     (select dbo.jfn_FormatChartOfAccount(ChtSysNbr) + '  ' + ChtDesc from ChartOfAccounts where ChtSysNbr = @to) as [New Account]
              from Vendor v left join ChartOfAccounts c on c.ChtSysNbr = v." + _field.Column + @"
              where " + Where(from, "v.") + @"
              order by v.VenCode",
            Sql.P("@from", from), Sql.P("@to", to));

        /// <summary>Makes the change and logs it, all in one transaction. Returns the number of vendors changed.</summary>
        public int Apply(int? from, int to, string fromText, string toText) => _session.InTransaction(() =>
        {
            var exists = Convert.ToInt32(_session.Scalar("select count(*) from ChartOfAccounts where ChtSysNbr = @to", Sql.P("@to", to)) ?? 0);
            if (exists == 0)
                throw new InvalidOperationException("The new account no longer exists in the chart of accounts. Nothing was changed.");
            var n = _session.ExecuteRows("update Vendor set " + _field.Column + " = @to where " + Where(from), Sql.P("@from", from), Sql.P("@to", to));
            if (n > 0)
                _session.WriteUtilityLog(_field.ToolName + ": changed " + _field.Column + " from " + (from == null ? "ALL" : fromText)
                                         + " to " + toText + " on " + n + " vendor(s)");
            return n;
        });
    }
}
