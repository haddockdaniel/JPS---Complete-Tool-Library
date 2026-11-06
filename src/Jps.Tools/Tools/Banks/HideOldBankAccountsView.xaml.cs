using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;
using Wpf.Ui.Controls;

namespace Jps.Tools.Tools.Banks
{
    public sealed class HideOldBankAccounts : IJurisTool
    {
        public string Id => "JPS.HIDEOLDBANK";
        public FrameworkElement CreateView(IToolContext context) => new HideOldBankAccountsView(context);
    }

    /// <summary>
    /// Removes a bank account from the Juris Tables view (DocumentTree) once it's provably dormant.
    /// Ported from "JPS - Hide Old Bank Accounts v10" with these fixes: a bank with no GL mapping could never be hidden;
    /// an office-linked bank could slip through when it had several GL rows; OfficeCode.OfcBankCode (a real FK) was never
    /// checked; only CheckRegister counted as history (trust activity was ignored); "complete" was shown even when nothing
    /// was hidden.
    /// </summary>
    public partial class HideOldBankAccountsView : UserControl
    {
        private sealed class CheckResult
        {
            public bool Passed { get; set; }
            public string Text { get; set; }
            public SymbolRegular Icon => Passed ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24;
            public Brush Brush => Passed ? Brushes.SeaGreen : Brushes.IndianRed;
        }

        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;
        private string _checkedBank;

        public HideOldBankAccountsView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                var banks = await Frame.RunAsync("Loading bank accounts…", () =>
                    Lookup.Load(Session, "select BnkCode, BnkCode as Code, BnkDesc from BankAccount order by BnkCode"));
                if (banks == null) return;
                BankBox.ItemsSource = banks;
                if (banks.Count == 0)
                    Frame.ShowWarning("Juris has no bank accounts set up, so there's nothing to hide.");
                else
                    BankBox.SelectedIndex = 0;
            };
        }

        private string SelectedBank => (BankBox.SelectedItem as LookupItem)?.Code;

        private void OnBankChanged(object sender, SelectionChangedEventArgs e)
        {
            Results.ItemsSource = null;
            _checkedBank = null;
            HideButton.IsEnabled = false;
            Frame.ClearMessage();
        }

        private async void OnCheck(object sender, RoutedEventArgs e)
        {
            var bank = SelectedBank;
            if (bank == null)
            {
                Frame.ShowWarning("Pick a bank account first.");
                return;
            }
            var results = await Frame.RunAsync("Checking " + bank + "…", () => RunChecks(bank));
            if (results == null) return;
            Results.ItemsSource = results;
            var ok = results.All(r => r.Passed);
            _checkedBank = ok ? bank : null;
            HideButton.IsEnabled = ok;
            if (!ok)
                Frame.ShowWarning("Bank account " + bank + " can't be hidden yet. Fix the items marked with a cross, then run the checks again.");
        }

        private List<CheckResult> RunChecks(string bank)
        {
            var results = new List<CheckResult>();

            var glOffices = Session.Query(
                @"select distinct OGAOffice from BkAcctGLAcct inner join OfficeGLAccount on OGAAcct = BGAAcct
                  where BGABkAcct = @bnk and BGAType = 'A'", Sql.P("@bnk", bank)).Rows().Select(r => Sql.Text(r, "OGAOffice")).ToList();
            var ownOffices = Session.Query("select OfcOfficeCode from OfficeCode where OfcBankCode = @bnk", Sql.P("@bnk", bank))
                .Rows().Select(r => Sql.Text(r, "OfcOfficeCode")).ToList();
            var offices = glOffices.Concat(ownOffices).Distinct().ToList();
            results.Add(new CheckResult
            {
                Passed = offices.Count == 0,
                Text = offices.Count == 0 ? "Not assigned to any office." : "Assigned to office " + string.Join(", ", offices) + ". Remove it from the office first.",
            });

            var last = Session.Scalar(
                @"select max(DT) from (
                      select max(CkRegDate) as DT from CheckRegister where CkRegBank = @bnk
                      union all select max(TLDate) from TrustLedger where TLBank = @bnk
                      union all select max(CBCCheckDate) from CBCheck where CBCBank = @bnk
                      union all select max(BRHStmtDate) from BankReconHistory where BRHBank = @bnk) h", Sql.P("@bnk", bank));
            var lastDate = last == null ? (DateTime?)null : Convert.ToDateTime(last);
            var quiet = lastDate == null || lastDate.Value < DateTime.Today.AddMonths(-9);
            results.Add(new CheckResult
            {
                Passed = quiet,
                Text = lastDate == null ? "No activity on record."
                    : (quiet ? "No activity in the last 9 months" : "Activity in the last 9 months") + " (last: " + lastDate.Value.ToString("MM/dd/yyyy") + ").",
            });

            // Count matters with a non-zero balance (a plain sum could net offsetting balances to zero).
            var trustRows = Convert.ToInt32(Session.Scalar("select count(*) from TrustAccount where TABank = @bnk and TABalance <> 0", Sql.P("@bnk", bank)) ?? 0);
            var trust = Convert.ToDecimal(Session.Scalar("select isnull(sum(TABalance),0) from TrustAccount where TABank = @bnk", Sql.P("@bnk", bank)) ?? 0m);
            results.Add(new CheckResult
            {
                Passed = trustRows == 0,
                Text = trustRows == 0 ? "Holds no trust money." : trustRows + " matter trust account(s) still have a balance (net " + trust.ToString("C") + ").",
            });

            // Work still in progress that names this bank. Batch lines only count while their batch is unposted
            // (not P = posted or D = deleted); posted lines are history and are covered by the activity check above.
            var pending = Session.Query(
                @"select 'open voucher(s)' as What, count(*) as N from Voucher where VchBank = @bnk and VchStatus = 'O'
                  union all select 'unposted voucher batch line(s)', count(*) from VoucherBatchDetail d inner join VoucherBatch b on b.VBBatchNbr = d.VBDBatch
                      where d.VBDBank = @bnk and b.VBStatus not in ('P','D')
                  union all select 'unposted check(s)', count(*) from CBCheck c inner join CheckBatch b on b.CBBatchNbr = c.CBCBatch
                      where c.CBCBank = @bnk and b.CBStatus not in ('P','D')
                  union all select 'unposted trust adjustment line(s)', count(*) from TrAdjBatchDetail d inner join TrAdjBatch b on b.TABBatchNbr = d.TABDBatch
                      where d.TABDBank = @bnk and b.TABStatus not in ('P','D')
                  union all select 'unposted cash receipt trust line(s)', count(*) from CRTrustAlloc a inner join CashReceiptsBatch b on b.CRBBatchNbr = a.CRTBatch
                      where a.CRTBank = @bnk and b.CRBStatus not in ('P','D')
                  union all select 'unposted cash receipt non-client line(s)', count(*) from CRNonCliAlloc a inner join CashReceiptsBatch b on b.CRBBatchNbr = a.CRNBatch
                      where a.CRNBankCode = @bnk and b.CRBStatus not in ('P','D')
                  union all select 'unposted cash receipt A/R line(s)', count(*) from CRARAlloc a inner join CashReceiptsBatch b on b.CRBBatchNbr = a.CRABatch
                      where a.CRABank = @bnk and b.CRBStatus not in ('P','D')
                  union all select 'prebill trust application(s)',
                      (select count(*) from PrebillFeeTrustApplied where PBFTABank = @bnk)
                    + (select count(*) from PrebillExpenseTrustApplied where PBETABank = @bnk)
                    + (select count(*) from PrebillMatterTrustApplied where PBMTABank = @bnk)
                  union all select 'voucher template(s)', count(*) from VchTemplate where VTVchBank = @bnk", Sql.P("@bnk", bank))
                .Rows().Where(r => Convert.ToInt32(r["N"]) > 0).Select(r => Convert.ToInt32(r["N"]) + " " + Sql.Text(r, "What")).ToList();
            results.Add(new CheckResult
            {
                Passed = pending.Count == 0,
                Text = pending.Count == 0 ? "No open vouchers, unposted batch lines, prebill trust applications or voucher templates use it."
                    : "Still used by " + string.Join(", ", pending) + ".",
            });
            return results;
        }

        private async void OnHide(object sender, RoutedEventArgs e)
        {
            var bank = _checkedBank;
            if (bank == null || bank != SelectedBank) return;
            if (!await Dialogs.ConfirmAsync("Hide bank account " + bank + "?",
                    "It will no longer appear in the Tables view in Juris. Its history is kept. This can't be undone from here.", "Hide it"))
                return;

            var rows = await Frame.RunAsync("Hiding " + bank + "…", () => Session.InTransaction(() =>
            {
                // Lock the bank row so nobody can start using it between the re-check and the delete, then repeat every
                // check inside this transaction: the screen's results may be stale.
                if (Session.Scalar("select BnkCode from BankAccount with (updlock, holdlock) where BnkCode = @bnk", Sql.P("@bnk", bank)) == null)
                    throw new InvalidOperationException("Bank account " + bank + " no longer exists. Nothing was changed.");
                var failed = RunChecks(bank).Where(r => !r.Passed).Select(r => r.Text).ToList();
                if (failed.Count > 0)
                    throw new InvalidOperationException("Bank account " + bank + " changed since it was checked: " + string.Join(" ", failed) + " Nothing was changed.");

                // Only the bank's own leaf: an 'R' row with nothing filed under it (FK1_DocumentTree), never the folder.
                var n = Session.ExecuteRows(
                    @"delete d from DocumentTree d where d.DTDocClass = 6400 and d.DTDocType = 'R' and d.DTParentID = 10 and d.DTKeyT = @bnk
                        and not exists (select 1 from DocumentTree ch where ch.DTParentID = d.DTDocID)", Sql.P("@bnk", bank));
                if (n > 0) Session.WriteUtilityLog("JPS Hide Old Bank Accounts: hid bank account " + bank + " from the Tables view.");
                return n;
            }));
            if (Frame.HasMessage) return; // error already shown
            HideButton.IsEnabled = false;
            _checkedBank = null;
            if (rows == 0)
                Frame.ShowWarning("Bank account " + bank + " wasn't in the Tables view (it may already be hidden). Nothing was changed.");
            else
                Frame.ShowSuccess("Bank account " + bank + " is hidden from the Tables view.");
        }
    }
}
