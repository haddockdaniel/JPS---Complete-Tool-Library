using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;
using Jps.Tools.Tools.MatterCloser;

namespace Jps.Tools.Tools.MatterStatusLock
{
    public sealed class MatterStatusLockTool : IJurisTool
    {
        public string Id => "JPS.MATSTATUSLOCK";
        public FrameworkElement CreateView(IToolContext context) => new MatterStatusLockView(context);
    }

    /// <summary>
    /// Sets the status (Open, Closed, Final Bill Sent) and/or lock flag of matters picked by client (all or chosen
    /// matters), billing timekeeper, last bill date, or a spreadsheet of client/matter codes. Closing uses the shared Matter
    /// Closer rules and spreadsheet resolution (<see cref="MatterCloseRules"/>, <see cref="SpreadsheetMatters"/>), so
    /// matters with balances or split billing are listed instead of closed. Ported from "JPS – Matter Status and Lock
    /// Update 5.3" with these fixes: the old status was read from the LAST matter only and one branch applied to all
    /// (lock-only changes hitting closed matters, stale close dates); close date carried a time; $0 unbilled entries didn't
    /// block closing; a global ##TempBals table locked the tool after any error and collided between users; specific-matter
    /// picks survived a client change and were re-applied to the new client's matters; blank cells, no file and empty lists
    /// crashed; "include never-billed matters" removed matters instead of adding them; Suite-only tables broke non-Suite
    /// companies; values were concatenated into SQL; combos were editable; UtilityLog is now written.
    /// </summary>
    public partial class MatterStatusLockView : UserControl
    {
        private readonly IToolContext _context;
        private readonly MatterStatusLockService _service;
        private string _file;
        private bool _loaded;

        private JurisSession Session => _context.Session;

        public MatterStatusLockView(IToolContext context)
        {
            _context = context;
            _service = new MatterStatusLockService(context.Session);
            InitializeComponent();
            StatusBox.ItemsSource = new List<LookupItem>
            {
                new LookupItem("-", "-", "Leave as is"),
                new LookupItem("O", "O", "Open"),
                new LookupItem("C", "C", "Closed"),
                new LookupItem("F", "F", "Final Bill Sent"),
            };
            LockBox.ItemsSource = new List<LookupItem>
            {
                new LookupItem("-", "-", "Leave as is"),
                new LookupItem("0", "0", MatterStatusLockService.LockName("0")),
                new LookupItem("1", "1", MatterStatusLockService.LockName("1")),
                new LookupItem("2", "2", MatterStatusLockService.LockName("2")),
                new LookupItem("3", "3", MatterStatusLockService.LockName("3")),
            };
            StatusBox.SelectedIndex = 0;
            LockBox.SelectedIndex = 0;
            OpenedDate.SelectedDate = DateTime.Today;
            LastBillDate.SelectedDate = DateTime.Today;
            NeverBilledDate.SelectedDate = DateTime.Today;

            Loaded += async (_, __) =>
            {
                if (_loaded) return;
                var lists = await Frame.RunAsync("Loading clients and billing timekeepers…", () => new
                {
                    Clients = Lookup.Load(Session, "select CliSysNbr, dbo.jfn_FormatClientCode(CliCode) as Code, CliReportingName from Client order by 2"),
                    Tkprs = Lookup.Load(Session,
                        "select distinct EmpSysNbr, EmpID, EmpName from Employee inner join BillTo on BillToBillingAtty = EmpSysNbr order by 2"),
                });
                if (lists == null) return;
                _loaded = true;
                ClientBox.ItemsSource = lists.Clients;
                TkprBox.ItemsSource = lists.Tkprs;
                UpdateState();
            };
        }

        private string NewStatus => (StatusBox.SelectedItem as LookupItem)?.Code ?? "-";
        private string NewLock => (LockBox.SelectedItem as LookupItem)?.Code ?? "-";

        private SelectionMode Mode =>
            SheetMode.IsChecked == true ? SelectionMode.Spreadsheet
            : TkprMode.IsChecked == true ? SelectionMode.BillingTimekeeper
            : BillMode.IsChecked == true ? SelectionMode.LastBillDate
            : SomeMatters.IsChecked == true ? SelectionMode.ClientSpecific
            : SelectionMode.ClientAll;

        // ---------- Inputs ----------

        private void OnStatusChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LockBox == null) return;
            if (NewStatus == "C")
            {
                LockBox.SelectedIndex = 4; // Disallow time and expenses
                LockBox.IsEnabled = false;
            }
            else if (!LockBox.IsEnabled)
            {
                LockBox.IsEnabled = true;
                LockBox.SelectedIndex = 0;
            }
            UpdateState();
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (ClientPanel == null) return; // during InitializeComponent
            ClientPanel.Visibility = ClientMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            TkprPanel.Visibility = TkprMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            BillPanel.Visibility = BillMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SheetPanel.Visibility = SheetMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Frame.ClearMessage();
            UpdateState();
        }

        private async void OnClientChanged(object sender, SelectionChangedEventArgs e) => await LoadMatters();

        private async System.Threading.Tasks.Task LoadMatters()
        {
            // A new client always clears the picked matters (they belonged to the old client).
            MatterList.ItemsSource = null;
            UpdateState();
            var client = (ClientBox.SelectedItem as LookupItem)?.Key;
            if (client == null) return;
            var matters = await Frame.RunAsync("Loading the client's matters…", () => Lookup.Load(Session,
                @"select MatSysNbr, dbo.jfn_FormatMatterCode(MatCode) as Code,
                         MatReportingName + '  (' + case MatStatusFlag when 'O' then 'Open' when 'C' then 'Closed' when 'F' then 'Final Bill Sent' else MatStatusFlag end + ')'
                  from Matter where MatCliNbr = @Cli order by 2", Sql.P("@Cli", client)));
            if (matters == null || !Equals((ClientBox.SelectedItem as LookupItem)?.Key, client)) return;
            MatterList.ItemsSource = matters;
            UpdateState();
        }

        private void OnScopeChanged(object sender, RoutedEventArgs e)
        {
            if (MatterList == null) return;
            MatterList.Visibility = SomeMatters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateState();
        }

        private void OnMattersPicked(object sender, EventArgs e) => UpdateState();

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateState();

        private void OnChooseFile(object sender, RoutedEventArgs e)
        {
            var path = Dialogs.PickImportFile();
            if (path == null) return;
            _file = path;
            FileText.Text = path;
            Frame.ClearMessage();
            UpdateState();
        }

        /// <summary>The first thing still missing, in the original's validation order; null when ready to run.</summary>
        private string Missing()
        {
            switch (Mode)
            {
                case SelectionMode.ClientAll:
                case SelectionMode.ClientSpecific:
                    if (ClientBox.SelectedItem == null) return "Choose a client to continue.";
                    if (Mode == SelectionMode.ClientSpecific && (MatterList.ItemsSource == null || MatterList.Checked.Count == 0))
                        return "Pick at least one matter, or choose \"All matters for the client\".";
                    break;
                case SelectionMode.BillingTimekeeper:
                    if (TkprBox.SelectedItem == null) return "Choose a billing timekeeper to continue.";
                    if (OpenedLimit.IsChecked == true && !OpenedDate.SelectedDate.HasValue) return "Choose the date for \"Only matters opened on or before\".";
                    break;
                case SelectionMode.LastBillDate:
                    if (!LastBillDate.SelectedDate.HasValue) return "Choose the last bill date.";
                    if (NeverBilled.IsChecked == true && !NeverBilledDate.SelectedDate.HasValue) return "Choose the opened-before date for never-billed matters.";
                    break;
                case SelectionMode.Spreadsheet:
                    if (_file == null) return "Choose a spreadsheet to continue.";
                    break;
            }
            if (NewStatus == "-" && NewLock == "-")
                return "Choose a new status or a new lock; at least one must change.";
            return null;
        }

        private void UpdateState()
        {
            if (RunButton == null || MatterList == null || NeverBilledDate == null) return;
            var missing = _loaded ? Missing() : null;
            RunButton.IsEnabled = _loaded && missing == null;
            NotReadyText.Text = missing ?? "";
        }

        private StatusLockRequest ReadRequest()
        {
            var request = new StatusLockRequest { NewStatus = NewStatus, NewLock = NewLock, Mode = Mode };
            switch (request.Mode)
            {
                case SelectionMode.ClientAll:
                    request.CliSysNbr = (ClientBox.SelectedItem as LookupItem)?.Key;
                    request.SelectionText = "all matters for client " + (ClientBox.SelectedItem as LookupItem)?.Code;
                    break;
                case SelectionMode.ClientSpecific:
                    request.MatterIds = MatterList.Checked.Select(i => Convert.ToInt32(i.Key)).ToList();
                    request.SelectionText = request.MatterIds.Count + " chosen matters for client " + (ClientBox.SelectedItem as LookupItem)?.Code;
                    break;
                case SelectionMode.BillingTimekeeper:
                    request.BillTkprSysNbr = (TkprBox.SelectedItem as LookupItem)?.Key;
                    request.OpenedOnOrBefore = OpenedLimit.IsChecked == true ? OpenedDate.SelectedDate : null;
                    request.SelectionText = "billing timekeeper " + (TkprBox.SelectedItem as LookupItem)?.Code +
                                            (request.OpenedOnOrBefore.HasValue ? ", opened on or before " + request.OpenedOnOrBefore.Value.ToString("MM/dd/yyyy") : "");
                    break;
                case SelectionMode.LastBillDate:
                    request.LastBillOnOrBefore = LastBillDate.SelectedDate ?? DateTime.Today;
                    request.NeverBilledOpenedBefore = NeverBilled.IsChecked == true ? NeverBilledDate.SelectedDate : null;
                    request.SelectionText = "last bill on or before " + request.LastBillOnOrBefore.ToString("MM/dd/yyyy") +
                                            (request.NeverBilledOpenedBefore.HasValue ? ", plus never-billed matters opened before " + request.NeverBilledOpenedBefore.Value.ToString("MM/dd/yyyy") : "");
                    break;
                case SelectionMode.Spreadsheet:
                    request.SelectionText = "spreadsheet " + Path.GetFileName(_file);
                    break;
            }
            return request;
        }

        // ---------- Run ----------

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var missing = Missing();
            if (missing != null)
            {
                Frame.ShowWarning(missing);
                return;
            }
            var request = ReadRequest();
            var file = _file;

            var plan = await Frame.RunAsync("Finding matters…", () =>
            {
                if (request.Mode == SelectionMode.Spreadsheet)
                {
                    var rows = _service.ReadSpreadsheet(file);
                    var result = new StatusLockPlan();
                    result.SheetProblems.AddRange(rows.Where(r => r.Problem != null));
                    if (rows.Count == 0 || result.SheetProblems.Count > 0)
                        return result;
                    request.MatterIds = rows.Where(r => r.IsUsable).Select(r => r.MatSysNbr.Value).ToList();
                }
                return _service.Plan(request);
            });
            if (plan == null) return;

            if (request.Mode == SelectionMode.Spreadsheet && plan.SheetProblems.Count > 0)
            {
                Frame.ShowWarning("The spreadsheet has rows that must be fixed first, so nothing was changed. The report lists them.");
                ReportWindow.Show(this, "Spreadsheet problems", SheetProblemTable(plan.SheetProblems),
                    "Fix these rows in " + Path.GetFileName(file) + " and run it again. Row is the row number in your spreadsheet.");
                return;
            }
            if (request.Mode == SelectionMode.Spreadsheet && request.MatterIds.Count == 0)
            {
                Frame.ShowWarning("No matters were found in the spreadsheet. Put the Client Code in column A and the Matter Code in column B of the first sheet, one matter per row starting on row 2.");
                return;
            }
            if (plan.Matters.Count == 0)
            {
                Frame.ShowWarning(request.Mode == SelectionMode.ClientAll
                    ? "All of the matters for this client already have your selections. Choose another client or different settings."
                    : "No matters need changing: every matching matter already has your selections.");
                return;
            }
            if (plan.ChangeCount == 0)
            {
                Frame.ShowWarning(plan.BlockedCount > 0
                    ? "None of the matters can be closed because they still have open items or split billing. Nothing was changed; the report shows why."
                    : "No matters need changing: every matching matter already has your selections.");
                if (plan.BlockedCount > 0)
                    ReportWindow.Show(this, "Matters that can't be closed", MatterStatusLockService.ExceptionTable(plan), ExceptionSubtitle);
                return;
            }

            var subtitle = Matters(plan.ChangeCount) + " will be updated." +
                           (plan.BlockedCount > 0 ? " " + plan.BlockedCount.ToString("N0") + " can't be closed and will be left as they are." : "") +
                           " Check the \"What will happen\" column, then continue.";
            if (!ReportWindow.Confirm(this, "Matters to update", MatterStatusLockService.PreviewTable(plan), subtitle,
                    "Update " + Matters(plan.ChangeCount)))
                return;

            var applied = await Frame.RunAsync("Updating matters…", () => _service.Apply(request, plan));
            if (applied == null) return;
            if (request.Mode == SelectionMode.ClientAll || request.Mode == SelectionMode.ClientSpecific)
                await LoadMatters(); // the matter list shows statuses, so reload it (this clears the picks)

            var done = "Updated " + Matters(applied.Updated + applied.Closed) +
                       (request.NewStatus == "C" ? " (" + applied.Closed.ToString("N0") + " closed)" : "") + ".";
            var notClosed = plan.BlockedCount + applied.LeftOpen;
            if (notClosed == 0)
            {
                Frame.ShowSuccess(done);
                return;
            }
            Frame.ShowWarning(done + " " + Matters(notClosed) + " still have open items or split billing and couldn't be closed." +
                              (applied.LeftOpen > 0 ? " (" + applied.LeftOpen.ToString("N0") + " of them had new activity after the review.)" : ""));
            if (plan.BlockedCount > 0 && await Dialogs.ConfirmAsync("Show the matters that couldn't be closed?",
                    "Some matters still have open items or split billing and can't be closed. Would you like to see the details?", "Show details"))
                ReportWindow.Show(this, "Matters that can't be closed", MatterStatusLockService.ExceptionTable(plan), ExceptionSubtitle);
        }

        private const string ExceptionSubtitle =
            "These matters still have open items or split billing. The \"Why It Can't Be Closed\" column lists what must be cleared first.";

        private static DataTable SheetProblemTable(IEnumerable<SheetMatterRow> rows)
        {
            var t = new DataTable();
            t.Columns.Add("Row", typeof(int));
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Problem", typeof(string));
            foreach (var r in rows)
                t.Rows.Add(r.RowNumber, r.ClientCode, r.MatterCode, r.Problem);
            return t;
        }
        private static string Matters(int n) => n.ToString("N0") + (n == 1 ? " matter" : " matters");
    }
}
