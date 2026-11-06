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

namespace Jps.Tools.Tools.MatterCloser
{
    public sealed class MatterCloserTool : IJurisTool
    {
        public string Id => "JPS.MATCLOSER";
        public FrameworkElement CreateView(IToolContext context) => new MatterCloserView(context);
    }

    /// <summary>
    /// Closes eligible matters (status C, lock "Disallow Time and Expense", close date today) picked either by criteria
    /// (no activity for N days / since a date, plus timekeeper, client, practice class filters and exceptions) or from a
    /// spreadsheet of client/matter codes. Merges "JPS - Matter Closer v10" and "Matter Closer from Excel" onto ONE set of
    /// close rules (<see cref="MatterCloseRules"/>, stricter rule wins: trust in TrustAccount or TrustLedger blocks, split
    /// billing blocks). Fixes: Suite detection is recomputed per run (was sticky across companies and missing in the Excel
    /// tool, so non-Suite companies failed or Suite entries were ignored); typed combo text and culture date strings were
    /// concatenated into SQL (crashes, injection, silently dropped filters/exceptions); exceptions by timekeeper matched
    /// initials, not the timekeeper; matters with no originator were never reported or closed; blank counts and null
    /// results crashed; blank spreadsheet cells crashed and leaked Excel; alphanumeric codes were zero-padded; unposted
    /// Suite batches were missed when a link row was NULL (NOT IN); the rules are re-checked at update time; UtilityLog is
    /// written.
    /// </summary>
    public partial class MatterCloserView : UserControl
    {
        private static readonly int[] DayChoices = { 90, 180, 270, 365, 730 };

        private readonly IToolContext _context;
        private readonly MatterCloserService _service;
        private string _file;

        private JurisSession Session => _context.Session;

        public MatterCloserView(IToolContext context)
        {
            _context = context;
            _service = new MatterCloserService(context.Session);
            InitializeComponent();
            DaysBox.ItemsSource = DayChoices;
            DaysBox.SelectedIndex = 0;
            SinceDate.SelectedDate = DateTime.Today;
            OpenedDate.SelectedDate = DateTime.Today;
            Loaded += async (_, __) =>
            {
                if (BillTkprBox.ItemsSource != null) return;
                var lists = await Frame.RunAsync("Loading timekeepers, clients and practice classes…", () => new
                {
                    Tkprs = Lookup.Load(Session,
                        @"select EmpSysNbr, case when EmpInitials <= '   ' then EmpID else EmpInitials end as Code, EmpName
                          from Employee where EmpValidAsTkpr = 'Y' order by 2, 3"),
                    Clients = Lookup.Load(Session,
                        "select CliSysNbr, dbo.jfn_FormatClientCode(CliCode) as Code, CliReportingName from Client order by 2"),
                    Classes = Lookup.Load(Session, "select PrctClsCode, PrctClsCode as Code, PrctClsDesc from PracticeClass order by PrctClsCode"),
                });
                if (lists == null) return;
                Fill(BillTkprBox, lists.Tkprs, "All billing timekeepers");
                Fill(ClientBox, lists.Clients, "All clients");
                Fill(PracticeClassBox, lists.Classes, "All practice classes");
                Fill(ExceptBillTkprBox, lists.Tkprs, "None");
                Fill(ExceptClientBox, lists.Clients, "None");
                UpdateState();
            };
        }

        private static void Fill(ComboBox box, List<LookupItem> items, string noneText)
        {
            var list = new List<LookupItem> { new LookupItem(null, noneText, "") };
            list.AddRange(items);
            box.ItemsSource = list;
            box.SelectedIndex = 0;
        }

        private static object KeyOf(ComboBox box) => (box.SelectedItem as LookupItem)?.Key;
        private static string TextOf(ComboBox box) => (box.SelectedItem as LookupItem)?.Display;

        private bool IsSheetMode => SheetMode.IsChecked == true;

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (CriteriaPanel == null) return; // during InitializeComponent
            CriteriaPanel.Visibility = IsSheetMode ? Visibility.Collapsed : Visibility.Visible;
            SheetPanel.Visibility = IsSheetMode ? Visibility.Visible : Visibility.Collapsed;
            CriteriaReports.Visibility = IsSheetMode ? Visibility.Collapsed : Visibility.Visible;
            CheckFileButton.Visibility = IsSheetMode ? Visibility.Visible : Visibility.Collapsed;
            Frame.ClearMessage();
            UpdateState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateState();

        /// <summary>Enable rules: criteria mode needs a day count or date (and the opened-after date when that box is ticked); spreadsheet mode needs a file.</summary>
        private void UpdateState()
        {
            if (CloseButton == null || DaysBox == null || OpenedDate == null) return;
            var listsLoaded = BillTkprBox.ItemsSource != null;
            var criteriaOk = listsLoaded
                             && (DaysMode.IsChecked == true ? DaysBox.SelectedItem is int : SinceDate.SelectedDate.HasValue)
                             && (OpenedLimit.IsChecked != true || OpenedDate.SelectedDate.HasValue);
            CloseButton.IsEnabled = IsSheetMode ? _file != null : criteriaOk;
            ExceptionsButton.IsEnabled = criteriaOk;
            ToCloseButton.IsEnabled = criteriaOk;
            CheckFileButton.IsEnabled = _file != null;
        }

        private CloseCriteria ReadCriteria()
        {
            if (DaysMode.IsChecked == true && !(DaysBox.SelectedItem is int))
            {
                Frame.ShowWarning("Please choose a whole number of days of no activity.");
                return null;
            }
            return new CloseCriteria
            {
                Days = DaysMode.IsChecked == true ? (int?)(int)DaysBox.SelectedItem : null,
                Since = DaysMode.IsChecked == true ? null : SinceDate.SelectedDate,
                BillTkpr = KeyOf(BillTkprBox),
                BillTkprText = TextOf(BillTkprBox),
                Client = KeyOf(ClientBox),
                ClientText = TextOf(ClientBox),
                PracticeClass = KeyOf(PracticeClassBox),
                OpenedOnOrBefore = OpenedLimit.IsChecked == true ? OpenedDate.SelectedDate : null,
                ExceptBillTkpr = KeyOf(ExceptBillTkprBox),
                ExceptBillTkprText = TextOf(ExceptBillTkprBox),
                ExceptClient = KeyOf(ExceptClientBox),
                ExceptClientText = TextOf(ExceptClientBox),
            };
        }

        // ---------- Reports ----------

        private async void OnExceptionReport(object sender, RoutedEventArgs e)
        {
            var criteria = ReadCriteria();
            if (criteria == null) return;
            var table = await Frame.RunAsync("Finding matters that can't be closed…", () => _service.CriteriaReport(criteria, true));
            if (table != null) ShowExceptions(table, criteria);
        }

        private void ShowExceptions(DataTable table, CloseCriteria criteria) =>
            ReportWindow.Show(this, "Matters that can't be closed", table,
                "Matching matters that still have open items. The \"Why It Can't Be Closed\" column lists what must be cleared first. Criteria: " + criteria.Describe());

        private async void OnToCloseReport(object sender, RoutedEventArgs e)
        {
            var criteria = ReadCriteria();
            if (criteria == null) return;
            var table = await Frame.RunAsync("Finding matters to close…", () => _service.CriteriaReport(criteria, false));
            if (table != null)
                ReportWindow.Show(this, "Matters to be closed", table, "These matters match the criteria and are free of open items. Criteria: " + criteria.Describe());
        }

        // ---------- Spreadsheet ----------

        private void OnChooseFile(object sender, RoutedEventArgs e)
        {
            var path = Dialogs.PickImportFile();
            if (path == null) return;
            _file = path;
            FileText.Text = path;
            Frame.ClearMessage();
            UpdateState();
        }

        private async void OnCheckFile(object sender, RoutedEventArgs e)
        {
            var file = _file;
            var pad = PadCodes.IsChecked == true;
            if (file == null) return;
            var check = await Frame.RunAsync("Reading and checking " + Path.GetFileName(file) + "…", () => _service.CheckSpreadsheet(file, pad));
            if (check == null) return;
            ReportWindow.Show(this, "Spreadsheet check", check.Report, SheetSummary(check));
        }

        private static string SheetSummary(SheetCheck check) =>
            check.ReadyIds.Count + " of " + check.Rows.Count + " rows can be closed; " + check.ProblemCount + " can't (see the Reason column). " +
            "Row is the row number in your spreadsheet.";

        // ---------- Close ----------

        private async void OnClose(object sender, RoutedEventArgs e)
        {
            if (IsSheetMode)
                await CloseFromSheet();
            else
                await CloseByCriteria();
        }

        private async System.Threading.Tasks.Task CloseByCriteria()
        {
            var criteria = ReadCriteria();
            if (criteria == null) return;
            var counts = await Frame.RunAsync("Counting matching matters…", () => _service.CountCriteria(criteria));
            if (counts == null) return; // the error is shown; nothing was changed
            if (counts.Matching == 0)
            {
                Frame.ShowWarning("No Open or Final Bill Sent matters match these criteria. Nothing was changed.");
                return;
            }
            if (counts.Ready == 0)
            {
                Frame.ShowWarning("All " + counts.Matching.ToString("N0") + " matching matters still have open items, so none can be closed. Use \"Matters that can't be closed\" to see why.");
                return;
            }

            if (!await Dialogs.ConfirmAsync("Close " + counts.Ready.ToString("N0") + " matters?",
                    counts.Blocked.ToString("N0") + " matching matters can't be closed because they still have open items; you can review them on the \"Matters that can't be closed\" report.\n\n" +
                    counts.Ready.ToString("N0") + " matters will be closed and locked against new time and expenses. Do you want to continue?",
                    "Close matters"))
                return;

            var closed = await Frame.RunAsync("Closing matters…", () => (int?)_service.CloseByCriteria(criteria));
            if (closed == null) return;
            var skipped = counts.Ready - closed.Value;
            Frame.ShowSuccess("Closed " + closed.Value.ToString("N0") + " matters. " + counts.Blocked.ToString("N0") + " matters couldn't be closed because of open items." +
                              (skipped > 0 ? " " + skipped.ToString("N0") + " more were left open because they gained activity after the count." : ""));

            if (counts.Blocked > 0 && await Dialogs.ConfirmAsync("See the matters that weren't closed?",
                    "Closed " + closed.Value.ToString("N0") + " matters. Would you like to see the report of matters that can't be closed?", "Show report"))
            {
                var table = await Frame.RunAsync("Finding matters that can't be closed…", () => _service.CriteriaReport(criteria, true));
                if (table != null) ShowExceptions(table, criteria);
            }
        }

        private async System.Threading.Tasks.Task CloseFromSheet()
        {
            var file = _file;
            var pad = PadCodes.IsChecked == true;
            if (file == null) return;
            var check = await Frame.RunAsync("Reading and checking " + Path.GetFileName(file) + "…", () => _service.CheckSpreadsheet(file, pad));
            if (check == null) return;

            if (check.ReadyIds.Count == 0)
            {
                Frame.ShowWarning("None of the " + check.Rows.Count.ToString("N0") + " matters in the file can be closed. Nothing was changed; the report shows why.");
                ReportWindow.Show(this, "Spreadsheet check", check.Report, SheetSummary(check));
                return;
            }
            if (!ReportWindow.Confirm(this, "Close matters from the spreadsheet", check.Report, SheetSummary(check) + " Only rows marked \"Will be closed\" are changed.",
                    "Close " + check.ReadyIds.Count.ToString("N0") + " matters"))
                return;

            var ready = check.ReadyIds;
            var name = Path.GetFileName(file);
            var closedIds = await Frame.RunAsync("Closing matters…", () => _service.CloseFromSpreadsheet(ready, name));
            if (closedIds == null) return;

            // Post-report: every row that was not closed, with the reason.
            var notClosed = new DataTable();
            notClosed.Columns.Add(MatterCloserService.RowColumn, typeof(int));
            notClosed.Columns.Add(MatterCloserService.ClientColumn, typeof(string));
            notClosed.Columns.Add(MatterCloserService.MatterColumn, typeof(string));
            notClosed.Columns.Add(MatterCloserService.ReasonColumn, typeof(string));
            var byRow = check.Rows.ToDictionary(r => r.RowNumber);
            foreach (DataRow r in check.Report.Rows)
            {
                var row = byRow[(int)r[MatterCloserService.RowColumn]];
                var willClose = (string)r[MatterCloserService.ResultColumn] == MatterCloserService.WillClose;
                if (willClose && closedIds.Contains(row.MatSysNbr.Value)) continue;
                var error = willClose ? "Not closed: the matter had new activity or balances after the check" : (string)r[MatterCloserService.ReasonColumn];
                notClosed.Rows.Add(r[MatterCloserService.RowColumn], r[MatterCloserService.ClientColumn], r[MatterCloserService.MatterColumn], error);
            }

            if (notClosed.Rows.Count == 0)
            {
                Frame.ShowSuccess("The process was successful and had no issues. Closed " + closedIds.Count.ToString("N0") + " matters.");
                return;
            }
            Frame.ShowSuccess("Closed " + closedIds.Count.ToString("N0") + " matters. " + notClosed.Rows.Count.ToString("N0") + " rows were not closed; see the report.");
            ReportWindow.Show(this, "Rows not closed", notClosed, "These rows in " + name + " were not closed. Fix the items listed and run the file again.");
        }
    }
}
