using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.InactivateUsersTkprs
{
    public sealed class InactivateUsersTkprsTool : IJurisTool
    {
        public string Id => "JPS.INACTUSERTKPR";
        public FrameworkElement CreateView(IToolContext context) => new InactivateUsersTkprsView(context);
    }

    /// <summary>
    /// Lists users and/or timekeepers with no activity since a chosen date and turns off their user/timekeeper flag
    /// (removing them from the Juris Tables view), with a pre-report to approve and a post-report. Ported from "JPS -
    /// Inactivate Users and Tkprs with no Activity" with these fixes: the date picked is actually used (the original
    /// hard-coded 04/01/2025); activity now includes time, expense, unposted time and batches, not just the last
    /// Employee_Log change; the selection uses the employee number (the original matched on the NAME, so nothing ever
    /// changed); the non-existent "doctree" table is now DocumentTree; timekeepers still assigned on open matters are
    /// left out and listed with the reason; an empty list stops instead of "processing"; the run is one transaction with
    /// a UtilityLog line and nothing is built from text.
    /// </summary>
    public partial class InactivateUsersTkprsView : UserControl
    {
        private readonly InactivateUsersTkprsService _service;
        private List<Candidate> _excluded = new List<Candidate>();
        private Dictionary<int, Candidate> _offered = new Dictionary<int, Candidate>();
        private DateTime _searchedSince;

        public InactivateUsersTkprsView(IToolContext context)
        {
            _service = new InactivateUsersTkprsService(context.Session);
            InitializeComponent();
            SinceBox.DisplayDateEnd = DateTime.Today;
            SinceBox.SelectedDate = DateTime.Today.AddYears(-1);
        }

        private PeopleFilter Filter =>
            TkprsOnly.IsChecked == true ? PeopleFilter.TimekeepersOnly : Everyone.IsChecked == true ? PeopleFilter.All : PeopleFilter.UsersOnly;

        /// <summary>Changing the date or who to look at invalidates the list, so the action can't run on stale criteria.</summary>
        private void OnCriteriaChanged(object sender, RoutedEventArgs e)
        {
            if (RunButton == null) return; // fired while the page is still being built
            ClearList();
            Frame.ClearMessage();
            FindButton.IsEnabled = SinceBox.SelectedDate != null;
        }

        private void ClearList()
        {
            People.ItemsSource = null;
            _offered = new Dictionary<int, Candidate>();
            _excluded = new List<Candidate>();
            ExcludedText.Visibility = Visibility.Collapsed;
            SkippedText.Visibility = Visibility.Collapsed;
            ExcludedButton.IsEnabled = false;
            RunButton.IsEnabled = false;
        }

        private void OnSelectionChanged(object sender, EventArgs e) => RunButton.IsEnabled = People.Checked.Count > 0;

        private async void OnFind(object sender, RoutedEventArgs e) => await Find(showEmptyMessage: true);

        private async System.Threading.Tasks.Task<bool> Find(bool showEmptyMessage)
        {
            var since = SinceBox.SelectedDate?.Date;
            if (since == null)
            {
                Frame.ShowWarning("Pick a cut-off date first.");
                return false;
            }
            if (since.Value > DateTime.Today)
            {
                Frame.ShowWarning("The cut-off date can't be in the future.");
                return false;
            }
            var filter = Filter;
            ClearList();

            var found = await Frame.RunAsync("Looking for people with no activity since " + since.Value.ToShortDateString() + "…",
                () => _service.Search(since.Value, filter));
            if (found == null) return false;

            _searchedSince = since.Value;
            _offered = found.Offered.ToDictionary(c => c.EmpSysNbr);
            _excluded = found.Excluded;
            People.ItemsSource = found.Offered
                .Select(c => new LookupItem(c.EmpSysNbr, c.EmpId, c.Name + "  (" + InactivateUsersTkprsService.KindText(c.Kind) + ")") { IsChecked = true })
                .ToList();
            RunButton.IsEnabled = found.Offered.Count > 0;

            if (_excluded.Count > 0)
            {
                ExcludedText.Text = _excluded.Count.ToString("N0") + " more had no activity but were left out — most often because they are still a timekeeper on open matters. Use \"Who was left out\" to see why.";
                ExcludedText.Visibility = Visibility.Visible;
                ExcludedButton.IsEnabled = true;
            }
            if (found.SkippedSources.Count > 0)
            {
                SkippedText.Text = "Not checked because this Juris version doesn't have them: " + string.Join(", ", found.SkippedSources) + ".";
                SkippedText.Visibility = Visibility.Visible;
            }
            if (found.Offered.Count == 0 && showEmptyMessage)
                Frame.ShowInfo("No users or timekeepers match the selected criteria.");
            return true;
        }

        private void OnShowExcluded(object sender, RoutedEventArgs e)
        {
            ReportWindow.Show(this, "People left out", InactivateUsersTkprsService.ListTable(_excluded, withReason: true),
                "No activity since " + _searchedSince.ToShortDateString() + ", but not offered for inactivation.");
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var chosen = People.Checked
                .Select(i => _offered.TryGetValue(Convert.ToInt32(i.Key), out var c) ? c : null)
                .Where(c => c != null)
                .ToList();
            if (chosen.Count == 0)
            {
                Frame.ShowWarning("Tick at least one person to inactivate.");
                return;
            }
            var since = _searchedSince;

            var users = chosen.Count(c => c.Kind == "User");
            var tkprs = chosen.Count(c => c.Kind == "Timekeeper");
            var both = chosen.Count(c => c.Kind == "Both");
            var summary = "This will inactivate " + users + " user(s), " + tkprs + " timekeeper(s) and " + both
                          + " who are both, and remove them from the Tables view in Juris. Their history is kept.";
            if (!ReportWindow.Confirm(this, "Inactivate users and timekeepers — check before changing",
                    InactivateUsersTkprsService.ListTable(chosen, withReason: false), summary, "Inactivate"))
                return;

            var count = await Frame.RunAsync("Inactivating " + chosen.Count + " people…", () => _service.Inactivate(chosen, since));
            if (Frame.HasMessage) return; // error already shown, nothing saved

            // Reload the list (the people just inactivated drop out), then report.
            var reloaded = await Find(showEmptyMessage: false);
            Frame.ShowSuccess("Inactivated " + count.ToString("N0") + " " + (count == 1 ? "person" : "people") + " with no activity since " + since.ToShortDateString() + "."
                              + (reloaded ? "" : " The list couldn't be refreshed — select Find inactive people to search again."));
            ReportWindow.Show(this, "Inactivated users and timekeepers", InactivateUsersTkprsService.ListTable(chosen, withReason: false),
                "Inactivated on " + DateTime.Today.ToShortDateString() + " — no activity since " + since.ToShortDateString() + ".");
        }
    }
}
