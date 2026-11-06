using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;
using Wpf.Ui.Controls;

namespace Jps.Tools.Tools.TrustTransfer
{
    public sealed class TrustTransfer : IJurisTool
    {
        public string Id => "JPS.TRUSTXFER";
        public FrameworkElement CreateView(IToolContext context) => new TrustTransferView(context);
    }

    /// <summary>
    /// Moves every matter's trust balance from one trust bank to another by creating ONE unposted Trust Adjustment batch
    /// (a -balance line in the FROM bank and a +balance line in the TO bank per matter), adding the matters' TO-bank
    /// TrustAccount rows and a batch-tree entry; the user posts the batch in Juris. Ported from "JPS - Trust Balance
    /// Transfer Utility" with these fixes: everything now runs in one transaction (the original had none, and on a failed
    /// header insert it deleted an existing, possibly posted, batch); the batch number comes from Juris' LastBatchTrust
    /// counter, locked and advanced in the same transaction, never lowered; the period lookup no longer crashes when the
    /// firm's periods don't end on the calendar month end; banks without GL mappings no longer crash the checks; prebill
    /// checks cover fee, expense and matter trust-applied rows; locked voucher batches count as unposted; amounts are typed
    /// parameters (no culture-formatted SQL); and a batch-tree failure rolls the batch back instead of leaving it invisible.
    /// </summary>
    public partial class TrustTransferView : UserControl
    {
        private sealed class CheckLine
        {
            public bool Passed { get; set; }
            public string Text { get; set; }
            public SymbolRegular Icon => Passed ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24;
            public Brush Brush => Passed ? Brushes.SeaGreen : Brushes.IndianRed;
        }

        private readonly IToolContext _context;
        private readonly TrustTransferService _service;
        private TransferReview _review;      // last review that passed every check (what the action uses)
        private TransferReview _lastReview;  // last review, passed or not (for the issues / negatives lists)

        public TrustTransferView(IToolContext context)
        {
            _context = context;
            _service = new TrustTransferService(context.Session);
            InitializeComponent();
            Loaded += async (_, __) => await LoadBanksAsync();
        }

        private JurisSession Session => _context.Session;

        private async System.Threading.Tasks.Task LoadBanksAsync()
        {
            var banks = await Frame.RunAsync("Loading trust banks…", () => new
            {
                From = Lookup.Load(Session,
                    @"select distinct BnkCode, BnkCode as Code, BnkDesc from TrustLedger inner join BankAccount on TLBank = BnkCode
                      where BnkAcctType = 'T' order by BnkCode"),
                To = Lookup.Load(Session, "select BnkCode, BnkCode as Code, BnkDesc from BankAccount where BnkAcctType = 'T' order by BnkCode"),
            });
            if (banks == null) return;
            FromBox.ItemsSource = banks.From;
            ToBox.ItemsSource = banks.To;

            if (banks.From.Count == 0)
            {
                CheckButton.IsEnabled = false;
                Frame.ShowWarning("No trust bank has any ledger history, so there are no balances to move.");
                return;
            }
            if (banks.To.Count < 2)
            {
                CheckButton.IsEnabled = false;
                Frame.ShowWarning("Juris needs at least two trust bank accounts to move balances between. Set up the new trust bank in Juris first.");
                return;
            }
            FromBox.SelectedIndex = 0; // TO is left blank on purpose: the user picks the destination deliberately.
        }

        private string FromBank => (FromBox.SelectedItem as LookupItem)?.Code;
        private string ToBank => (ToBox.SelectedItem as LookupItem)?.Code;

        private void OnBankChanged(object sender, SelectionChangedEventArgs e) => ResetReview();

        private void ResetReview()
        {
            _review = null;
            _lastReview = null;
            Results.ItemsSource = null;
            TransferButton.IsEnabled = false;
            IssuesButton.Visibility = Visibility.Collapsed;
            NegativesButton.Visibility = Visibility.Collapsed;
            Frame.ClearMessage();
        }

        private async void OnCheck(object sender, RoutedEventArgs e)
        {
            var from = FromBank;
            var to = ToBank;
            ResetReview();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                Frame.ShowWarning("Pick both the bank to move balances FROM and the bank to move them TO.");
                return;
            }
            if (from == to)
            {
                Frame.ShowWarning("The FROM and TO banks must be different.");
                return;
            }

            var review = await Frame.RunAsync("Checking bank " + from + "…", () => _service.Review(from, to));
            if (review == null) return;
            if (from != FromBank || to != ToBank) return; // selection changed while checking

            _lastReview = review;
            Results.ItemsSource = review.Checks.Select(c => new CheckLine { Passed = c.Passed, Text = c.Text }).ToList();
            IssuesButton.Visibility = review.Issues != null && review.Issues.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NegativesButton.Visibility = review.NegativeMatters != null && review.NegativeMatters.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!review.CanTransfer)
            {
                Frame.ShowWarning("Balances in bank " + from + " can't be moved yet. Fix the items marked with a cross, then run the checks again.");
                return;
            }
            _review = review;
            TransferButton.IsEnabled = true;
            Frame.ShowInfo(review.MatterCount + " matter(s) holding " + Money(review.Total) + " are ready to move from bank " + from + " to bank " + to + ".");
        }

        private void OnShowIssues(object sender, RoutedEventArgs e)
        {
            if (_lastReview?.Issues != null)
                ReportWindow.Show(this, "Bank " + _lastReview.FromBank + " issues to resolve", _lastReview.Issues,
                    "These items still use the FROM bank. Post or remove them in Juris, then run the checks again.");
        }

        private void OnShowNegatives(object sender, RoutedEventArgs e)
        {
            if (_lastReview?.NegativeMatters != null)
                ReportWindow.Show(this, "Matters with negative trust balances", _lastReview.NegativeMatters,
                    "Fix these balances in bank " + _lastReview.FromBank + " in Juris before moving the bank.");
        }

        private async void OnTransfer(object sender, RoutedEventArgs e)
        {
            var review = _review;
            if (review == null || review.FromBank != FromBank || review.ToBank != ToBank) return;

            var subtitle = "One unposted Trust Adjustment batch will move " + Money(review.Total) + " for " + review.MatterCount
                + " matter(s) from bank " + review.FromBank + " to bank " + review.ToBank + ", dated today in period "
                + review.PeriodYear + "-" + review.PeriodNumber.ToString("00") + ". Nothing is posted until you post the batch in Juris.";
            if (!ReportWindow.Confirm(this, "Matters to move", review.Matters, subtitle, "Create batch"))
                return;

            TransferButton.IsEnabled = false;
            var result = await Frame.RunAsync("Creating the trust adjustment batch…", () => _service.CreateBatch(review));
            _review = null;
            if (result == null)
            {
                Frame.ShowError("There was a problem creating the trust batch. No changes were made to Juris. "
                                + (Frame.HasMessage ? Frame.Message : ""));
                return;
            }
            Results.ItemsSource = null;
            Frame.ShowSuccess("Created unposted Trust Adjustment batch " + result.BatchNumber + " (" + result.LineCount + " lines, "
                + result.MatterCount + " matters, " + Money(result.Total) + "). Post batch " + result.BatchNumber
                + " in Juris to move the balances in the matter ledgers and the GL.");
        }

        private static string Money(decimal amount) => amount.ToString("C", CultureInfo.CurrentCulture);
    }
}
