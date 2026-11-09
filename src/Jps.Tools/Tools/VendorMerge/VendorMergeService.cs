using System;
using System.Data;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Tools.VendorMerge
{
    public enum Use1099From { KeepVendor, MergeVendor }

    public enum SeparateCheckOption { KeepExisting, ChangeToYes, ChangeToNo }

    public sealed class MergeRequest
    {
        public int Keep { get; set; }
        public int Del { get; set; }
        public string KeepCode { get; set; }
        public string DelCode { get; set; }
        public Use1099From Use1099 { get; set; }
        public SeparateCheckOption SeparateCheck { get; set; }
    }

    /// <summary>The SQL for Vendor Merge: the pre-merge summary and the merge itself (one transaction).</summary>
    public sealed class VendorMergeService
    {
        // Juris's own system vendors; never deleted by a merge.
        private static readonly int[] SystemVendors = { 1, 2 };

        private readonly JurisSession _session;

        public VendorMergeService(JurisSession session)
        {
            _session = session;
        }

        public static bool IsSystemVendor(int venSysNbr) => SystemVendors.Contains(venSysNbr);

        /// <summary>What will move from the merge vendor to the keep vendor (row counts per kind of data).</summary>
        public DataTable Summary(MergeRequest r)
        {
            var counts = _session.Query(
                @"select
                    (select count(*) from Voucher            where VchVendor   = @Del) as Vouchers,
                    (select count(*) from CheckRegister      where CkRegVend   = @Del) as Checks,
                    (select count(*) from VoucherBatchDetail where VBDVendor   = @Del) as BatchLines,
                    (select count(*) from MatDisbHistory     where MDHVendor   = @Del) as MatterDisbursements,
                    (select count(*) from VchTemplate        where VTVchVendor = @Del) as RecurringTemplates,
                    (select count(*) from VenNote            where VNVendor    = @Del) as Notes,
                    (select count(*) from VenNote d where d.VNVendor = @Del
                        and exists (select 1 from VenNote k where k.VNVendor = @Keep and k.VNNoteIndex = d.VNNoteIndex)) as NoteClashes,
                    (select count(*) from Ven1099            where V99Vendor   = @Del) as Years1099,
                    (select count(*) from VenSumByPrd        where VSPVendor   = @Del) as PeriodTotals,
                    (select count(*) from VendorUDF          where VUDFVenSysNbr = @Del) as UdfRows,
                    (select count(*) from VendorUDF          where VUDFVenSysNbr = @Keep) as KeepUdfRows,
                    (select count(distinct d.VchInvoiceNbr) from Voucher d
                        where d.VchVendor = @Del and isnull(d.VchInvoiceNbr, '') <> ''
                          and exists (select 1 from Voucher k where k.VchVendor = @Keep and k.VchInvoiceNbr = d.VchInvoiceNbr)) as SharedInvoices",
                Sql.P("@Keep", r.Keep), Sql.P("@Del", r.Del)).Rows().First();

            int N(string col) => Convert.ToInt32(counts[col]);

            var t = new DataTable();
            t.Columns.Add("What");
            t.Columns.Add("How Many", typeof(int));
            t.Columns.Add("What Happens");
            t.Rows.Add("Vouchers", N("Vouchers"), "Moved to " + r.KeepCode);
            t.Rows.Add("Checks", N("Checks"), "Moved to " + r.KeepCode);
            t.Rows.Add("Voucher batch lines", N("BatchLines"), "Moved to " + r.KeepCode);
            t.Rows.Add("Matter disbursement history", N("MatterDisbursements"), "Moved to " + r.KeepCode);
            t.Rows.Add("Recurring voucher templates", N("RecurringTemplates"), "Moved to " + r.KeepCode);
            t.Rows.Add("Vendor notes", N("Notes"), N("NoteClashes") == 0
                ? "Moved to " + r.KeepCode
                : "Moved to " + r.KeepCode + "; " + N("NoteClashes") + " note(s) with the same name as one of " + r.KeepCode + "'s are renamed (name-1, name-2...)");
            t.Rows.Add("1099 totals by year", N("Years1099"), "Added to " + r.KeepCode + "'s totals for the same year");
            t.Rows.Add("Totals by period", N("PeriodTotals"), "Added to " + r.KeepCode + "'s totals for the same period");
            t.Rows.Add("User-defined fields", N("UdfRows"), N("UdfRows") == 0 ? "-"
                : N("KeepUdfRows") > 0 ? r.KeepCode + "'s values are kept; " + r.DelCode + "'s are discarded" : "Moved to " + r.KeepCode);
            t.Rows.Add("1099 settings", DBNull.Value, r.Use1099 == Use1099From.MergeVendor
                ? "Copied from " + r.DelCode + " (gets 1099 and 1099 box)" : r.KeepCode + "'s settings are kept");
            t.Rows.Add("Separate checks", DBNull.Value, r.SeparateCheck == SeparateCheckOption.ChangeToYes ? "Set to Yes on " + r.KeepCode
                : r.SeparateCheck == SeparateCheckOption.ChangeToNo ? "Set to No on " + r.KeepCode : r.KeepCode + "'s setting is kept");
            if (N("SharedInvoices") > 0)
                t.Rows.Add("Invoice numbers both vendors use", N("SharedInvoices"), r.KeepCode + " will have more than one voucher with these invoice numbers");
            t.Rows.Add("Vendor " + r.DelCode, 1, "Deleted");
            return t;
        }

        /// <summary>Runs the whole merge in one transaction. Throws (after rollback) on any problem.</summary>
        public void Merge(MergeRequest r)
        {
            if (r.Keep == r.Del)
                throw new InvalidOperationException("The vendor to keep and the vendor to merge into it can't be the same vendor.");
            if (IsSystemVendor(r.Del))
                throw new InvalidOperationException("Vendor " + r.DelCode + " is a Juris system vendor and can't be merged away.");

            _session.InTransaction(() =>
            {
                // No SET XACT_ABORT here: it would stay on the shared connection. Any SQL error throws, and
                // InTransaction rolls the whole merge back.

                // Both vendors must still exist (someone may have changed them since the lists were loaded). The rows stay
                // locked (updlock, holdlock) until commit, so nobody can change or delete either vendor mid-merge.
                var found = Convert.ToInt32(Scalar("select count(*) from Vendor with (updlock, holdlock) where VenSysNbr in (@Keep, @Del)", r));
                if (found != 2)
                    throw new InvalidOperationException("One of the vendors no longer exists. Reload the page and try again.");

                // A temporary vendor's vouchers carry their payee in VchTemporaryVendor; merging into or out of one would
                // leave vouchers with no real payee.
                if (Convert.ToInt32(Scalar("select count(*) from Vendor where VenSysNbr in (@Keep, @Del) and VenIsTemp = 'Y'", r)) > 0)
                    throw new InvalidOperationException("One of the vendors is a temporary vendor. Temporary vendors can't be merged.");

                // Vouchers of the merge vendor waiting in an unposted or locked check batch: the batch already has a check
                // made out to the merge vendor. Make the merge wait until that batch is posted or deleted.
                var pendingChecks = Convert.ToInt32(Scalar(
                    @"select count(distinct cb.CBBatchNbr)
                      from CBCheckDetail d
                      inner join CheckBatch cb on cb.CBBatchNbr = d.CBCDBatch
                      inner join Voucher v on v.VchVoucherNbr = d.CBCDVoucher
                      where v.VchVendor = @Del and cb.CBStatus in ('U', 'L')", r));
                if (pendingChecks > 0)
                    throw new InvalidOperationException("Vendor " + r.DelCode + " has vouchers in " + pendingChecks
                        + " unposted check batch(es). Post or delete those check batches first, then merge.");

                // Step 0 - separate-check override.
                if (r.SeparateCheck != SeparateCheckOption.KeepExisting)
                    _session.Execute("update Vendor set VenSeparateCheck = @Sep where VenSysNbr = @Keep",
                        Sql.P("@Sep", r.SeparateCheck == SeparateCheckOption.ChangeToYes ? "Y" : "N"), Sql.P("@Keep", r.Keep));

                // Step 1 - repoint transactional references.
                Exec("update Voucher            set VchVendor   = @Keep where VchVendor   = @Del", r);
                Exec("update CheckRegister      set CkRegVend   = @Keep where CkRegVend   = @Del", r);
                Exec("update VoucherBatchDetail set VBDVendor   = @Keep where VBDVendor   = @Del", r);
                Exec("update MatDisbHistory     set MDHVendor   = @Keep where MDHVendor   = @Del", r);
                Exec("update VchTemplate        set VTVchVendor = @Keep where VTVchVendor = @Del", r);

                // Step 2 - notes; rename merge-vendor note indexes that clash with the keep vendor's.
                var clashes = Query(@"select VNNoteIndex from VenNote
                                      where VNVendor = @Del and VNNoteIndex in (select VNNoteIndex from VenNote where VNVendor = @Keep)", r)
                    .Rows().Select(row => Convert.ToString(row["VNNoteIndex"])).ToList();
                foreach (var old in clashes)
                {
                    var trimmed = old.TrimEnd();
                    var stem = trimmed.Length > 12 ? trimmed.Substring(0, 12) : trimmed;
                    string fresh = null;
                    for (var n = 1; n <= 99 && fresh == null; n++)
                    {
                        var candidate = stem + "-" + n;
                        var taken = _session.Scalar("select 1 from VenNote where VNVendor in (@Keep, @Del) and VNNoteIndex = @New",
                            Sql.P("@Keep", r.Keep), Sql.P("@Del", r.Del), Sql.P("@New", candidate));
                        if (taken == null) fresh = candidate;
                    }
                    if (fresh == null)
                        throw new InvalidOperationException("Couldn't find a free name for vendor note \"" + trimmed + "\".");
                    _session.Execute("update VenNote set VNNoteIndex = @New where VNVendor = @Del and VNNoteIndex = @Old",
                        Sql.P("@New", fresh), Sql.P("@Del", r.Del), Sql.P("@Old", old));
                }
                Exec("update VenNote set VNVendor = @Keep where VNVendor = @Del", r);

                // Step 3 - 1099 settings from the merge vendor (box AND gets-1099 flag).
                if (r.Use1099 == Use1099From.MergeVendor)
                    Exec(@"update Vendor set Ven1099Box = DV.Box, VenGets1099 = DV.Gets
                           from (select Ven1099Box as Box, VenGets1099 as Gets from Vendor where VenSysNbr = @Del) DV
                           where Vendor.VenSysNbr = @Keep", r);

                // Step 4 - 1099 amounts per year: insert-or-update onto the keep vendor.
                var years = Query(@"select V99Year,
                                           sum(V99AmountPaid)       as paid,
                                           sum(V99SupplementAmount) as supp
                                    from Ven1099 where V99Vendor in (@Del, @Keep) group by V99Year", r);
                foreach (var y in years.Rows())
                    _session.Execute(
                        @"if exists (select 1 from Ven1099 where V99Vendor = @Keep and V99Year = @Year)
                              update Ven1099 set V99AmountPaid = @Paid, V99SupplementAmount = @Supp where V99Vendor = @Keep and V99Year = @Year;
                          else
                              insert into Ven1099 (V99Vendor, V99Year, V99AmountPaid, V99SupplementAmount) values (@Keep, @Year, @Paid, @Supp);",
                        Sql.P("@Keep", r.Keep), Sql.P("@Year", y["V99Year"]), Sql.P("@Paid", y["paid"]), Sql.P("@Supp", y["supp"]));

                // Step 5 - totals by period.
                var periods = Query(@"select VSPPrdYear, VSPPrdNbr,
                                             sum(VSPVouchers)       as vouch,
                                             sum(VSPPayments)       as pymt,
                                             sum(VSPDiscountsTaken) as disc
                                      from VenSumByPrd where VSPVendor in (@Del, @Keep)
                                      group by VSPPrdYear, VSPPrdNbr order by VSPPrdYear, VSPPrdNbr", r);
                foreach (var p in periods.Rows())
                    _session.Execute(
                        @"if exists (select 1 from VenSumByPrd where VSPVendor = @Keep and VSPPrdYear = @Y and VSPPrdNbr = @P)
                              update VenSumByPrd set VSPVouchers = @Vouch, VSPPayments = @Pymt, VSPDiscountsTaken = @Disc
                              where VSPVendor = @Keep and VSPPrdYear = @Y and VSPPrdNbr = @P;
                          else
                              insert into VenSumByPrd (VSPVendor, VSPPrdYear, VSPPrdNbr, VSPVouchers, VSPPayments, VSPDiscountsTaken)
                              values (@Keep, @Y, @P, @Vouch, @Pymt, @Disc);",
                        Sql.P("@Keep", r.Keep), Sql.P("@Y", p["VSPPrdYear"]), Sql.P("@P", p["VSPPrdNbr"]),
                        Sql.P("@Vouch", p["vouch"]), Sql.P("@Pymt", p["pymt"]), Sql.P("@Disc", p["disc"]));

                // Step 6 - user-defined fields: keep vendor's row wins; move the merge vendor's only if keep has none.
                Exec(@"update VendorUDF set VUDFVenSysNbr = @Keep
                       where VUDFVenSysNbr = @Del and not exists (select 1 from VendorUDF where VUDFVenSysNbr = @Keep)", r);
                Exec("delete from VendorUDF where VUDFVenSysNbr = @Del", r);

                // Step 7 - last purchase date.
                Exec(@"update Vendor set VenLastPurchaseDate = (select max(VenLastPurchaseDate) from Vendor where VenSysNbr in (@Keep, @Del))
                       where VenSysNbr = @Keep", r);

                // Step 8 - Juris document tree: move anything filed under the merge vendor's node to the keep vendor's node,
                // so nothing is orphaned; refuse if there is nowhere to move it.
                var children = Convert.ToInt32(Scalar(
                    @"select count(*) from DocumentTree c
                      where c.DTParentID in (select DTDocID from DocumentTree where DTDocClass = 7000 and DTKeyL = @Del)", r));
                if (children > 0)
                {
                    var keepNode = Scalar("select min(DTDocID) from DocumentTree where DTDocClass = 7000 and DTKeyL = @Keep", r);
                    if (keepNode == null)
                        throw new InvalidOperationException("Vendor " + r.DelCode + " has items filed under it in the Juris Tables view, and vendor "
                            + r.KeepCode + " has no entry there to move them to.");
                    _session.Execute(
                        @"update DocumentTree set DTParentID = @KeepNode
                          where DTParentID in (select DTDocID from DocumentTree where DTDocClass = 7000 and DTKeyL = @Del)",
                        Sql.P("@KeepNode", keepNode), Sql.P("@Del", r.Del));
                }

                // Step 9 - remove the merge vendor (same order as Juris's qdVendor_CASCADE).
                Exec("delete from Ven1099     where V99Vendor = @Del", r);
                Exec("delete from VenNote     where VNVendor  = @Del", r);
                Exec("delete from VenSumByPrd where VSPVendor = @Del", r);

                // Nothing may still point at the merge vendor. Foreign keys only guard some of these (VchTemplate has none),
                // so check every vendor column explicitly before the vendor row goes.
                var left = Query(
                    @"select 'vouchers' as What, count(*) as N from Voucher where VchVendor = @Del
                      union all select 'checks', count(*) from CheckRegister where CkRegVend = @Del
                      union all select 'voucher batch lines', count(*) from VoucherBatchDetail where VBDVendor = @Del
                      union all select 'matter disbursement history', count(*) from MatDisbHistory where MDHVendor = @Del
                      union all select 'recurring voucher templates', count(*) from VchTemplate where VTVchVendor = @Del
                      union all select 'user-defined fields', count(*) from VendorUDF where VUDFVenSysNbr = @Del
                      union all select 'Tables-view items', count(*) from DocumentTree c
                          where c.DTParentID in (select DTDocID from DocumentTree where DTDocClass = 7000 and DTKeyL = @Del)", r)
                    .Rows().Where(row => Convert.ToInt32(row["N"]) > 0)
                    .Select(row => Convert.ToInt32(row["N"]) + " " + Convert.ToString(row["What"])).ToList();
                if (left.Count > 0)
                    throw new InvalidOperationException("Vendor " + r.DelCode + " still has " + string.Join(", ", left)
                        + " after the merge steps, so it was not deleted.");

                Exec("delete from Vendor      where VenSysNbr = @Del", r);
                Exec("delete from DocumentTree where DTDocClass = 7000 and DTKeyL = @Del", r);

                _session.WriteUtilityLog("Vendor Merge: merged vendor " + r.DelCode + " (" + r.Del + ") into vendor " + r.KeepCode + " (" + r.Keep
                    + ") and deleted vendor " + r.DelCode);
            });
        }

        private void Exec(string sql, MergeRequest r) => _session.Execute(sql, Sql.P("@Keep", r.Keep), Sql.P("@Del", r.Del));

        private DataTable Query(string sql, MergeRequest r) => _session.Query(sql, Sql.P("@Keep", r.Keep), Sql.P("@Del", r.Del));

        private object Scalar(string sql, MergeRequest r) => _session.Scalar(sql, Sql.P("@Keep", r.Keep), Sql.P("@Del", r.Del));
    }
}
