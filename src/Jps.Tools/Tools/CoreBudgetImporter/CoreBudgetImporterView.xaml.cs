using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.CoreBudgetImporter
{
    public sealed class CoreBudgetImporter : IJurisTool
    {
        public string Id => "JPS.GLBUDGET"; // historical catalog id; the tool imports MATTER fee/expense budgets, not GL budgets
        public FrameworkElement CreateView(IToolContext context) => new CoreBudgetImporterView(context);
    }

    /// <summary>
    /// Imports matter fee and expense budgets (MatterFeeBudget / MatterExpBudget) from a spreadsheet, with every option
    /// Juris' budget lines and matter budget settings have:
    /// - Lines: Fee rows may name a Task Code, Activity Code, Timekeeper (EmpID, stored as EmpSysNbr in MFBTkpr) and
    ///   Personnel Type; Expense rows a Task Code and Expense Code; any blank = "any value". Each row has a Budget Phase
    ///   (0-999, blank = the tool's default phase). Duplicate lines (same matter, phase and codes) are rejected.
    /// - Matter settings (all matters in the file): budgets on for the matter and client (MatBudgetOption /
    ///   CliBudgetOption = '1'); current phase MatBudgetPhase kept (0 when budgets were off, as Juris then gives entries
    ///   phase 0) or set; MatReqPhaseOnTrans kept / Y / N. When it ends up Y, every phase used and the current phase need a
    ///   fee AND an expense line (Juris checks time against MatterFeeBudget and expenses against MatterExpBudget).
    /// - Every row is checked first (problems listed by spreadsheet row, nothing imported while any remain); settings are
    ///   re-checked in memory when changed. The pre-report shows old/new lines and totals, phases, current phase and
    ///   require-phase before/after, actuals kept and unbilled/unposted entries no new line would take (they would post to
    ///   a catch-all line Juris adds).
    /// - Import: one transaction, matters and budget lines locked and re-read; each matter's whole budget is replaced and
    ///   each old line's posted actuals move to the new line Juris posting would pick for it (BudgetMatch, copied from
    ///   ModFees.JGetMFBRow / ModExp.JGetMEBRow); the import is blocked if any old line's actuals have no such line.
    /// Ported from "Juris Core Budget Import" with these fixes: only the first row's matter was collected and every line
    /// was inserted on every matter; rank() gave duplicate line numbers; codes weren't validated; no transaction; values
    /// concatenated into SQL; crashes on cancel / empty sheet / short header. Its "calculate actuals" choice (ITD / yearly /
    /// monthly) was removed: it only wrote 0/2/3/4 into the phase, which Juris treats as a plain phase number. Files in the
    /// original layout (Client, Matter, Task_Type, TaskExp, Task_Code, Task_Desc, Hours, Amount, MatSysNbr in column I)
    /// still import; there Task_Code holds the expense code on Expense rows. Zero-amount rows are now imported as lines
    /// (they decide where posted work goes and can receive carried actuals); the original skipped them.
    /// </summary>
    public partial class CoreBudgetImporterView : UserControl
    {
        private readonly IToolContext _context;
        private readonly CoreBudgetImportService _service;
        private readonly bool _ready;
        private string _path;
        private ImportPlan _plan;       // last checked file (may have problems)

        public CoreBudgetImporterView(IToolContext context)
        {
            _context = context;
            _service = new CoreBudgetImportService(context.Session);
            InitializeComponent();
            _ready = true;
        }

        private static int? ReadPhase(TextBox box) =>
            int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 0 && p <= CoreBudgetImportService.MaxPhase ? p : (int?)null;

        /// <summary>The settings as entered, or null (with a message) when a phase box isn't a whole number from 0 to 999.</summary>
        private BudgetSettings ReadSettings(out string problem)
        {
            problem = null;
            var defaultPhase = ReadPhase(DefaultPhaseBox);
            var currentPhase = ReadPhase(CurrentPhaseBox);
            if (defaultPhase == null)
                problem = "\"Budget phase for rows that leave Budget Phase blank\" must be a whole number from 0 to 999.";
            else if (PhaseSet.IsChecked == true && currentPhase == null)
                problem = "The current budget phase must be a whole number from 0 to 999.";
            if (problem != null) return null;
            return new BudgetSettings
            {
                DefaultPhase = defaultPhase.Value,
                CurrentPhase = PhaseSet.IsChecked == true ? currentPhase : null,
                RequirePhase = RequireYes.IsChecked == true ? true : RequireNo.IsChecked == true ? false : (bool?)null,
            };
        }

        private async void OnChooseFile(object sender, RoutedEventArgs e)
        {
            var path = Dialogs.PickImportFile();
            if (path == null) return; // cancelled: nothing changes
            _path = path;
            await CheckFileAsync();
        }

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            if (_path != null) await CheckFileAsync();
        }

        private void Reset()
        {
            _plan = null;
            ImportButton.IsEnabled = false;
            ProblemsButton.Visibility = Visibility.Collapsed;
            FileButton.Visibility = Visibility.Collapsed;
            PhaseNote.Visibility = Visibility.Collapsed;
            SummaryText.Text = "Choose a file to check it against Juris.";
        }

        private async System.Threading.Tasks.Task CheckFileAsync()
        {
            var path = _path;
            var settings = ReadSettings(out var problem);
            Reset();
            FileText.Text = Path.GetFileName(path) + "   (" + Path.GetDirectoryName(path) + ")";
            ReloadButton.IsEnabled = true;
            if (settings == null)
            {
                Frame.ShowWarning(problem);
                return;
            }

            var plan = await Frame.RunAsync("Reading and checking " + Path.GetFileName(path) + "…", () =>
                _service.Check(path, CoreBudgetImportService.ReadFile(path), settings));
            if (plan == null)
            {
                SummaryText.Text = "The file couldn't be checked. See the message above.";
                return;
            }
            if (path != _path) return;

            _plan = plan;
            FileButton.Visibility = Visibility.Visible;
            ShowPlan();
        }

        /// <summary>Re-checks the loaded file against the changed settings (in memory, no database work).</summary>
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            CurrentPhaseBox.IsEnabled = PhaseSet.IsChecked == true;
            if (_plan == null) return;
            var settings = ReadSettings(out var problem);
            if (settings == null)
            {
                ImportButton.IsEnabled = false;
                Frame.ShowWarning(problem);
                return;
            }
            CoreBudgetImportService.Evaluate(_plan, settings);
            ShowPlan();
        }

        private void ShowPlan()
        {
            var plan = _plan;
            var rows = plan.FileRows.Rows.Count;
            ImportButton.IsEnabled = false;
            ProblemsButton.Visibility = plan.Errors.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var off = plan.Matters.Count(m => !m.BudgetsWereOn);
            PhaseNote.Visibility = plan.Loaded && off > 0 ? Visibility.Visible : Visibility.Collapsed;
            PhaseNote.Text = off + " matter(s) in this file have budgets switched off now; their current budget phase will be "
                             + (plan.Settings.CurrentPhase ?? 0) + ".";

            if (plan.FileErrors.Rows.Count > 0)
            {
                SummaryText.Text = rows + " row(s) read. " + plan.FileErrors.Rows.Count + " row(s) have problems, so nothing can be imported yet.";
                Frame.ShowWarning(plan.FileErrors.Rows.Count + " row(s) in the file have problems. Click Problems for the list (by spreadsheet row), fix them, save the file and click Check again. No budgets were changed.");
                return;
            }
            if (plan.Matters.Count == 0)
            {
                SummaryText.Text = rows + " row(s) read, but none of them is a budget line.";
                Frame.ShowWarning("The file does not contain any budget rows.");
                return;
            }

            var existing = plan.Matters.Count(m => m.OldFeeLines + m.OldExpenseLines > 0);
            var timeCatchAll = plan.Matters.Sum(m => m.TimeToCatchAll);
            var expCatchAll = plan.Matters.Sum(m => m.ExpensesToCatchAll);
            SummaryText.Text = rows + " row(s) read for " + plan.Matters.Count + " matter(s): " + plan.FeeLines + " fee line(s) and "
                + plan.ExpenseLines + " expense line(s). "
                + (existing > 0 ? existing + " of these matters already have a budget, which will be replaced." : "None of these matters has a budget yet.")
                + (timeCatchAll + expCatchAll > 0
                    ? " " + timeCatchAll + " unbilled or unposted time entr(ies) and " + expCatchAll + " expense entr(ies) fit no line in the new budget; they will go to a catch-all line Juris adds."
                    : "");

            if (plan.Errors.Rows.Count > 0)
            {
                Frame.ShowWarning(plan.Errors.Rows.Count + " problem(s) with these settings. Click Problems for the list, then change the settings or the file. No budgets were changed.");
                return;
            }
            ImportButton.IsEnabled = true;
            Frame.ShowInfo("The file is ready to import. Click Import budgets to review exactly what will change.");
        }

        private void OnShowProblems(object sender, RoutedEventArgs e)
        {
            if (_plan?.Errors != null)
                ReportWindow.Show(this, "Problems in " + Path.GetFileName(_plan.FilePath), _plan.Errors,
                    "Nothing was imported. Row numbers are spreadsheet rows (row 1 is the heading row). A blank row number means the problem is with the whole matter. Fix these, save the file and click Check again.");
        }

        private void OnShowFile(object sender, RoutedEventArgs e)
        {
            if (_plan?.FileRows != null)
                ReportWindow.Show(this, Path.GetFileName(_plan.FilePath), _plan.FileRows, "The rows read from the file, as the tool sees them.");
        }

        private async void OnImport(object sender, RoutedEventArgs e)
        {
            var plan = _plan;
            if (plan == null || !plan.CanImport) return;

            // Juris keeps posted actuals only on the budget lines themselves; never delete a line whose actuals can't be carried over.
            if (plan.LostCount > 0)
            {
                var lost = CoreBudgetImportService.LostActualsTable(plan);
                Frame.ShowWarning(lost.Rows.Count + " current budget line(s) have posted actuals that no line in the file would take over, so replacing the budget would lose them. Nothing was changed.");
                ReportWindow.Show(this, "Budget lines whose actuals would be lost", lost,
                    "Juris adds posted time, expenses and bills to these lines and can't rebuild them. For each one, add a row in the same Budget Phase that this work would post to: the same codes, or blank codes for a line that takes anything (a 0 amount is fine). Then Check again.");
                return;
            }

            var review = CoreBudgetImportService.ReviewTable(plan);
            var subtitle = "The WHOLE fee and expense budget of each matter below is deleted and replaced by the rows in the file ("
                + plan.FeeLines + " fee line(s), " + plan.ExpenseLines + " expense line(s)). Budgets are switched on for each matter and its client. "
                + "Actuals already posted are kept on the new line the same work would post to. Entries with no matching line will go to a catch-all line Juris adds when they post or are billed. "
                + "Everything is saved together, or nothing is.";
            if (!ReportWindow.Confirm(this, "Replace the budget of " + plan.Matters.Count + " matter(s)?", review, subtitle, "Replace budgets"))
                return;

            ImportButton.IsEnabled = false;
            var result = await Frame.RunAsync("Importing budgets for " + plan.Matters.Count + " matter(s)…", () => _service.Apply(plan));
            if (result == null)
            {
                Frame.ShowError("The budget import failed, so no budgets were changed. " + (Frame.HasMessage ? Frame.Message : ""));
                ImportButton.IsEnabled = true;
                return;
            }

            // Clear the checked plan so the same file can't be imported twice by accident; Check again re-reads it against Juris.
            Reset();
            Frame.ShowSuccess("Budgets replaced for " + result.Matters + " matter(s): " + result.FeeLines + " fee line(s) and "
                + result.ExpenseLines + " expense line(s) imported, " + result.DeletedLines + " old line(s) removed.");
            SummaryText.Text = "Import complete. Choose another file, or click Check again to re-check this one.";
        }
    }
}
