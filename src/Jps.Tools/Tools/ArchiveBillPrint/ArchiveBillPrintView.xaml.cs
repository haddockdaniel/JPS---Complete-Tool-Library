using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.ArchiveBillPrint
{
    public sealed class ArchiveBillPrint : IJurisTool
    {
        public string Id => "JPS.ARCHBILLPRINT";
        public FrameworkElement CreateView(IToolContext context) => new ArchiveBillPrintView(context);
    }

    /// <summary>
    /// Saves archived Juris invoices (JBills archive images) as PDF files in a folder, chosen by date, number, client/matter,
    /// balance due and/or a file of invoice numbers, optionally with their expense attachments; invoices already saved are
    /// skipped and at most 2,500 are printed per run. Ported from "JPS - Archive Bill Bulk Print Tool" with these fixes: the
    /// promised 2,500 limit didn't exist; the default folder was one customer's hard-coded drive; the run closed the Juris
    /// connection so a second run failed; a CSV choice with no file caused an SQL error; dates and codes were concatenated
    /// into SQL; every expense attachment in the database was loaded at once and only attached to a bill's first matter;
    /// a failure left every control disabled; lower-case tokens passed validation but weren't replaced; illegal file-name
    /// characters weren't removed and "pdf" was stripped anywhere in the name; a matter without bill copies crashed with
    /// [BilAdrNick]; render failures were swallowed and reported as success, and an unclosed file broke every later bill;
    /// empty files from a failed run counted as printed; UtilityLog was never written.
    /// Rendering goes through Juris's JurisSVR21 + the ActiveReports 6 runtime the original shipped (see <see cref="JurisArchiveRenderer"/>).
    /// </summary>
    public partial class ArchiveBillPrintView : UserControl
    {
        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;
        private readonly ArchiveBillPrintService _service;
        private List<int> _csvBills;
        private DataTable _problems;
        private CancellationTokenSource _cancel;

        public ArchiveBillPrintView(IToolContext context)
        {
            _context = context;
            _service = new ArchiveBillPrintService(context.Session);
            InitializeComponent();
            FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JPS Archive Bills");
            NamingBox.Text = ArchiveBillPrintService.DefaultNaming;
            ByDate.IsChecked = true;    // original defaults
            ByBalance.IsChecked = true;
            Loaded += async (_, __) =>
            {
                if (ClientBox.ItemsSource != null) return;
                var clients = await Frame.RunAsync("Loading clients…", () => Lookup.Load(Session, ArchiveBillPrintService.ClientListSql));
                if (clients == null) return;
                ClientBox.ItemsSource = clients;
                if (clients.Count == 0)
                {
                    ByClient.IsChecked = false;
                    ByClient.IsEnabled = false;
                    Frame.ShowWarning("There are no billed clients in this company, so there are no invoices to print.");
                }
            };
        }

        // ---------- input state ----------

        private int CriteriaCount =>
            new[] { ByDate, ByNumber, ByClient, ByBalance, ByCsv }.Count(b => b.IsChecked == true); // by-matter refines by-client, not counted

        private void OnCriteriaChanged(object sender, RoutedEventArgs e)
        {
            if (MatchPanel == null) return;
            if (ByClient.IsChecked != true) ByMatter.IsChecked = false;
            MatchPanel.Visibility = CriteriaCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
            UpdateEnabled();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

        private void UpdateEnabled()
        {
            if (PrintButton == null) return;
            PrintButton.IsEnabled = CriteriaCount > 0 && !string.IsNullOrWhiteSpace(FolderBox.Text);
        }

        private string SelectedClient => ClientBox.SelectedItem is LookupItem c ? Convert.ToString(c.Key) : null;
        private string SelectedMatter => MatterBox.SelectedItem is LookupItem m ? Convert.ToString(m.Key) : null;

        private async void OnClientChanged(object sender, SelectionChangedEventArgs e)
        {
            MatterBox.ItemsSource = null;
            if (ByMatter.IsChecked == true) await LoadMatters();
        }

        private async void OnMatterToggled(object sender, RoutedEventArgs e)
        {
            if (ByMatter.IsChecked == true && MatterBox.ItemsSource == null) await LoadMatters();
        }

        private async System.Threading.Tasks.Task LoadMatters()
        {
            var client = SelectedClient;
            if (client == null) return;
            var matters = await Frame.RunAsync("Loading matters…", () =>
                Lookup.Load(Session, ArchiveBillPrintService.MatterListSql, Sql.P("@Cli", client)));
            if (matters == null || client != SelectedClient) return;
            MatterBox.ItemsSource = matters;
            if (matters.Count == 1) MatterBox.SelectedIndex = 0;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var folder = FolderPicker.Pick(Window.GetWindow(this), "Where to save the PDF files", FolderBox.Text.Trim());
            if (folder != null) FolderBox.Text = folder;
        }

        private void OnPickCsv(object sender, RoutedEventArgs e)
        {
            var file = Dialogs.PickImportFile("CSV and text files (*.csv;*.txt)|*.csv;*.txt");
            if (file == null) return;
            try
            {
                _csvBills = ArchiveBillPrintService.ParseInvoiceNumbers(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _csvBills = null;
                CsvSummary.Text = "No file chosen";
                Frame.ShowError("Couldn't read the file: " + ex.Message);
                return;
            }
            CsvSummary.Text = Path.GetFileName(file) + " — " + _csvBills.Count.ToString("N0") + " invoice number(s)";
            if (_csvBills.Count == 0)
                Frame.ShowWarning("The file has no invoice numbers in it.");
            else
                Frame.ShowInfo(_csvBills.Count.ToString("N0") + " invoices added.");
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            var folder = FolderBox.Text.Trim();
            if (folder.Length > 0 && Directory.Exists(folder))
                Process.Start("explorer.exe", "\"" + folder + "\"");
            else
                Frame.ShowInfo("That folder doesn't exist yet. It's created when you print.");
        }

        private void OnShowProblems(object sender, RoutedEventArgs e)
        {
            if (_problems != null)
                ReportWindow.Show(this, "Invoices that weren't printed", _problems, FolderBox.Text.Trim());
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _cancel?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Stopping after this invoice…";
        }

        // ---------- run ----------

        private string Validate(out SelectionCriteria criteria)
        {
            criteria = new SelectionCriteria { BalanceOnly = ByBalance.IsChecked == true, UseOr = CriteriaCount >= 2 && MatchAny.IsChecked == true };
            if (string.IsNullOrWhiteSpace(FolderBox.Text))
                return "Choose a folder for the PDF files.";
            if (ByDate.IsChecked == true)
            {
                if (DateFrom.SelectedDate == null || DateTo.SelectedDate == null)
                    return "Fill in both dates, or untick \"Invoices dated\".";
                if (DateFrom.SelectedDate > DateTo.SelectedDate)
                    return "The first date must be on or before the second date.";
                criteria.DateFrom = DateFrom.SelectedDate;
                criteria.DateTo = DateTo.SelectedDate;
            }
            if (ByNumber.IsChecked == true)
            {
                if (!int.TryParse(NumberFrom.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var from)
                    || !int.TryParse(NumberTo.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var to))
                    return "Fill in both invoice numbers as whole numbers, or untick \"Invoice numbers\".";
                criteria.NumberFrom = Math.Min(from, to);
                criteria.NumberTo = Math.Max(from, to);
            }
            if (ByClient.IsChecked == true)
            {
                if (SelectedClient == null) return "Pick the client.";
                criteria.Client = SelectedClient;
                if (ByMatter.IsChecked == true)
                {
                    if (SelectedMatter == null) return "Pick the matter, or untick \"Only one of the client's matters\".";
                    criteria.Matter = SelectedMatter;
                }
            }
            if (ByCsv.IsChecked == true)
            {
                if (_csvBills == null || _csvBills.Count == 0) return "Choose a file with the invoice numbers, or untick \"Invoice numbers from a file\".";
                criteria.CsvBills = _csvBills.ToList();
            }
            if (!ArchiveBillPrintService.IsValidNaming(NamingBox.Text))
                return "The file name pattern isn't valid. Use only the fields listed under it, dashes and spaces, and end it with .pdf.";
            return null;
        }

        private async void OnPrint(object sender, RoutedEventArgs e)
        {
            Frame.ClearMessage();
            var problem = Validate(out var criteria);
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return;
            }
            var folder = FolderBox.Text.Trim();
            var naming = ArchiveBillPrintService.Normalize(NamingBox.Text.Trim());
            var attachments = Attachments.IsChecked == true;

            if ((ArchiveBillPrintService.Uses(naming, "[NowDate]") || ArchiveBillPrintService.Uses(naming, "[NowTime]"))
                && !await Dialogs.ConfirmAsync("Names include today's date or time",
                    "Files named with the current date/time are never skipped on re-runs, so every run prints all the invoices again. Continue?", "Continue"))
                return;
            if (!ArchiveBillPrintService.Uses(naming, "[BillNum]")
                && !await Dialogs.ConfirmAsync("Names don't include the invoice number",
                    "Without [Invoice Number], different invoices can get the same file name, and only the first of them is saved. Continue?", "Continue"))
                return;

            var now = DateTime.Now;
            var plan = await Frame.RunAsync("Finding invoices…", () =>
            {
                var rows = _service.SelectBills(criteria);
                return rows.Count == 0 ? null : _service.BuildPlan(rows, folder, naming, attachments, now);
            });
            if (Frame.HasMessage) return; // error already shown
            if (plan == null)
            {
                Frame.ShowWarning("No invoices match your choices. Change them and try again.");
                return;
            }
            KeepProblems(plan.NoArchive.Count == 0 ? null : ProblemsFor(plan));

            if (plan.NoArchive.Count == plan.SelectedInvoices)
            {
                Frame.ShowWarning("None of the " + Count(plan.SelectedInvoices, "selected invoice") + " has an archived copy in Juris, so there is nothing to print.");
                return;
            }
            if (plan.ThisRun.Count == 0)
            {
                Frame.ShowSuccess("All " + Count(plan.AlreadyPrinted, "invoice") + " with an archived copy are already in the folder. Nothing new to print."
                                  + NoArchiveNote(plan.NoArchive.Count));
                return;
            }

            var renderer = ArchiveBillRenderer.Create(Session);
            if (!renderer.IsAvailable)
            {
                Frame.ShowWarning(Count(plan.ThisRun.Count + plan.LeftForNextRun, "invoice") + " would be printed, but " + renderer.UnavailableReason + " Nothing was written.");                return;
            }

            var summary = Count(plan.ThisRun.Count, "invoice") + " will be saved to " + folder + ".";
            if (plan.AlreadyPrinted > 0) summary += "\n\n" + Count(plan.AlreadyPrinted, "invoice") + " already in the folder will be skipped.";
            if (plan.NoArchive.Count > 0) summary += "\n\n" + Count(plan.NoArchive.Count, "invoice") + " have no archived copy in Juris and can't be printed.";
            if (plan.LeftForNextRun > 0)
                summary += "\n\nOnly " + ArchiveBillPrintService.MaxInvoicesPerRun.ToString("N0") + " invoices are printed per run, so "
                           + Count(plan.LeftForNextRun, "invoice") + " will be left. Run the tool again to print them; invoices already printed are skipped.";
            if (!await Dialogs.ConfirmAsync("Print " + Count(plan.ThisRun.Count, "invoice") + " to PDF?", summary, "Print"))
                return;

            PrintOutcome outcome;
            _cancel = new CancellationTokenSource();
            CancelButton.Content = "Stop after this invoice";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            try
            {
                var token = _cancel.Token;
                outcome = await Frame.RunAsync("Printing invoice 1 of " + plan.ThisRun.Count.ToString("N0") + "…", () =>
                    StaThread.Run(() => _service.Print(plan, renderer, (i, n) => Dispatcher.BeginInvoke(new Action(() =>
                        Frame.BusyText = "Printing invoice " + i.ToString("N0") + " of " + n.ToString("N0") + "…")), token)));
            }
            finally
            {
                CancelButton.Visibility = Visibility.Collapsed;
                _cancel.Dispose();
                _cancel = null;
                // Close the bill printing helper so its memory goes back to Windows straight away.
                await System.Threading.Tasks.Task.Run(() => renderer.Dispose());
            }
            if (outcome == null) return; // error already shown

            if (outcome.PdfFilesWritten + outcome.AttachmentsWritten > 0)
            {
                var logged = await Frame.RunAsync("Finishing…", () => Session.InTransaction(() =>
                    Session.WriteUtilityLog("Archive Bill Bulk Print - " + outcome.InvoicesPrinted + " invoices printed to " + folder)));
                if (!logged) return; // error already shown
            }

            KeepProblems(outcome.Problems.Rows.Count == 0 ? null : outcome.Problems);
            var message = (outcome.Cancelled ? "Stopped as you asked. " : "")
                          + Count(outcome.InvoicesPrinted, "invoice") + " printed (" + Count(outcome.PdfFilesWritten, "PDF file")
                          + (attachments ? ", " + Count(outcome.AttachmentsWritten, "attachment") : "") + ").";
            var remaining = plan.LeftForNextRun + outcome.NotReached;
            if (remaining > 0)
                message += " " + Count(remaining, "invoice") + " still to print: run the tool again to print them (invoices already printed are skipped).";

            if (outcome.Failed > 0 || plan.NoArchive.Count > 0)
            {
                Frame.ShowWarning(message + (outcome.Failed > 0 ? " " + Count(outcome.Failed, "invoice") + " couldn't be printed." : "")
                                  + NoArchiveNote(plan.NoArchive.Count) + " See the list of problems.");
                ReportWindow.Show(this, "Invoices that weren't printed", _problems, folder);
            }
            else
            {
                Frame.ShowSuccess(message);
            }
        }

        private void KeepProblems(DataTable problems)
        {
            _problems = problems;
            ProblemsButton.IsEnabled = problems != null;
        }

        private static DataTable ProblemsFor(PrintPlan plan)
        {
            var table = ArchiveBillPrintService.NewProblemTable();
            foreach (var row in plan.NoArchive)
                table.Rows.Add(row.BillNo, row.ClientCode + "  " + row.ClientName, row.MatterCode + "  " + row.MatterName,
                    "Juris has no archived copy of invoice " + row.BillNo + ", so it can't be printed");
            return table;
        }

        private static string NoArchiveNote(int count) =>
            count == 0 ? "" : " " + Count(count, "invoice") + " had no archived copy in Juris.";

        private static string Count(int n, string noun) => n.ToString("N0") + " " + noun + (n == 1 ? "" : "s");
    }
}
