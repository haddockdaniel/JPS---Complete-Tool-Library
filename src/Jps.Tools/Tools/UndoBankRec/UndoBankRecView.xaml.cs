using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.UndoBankRec
{
    public sealed class UndoBankRecTool : IJurisTool
    {
        public string Id => "JPS.UNDOBANKREC";
        public FrameworkElement CreateView(IToolContext context) => new UndoBankRecView(context);
    }

    /// <summary>
    /// Sets a bank account's most recent recorded reconciliation back to "in progress" so it can be corrected and recorded
    /// again in Juris, discarding any newer in-progress reconciliation first. Ported from "JPS - Undo Bank Rec v1.0" with these
    /// fixes: everything runs in one transaction (a failure no longer leaves a half-undone rec); the history row is matched on
    /// its primary key with an exact DateTime and must update exactly one row (text-date matching could miss it while the other
    /// updates still ran); the bank account goes back to the PREVIOUS reconciliation's statement date and ending balance (or is
    /// cleared when there is none) instead of the undone rec's values; a 0.00 balance no longer produces broken SQL; the undo is
    /// always confirmed, showing exactly what will change, and refused if the rec changed since it was shown; un-cleared items
    /// have CkRegReconDate cleared; a note is shown when adjustment entries were posted during the rec (they stay posted and are
    /// ticked again with the other items); the undo is refused when the register no longer holds every item the rec cleared
    /// (BRHBookDepositCount + BRHBookCheckCount, e.g. after a purge); the screen reloads afterwards and a UtilityLog line is written.
    /// </summary>
    public partial class UndoBankRecView : UserControl
    {
        private readonly IToolContext _context;
        private readonly UndoBankRecService _service;
        private BankRecState _state;
        private bool _loaded;

        private JurisSession Session => _context.Session;

        public UndoBankRecView(IToolContext context)
        {
            _context = context;
            _service = new UndoBankRecService(context.Session);
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                if (_loaded) return;
                _loaded = true;
                var banks = await Frame.RunAsync("Loading bank accounts…", () =>
                    Lookup.Load(Session, "select BnkCode, BnkCode as Code, BnkDesc from BankAccount order by BnkCode"));
                if (banks == null) return;
                BankBox.ItemsSource = banks;
                if (banks.Count == 0)
                    Frame.ShowWarning("Juris has no bank accounts set up.");
                else
                    BankBox.SelectedIndex = 0;
            };
        }

        private LookupItem SelectedBank => BankBox.SelectedItem as LookupItem;

        private async void OnBankChanged(object sender, SelectionChangedEventArgs e) => await LoadStateAsync();

        private async Task LoadStateAsync()
        {
            Show(null);
            var bank = SelectedBank?.Code;
            if (bank == null) return;
            var state = await Frame.RunAsync("Reading reconciliations for " + bank + "…", () => _service.Load(bank));
            if (state == null || SelectedBank?.Code != bank) return;
            Show(state);
            if (state.Latest == null)
                Frame.ShowWarning("There are no recorded reconciliations for bank " + bank + ", so there's nothing to undo.");
        }

        private static string Money(decimal value) => value.ToString("#,##0.00");
        private static string Date(DateTime value) => value.ToString("MM/dd/yyyy");

        private void Show(BankRecState state)
        {
            _state = state?.Latest == null ? null : state;
            UndoButton.IsEnabled = _state != null;
            Details.Visibility = _state != null ? Visibility.Visible : Visibility.Collapsed;
            NoneText.Visibility = _state == null ? Visibility.Visible : Visibility.Collapsed;
            NoneText.Text = state == null ? "Pick a bank account to see its last reconciliation." : "Nothing to undo for this bank account.";
            SetWarning(ItemsWarning, null);
            SetWarning(AdjustmentWarning, null);
            SetWarning(MismatchWarning, null);
            if (_state == null) return;
            if (_state.ItemCountProblem != null) UndoButton.IsEnabled = false;
            SetWarning(ItemsWarning, _state.ItemCountProblem);

            var rec = _state.Latest;
            StmtDateText.Text = Date(rec.StmtDate);
            RecordedText.Text = rec.RecordedDate.HasValue && rec.RecordedDate.Value.Year >= 1900 ? Date(rec.RecordedDate.Value) : "(no date)";
            OpenBalText.Text = Money(rec.OpenBal);
            EndBalText.Text = Money(rec.EndBal);
            ItemsText.Text = _state.ItemsToUnclear + " cleared item(s) go back to ticked, ready to record again.";
            AfterText.Text = AfterDescription(_state);
            InProgressText.Text = InProgressDescription(_state) ?? "None";
            SetWarning(AdjustmentWarning, AdjustmentDescription(_state));
            SetWarning(MismatchWarning, _state.PriorMismatch == null ? null
                : _state.PriorMismatch + " The tool will set the bank's last reconciliation to " + AfterDescription(_state) + ".");
        }

        private static void SetWarning(Wpf.Ui.Controls.InfoBar bar, string message)
        {
            bar.Message = message ?? "";
            bar.IsOpen = message != null;
            bar.Visibility = message != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string AfterDescription(BankRecState s) =>
            s.Previous == null
                ? "none (cleared, as there is no earlier recorded reconciliation)"
                : "statement " + Date(s.Previous.StmtDate) + ", ending balance " + Money(s.Previous.EndBal);

        private static string InProgressDescription(BankRecState s)
        {
            if (s.InProgress.Count == 0) return null;
            var dates = string.Join(", ", s.InProgress.ConvertAll(r => Date(r.StmtDate)));
            return "Statement " + dates + " with " + s.TickedInProgress + " ticked item(s). It will be DELETED and its items un-ticked.";
        }

        private static string AdjustmentDescription(BankRecState s)
        {
            // The check-register JE batch's entries are posted GL entries with matching CheckRegister rows (type J). Those rows are
            // ordinary register items: un-recording ticks them again with the rest and recording again clears them, so they must
            // NOT be reversed as part of an undo. Juris often creates the batch empty; only mention it when it has entries.
            var text = new StringBuilder();
            if (s.Latest.AdjustmentBatch != 0 && s.Latest.AdjustmentEntries > 0)
                text.Append("Adjustment entries were posted while this reconciliation was done (journal entry batch " + s.Latest.AdjustmentBatch + ", "
                            + s.Latest.AdjustmentEntries + " entries). They stay posted and stay in the check register, ticked with the other items, "
                            + "so recording again includes them. Reverse them in Juris only if an entry itself was wrong. ");
            foreach (var r in s.InProgress)
                if (r.AdjustmentBatch != 0 && r.AdjustmentEntries > 0)
                    text.Append("Adjustments posted during the in-progress reconciliation being deleted (journal entry batch " + r.AdjustmentBatch
                                + ") also stay posted; their items go back to not cleared. ");
            return text.Length == 0 ? null : text.ToString().Trim();
        }

        private async void OnUndo(object sender, RoutedEventArgs e)
        {
            var state = _state;
            var bankItem = SelectedBank;
            if (state == null || bankItem == null || bankItem.Code != state.Bank || state.ItemCountProblem != null) return;
            var bank = state.Bank;
            var rec = state.Latest;

            if (state.PriorMismatch != null && !await Dialogs.ConfirmAsync("Prior statement doesn't match",
                    state.PriorMismatch + "\r\n\r\nThe tool will set the bank's last reconciliation to " + AfterDescription(state)
                    + ". Continue?", "Continue"))
                return;

            var message = new StringBuilder();
            message.AppendLine("Bank: " + bankItem.Display);
            message.AppendLine("Statement date: " + Date(rec.StmtDate));
            message.AppendLine("Ending balance: " + Money(rec.EndBal));
            message.AppendLine("Items to un-clear: " + state.ItemsToUnclear + " (they go back to ticked, ready to record again)");
            message.AppendLine();
            message.AppendLine("The reconciliation will be set to 'unrecorded'. The bank's last reconciliation becomes " + AfterDescription(state) + ".");
            var inProgress = InProgressDescription(state);
            if (inProgress != null)
                message.AppendLine().AppendLine("Reconciliation in progress: " + inProgress);
            var adjustments = AdjustmentDescription(state);
            if (adjustments != null)
                message.AppendLine().AppendLine("Note: " + adjustments);

            if (!await Dialogs.ConfirmAsync("Undo the " + Date(rec.StmtDate) + " reconciliation for bank " + bank + "?",
                    message.ToString().TrimEnd(), "Undo reconciliation"))
                return;

            var ok = await Frame.RunAsync("Undoing the reconciliation…", () => _service.Undo(state));
            if (!ok) return; // error already shown; nothing was changed

            await LoadStateAsync();
            Frame.ShowSuccess("The " + Date(rec.StmtDate) + " reconciliation for bank " + bank + " is unrecorded again and its "
                              + state.ItemsToUnclear + " item(s) are ticked. Open it in Juris to correct and record it.");
        }
    }
}
