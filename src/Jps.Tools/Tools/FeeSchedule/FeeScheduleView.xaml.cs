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
using Jps.Tools.Tools.PracticeClass;

namespace Jps.Tools.Tools.FeeSchedule
{
    public sealed class FeeScheduleReassignment : IJurisTool
    {
        public string Id => "JPS.FEESCHED";
        public FrameworkElement CreateView(IToolContext context) => new FeeScheduleView(context);
    }

    /// <summary>
    /// Moves clients and/or matters (open, closed or all) from one fee schedule to another, optionally narrowed by practice
    /// class, office and client, with a pre-report to approve and a post-report, and can delete the old fee schedule in
    /// the same transaction. Ported from "JPS - Fee Schedule Reassignment v10" with these fixes: validation only fired when
    /// both lists were empty (one empty or From = To ran anyway); the delete was offered on a guess that ignored the office
    /// and client filters, so it wiped the schedule's rates and re-coded time history before failing on the Client/Matter
    /// FK - it is now refused up front (and re-checked inside the transaction) while any client or matter would remain on
    /// the code or it is the firm's standard rate schedule, and the user sees exactly how many time and rate rows it will
    /// touch before confirming; nothing ran in a transaction; the Client-and-Matter report's ORDER BY was invalid for a
    /// UNION; empty lists crashed on load; SQL was concatenated from combo text. Inactive schedules can now be moved away from.
    /// </summary>
    public partial class FeeScheduleView : UserControl
    {
        private sealed class Lists
        {
            public List<LookupItem> From, To, Practice, Offices, Clients;
        }

        private sealed class Preflight
        {
            public DataTable Report;
            public List<DeleteBlocker> Blockers;
            public List<string> Impact;
        }

        private sealed class Outcome
        {
            public DataTable Post;
            public int Clients;
            public int Matters;
        }

        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;
        private bool _ready;

        public FeeScheduleView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                if (_ready) return;
                await ReloadAsync();
                _ready = true;
                UpdateState();
            };
        }

        /// <summary>Loads all five lists and resets every selection.</summary>
        private async Task ReloadAsync()
        {
            var lists = await Frame.RunAsync("Loading fee schedules…", () => new Lists
            {
                From = Lookup.Load(Session, FeeScheduleChange.FromListSql),
                To = Lookup.Load(Session, FeeScheduleChange.ToListSql),
                Practice = Lookup.Load(Session, FeeScheduleChange.PracticeClassesSql),
                Offices = Lookup.Load(Session, FeeScheduleChange.OfficesSql),
                Clients = Lookup.Load(Session, FeeScheduleChange.ClientsSql),
            });
            if (lists == null) return;
            var wasReady = _ready;
            _ready = false;
            FromBox.ItemsSource = lists.From;
            ToBox.ItemsSource = lists.To;
            PracticeBox.ItemsSource = WithNoFilter("All practice classes", lists.Practice);
            OfficeBox.ItemsSource = WithNoFilter("All offices", lists.Offices);
            ClientBox.ItemsSource = WithNoFilter("All clients", lists.Clients);
            FromBox.SelectedItem = null;
            ToBox.SelectedItem = null;
            PracticeBox.SelectedIndex = 0;
            OfficeBox.SelectedIndex = 0;
            ClientBox.SelectedIndex = 0;
            BothRadio.IsChecked = true;
            OpenRadio.IsChecked = true;
            DeleteCheck.IsChecked = false;
            _ready = wasReady;
            if (lists.From.Count < 2 || lists.To.Count == 0)
                Frame.ShowWarning("Juris needs at least two fee schedules, one of them active, before anything can be moved from one to another.");
        }

        private static List<LookupItem> WithNoFilter(string text, IEnumerable<LookupItem> items) =>
            new[] { new LookupItem(null, "", text) }.Concat(items).ToList();

        private string FromCode => (FromBox.SelectedItem as LookupItem)?.Code;
        private string ToCode => (ToBox.SelectedItem as LookupItem)?.Code;

        private ReassignScope Scope => ClientsRadio.IsChecked == true ? ReassignScope.ClientsOnly
            : MattersRadio.IsChecked == true ? ReassignScope.MattersOnly : ReassignScope.ClientsAndMatters;

        private MatterStatusFilter Status => ClosedRadio.IsChecked == true ? MatterStatusFilter.ClosedOnly
            : AllRadio.IsChecked == true ? MatterStatusFilter.All : MatterStatusFilter.OpenOnly;

        private static object KeyOf(ComboBox box) => (box.SelectedItem as LookupItem)?.Key;

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Frame.ClearMessage();
            UpdateState();
        }

        private string Problem()
        {
            if (FromCode == null || ToCode == null) return "Choose the fee schedule to change from and the one to change to.";
            if (string.Equals(FromCode, ToCode, StringComparison.OrdinalIgnoreCase)) return "The two fee schedules must be different.";
            return null;
        }

        private void UpdateState()
        {
            var problem = _ready ? Problem() : "";
            WhyDisabled.Text = problem ?? "";
            RunButton.IsEnabled = problem == null;
            PreviewButton.IsEnabled = problem == null;
            RunButton.Content = DeleteCheck.IsChecked == true ? "Change and delete old schedule" : "Change fee schedule";
        }

        /// <summary>Captures every input into a change object (UI thread only).</summary>
        private FeeScheduleChange BuildChange()
        {
            var problem = Problem();
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return null;
            }
            var client = KeyOf(ClientBox);
            return new FeeScheduleChange(FromCode, ToCode, Scope, Status,
                KeyOf(PracticeBox) as string, KeyOf(OfficeBox) as string, client == null ? (int?)null : Convert.ToInt32(client));
        }

        private string Subtitle(FeeScheduleChange change, bool delete, IEnumerable<string> impact)
        {
            var filters = new List<string>();
            if (PracticeBox.SelectedIndex > 0) filters.Add("practice class " + ((LookupItem)PracticeBox.SelectedItem).Code);
            if (OfficeBox.SelectedIndex > 0) filters.Add("office " + ((LookupItem)OfficeBox.SelectedItem).Code);
            if (ClientBox.SelectedIndex > 0) filters.Add("client " + ((LookupItem)ClientBox.SelectedItem).Code);
            var text = "Fee schedules for " + change.ScopeText + (filters.Count > 0 ? " (" + string.Join(", ", filters) + ")" : "")
                       + " will change from " + change.From + " to " + change.To + ". " + change.StatusText;
            if (delete)
            {
                text += " Fee schedule " + change.From + " will then be deleted";
                var lines = impact?.ToList() ?? new List<string>();
                text += lines.Count == 0 ? "." : ", which also means: " + string.Join("; ", lines) + ".";
            }
            return text.Trim();
        }

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var change = BuildChange();
            if (change == null) return;
            var subtitle = Subtitle(change, false, null);
            var table = await Frame.RunAsync("Building the report…", () => change.Report(Session));
            if (table == null) return;
            ReportWindow.Show(this, "Fee schedule change - preview", table, subtitle);
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var change = BuildChange();
            if (change == null) return;
            var delete = DeleteCheck.IsChecked == true;

            var pre = await Frame.RunAsync("Checking what will change…", () => new Preflight
            {
                Report = change.Report(Session),
                Blockers = delete ? change.DeleteBlockers(Session, false) : new List<DeleteBlocker>(),
                Impact = delete ? change.DeleteImpact(Session) : new List<string>(),
            });
            if (pre == null) return;
            if (pre.Blockers.Count > 0)
            {
                Frame.ShowWarning(DeleteBlocker.Message(
                    "Fee schedule " + change.From + " can't be deleted because of the following. Nothing has been changed.", pre.Blockers)
                    + Environment.NewLine + "Fix these first, or untick \"Delete the old fee schedule\" to move the clients and matters without deleting it.");
                return;
            }

            var subtitle = Subtitle(change, delete, pre.Impact);
            if (pre.Report.Rows.Count > 0)
            {
                if (!ReportWindow.Confirm(this, "Fee schedule change - pre-report", pre.Report, subtitle,
                        delete ? "Change and delete " + change.From : "Change fee schedule"))
                    return;
            }
            else if (delete)
            {
                if (!await Dialogs.ConfirmAsync("Delete fee schedule " + change.From + "?",
                        "No " + change.ScopeText + " match your choices, so nothing will be moved, and no client or matter uses "
                        + change.From + ". " + subtitle.Substring(subtitle.IndexOf("Fee schedule " + change.From, StringComparison.Ordinal)),
                        "Delete it"))
                    return;
            }
            else
            {
                Frame.ShowInfo("No " + change.ScopeText + " match your choices, so there's nothing to change.");
                return;
            }

            var outcome = await Frame.RunAsync("Changing fee schedules…", () => Session.InTransaction(() =>
            {
                change.LockCodes(Session, "Fee schedule");
                var post = change.Report(Session); // captured fresh, just before the update
                change.Apply(Session, out var clients, out var matters);
                if (delete)
                {
                    var left = change.DeleteBlockers(Session, true);
                    if (left.Count > 0)
                        throw new InvalidOperationException(DeleteBlocker.Message(
                            "Fee schedule " + change.From + " can't be deleted because of the following, so nothing was changed:", left));
                    change.DeleteFrom(Session);
                }
                Session.WriteUtilityLog("Fee Schedule Assignment - " + change.From + " to " + change.To + (delete ? " (deleted)" : ""));
                return new Outcome { Post = post, Clients = clients, Matters = matters };
            }));
            if (outcome == null) return; // error already shown; the transaction rolled back

            var done = new List<string>();
            if (change.ChangesClients) done.Add(outcome.Clients.ToString("N0") + " client(s)");
            if (change.ChangesMatters) done.Add(outcome.Matters.ToString("N0") + " matter(s)");
            var message = "Moved " + string.Join(" and ", done) + " from fee schedule " + change.From + " to " + change.To + "."
                          + (delete ? " Fee schedule " + change.From + " was deleted." : "");
            if (outcome.Post.Rows.Count > 0)
                ReportWindow.Show(this, "Fee schedule change - post-report", FeeScheduleChange.PostReport(outcome.Post), message);

            await ReloadAsync();
            if (!Frame.HasMessage) Frame.ShowSuccess(message);
            UpdateState();
        }
    }
}
