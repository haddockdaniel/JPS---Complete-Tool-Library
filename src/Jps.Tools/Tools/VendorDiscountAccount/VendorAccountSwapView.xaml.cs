using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.VendorDiscountAccount
{
    /// <summary>
    /// Shared page for the two vendor GL account swap tools (Vendor Discount Account Updater and Vendor GL Default Dist
    /// Account Updater): moves every vendor whose discount / default distribution GL account is X to account Y, or sets
    /// every vendor to Y. Ported with these fixes: the change could run with no new account picked and wipe every vendor's
    /// account to NULL (Dist tool); accounts were re-found from their display text, which failed when several chart rows
    /// share a main account, and values were concatenated into SQL (now ChtSysNbr is used as a parameter); an empty chart
    /// crashed on load; SQL errors crashed the app; nothing was written to the UtilityLog; the two tools disagreed on
    /// whether system vendors 1 and 2 are changed (now both leave them alone).
    /// </summary>
    public partial class VendorAccountSwapView : UserControl
    {
        private static readonly LookupItem AllVendors = new LookupItem(null, "", "Every vendor, whatever account it has now");

        private readonly VendorAccountField _field;
        private readonly VendorAccountSwapService _service;
        private int _countVersion;

        public VendorAccountSwapView(IToolContext context, VendorAccountField field)
        {
            _field = field;
            _service = new VendorAccountSwapService(context.Session, field);
            InitializeComponent();
            FromTitle.Text = "1  Pick the " + field.AccountName + " to replace";
            ToTitle.Text = "2  Pick the new " + field.AccountName;
            ToHint.Text = "Only the vendor's " + field.AccountName + " setting changes. Existing vouchers, voucher templates and history are not touched. "
                          + "Juris system vendors 1 and 2 are never changed.";
            Loaded += async (_, __) => await LoadAccountsAsync();
        }

        private async System.Threading.Tasks.Task LoadAccountsAsync()
        {
            var accounts = await Frame.RunAsync("Loading the chart of accounts…", () => _service.LoadAccounts());
            if (accounts == null) return;
            FromBox.ItemsSource = new[] { AllVendors }.Concat(accounts).ToList();
            ToBox.ItemsSource = accounts;
            if (accounts.Count == 0)
                Frame.ShowWarning("This company has no chart of accounts set up, so there's nothing to pick.");
        }

        private LookupItem FromItem => FromBox.SelectedItem as LookupItem;
        private LookupItem ToItem => ToBox.SelectedItem as LookupItem;
        private static int? KeyOf(LookupItem item) => item == null || item == AllVendors ? (int?)null : Convert.ToInt32(item.Key);

        private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            FromHint.Text = "";
            Frame.ClearMessage();

            var fromItem = FromItem;
            var toItem = ToItem;
            if (fromItem == null || toItem == null) return;
            if (fromItem == toItem || (fromItem != AllVendors && Equals(KeyOf(fromItem), KeyOf(toItem))))
            {
                Frame.ShowWarning("The new account is the same as the one being replaced, so there's nothing to change.");
                return;
            }

            var from = KeyOf(fromItem);
            var to = KeyOf(toItem).Value;
            var version = ++_countVersion;
            var count = await Frame.RunAsync("Counting vendors…", () => (int?)_service.CountAffected(from, to));
            if (version != _countVersion || count == null) return;

            FromHint.Text = count == 0
                ? "No vendors would change."
                : count + " vendor(s) will be changed to " + toItem.Display + ".";
            ApplyButton.IsEnabled = count > 0;
            PreviewButton.IsEnabled = count > 0;
        }

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var fromItem = FromItem;
            var toItem = ToItem;
            if (fromItem == null || toItem == null) return;
            var from = KeyOf(fromItem);
            var to = KeyOf(toItem).Value;
            var table = await Frame.RunAsync("Finding vendors…", () => _service.AffectedVendors(from, to));
            if (table == null) return;
            ReportWindow.Show(this, "Vendors whose " + _field.AccountName + " will change", table,
                (fromItem == AllVendors ? "All vendors" : "From " + fromItem.Display) + "  →  " + toItem.Display);
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            var fromItem = FromItem;
            var toItem = ToItem;
            if (fromItem == null || toItem == null) return;
            var from = KeyOf(fromItem);
            var to = KeyOf(toItem).Value;
            var fromText = fromItem.Display;
            var toText = toItem.Display;

            var count = await Frame.RunAsync("Counting vendors…", () => (int?)_service.CountAffected(from, to));
            if (count == null) return;
            if (count == 0)
            {
                Frame.ShowWarning("No vendors would change, so nothing was done.");
                return;
            }

            var message = from == null
                ? "This will give every vendor (" + count + ") the " + _field.AccountName + " " + toText + ", including vendors that have none today."
                : "This will change the " + _field.AccountName + " on " + count + " vendor(s) from " + fromText + " to " + toText + ".";
            if (!await Dialogs.ConfirmAsync("Change " + count + " vendor(s)?", message + " This can't be undone from here.", "Change vendors"))
                return;

            var changed = await Frame.RunAsync("Updating vendors…", () => (int?)_service.Apply(from, to, fromText, toText));
            if (changed == null || Frame.HasMessage) return; // error already shown

            FromBox.SelectedItem = null; // resets the counts and buttons
            Frame.ShowSuccess(changed == 0
                ? "No vendors needed changing."
                : "Changed the " + _field.AccountName + " to " + toText + " on " + changed + " vendor(s).");
        }
    }
}
