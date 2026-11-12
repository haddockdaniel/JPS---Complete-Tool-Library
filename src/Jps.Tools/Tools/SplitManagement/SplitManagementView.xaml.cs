using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.SplitManagement
{
    public sealed class SplitManagementTool : IJurisTool
    {
        public string Id => "JPS.SPLITMGMT";
        public FrameworkElement CreateView(IToolContext context) => new SplitManagementView(context);
    }

    /// <summary>
    /// Creates, changes, removes or copies split billing for one matter or every open unlocked matter of a client.
    /// See <see cref="SplitManagementService"/> for what was ported and fixed.
    /// </summary>
    public partial class SplitManagementView : UserControl
    {
        private const int AllMatters = 0;

        private readonly IToolContext _context;
        private readonly SplitManagementService _service;
        private readonly ObservableCollection<SplitRow> _rows = new ObservableCollection<SplitRow>();
        private bool _loaded, _ready, _sourcesLoaded;

        private JurisSession Session => _context.Session;

        public SplitManagementView(IToolContext context)
        {
            _context = context;
            _service = new SplitManagementService(context.Session);
            InitializeComponent();
            SplitGrid.ItemsSource = _rows;
            _rows.CollectionChanged += (_, __) => UpdateState();
            _ready = true;
            Loaded += async (_, __) =>
            {
                if (_loaded) return;
                _loaded = true;
                var clients = await Frame.RunAsync("Loading clients…", () => Lookup.Load(Session, SplitManagementService.ClientsSql));
                if (clients == null) return;
                ClientBox.ItemsSource = clients;
                if (clients.Count == 0) Frame.ShowInfo("No client has an open matter that isn't locked, so there's nothing to split.");
                UpdateState();
            };
        }

        private LookupItem Client => ClientBox.SelectedItem as LookupItem;
        private LookupItem Matter => MatterBox.SelectedItem as LookupItem;
        private bool IsAll => Matter != null && Convert.ToInt32(Matter.Key) == AllMatters;

        private async void OnClientChanged(object sender, SelectionChangedEventArgs e)
        {
            MatterBox.ItemsSource = null;
            _rows.Clear();
            var client = Client;
            if (client == null) return;
            var matters = await Frame.RunAsync("Loading matters…", () => Lookup.Load(Session, SplitManagementService.MattersSql, Sql.P("@cli", client.Key)));
            if (matters == null || Client != client) return;
            matters.Insert(0, new LookupItem(AllMatters, "All", "every open matter of this client that isn't locked"));
            MatterBox.ItemsSource = matters;
            // The original started on "All"; start on the first real matter when there is only one, otherwise on All.
            MatterBox.SelectedIndex = matters.Count == 2 ? 1 : 0;
        }

        private async void OnMatterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModeCopy.IsChecked == true)
            {
                await LoadCopyAsync();
                return;
            }
            await LoadCurrentAsync();
        }

        /// <summary>Update mode: the matter's current splits, or the XXXX template for all matters.</summary>
        private async System.Threading.Tasks.Task LoadCurrentAsync()
        {
            _rows.Clear();
            var client = Client;
            var matter = Matter;
            UpdateState();
            if (client == null || matter == null) return;
            var all = IsAll;
            var rows = await Frame.RunAsync("Loading splits…", () => all
                ? _service.AllMattersTemplate(Convert.ToInt32(client.Key), client.Code)
                : _service.CurrentSplits(Convert.ToInt32(matter.Key)));
            if (rows == null || Client != client || Matter != matter || ModeCopy.IsChecked == true) return;
            foreach (var r in rows) _rows.Add(r);
            UpdateState();
        }

        private async void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (ModeCopy.IsChecked == true)
            {
                if (!_sourcesLoaded)
                {
                    var sources = await Frame.RunAsync("Loading matters with splits…", () => Lookup.Load(Session, SplitManagementService.SourceClientsSql));
                    if (sources == null) return;
                    SourceClientBox.ItemsSource = sources;
                    _sourcesLoaded = true;
                }
                await LoadCopyAsync();
            }
            else
            {
                SourceGrid.ItemsSource = null;
                await LoadCurrentAsync();
            }
        }

        private async void OnSourceClientChanged(object sender, SelectionChangedEventArgs e)
        {
            SourceMatterBox.ItemsSource = null;
            var source = SourceClientBox.SelectedItem as LookupItem;
            if (source == null) return;
            var matters = await Frame.RunAsync("Loading matters…", () => Lookup.Load(Session, SplitManagementService.SourceMattersSql, Sql.P("@cli", source.Key)));
            if (matters == null || SourceClientBox.SelectedItem != source) return;
            SourceMatterBox.ItemsSource = matters;
            SourceMatterBox.SelectedIndex = matters.Count > 0 ? 0 : -1;
        }

        private async void OnSourceMatterChanged(object sender, SelectionChangedEventArgs e) => await LoadCopyAsync();

        /// <summary>Copy mode: the source matter's splits, and the table filled the original's way.</summary>
        private async System.Threading.Tasks.Task LoadCopyAsync()
        {
            _rows.Clear();
            SourceGrid.ItemsSource = null;
            UpdateState();
            var source = SourceMatterBox.SelectedItem as LookupItem;
            var client = Client;
            var matter = Matter;
            if (source == null) return;
            var all = matter == null || IsAll;
            var result = await Frame.RunAsync("Loading splits…", () => new
            {
                Source = _service.CurrentSplits(Convert.ToInt32(source.Key)),
                Template = client == null || matter == null ? new List<SplitRow>()
                    : _service.CopyTemplate(Convert.ToInt32(source.Key), client.Code, all ? null : matter.Code, all ? null : matter.Name),
            });
            if (result == null || SourceMatterBox.SelectedItem != source || Client != client || Matter != matter || ModeCopy.IsChecked != true) return;
            SourceGrid.ItemsSource = result.Source;
            foreach (var r in result.Template) _rows.Add(r);
            UpdateState();
        }

        private void OnAddRow(object sender, RoutedEventArgs e)
        {
            var row = new SplitRow();
            _rows.Add(row);
            SplitGrid.SelectedItem = row;
            SplitGrid.ScrollIntoView(row);
            SplitGrid.CurrentCell = new DataGridCellInfo(row, ClientColumn);
            SplitGrid.BeginEdit();
        }

        private void OnRemoveRow(object sender, RoutedEventArgs e)
        {
            if (SplitGrid.SelectedItem is SplitRow row) _rows.Remove(row);
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(UpdateState), System.Windows.Threading.DispatcherPriority.Background);

        private void UpdateState()
        {
            if (!_ready) return;
            var copy = ModeCopy.IsChecked == true;
            CopyPanel.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;
            // As in the original, a copy is reviewed but not edited.
            SplitGrid.IsReadOnly = copy;
            AddRowButton.IsEnabled = !copy;
            RemoveRowButton.IsEnabled = !copy;
            TokenHint.Visibility = copy || IsAll ? Visibility.Visible : Visibility.Collapsed;
            var total = _rows.Sum(r => r.Percent);
            TotalText.Text = _rows.Count == 0 ? "" : "Total " + total.ToString("0.00##") + "%" + (total == 100m || total == 0m ? "" : "  (must be 100)");
            UpdateButton.IsEnabled = Client != null && Matter != null && (!copy || SourceMatterBox.SelectedItem != null);
        }

        private async void OnUpdate(object sender, RoutedEventArgs e)
        {
            SplitGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var client = Client;
            var matter = Matter;
            if (client == null || matter == null) return;
            var all = IsAll;
            var rows = _rows.ToList();
            var lockIt = LockCheck.IsChecked == true;
            var cli = Convert.ToInt32(client.Key);

            var plan = await Frame.RunAsync("Working out the changes…", () =>
                _service.Plan(cli, client.Code, _service.Targets(cli, all ? (int?)null : Convert.ToInt32(matter.Key)), rows, lockIt));
            if (plan == null) return;
            if (plan.Problems.Rows.Count > 0)
            {
                Frame.ShowWarning("Nothing was changed: " + plan.Problems.Rows.Count + (plan.Problems.Rows.Count == 1 ? " problem needs" : " problems need") + " fixing first. The list is open.");
                ReportWindow.Show(this, "Split problems", plan.Problems, "Fix these and try again. Nothing has been changed.");
                return;
            }
            if (plan.Preview.Rows.Count == 0 && plan.Targets.All(t => t.Method == "0"))
            {
                Frame.ShowInfo("There are no splits to add or remove, so nothing was changed.");
                return;
            }

            var summary = plan.Targets.Count + (plan.Targets.Count == 1 ? " matter" : " matters") + ": "
                          + plan.Added + " split(s) added, " + plan.Changed + " changed, " + plan.Removed + " removed"
                          + (plan.MattersToCreate > 0 ? ", " + plan.MattersToCreate + " new split-to matter(s) created" : "")
                          + (lockIt ? ", split-to matters locked against time and expenses" : "") + ". This is hard to reverse.";
            if (!ReportWindow.Confirm(this, "Split changes", plan.Preview, summary, "Update the splits"))
                return;

            var changed = await Frame.RunAsync("Updating splits…", () => _service.Apply(plan));
            if (changed == null) return;
            Frame.ShowSuccess("Splits updated for " + changed.Count + (changed.Count == 1 ? " matter" : " matters")
                              + (plan.MattersToCreate > 0 ? "; " + plan.MattersToCreate + " split-to matter(s) created" : "") + ".");
            var report = await Frame.RunAsync("Loading the new splits…", () => _service.SplitsReport(changed));
            if (report != null)
                ReportWindow.Show(this, "Splits after the update", report, "Every split of the matters just changed.");
            if (ModeCopy.IsChecked != true) await LoadCurrentAsync();
        }

        private async void OnNot100(object sender, RoutedEventArgs e)
        {
            var report = await Frame.RunAsync("Checking split totals…", () => _service.Not100Report());
            if (report == null) return;
            ReportWindow.Show(this, "Splits not totalling 100%", report,
                report.Rows.Count == 0 ? "Every split matter's percentages add up to exactly 100." : "Split matters whose percentages don't add up to exactly 100.");
        }
    }
}
