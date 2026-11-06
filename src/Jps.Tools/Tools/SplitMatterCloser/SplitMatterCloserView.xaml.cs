using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.SplitMatterCloser
{
    public sealed class SplitMatterCloserTool : IJurisTool
    {
        public string Id => "JPS.SPLITCLOSER";
        public FrameworkElement CreateView(IToolContext context) => new SplitMatterCloserView(context);
    }

    /// <summary>
    /// Removes split billing and closes whole split families (parent + every child) picked by client (all or chosen matters),
    /// billing timekeeper (optionally opened on/before a date) or last bill date (optionally plus never-billed matters opened
    /// before a date). Ported from the published "JPS – Split Matter Closer" (behaviour/UI) and the unfinished "JPS – Close
    /// Split Matters" spec, with these fixes: the parent's balances are now checked as well as the children's, and a family is
    /// closed all-or-nothing in one transaction per family (closes, notes and the SplitBill delete commit together); open
    /// pre-bills and membership of another split now block closing; skipped matters show the reasons that ARE outstanding
    /// (not the inverted list); no ## global temp tables (a failed run no longer breaks the next one or collides with another
    /// user); the billing timekeeper list is the split-only list and no longer crashes when empty; matter notes use a unique
    /// ToolNoteN index and parameters (apostrophes in names broke the old SQL); a post-report lists every matter not closed;
    /// a UtilityLog line is written per run.
    /// </summary>
    public partial class SplitMatterCloserView : UserControl
    {
        private readonly IToolContext _context;
        private readonly SplitMatterCloserService _service;
        private readonly bool _ready;
        private bool _loaded;
        private DataTable _notClosed;

        private JurisSession Session => _context.Session;

        public SplitMatterCloserView(IToolContext context)
        {
            _context = context;
            _service = new SplitMatterCloserService(context.Session);
            InitializeComponent();
            OpenedDate.SelectedDate = DateTime.Today;
            LastBillDate.SelectedDate = DateTime.Today;
            NeverBilledDate.SelectedDate = DateTime.Today;
            _ready = true;
            Loaded += async (_, __) =>
            {
                if (_loaded) return;
                _loaded = true;
                await ReloadListsAsync();
                if (!Frame.HasMessage && ClientBox.Items.Count == 0)
                    Frame.ShowInfo("No matters in this company are split-billed, so there's nothing to close.");
            };
        }

        private async System.Threading.Tasks.Task ReloadListsAsync()
        {
            var lists = await Frame.RunAsync("Loading split matters…", () => new
            {
                Clients = Lookup.Load(Session, SplitMatterCloserService.ClientsSql),
                Timekeepers = Lookup.Load(Session, SplitMatterCloserService.TimekeepersSql),
            });
            if (lists == null) return;

            var client = (ClientBox.SelectedItem as LookupItem)?.Key;
            var tkpr = (TimekeeperBox.SelectedItem as LookupItem)?.Key;
            ClientBox.ItemsSource = lists.Clients;
            TimekeeperBox.ItemsSource = lists.Timekeepers;
            ClientBox.SelectedItem = lists.Clients.FirstOrDefault(i => Equals(i.Key, client)) ?? lists.Clients.FirstOrDefault();
            TimekeeperBox.SelectedItem = lists.Timekeepers.FirstOrDefault(i => Equals(i.Key, tkpr)) ?? lists.Timekeepers.FirstOrDefault();
            if (ClientBox.SelectedItem == null) MatterList.ItemsSource = null;
            UpdateState();
        }

        private async void OnClientChanged(object sender, SelectionChangedEventArgs e)
        {
            // A new client always starts with an empty pick (the original kept the previous client's matters).
            MatterList.ItemsSource = null;
            UpdateState();
            var client = (ClientBox.SelectedItem as LookupItem)?.Key;
            if (client == null) return;
            var matters = await Frame.RunAsync("Loading matters…", () =>
                Lookup.Load(Session, SplitMatterCloserService.ClientMattersSql, Sql.P("@cli", client)));
            if (matters == null || !Equals((ClientBox.SelectedItem as LookupItem)?.Key, client)) return;
            MatterList.ItemsSource = matters;
            UpdateState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateState();

        private void OnMattersChanged(object sender, EventArgs e) => UpdateState();

        private void UpdateState()
        {
            if (!_ready) return;
            ClientPanel.Visibility = ByClient.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            TimekeeperPanel.Visibility = ByTimekeeper.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            LastBillPanel.Visibility = ByLastBill.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            MatterList.Visibility = PickMatters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.IsEnabled = Validate() == null;
        }

        /// <summary>Returns why the inputs aren't usable yet, or null when they are.</summary>
        private string Validate()
        {
            if (ByClient.IsChecked == true)
            {
                if (ClientBox.SelectedItem == null) return "Pick a client.";
                if (PickMatters.IsChecked == true && MatterList.Checked.Count == 0) return "Tick at least one matter, or choose every split matter for the client.";
                return null;
            }
            if (ByTimekeeper.IsChecked == true)
            {
                if (TimekeeperBox.SelectedItem == null) return "Pick a billing timekeeper.";
                if (OpenedCheck.IsChecked == true && OpenedDate.SelectedDate == null) return "Enter the opened-on-or-before date.";
                return null;
            }
            if (LastBillDate.SelectedDate == null) return "Enter the last bill date.";
            if (NeverBilledCheck.IsChecked == true && NeverBilledDate.SelectedDate == null) return "Enter the opened-before date for never-billed matters.";
            return null;
        }

        private SplitCriteria Capture()
        {
            var criteria = new SplitCriteria();
            if (ByClient.IsChecked == true)
            {
                criteria.ClientKey = ((LookupItem)ClientBox.SelectedItem).Key;
                if (PickMatters.IsChecked == true)
                {
                    criteria.Mode = SplitSelectMode.ClientMatters;
                    criteria.MatterKeys = MatterList.Checked.Select(i => i.Key).ToList();
                }
                else
                    criteria.Mode = SplitSelectMode.ClientAll;
            }
            else if (ByTimekeeper.IsChecked == true)
            {
                criteria.Mode = SplitSelectMode.BillingTimekeeper;
                criteria.TimekeeperKey = ((LookupItem)TimekeeperBox.SelectedItem).Key;
                criteria.OpenedOnOrBefore = OpenedCheck.IsChecked == true ? OpenedDate.SelectedDate : null;
            }
            else
            {
                criteria.Mode = SplitSelectMode.LastBillDate;
                criteria.LastBillOnOrBefore = LastBillDate.SelectedDate ?? DateTime.Today;
                criteria.NeverBilledOpenedBefore = NeverBilledCheck.IsChecked == true ? NeverBilledDate.SelectedDate : null;
            }
            return criteria;
        }

        private async void OnClose(object sender, RoutedEventArgs e)
        {
            var problem = Validate();
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return;
            }
            var criteria = Capture();

            var families = await Frame.RunAsync("Finding split families…", () => _service.FamiliesReport(criteria));
            if (families == null) return;
            var parents = SplitMatterCloserService.ParentsIn(families);
            families.Columns.Remove(SplitMatterCloserService.ParentIdColumn); // internal number, not for the report
            if (parents.Count == 0)
            {
                Frame.ShowWarning("No split families match what you picked. Nothing was changed.");
                return;
            }

            if (!ReportWindow.Confirm(this, "Split families to close", families,
                    parents.Count + (parents.Count == 1 ? " split family" : " split families") + " found. Each family is closed only if every matter in it is clear; "
                    + "the others are left exactly as they are and listed afterwards.",
                    "Close these families"))
                return;

            var today = DateTime.Today;
            var result = await Frame.RunAsync("Closing split families…", () => _service.Close(parents, today));
            if (result == null) return;

            _notClosed = result.NotClosed;
            NotClosedButton.IsEnabled = _notClosed.Rows.Count > 0;
            await ReloadListsAsync();

            var closed = "Closed " + result.FamiliesClosed + (result.FamiliesClosed == 1 ? " split family" : " split families")
                         + " (" + result.MattersClosed + " matters) and removed their splits.";
            if (result.FamiliesSkipped == 0)
            {
                Frame.ShowSuccess(closed);
                return;
            }
            Frame.ShowWarning(closed + " " + result.FamiliesSkipped + (result.FamiliesSkipped == 1 ? " family wasn't" : " families weren't")
                              + " closed because a matter in it still has balances or open items. Nothing in those families was changed.");
            if (await Dialogs.ConfirmAsync("Some matters couldn't be closed",
                    "Some matters still have balances or open items, so their split families were left open. Show the details?", "Show details"))
                ShowNotClosed();
        }

        private void OnShowNotClosed(object sender, RoutedEventArgs e) => ShowNotClosed();

        private void ShowNotClosed()
        {
            if (_notClosed == null) return;
            ReportWindow.Show(this, "Matters not closed", _notClosed,
                "Every matter in each split family that was left open, with what is still outstanding (see \"Why It Wasn't Closed\").");
        }
    }
}
