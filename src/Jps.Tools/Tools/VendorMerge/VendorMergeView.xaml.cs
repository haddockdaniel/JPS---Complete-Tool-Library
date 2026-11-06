using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.VendorMerge
{
    public sealed class VendorMerge : IJurisTool
    {
        public string Id => "JPS.VENMERGE";
        public FrameworkElement CreateView(IToolContext context) => new VendorMergeView(context);
    }

    /// <summary>
    /// Merges one vendor into another: vouchers, checks, unposted voucher batch lines, matter disbursement history, recurring
    /// voucher templates, notes, 1099 and period totals and user-defined fields move to the vendor being kept, then the merged
    /// vendor is deleted. Ported from "JPS - Vendor Merge" with these fixes: the same vendor could be picked twice (D1); no
    /// transaction, so a failure left a half-merged vendor (D2); recurring voucher templates were not repointed (D3); notes with
    /// the same index on both vendors crashed the merge (D4); 1099 years only the merged vendor had were lost (D5); a VendorUDF
    /// row blocked the vendor delete (D6); "use merge vendor's 1099" copied the box but not the gets-1099 flag (D7); SQL built
    /// from strings with culture-dependent money values (D8/D9); crash with no vendors (D10); no UtilityLog entry (D11).
    /// </summary>
    public partial class VendorMergeView : UserControl
    {
        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;

        public VendorMergeView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                if (_loaded) return;
                _loaded = true;
                await LoadVendorsAsync();
            };
        }

        private bool _loaded;

        private async System.Threading.Tasks.Task LoadVendorsAsync()
        {
            var activeOnly = ActiveOnly.IsChecked == true;
            var sql = "select VenSysNbr, dbo.jfn_FormatVendorCode(VenCode), VenName from Vendor"
                      + (activeOnly ? " where VenActive = 'Y'" : "") + " order by VenCode";
            var vendors = await Frame.RunAsync("Loading vendors…", () => Lookup.Load(Session, sql));
            if (vendors == null) return;
            KeepBox.ItemsSource = vendors;
            MergeBox.ItemsSource = vendors;
            KeepBox.SelectedItem = null;
            MergeBox.SelectedItem = null;
            if (vendors.Count < 2)
                Frame.ShowWarning(activeOnly
                    ? "There aren't two active vendors to merge. Choose \"Include inactive vendors\" to see the rest."
                    : "Juris doesn't have two vendors to merge.");
            UpdateState();
        }

        private async void OnScopeChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            await LoadVendorsAsync();
        }

        private void OnVendorChanged(object sender, SelectionChangedEventArgs e)
        {
            Frame.ClearMessage();
            UpdateState();
        }

        private LookupItem KeepItem => KeepBox.SelectedItem as LookupItem;
        private LookupItem MergeItem => MergeBox.SelectedItem as LookupItem;

        /// <summary>Enable rules: merge list once a keep vendor is chosen; actions once two different vendors are chosen.</summary>
        private void UpdateState()
        {
            MergeBox.IsEnabled = KeepItem != null;
            string problem = null;
            if (KeepItem != null && MergeItem != null)
            {
                if (Equals(KeepItem.Key, MergeItem.Key))
                    problem = "The vendor to keep and the vendor to merge into it can't be the same vendor.";
                else if (VendorMergeService.IsSystemVendor(Convert.ToInt32(MergeItem.Key)))
                    problem = "Vendor " + MergeItem.Code + " is a Juris system vendor and can't be merged away. Pick it as the vendor to keep instead.";
            }
            PickProblem.Text = problem ?? "";
            PickProblem.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            var ok = KeepItem != null && MergeItem != null && problem == null;
            MergeButton.IsEnabled = ok;
            PreviewButton.IsEnabled = ok;
        }

        private MergeRequest BuildRequest()
        {
            if (KeepItem == null || MergeItem == null)
            {
                Frame.ShowWarning("Please pick a vendor in both lists.");
                return null;
            }
            UpdateState();
            if (!MergeButton.IsEnabled) return null;
            return new MergeRequest
            {
                Keep = Convert.ToInt32(KeepItem.Key),
                Del = Convert.ToInt32(MergeItem.Key),
                KeepCode = KeepItem.Code,
                DelCode = MergeItem.Code,
                Use1099 = Use1099Merge.IsChecked == true ? Use1099From.MergeVendor : Use1099From.KeepVendor,
                SeparateCheck = SepYes.IsChecked == true ? SeparateCheckOption.ChangeToYes
                    : SepNo.IsChecked == true ? SeparateCheckOption.ChangeToNo : SeparateCheckOption.KeepExisting,
            };
        }

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var request = BuildRequest();
            if (request == null) return;
            var table = await Frame.RunAsync("Counting what moves…", () => new VendorMergeService(Session).Summary(request));
            if (table == null) return;
            ReportWindow.Show(this, "Vendor merge preview", table, "Merging vendor " + request.DelCode + " into vendor " + request.KeepCode);
        }

        private async void OnMerge(object sender, RoutedEventArgs e)
        {
            var request = BuildRequest();
            if (request == null) return;

            var table = await Frame.RunAsync("Counting what moves…", () => new VendorMergeService(Session).Summary(request));
            if (table == null) return;
            if (!ReportWindow.Confirm(this, "Merge vendor " + request.DelCode + " into " + request.KeepCode + "?", table,
                    "This will merge all transactions and data from vendor " + request.DelCode + " into vendor " + request.KeepCode
                    + ". Vendor " + request.DelCode + " will be deleted. This can't be undone from here.",
                    "Merge vendors"))
                return;

            var done = await Frame.RunAsync("Merging vendor " + request.DelCode + " into " + request.KeepCode + "…", () =>
            {
                try
                {
                    new VendorMergeService(Session).Merge(request);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("The merge failed, so nothing was changed. " + ex.Message, ex);
                }
            });
            if (!done) return;

            await LoadVendorsAsync();
            Frame.ShowSuccess("Vendor " + request.DelCode + " was merged into vendor " + request.KeepCode + " and deleted.");
        }
    }
}
