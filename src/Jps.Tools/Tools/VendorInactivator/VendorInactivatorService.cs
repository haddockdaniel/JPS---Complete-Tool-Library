using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.VendorInactivator
{
    public enum InactivatorMode { ByDate, ByName, Set1099 }

    public sealed class InactivatorRequest
    {
        public InactivatorMode Mode { get; set; }
        public DateTime Date { get; set; }
        public IReadOnlyList<int> Vendors { get; set; } = new int[0];
        public IReadOnlyList<string> VendorCodes { get; set; } = new string[0];
        public IReadOnlyList<string> Groups { get; set; } = new string[0];
        public int Type1099 { get; set; }
        public string Type1099Text { get; set; }
    }

    public sealed class InactivatorResult
    {
        public int Changed { get; set; }
        public DataTable Exceptions { get; set; }
    }

    /// <summary>The SQL for Inactivate Vendors: pre-reports, the change (one transaction) and the exception report.</summary>
    public sealed class VendorInactivatorService
    {
        public const string NoGroup = "NO GROUP";

        private const string VCols =
            "dbo.jfn_FormatVendorCode([VenCode]) as [Vendor Code], [VenName] as [Vendor Name], [VenAddress] as [Address], [VenCity] as [City], [VenState] as [State], [VenZip] as [Zip Code], [VenCountry] as [Country]";

        private const string Unpaid = "select VchVendor from Voucher where VchStatus <> 'P' and VchVendor is not null";

        // Last voucher invoice date OR last check date on/before @Date (original's two independent rules).
        private const string DateRule =
            @"( VenSysNbr in (select VchVendor from Voucher group by VchVendor having max(VchInvoiceDate) <= @Date)
             or VenSysNbr in (select CkRegVend from CheckRegister where CkRegVend is not null and CkRegDate is not null
                              group by CkRegVend having max(CkRegDate) <= @Date) )";

        private const string LastActivityCols =
            @", (select max(VchInvoiceDate) from Voucher where VchVendor = Vendor.VenSysNbr) as [Last Invoice Date]
              , (select max(CkRegDate) from CheckRegister where CkRegVend = Vendor.VenSysNbr) as [Last Check Date]";

        // Removes the Juris Tables-view entries of the vendors just inactivated; never a node that has items under it.
        private const string DeleteDocTree =
            @"delete from DocumentTree
              where DTDocClass = 7000 and DTKeyL not in (1,2) and DTKeyL in (select id from @done)
                and not exists (select 1 from DocumentTree c where c.DTParentID = DocumentTree.DTDocID);";

        private readonly JurisSession _session;

        public VendorInactivatorService(JurisSession session)
        {
            _session = session;
        }

        public DataTable PreReport(InactivatorRequest r)
        {
            var p = new List<SqlParameter>();
            switch (r.Mode)
            {
                case InactivatorMode.ByDate:
                    p.Add(Sql.P("@Date", r.Date.Date));
                    return _session.Query(
                        "select " + VCols + LastActivityCols + @" from Vendor
                         where VenActive = 'Y' and VenSysNbr not in (1,2)
                           and VenSysNbr not in (" + Unpaid + ") and " + DateRule + " order by VenCode", p.ToArray());

                case InactivatorMode.ByName:
                    var inList = Sql.InList("v", r.Vendors.Cast<object>(), p);
                    return _session.Query(
                        "select " + VCols + @",
                                case when VenSysNbr in (" + Unpaid + @") then 'No - has unpaid vouchers' else 'Yes' end as [Will Be Inactivated]
                         from Vendor where VenSysNbr not in (1,2) and VenSysNbr in " + inList + " order by VenCode", p.ToArray());

                default:
                    p.Add(Sql.P("@TypeText", r.Type1099Text));
                    return _session.Query(
                        "select " + VCols + @", @TypeText as [New 1099 Type], VenPaymentGroup as [Vendor Group],
                                case when VenActive = 'Y' then 'Yes' else 'No' end as [Active]
                         from Vendor where VenSysNbr not in (1,2) and " + GroupWhere(r.Groups, p) + " order by VenCode", p.ToArray());
            }
        }

        /// <summary>Makes the change in one transaction and writes the UtilityLog line; then builds the exception report.</summary>
        public InactivatorResult Run(InactivatorRequest r)
        {
            var result = new InactivatorResult();
            _session.InTransaction(() =>
            {
                var p = new List<SqlParameter>();
                switch (r.Mode)
                {
                    case InactivatorMode.ByDate:
                        p.Add(Sql.P("@Date", r.Date.Date));
                        result.Changed = Convert.ToInt32(_session.Scalar(
                            @"set nocount on;
                              declare @done table (id int primary key);
                              update Vendor set VenActive = 'N' output inserted.VenSysNbr into @done
                              where VenSysNbr not in (1,2) and VenActive = 'Y'
                                and VenSysNbr in (select VchVendor from Voucher group by VchVendor having max(VchInvoiceDate) <= @Date)
                                and VenSysNbr not in (" + Unpaid + @");
                              update Vendor set VenActive = 'N' output inserted.VenSysNbr into @done
                              where VenSysNbr not in (1,2) and VenActive = 'Y'
                                and VenSysNbr in (select CkRegVend from CheckRegister where CkRegVend is not null and CkRegDate is not null
                                                  group by CkRegVend having max(CkRegDate) <= @Date)
                                and VenSysNbr not in (" + Unpaid + @");
                              " + DeleteDocTree + @"
                              select count(*) from @done;", p.ToArray()));
                        _session.WriteUtilityLog("Vendor Inactivator: inactivated " + result.Changed
                            + " vendor(s) with no activity on or before " + r.Date.ToString("MM/dd/yyyy"));
                        break;

                    case InactivatorMode.ByName:
                        var inList = Sql.InList("v", r.Vendors.Cast<object>(), p);
                        result.Changed = Convert.ToInt32(_session.Scalar(
                            @"set nocount on;
                              declare @done table (id int primary key);
                              update Vendor set VenActive = 'N' output inserted.VenSysNbr into @done
                              where VenSysNbr not in (1,2) and VenActive = 'Y' and VenSysNbr in " + inList + @"
                                and VenSysNbr not in (" + Unpaid + @");
                              " + DeleteDocTree + @"
                              select count(*) from @done;", p.ToArray()));
                        _session.WriteUtilityLog(Truncate("Vendor Inactivator: inactivated " + result.Changed + " of the selected vendors "
                            + string.Join(", ", r.VendorCodes)));
                        break;

                    default:
                        p.Add(Sql.P("@Type", r.Type1099));
                        result.Changed = _session.ExecuteRows(
                            "update Vendor set VenGets1099 = 'Y', Ven1099Box = @Type where VenSysNbr not in (1,2) and " + GroupWhere(r.Groups, p),
                            p.ToArray());
                        _session.WriteUtilityLog(Truncate("Vendor Inactivator: set 1099 type " + r.Type1099 + " on " + result.Changed
                            + " vendor(s) in vendor groups " + string.Join(", ", r.Groups)));
                        break;
                }
            });

            if (r.Mode != InactivatorMode.Set1099)
                result.Exceptions = Exceptions(r);
            return result;
        }

        /// <summary>Vendors that met the criteria but stayed active because they still have unpaid vouchers.</summary>
        private DataTable Exceptions(InactivatorRequest r)
        {
            var p = new List<SqlParameter>();
            if (r.Mode == InactivatorMode.ByDate)
            {
                p.Add(Sql.P("@Date", r.Date.Date));
                return _session.Query(
                    "select " + VCols + LastActivityCols + @" from Vendor
                     where VenActive = 'Y' and VenSysNbr not in (1,2)
                       and VenSysNbr in (" + Unpaid + ") and " + DateRule + " order by VenCode", p.ToArray());
            }
            var inList = Sql.InList("v", r.Vendors.Cast<object>(), p);
            return _session.Query(
                "select " + VCols + " from Vendor where VenSysNbr in " + inList + " and VenSysNbr in (" + Unpaid + ") order by VenCode",
                p.ToArray());
        }

        /// <summary>Selected payment groups as a parameterised condition; "NO GROUP" means blank or NULL (original missed NULL).</summary>
        private static string GroupWhere(IEnumerable<string> groups, List<SqlParameter> p)
        {
            var list = groups.ToList();
            var parts = new List<string>();
            var named = list.Where(g => g != NoGroup).Cast<object>().ToList();
            if (named.Count > 0)
                parts.Add("VenPaymentGroup in " + Sql.InList("g", named, p));
            if (list.Contains(NoGroup))
                parts.Add("VenPaymentGroup <= '' or VenPaymentGroup is null");
            return parts.Count == 0 ? "(1 = 0)" : "(" + string.Join(" or ", parts) + ")";
        }

        private static string Truncate(string text) => text.Length <= 7900 ? text : text.Substring(0, 7900) + "…";
    }
}
