using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.CodeReassignment
{
    /// <summary>
    /// Shared page for every "reassign code X to code Y" tool (Activity Code, Task Code). The tool supplies a
    /// <see cref="CodeReassignmentMap"/>; this page lets the user pick From and To (never the same code), optionally tick
    /// "delete the From code", confirms, then runs <see cref="CodeReassignmentEngine"/> in ONE transaction. Fixes shared by
    /// both originals: summary rows (ARFTaskAlloc, CRFeeAlloc, FeeSumByPrd) are added into the To row instead of losing the
    /// From amounts; From = To is blocked (it used to wipe every summary row for the code); all money/hours arithmetic is
    /// set-based SQL (no double / culture-formatted strings); one transaction instead of ~25 unguarded statements; the
    /// delete is pre-checked and blocked with a list of remaining references instead of crashing; UtilityLog is written;
    /// both lists are reloaded after a run so a deleted code no longer shows; an empty code list no longer throws.
    /// </summary>
    public partial class CodeReassignmentView : UserControl
    {
        private readonly IToolContext _context;
        private readonly CodeReassignmentMap _map;
        private JurisSession Session => _context.Session;
        private DataTable _lastChanges;
        private string _lastChangesTitle;

        public CodeReassignmentView(IToolContext context, CodeReassignmentMap map)
        {
            _context = context;
            _map = map;
            InitializeComponent();

            var noun = map.Noun;
            ChooseTitle.Text = "1  Choose the " + noun + "s";
            FromLabel.Text = "Select " + noun + " to change (From)";
            ToLabel.Text = "Select new " + noun + " (To)";
            ChooseHint.Text = "Everything in Juris that uses the From " + noun + " is changed to the To " + noun + ", including billed history. "
                              + "Where Juris already keeps a total for the To " + noun + " (for example fee totals by period), the From amounts are added to it.";
            DeleteBox.Content = "Delete the From " + noun + " after reassignment";
            DeleteHint.Text = "Done in the same step. If anything still uses the From " + noun + " afterwards, it is kept and you'll see what still uses it.";
            if (!string.IsNullOrEmpty(map.ReportSql))
            {
                ReportButton.Content = map.ReportButtonText;
                ReportButton.Visibility = Visibility.Visible;
            }

            Loaded += async (_, __) => await LoadCodesAsync();
        }

        private string FromCode => (FromBox.SelectedItem as LookupItem)?.Code;
        private string ToCode => (ToBox.SelectedItem as LookupItem)?.Code;

        private async System.Threading.Tasks.Task LoadCodesAsync()
        {
            var sql = "select " + _map.CodeColumn + ", " + _map.CodeColumn + " as Code, " + _map.DescColumn
                      + " from " + _map.CodeTable + " order by " + _map.CodeColumn;
            var codes = await Frame.RunAsync("Loading " + _map.Noun.ToLowerInvariant() + "s…", () => Lookup.Load(Session, sql));
            if (codes == null)
            {
                Frame.ShowError("There was an error reading the " + _map.Noun.ToLowerInvariant() + "s. This usually means this company has none set up.");
                return;
            }
            FromBox.ItemsSource = codes;
            ToBox.ItemsSource = codes.ToList();
            FromBox.SelectedIndex = -1;
            ToBox.SelectedIndex = -1;
            if (codes.Count < 2)
                Frame.ShowWarning("This company has " + (codes.Count == 0 ? "no " : "only one ") + _map.Noun + (codes.Count == 0 ? "s" : "")
                                  + " set up, so there's nothing to reassign.");
            UpdateRunEnabled();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRunEnabled();
            if (FromCode != null && FromCode == ToCode)
                Frame.ShowWarning("The From and To " + _map.Noun + "s must be different.");
            else if (!Frame.IsBusy)
                Frame.ClearMessage();
        }

        private void UpdateRunEnabled() => RunButton.IsEnabled = FromCode != null && ToCode != null && FromCode != ToCode;

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var from = FromCode;
            var to = ToCode;
            var delete = DeleteBox.IsChecked == true;
            var noun = _map.Noun;
            if (from == null || to == null)
            {
                Frame.ShowWarning("Please select both a From and a To " + noun + ".");
                return;
            }
            if (from == to)
            {
                Frame.ShowWarning("The From and To " + noun + "s must be different.");
                return;
            }

            var usage = await Frame.RunAsync("Counting where " + from + " is used…", () =>
                CodeReassignmentEngine.CountReferences(Session, CodeReassignmentEngine.PresentReferences(Session, _map), from));
            if (usage == null) return;
            var total = usage.Rows().Sum(r => Convert.ToInt32(r["Rows"]));

            var message = total == 0
                ? noun + " " + from + " isn't used anywhere, so no entries will change."
                : "This will change the " + noun + " from " + from + " to " + to + " on " + total.ToString("N0") + " entr" + (total == 1 ? "y" : "ies")
                  + " (in " + usage.Rows.Count + " place" + (usage.Rows.Count == 1 ? "" : "s") + " in Juris), including billed history.";
            message += delete
                ? "\n\nThen " + noun + " " + from + " will be deleted, unless something still uses it."
                : "\n\n" + noun + " " + from + " itself is kept.";
            message += "\n\nThis can't be undone from here. Make sure you have a current backup.";
            if (!await Dialogs.ConfirmAsync("Reassign " + noun + " " + from + " to " + to + "?", message, "Reassign"))
                return;

            var progress = new Progress<string>(text => Frame.BusyText = text);
            var result = await Frame.RunAsync("Reassigning " + from + " to " + to + "…",
                () => CodeReassignmentEngine.Run(Session, _map, from, to, delete, progress));
            if (result == null) return; // error shown, nothing was saved

            _lastChanges = result.Changes;
            _lastChangesTitle = noun + " " + from + " reassigned to " + to;
            ChangesButton.IsEnabled = true;
            DeleteBox.IsChecked = false;
            await LoadCodesAsync();

            var done = noun + " " + from + " was reassigned to " + to + " (" + result.TotalRows.ToString("N0") + " entr"
                       + (result.TotalRows == 1 ? "y" : "ies") + " changed).";
            if (result.Skipped.Count > 0)
                done += " Not part of this version of Juris, so skipped: " + string.Join(", ", result.Skipped) + ".";
            if (result.Deleted)
                Frame.ShowSuccess(done + " " + noun + " " + from + " was deleted.");
            else if (result.DeleteRequested)
                Frame.ShowWarning(done + " But " + noun + " " + from + " was not deleted because it is still used in: "
                                  + string.Join("; ", result.DeleteBlockers) + ". Change or remove those, then delete it in Juris or run this again.");
            else
                Frame.ShowSuccess(done);
        }

        private void OnShowChanges(object sender, RoutedEventArgs e)
        {
            if (_lastChanges != null)
                ReportWindow.Show(this, _lastChangesTitle, _lastChanges, "How many entries the last run changed in each part of Juris.");
        }

        private async void OnReport(object sender, RoutedEventArgs e)
        {
            var sql = _map.ReportSql;
            var table = await Frame.RunAsync("Building the report…", () => Session.Query(sql));
            if (table != null)
                ReportWindow.Show(this, _map.ReportTitle, table, _map.ReportSubtitle);
        }
    }
}
