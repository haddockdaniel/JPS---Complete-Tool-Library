using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.VendorInactivator
{
    public sealed class VendorInactivator : IJurisTool
    {
        public string Id => "JPS.VENINACT";
        public FrameworkElement CreateView(IToolContext context) => new VendorInactivatorView(context);
    }

    /// <summary>
    /// Inactivates vendors by date of last activity (last voucher invoice date or last check date on or before a cut-off) or
    /// from a picked list, skipping vendors with unpaid vouchers and listing them afterwards; or sets every vendor in the chosen
    /// payment groups to receive a 1099 of a chosen type. Juris system vendors 1 and 2 are never touched. Ported from
    /// "JPS - Vendor Inactivator v10" with these fixes: the check-date rule could inactivate system vendors (D1); an empty
    /// selection crashed with an SQL syntax error (D2); vendors with a NULL payment group were never updated under "NO GROUP"
    /// (D3); the by-date pre-report did not match what Run changed (D4); the 1099 update included system vendors (D5); codes and
    /// group names were concatenated into SQL (D6); no transaction and no UtilityLog entry (D7); the by-date exception report
    /// also listed inactive and system vendors (D8).
    /// </summary>
    public partial class VendorInactivatorView : UserControl
    {
        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;
        private bool _ready;

        public VendorInactivatorView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            CutOff.SelectedDate = DateTime.Today;
            TypeBox.ItemsSource = new List<LookupItem>
            {
                new LookupItem(0, "0", "Non-employee compensation"),
                new LookupItem(1, "1", "Gross proceeds paid to an attorney"),
                new LookupItem(2, "2", "Rents"),
                new LookupItem(3, "3", "Other income"),
            };
            VendorList.SelectionChanged += (_, __) => UpdateState();
            GroupList.SelectionChanged += (_, __) => UpdateState();
            Loaded += async (_, __) =>
            {
                if (_ready) return;
                _ready = true;
                UpdateState();
                await LoadListsAsync();
            };
        }

        private async Task LoadListsAsync()
        {
            var lists = await Frame.RunAsync("Loading vendors…", () => new
            {
                Vendors = Lookup.Load(Session,
                    @"select VenSysNbr, dbo.jfn_FormatVendorCode(VenCode),
                             VenName + case when VenGets1099 = 'Y' then '  (gets 1099)' else '' end
                      from Vendor where VenActive = 'Y' and VenSysNbr not in (1,2) order by VenCode"),
                Groups = Lookup.Load(Session,
                    @"select g from (select case when VenPaymentGroup <= '' or VenPaymentGroup is null then 'NO GROUP' else VenPaymentGroup end as g
                                        from Vendor) x group by g order by g"),
            });
            if (lists == null) return;
            VendorList.ItemsSource = lists.Vendors;
            GroupList.ItemsSource = lists.Groups;
            UpdateState();
        }

        private InactivatorMode Mode => Mode1099.IsChecked == true ? InactivatorMode.Set1099
            : ByName.IsChecked == true ? InactivatorMode.ByName : InactivatorMode.ByDate;

        private void OnModeChanged(object sender, RoutedEventArgs e) => UpdateState();

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateState();

        /// <summary>Shows the active mode's panel and enables the buttons only when the inputs are complete.</summary>
        private void UpdateState()
        {
            if (!_ready) return;
            var mode = Mode;
            InactivatePanel.Visibility = mode == InactivatorMode.Set1099 ? Visibility.Collapsed : Visibility.Visible;
            Panel1099.Visibility = mode == InactivatorMode.Set1099 ? Visibility.Visible : Visibility.Collapsed;
            DatePanel.Visibility = mode == InactivatorMode.ByDate ? Visibility.Visible : Visibility.Collapsed;
            NamePanel.Visibility = mode == InactivatorMode.ByName ? Visibility.Visible : Visibility.Collapsed;
            RunButton.Content = mode == InactivatorMode.Set1099 ? "Set 1099 type" : "Inactivate vendors";

            var ok = mode == InactivatorMode.ByDate ? CutOff.SelectedDate != null
                : mode == InactivatorMode.ByName ? VendorList.Checked.Count > 0
                : GroupList.Checked.Count > 0 && TypeBox.SelectedItem != null;
            RunButton.IsEnabled = ok;
            PreviewButton.IsEnabled = ok;
        }

        /// <summary>Captures the screen into a request (UI thread), or explains what's missing.</summary>
        private InactivatorRequest BuildRequest()
        {
            var mode = Mode;
            var request = new InactivatorRequest { Mode = mode };
            switch (mode)
            {
                case InactivatorMode.ByDate:
                    if (CutOff.SelectedDate == null) { Frame.ShowWarning("Please pick the date of last activity."); return null; }
                    request.Date = CutOff.SelectedDate.Value.Date;
                    break;
                case InactivatorMode.ByName:
                    var picked = VendorList.Checked;
                    if (picked.Count == 0) { Frame.ShowWarning("Please select at least one vendor from the list."); return null; }
                    request.Vendors = picked.Select(i => Convert.ToInt32(i.Key)).ToList();
                    request.VendorCodes = picked.Select(i => i.Code).ToList();
                    break;
                default:
                    var groups = GroupList.Checked;
                    if (groups.Count == 0) { Frame.ShowWarning("Please select at least one vendor group from the list."); return null; }
                    if (!(TypeBox.SelectedItem is LookupItem type)) { Frame.ShowWarning("Please choose the 1099 type to give these vendors."); return null; }
                    request.Groups = groups.Select(i => Convert.ToString(i.Key)).ToList();
                    request.Type1099 = Convert.ToInt32(type.Key);
                    request.Type1099Text = type.Display;
                    break;
            }
            return request;
        }

        private static string ReportTitle(InactivatorRequest r) =>
            r.Mode == InactivatorMode.Set1099 ? "Vendors that will receive a 1099" : "Vendors to inactivate";

        private static string ReportSubtitle(InactivatorRequest r) =>
            r.Mode == InactivatorMode.ByDate ? "Active vendors with no activity after " + r.Date.ToString("MM/dd/yyyy") + " and no unpaid vouchers"
            : r.Mode == InactivatorMode.ByName ? "Selected vendors; those with unpaid vouchers stay active"
            : "Vendor groups " + string.Join(", ", r.Groups) + " - 1099 type " + r.Type1099Text;

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var request = BuildRequest();
            if (request == null) return;
            var table = await Frame.RunAsync("Building the preview…", () => new VendorInactivatorService(Session).PreReport(request));
            if (table == null) return;
            ReportWindow.Show(this, ReportTitle(request), table, ReportSubtitle(request));
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var request = BuildRequest();
            if (request == null) return;

            var table = await Frame.RunAsync("Finding the vendors…", () => new VendorInactivatorService(Session).PreReport(request));
            if (table == null) return;
            if (table.Rows.Count == 0)
            {
                Frame.ShowInfo(request.Mode == InactivatorMode.ByDate
                    ? "No active vendors without unpaid vouchers had their last activity on or before " + request.Date.ToString("MM/dd/yyyy") + ". Nothing was changed."
                    : "No vendors match. Nothing was changed.");
                return;
            }
            if (!ReportWindow.Confirm(this, ReportTitle(request), table, ReportSubtitle(request),
                    request.Mode == InactivatorMode.Set1099 ? "Set 1099 type" : "Inactivate vendors"))
                return;

            var result = await Frame.RunAsync(request.Mode == InactivatorMode.Set1099 ? "Updating vendors…" : "Inactivating vendors…",
                () => new VendorInactivatorService(Session).Run(request));
            if (result == null) return;

            await LoadListsAsync();
            if (request.Mode == InactivatorMode.Set1099)
            {
                Frame.ShowSuccess(result.Changed + " vendor(s) now receive a 1099 of type " + request.Type1099Text + ".");
                return;
            }

            var exceptions = result.Exceptions ?? new DataTable();
            if (exceptions.Rows.Count == 0)
            {
                Frame.ShowSuccess(result.Changed + " vendor(s) were inactivated. None had to be skipped.");
                return;
            }
            Frame.ShowWarning(result.Changed + " vendor(s) were inactivated. " + exceptions.Rows.Count
                + " vendor(s) stayed active because they have unpaid vouchers.");
            if (await Dialogs.ConfirmAsync("Some vendors stayed active",
                    exceptions.Rows.Count + " vendor(s) could not be inactivated because they have unpaid vouchers. Would you like to see them?",
                    "Show them"))
                ReportWindow.Show(this, "Vendors not inactivated (unpaid vouchers)", exceptions, ReportSubtitle(request));
        }
    }
}
