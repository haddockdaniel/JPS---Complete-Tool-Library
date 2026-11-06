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

namespace Jps.Tools.Tools.PracticeClass
{
    public sealed class PracticeClassReassignment : IJurisTool
    {
        public string Id => "JPS.PRACCLASS";
        public FrameworkElement CreateView(IToolContext context) => new PracticeClassView(context);
    }

    /// <summary>
    /// Moves clients and/or matters from one practice class to another, optionally only those with a chosen billing
    /// timekeeper, with a pre-report to approve and a post-report, and can delete the old practice class in the same
    /// transaction. Ported from "JPS - Practice Class Reassignment v10" with these fixes: the delete never checked
    /// ClientFilterPracticeCodes (a real FK) and its client/matter check inner-joined Matter (so clients without matters, or
    /// ones excluded by the timekeeper filter, were missed) - both crashed on the FK; now every referencing table
    /// (Client, Matter, PractClassGLAcct, ClientFilterPracticeCodes) is checked up front and again inside the transaction,
    /// and the delete is refused with a list of what still uses the code. The post-report reused a stale pre-report and
    /// removed columns by index (crash / wrong columns); it is now captured at run time and trimmed by name. The
    /// Client-and-Matter report missed clients without matters and listed rows the update wouldn't change. Timekeeper
    /// ticked with none chosen crashed; From = To was allowed; nothing ran in a transaction; SQL was concatenated.
    /// </summary>
    public partial class PracticeClassView : UserControl
    {
        private sealed class Preflight
        {
            public DataTable Report;
            public List<DeleteBlocker> Blockers;
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

        public PracticeClassView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                if (_ready) return;
                await ReloadAsync(null, null, loadAttorneys: true);
                _ready = true;
                UpdateState();
            };
        }

        private async Task ReloadAsync(string keepFrom, string keepTo, bool loadAttorneys)
        {
            var lists = await Frame.RunAsync("Loading practice classes…", () => new
            {
                Classes = Lookup.Load(Session, PracticeClassChange.ClassesSql),
                Attorneys = loadAttorneys ? Lookup.Load(Session, PracticeClassChange.AttorneysSql) : null,
            });
            if (lists == null) return;
            var wasReady = _ready;
            _ready = false;
            // Separate lists so the two combo boxes don't share a selection.
            FromBox.ItemsSource = lists.Classes;
            ToBox.ItemsSource = lists.Classes.Select(i => new LookupItem(i.Key, i.Code, i.Name)).ToList();
            FromBox.SelectedItem = lists.Classes.FirstOrDefault(i => i.Code == keepFrom);
            ToBox.SelectedItem = ((List<LookupItem>)ToBox.ItemsSource).FirstOrDefault(i => i.Code == keepTo);
            if (lists.Attorneys != null) AttyBox.ItemsSource = lists.Attorneys;
            _ready = wasReady;
            if (lists.Classes.Count < 2)
                Frame.ShowWarning("Juris needs at least two practice classes before anything can be moved from one to another.");
        }

        private string FromCode => (FromBox.SelectedItem as LookupItem)?.Code;
        private string ToCode => (ToBox.SelectedItem as LookupItem)?.Code;

        private ReassignScope Scope => ClientsRadio.IsChecked == true ? ReassignScope.ClientsOnly
            : MattersRadio.IsChecked == true ? ReassignScope.MattersOnly : ReassignScope.ClientsAndMatters;

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Frame.ClearMessage();
            UpdateState();
        }

        /// <summary>Why the change can't run yet, or null when it can.</summary>
        private string Problem()
        {
            if (FromCode == null || ToCode == null) return "Choose the practice class to change from and the one to change to.";
            if (FromCode == ToCode) return "The two practice classes must be different.";
            if (AttyCheck.IsChecked == true && AttyBox.SelectedItem == null)
                return "Choose a billing timekeeper, or untick the billing timekeeper option.";
            return null;
        }

        private void UpdateState()
        {
            var problem = _ready ? Problem() : "";
            WhyDisabled.Text = problem ?? "";
            RunButton.IsEnabled = problem == null;
            PreviewButton.IsEnabled = problem == null;
            RunButton.Content = DeleteCheck.IsChecked == true ? "Change and delete old class" : "Change practice class";
        }

        /// <summary>Captures every input into a change object (UI thread only).</summary>
        private PracticeClassChange BuildChange()
        {
            var problem = Problem();
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return null;
            }
            int? atty = AttyCheck.IsChecked == true ? Convert.ToInt32(((LookupItem)AttyBox.SelectedItem).Key) : (int?)null;
            return new PracticeClassChange(FromCode, ToCode, Scope, atty);
        }

        private string Subtitle(PracticeClassChange change, bool delete)
        {
            var atty = AttyCheck.IsChecked == true ? " with billing timekeeper " + ((LookupItem)AttyBox.SelectedItem).Code : "";
            return Capitalise(change.ScopeText) + atty + " will move from practice class " + change.From + " to " + change.To + "."
                   + (delete ? " Practice class " + change.From + " will then be deleted." : "");
        }

        private static string Capitalise(string s) => char.ToUpper(s[0]) + s.Substring(1);

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var change = BuildChange();
            if (change == null) return;
            var subtitle = Subtitle(change, false);
            var table = await Frame.RunAsync("Building the report…", () => change.Report(Session));
            if (table == null) return;
            ReportWindow.Show(this, "Practice class change - preview", table, subtitle);
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var change = BuildChange();
            if (change == null) return;
            var delete = DeleteCheck.IsChecked == true;
            var subtitle = Subtitle(change, delete);

            var pre = await Frame.RunAsync("Checking what will change…", () => new Preflight
            {
                Report = change.Report(Session),
                Blockers = delete ? change.DeleteBlockers(Session, false) : new List<DeleteBlocker>(),
            });
            if (pre == null) return;
            if (pre.Blockers.Count > 0)
            {
                Frame.ShowWarning(DeleteBlocker.Message(
                    "Practice class " + change.From + " can't be deleted because these still use it. Nothing has been changed.", pre.Blockers)
                    + Environment.NewLine + "Fix these first, or untick \"Delete the old practice class\" to move the clients and matters without deleting it.");
                return;
            }

            if (pre.Report.Rows.Count > 0)
            {
                if (!ReportWindow.Confirm(this, "Practice class change - pre-report", pre.Report, subtitle,
                        delete ? "Change and delete " + change.From : "Change practice class"))
                    return;
            }
            else if (delete)
            {
                if (!await Dialogs.ConfirmAsync("Delete practice class " + change.From + "?",
                        "No " + change.ScopeText + " match your choices, so nothing will be moved. Practice class " + change.From
                        + " is not used anywhere and will be deleted.", "Delete it"))
                    return;
            }
            else
            {
                Frame.ShowInfo("No " + change.ScopeText + " match your choices, so there's nothing to change.");
                return;
            }

            var outcome = await Frame.RunAsync("Changing practice classes…", () => Session.InTransaction(() =>
            {
                change.LockCodes(Session, "Practice class");
                var post = change.Report(Session); // captured fresh, just before the update
                change.Apply(Session, out var clients, out var matters);
                if (delete)
                {
                    var left = change.DeleteBlockers(Session, true);
                    if (left.Count > 0)
                        throw new InvalidOperationException(DeleteBlocker.Message(
                            "Practice class " + change.From + " can't be deleted because these still use it, so nothing was changed:", left));
                    change.DeleteFrom(Session);
                }
                Session.WriteUtilityLog("Practice class assignment - " + change.From + " to " + change.To + (delete ? " (deleted)" : ""));
                return new Outcome { Post = post, Clients = clients, Matters = matters };
            }));
            if (outcome == null) return; // error already shown; the transaction rolled back

            var done = new List<string>();
            if (change.ChangesClients) done.Add(outcome.Clients.ToString("N0") + " client(s)");
            if (change.ChangesMatters) done.Add(outcome.Matters.ToString("N0") + " matter(s)");
            Frame.ShowSuccess("Moved " + string.Join(" and ", done) + " from practice class " + change.From + " to " + change.To + "."
                              + (delete ? " Practice class " + change.From + " was deleted." : ""));
            if (outcome.Post.Rows.Count > 0)
                ReportWindow.Show(this, "Practice class change - post-report", PracticeClassChange.PostReport(outcome.Post),
                    Subtitle(change, delete).Replace(" will move ", " moved ").Replace(" will then be deleted", " was deleted"));

            var message = Frame.Message;
            _ready = false;
            DeleteCheck.IsChecked = false;
            _ready = true;
            await ReloadAsync(delete ? null : change.From, change.To, loadAttorneys: false);
            if (!Frame.HasMessage) Frame.ShowSuccess(message);
            UpdateState();
        }
    }
}
