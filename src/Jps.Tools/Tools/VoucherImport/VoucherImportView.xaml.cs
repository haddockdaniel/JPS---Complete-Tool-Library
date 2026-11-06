using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;
using Wpf.Ui.Controls;

namespace Jps.Tools.Tools.VoucherImport
{
    public sealed class VoucherImport : IJurisTool
    {
        public string Id => "JPS.VCHIMPORT";
        public FrameworkElement CreateView(IToolContext context) => new VoucherImportView(context);
    }

    /// <summary>
    /// Imports a .csv/.xlsx/.xls file of AP (A) and trust (T) vouchers, with their GL (G) and matter expense (E) lines,
    /// into ONE new unposted Payment Voucher batch: the file is parsed in memory, every voucher is checked completely
    /// (all problems listed with their row), the user approves a pre-report of the vouchers to create, and the batch,
    /// vouchers, distributions, Juris counters and batch-tree entry are written in one transaction. Ported from
    /// "JPS - Voucher Import Utility" with these fixes: no shared ## temp tables and no SQL built from file text; G/E lines
    /// can no longer attach to the wrong voucher; the automatic expense GL line is the expense total (not expenses − GL);
    /// unbalanced vouchers and vouchers with no distribution are rejected; a missing vendor default distribution account or
    /// STDR rate no longer silently drops lines; one transaction (no half batches); batch and voucher numbers are claimed
    /// from LastBatchVoucher/LastSysNbrVoucher under lock and the counters never move backwards; invoice/PO numbers over
    /// 20 characters are rejected; duplicates are also checked against unposted batches and suffixed numbers re-checked;
    /// one bad trust voucher no longer rejects all later ones, and several file vouchers can't overdraw a trust account;
    /// no dummy "ZZZ" task code is created; the batch user and IncurredBy are looked up by EmpID; only the new batch is
    /// added to the batch tree; CSV quoted commas are kept; the leftover debug popup is gone; and Create is disabled after
    /// a run so a file can't be imported twice by a second click.
    /// </summary>
    public partial class VoucherImportView : UserControl
    {
        private sealed class ResultLine
        {
            public SymbolRegular Icon { get; set; }
            public Brush Brush { get; set; }
            public string Text { get; set; }
        }

        private readonly IToolContext _context;
        private readonly VoucherImportService _service;
        private string _path;          // chosen import file
        private ImportCheck _check;    // last completed check of _path
        private string _moveFolder;

        public VoucherImportView(IToolContext context)
        {
            _context = context;
            _service = new VoucherImportService(context.Session);
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                if (UserBox.ItemsSource != null) return;
                var users = await Frame.RunAsync("Loading Juris users…", () =>
                    Lookup.Load(Session, "select EmpSysNbr, EmpID, EmpName from Employee where EmpValidAsUser = 'Y' order by EmpName"));
                if (users == null) return;
                UserBox.ItemsSource = users;
                UserBox.SelectedItem = users.FirstOrDefault(u => string.Equals(u.Code, "SMGR", StringComparison.OrdinalIgnoreCase));
                if (users.Count == 0) Frame.ShowWarning("Juris has no employees set up as users, so no batch owner can be chosen.");
            };
        }

        private JurisSession Session => _context.Session;
        private string SelectedEmpId => (UserBox.SelectedItem as LookupItem)?.Code;
        private DuplicateMode Mode => IncrementRadio.IsChecked == true ? DuplicateMode.Increment : DuplicateMode.Reject;
        private string Suffix => (SuffixBox.Text ?? "").Trim();

        // ------------------------------------------------------------------ inputs

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateCreateButton();

        private void OnDuplicateOptionChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_check != null && !SettingsMatch(_check))
            {
                ClearCheck();
                Results.ItemsSource = new[] { Line(false, "The duplicate invoice setting changed. Click \"Check again\" to re-check the file.") };
            }
            UpdateCreateButton();
        }

        private void OnAfterOptionChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            MovePanel.Visibility = MoveRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateCreateButton();
        }

        private void OnChooseFolder(object sender, RoutedEventArgs e)
        {
            var folder = FolderPicker.Pick(Window.GetWindow(this), "Folder to move the import file to after the import",
                _moveFolder ?? (_path == null ? null : Path.GetDirectoryName(_path)));
            if (folder == null) return;
            _moveFolder = folder;
            FolderText.Text = "Move the file to " + folder;
            UpdateCreateButton();
        }

        private void UpdateCreateButton()
        {
            if (CreateButton == null) return;
            CreateButton.IsEnabled = _check != null && _check.Accepted.Count > 0 && SelectedEmpId != null
                                     && !string.IsNullOrWhiteSpace(TitleBox.Text)
                                     && (MoveRadio.IsChecked != true || _moveFolder != null)
                                     && SettingsMatch(_check);
        }

        private bool SettingsMatch(ImportCheck check) => check.Mode == Mode && (Mode == DuplicateMode.Reject || check.Suffix == Suffix);

        private void ClearCheck()
        {
            _check = null;
            Results.ItemsSource = null;
            ProblemsButton.Visibility = Visibility.Collapsed;
            LinesButton.Visibility = Visibility.Collapsed;
            CreateButton.IsEnabled = false;
        }

        // ------------------------------------------------------------------ check

        private async void OnChooseFile(object sender, RoutedEventArgs e)
        {
            var path = Dialogs.PickImportFile(VoucherImportParser.FileFilter);
            if (path == null) return;
            _path = path;
            FileText.Text = path;
            RecheckButton.IsEnabled = true;
            await RunCheckAsync();
        }

        private async void OnRecheck(object sender, RoutedEventArgs e) => await RunCheckAsync();

        private async System.Threading.Tasks.Task RunCheckAsync()
        {
            ClearCheck();
            Frame.ClearMessage();
            var path = _path;
            var mode = Mode;
            var suffix = Suffix;
            if (path == null) return;
            if (!File.Exists(path))
            {
                Frame.ShowWarning("The file " + path + " no longer exists. Choose the file again.");
                return;
            }
            if (mode == DuplicateMode.Increment && (suffix.Length == 0 || suffix.IndexOfAny(new[] { ' ', '\'', '"', '%' }) >= 0))
            {
                Frame.ShowWarning("Enter the text to add to duplicate invoice numbers (letters or digits, no spaces).");
                return;
            }

            var check = await Frame.RunAsync("Checking " + Path.GetFileName(path) + "…", () => _service.Check(path, mode, suffix));
            if (check == null) return;
            if (path != _path) return; // another file was chosen meanwhile

            _check = check;
            var accepted = check.Accepted;
            var lines = new List<ResultLine>();
            lines.Add(Line(accepted.Count > 0, accepted.Count == 0
                ? "No voucher in the file can be imported."
                : accepted.Count + " of " + check.Vouchers.Count + " voucher(s) are ready to import, totalling "
                  + accepted.Sum(v => v.InvoiceAmt).ToString("N2", CultureInfo.CurrentCulture) + "."));
            if (check.RejectedCount > 0)
                lines.Add(Line(false, check.RejectedCount + " voucher(s) have problems and will NOT be imported. See the Problems list (each problem shows its row)."));
            if (check.SkippedRowCount > 0)
                lines.Add(Line(false, check.SkippedRowCount + " row(s) were skipped (see the Problems list)."));
            var renamed = accepted.Count(v => v.Notes.Count > 0);
            if (renamed > 0)
                lines.Add(new ResultLine { Icon = SymbolRegular.Info24, Brush = Brushes.SteelBlue, Text = renamed + " invoice number(s) will be changed; see the Note column in the voucher list." });
            Results.ItemsSource = lines;

            ProblemsButton.Visibility = check.Problems.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LinesButton.Visibility = accepted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCreateButton();

            if (check.Problems.Rows.Count > 0)
            {
                Frame.ShowWarning(accepted.Count == 0
                    ? "None of the vouchers can be imported. Fix the rows in the Problems list, save the file and click Check again."
                    : "Some rows have problems. You can import the " + accepted.Count + " valid voucher(s) now, or fix the file and click Check again.");
                ShowProblems();
            }
            else
                Frame.ShowInfo("All invoices are valid. Click \"Create voucher batch\" to review them and create the batch.");
            if (SelectedEmpId == null)
                Frame.ShowWarning("Choose the batch owner (a Juris user) in step 1.");
        }

        private void OnShowProblems(object sender, RoutedEventArgs e) => ShowProblems();

        private void ShowProblems()
        {
            if (_check == null) return;
            ReportWindow.Show(this, "Voucher import problems", _check.Problems,
                "A voucher with any problem is left out whole. Row = row in the file (row 1 is the header row"
                + (Path.GetExtension(_check.FilePath).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ")." : "; blank rows in a workbook are not counted)."));
        }

        private void OnShowLines(object sender, RoutedEventArgs e)
        {
            if (_check == null) return;
            ReportWindow.Show(this, "Voucher distribution lines", VoucherImportService.LinesReport(_check.Accepted),
                "GL lines and expense lines of the vouchers that will be imported. When a voucher has expense lines, a GL line for their total is added automatically on the vendor's default distribution account.");
        }

        // ------------------------------------------------------------------ create

        private async void OnCreate(object sender, RoutedEventArgs e)
        {
            var check = _check;
            var empId = SelectedEmpId;
            var title = TitleBox.Text ?? "";
            var rename = MoveRadio.IsChecked != true;
            var folder = _moveFolder;
            if (check == null || empId == null || string.IsNullOrWhiteSpace(title)) return;
            if (!rename && (folder == null || !Directory.Exists(folder)))
            {
                Frame.ShowWarning("Choose the folder to move the import file to (step 3).");
                return;
            }
            var accepted = check.Accepted;

            var subtitle = "One unposted Payment Voucher batch owned by " + empId + " will be created with these " + accepted.Count
                + " voucher(s), totalling " + accepted.Sum(v => v.InvoiceAmt).ToString("N2", CultureInfo.CurrentCulture) + "."
                + (check.RejectedCount > 0 ? " " + check.RejectedCount + " voucher(s) with problems are NOT included." : "")
                + " Nothing is posted until you post the batch in Juris.";
            if (!ReportWindow.Confirm(this, "Vouchers to import", VoucherImportService.VoucherReport(accepted), subtitle, "Create batch"))
                return;

            CreateButton.IsEnabled = false;
            var result = await Frame.RunAsync("Creating the voucher batch…", () => _service.CreateBatch(check, empId, title));
            if (result == null)
            {
                Frame.ShowError("The import didn't finish, so nothing was saved and no batch was created. " + (Frame.HasMessage ? Frame.Message : ""));
                UpdateCreateButton();
                return;
            }

            // The batch is committed: whatever happens to the file now is only a warning.
            string fileNote;
            try
            {
                var moved = DisposeOfFile(check.FilePath, rename, folder);
                fileNote = " The import file was " + (rename ? "renamed to " : "moved to ") + moved + ".";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                fileNote = null;
                Frame.ShowWarning("Voucher import complete. Go to Juris and post Payment Voucher batch " + result.BatchNumber
                    + ". The import file couldn't be " + (rename ? "renamed" : "moved") + " (" + ex.Message + "), so rename or move it yourself so it isn't imported again.");
            }

            _path = null;
            FileText.Text = "";
            RecheckButton.IsEnabled = false;
            ClearCheck();
            if (fileNote != null)
                Frame.ShowSuccess("Voucher import complete. Go to Juris and post Payment Voucher batch " + result.BatchNumber + " ("
                    + result.VoucherCount + " voucher(s), numbers " + result.FirstVoucherNumber + "–" + result.LastVoucherNumber + ", totalling "
                    + result.Total.ToString("N2", CultureInfo.CurrentCulture) + ")." + fileNote);
        }

        /// <summary>Renames the file to .OLD or moves it to <paramref name="folder"/>; a name already taken gets -yyyyMMddHHmmss. Returns the new path.</summary>
        private static string DisposeOfFile(string path, bool rename, string folder)
        {
            var target = rename ? path + ".OLD" : Path.Combine(folder, Path.GetFileName(path));
            if (File.Exists(target))
                target = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + "-"
                    + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(target));
            File.Move(path, target);
            return target;
        }

        private static ResultLine Line(bool ok, string text) => new ResultLine
        {
            Icon = ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24,
            Brush = ok ? Brushes.SeaGreen : Brushes.IndianRed,
            Text = text,
        };
    }
}
