using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.TimekeeperReassignment
{
    public sealed class TimekeeperReassignmentTool : IJurisTool
    {
        public string Id => "JPS.TKPRNOTECARD";
        public FrameworkElement CreateView(IToolContext context) => new TimekeeperReassignmentView(context);
    }

    /// <summary>
    /// Moves billing, originating and/or responsible timekeeper assignments on clients and/or matters from one timekeeper
    /// to another, optionally adding a note card to each changed client/matter, with a pre-report (approved before the
    /// change) and a post-report. Ported from "JPS - Timekeeper Reassignment v10" with these fixes: the whole run (all
    /// steps, note cards and log) is one transaction and each percentage table is merged in one MERGE statement, so
    /// originating/responsible totals stay at 100% (the original deleted and re-added rows separately, without a
    /// transaction); choosing the same timekeeper twice is blocked (it used to wipe assignments); an unknown timekeeper
    /// stops the run instead of continuing with 0; the From list now includes responsible-only timekeepers; note-card
    /// indexes use the highest existing number + 1 instead of a count that collided after deletions; the Responsible +
    /// Matter count SQL, the Client&amp;Matter + Responsible post-report, the empty "in ()" post-report filter and the
    /// responsible reports showing system numbers instead of timekeeper IDs are all fixed; nothing is built from text.
    /// </summary>
    public partial class TimekeeperReassignmentView : UserControl
    {
        private readonly IToolContext _context;
        private readonly TimekeeperReassignmentService _service;
        private DataTable _lastPostReport;
        private string _lastPostTitle;

        public TimekeeperReassignmentView(IToolContext context)
        {
            _context = context;
            _service = new TimekeeperReassignmentService(context.Session);
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                if (ToBox.ItemsSource != null) return;
                var lists = await Frame.RunAsync("Loading timekeepers…", () => new
                {
                    From = Lookup.Load(_context.Session, TimekeeperReassignmentService.FromListSql),
                    To = Lookup.Load(_context.Session, TimekeeperReassignmentService.ToListSql),
                });
                if (lists == null) return;
                FromBox.ItemsSource = lists.From;
                ToBox.ItemsSource = lists.To;
                if (lists.From.Count == 0)
                    Frame.ShowWarning("No timekeeper is assigned to any client or matter, so there's nothing to reassign.");
                else if (lists.To.Count == 0)
                    Frame.ShowWarning("Juris has no valid timekeepers to move assignments to.");
            };
        }

        private LookupItem FromItem => FromBox.SelectedItem as LookupItem;
        private LookupItem ToItem => ToBox.SelectedItem as LookupItem;

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            if (RunButton == null) return; // radio buttons fire while the page is still being built
            var both = FromItem != null && ToItem != null;
            var same = both && Convert.ToInt32(FromItem.Key) == Convert.ToInt32(ToItem.Key);
            RunButton.IsEnabled = both && !same;
            PreReportButton.IsEnabled = both && !same;
            if (same)
                Frame.ShowWarning("The two timekeepers are the same. Pick a different timekeeper to give the assignments to.");
            else
                Frame.ClearMessage();
        }

        /// <summary>Reads the choices on the UI thread so background work never touches controls.</summary>
        private ReassignRequest CaptureRequest()
        {
            var from = FromItem;
            var to = ToItem;
            if (from == null || to == null) return null;
            return new ReassignRequest
            {
                From = Convert.ToInt32(from.Key),
                FromId = from.Code,
                To = Convert.ToInt32(to.Key),
                ToId = to.Code,
                Role = RoleBilling.IsChecked == true ? TkprRole.Billing
                     : RoleOriginating.IsChecked == true ? TkprRole.Originating
                     : RoleResponsible.IsChecked == true ? TkprRole.Responsible : TkprRole.All,
                Scope = ScopeClients.IsChecked == true ? TkprScope.ClientOnly
                      : ScopeMatters.IsChecked == true ? TkprScope.MatterOnly : TkprScope.ClientAndMatter,
                AddNoteCards = NoteCardBox.IsChecked == true,
            };
        }

        private static string RoleWords(ReassignRequest r) =>
            r.Role == TkprRole.All ? "billing, originating and responsible" : r.RoleText.Trim().ToLowerInvariant();

        private async void OnPreReport(object sender, RoutedEventArgs e)
        {
            var r = CaptureRequest();
            if (r == null || r.From == r.To) return;
            var data = await Frame.RunAsync("Building the preview…", () => new { Table = _service.PreReport(r), Counts = _service.Count(r) });
            if (data == null) return;
            ReportWindow.Show(this, "Timekeeper reassignment preview", data.Table,
                RoleWords(r) + " timekeeper on " + r.ScopeText + ": " + r.FromId + " to " + r.ToId + ". Will change " + TimekeeperReassignmentService.Describe(data.Counts) + ".");
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            var r = CaptureRequest();
            if (r == null) return;
            if (r.From == r.To)
            {
                Frame.ShowWarning("The two timekeepers are the same. Pick a different timekeeper to give the assignments to.");
                return;
            }

            var data = await Frame.RunAsync("Checking what will change…", () => new { Table = _service.PreReport(r), Counts = _service.Count(r) });
            if (data == null) return;
            if (data.Counts.Sum(c => c.Count) == 0)
            {
                Frame.ShowInfo(r.FromId + " isn't the " + RoleWords(r) + " timekeeper on any " + r.ScopeText + ". Nothing to change.");
                return;
            }

            var summary = "Move " + RoleWords(r) + " assignments on " + r.ScopeText + " from " + r.FromId + " to " + r.ToId
                          + ": " + TimekeeperReassignmentService.Describe(data.Counts) + "."
                          + (r.AddNoteCards ? " A note card is added to each one." : "");
            bool go;
            if (data.Table.Rows.Count > 0)
                go = ReportWindow.Confirm(this, "Timekeeper reassignment — check before changing", data.Table, summary, "Reassign timekeeper");
            else // e.g. a client with no matters: nothing to list, but the counts still show what will change
                go = await Dialogs.ConfirmAsync("Reassign timekeeper?", summary, "Reassign timekeeper");
            if (!go) return;

            var result = await Frame.RunAsync("Reassigning " + r.FromId + " to " + r.ToId + "…", () =>
            {
                var done = _service.Run(r);
                done.PostReport = _service.PostReport(r, done.Clients, done.Matters);
                return new { Done = done, FromList = Lookup.Load(_context.Session, TimekeeperReassignmentService.FromListSql) };
            });
            if (result == null) return; // error already shown, nothing saved

            // Reload the From list (the old timekeeper may no longer be assigned anywhere) before showing the result.
            FromBox.ItemsSource = result.FromList;
            FromBox.SelectedItem = result.FromList.FirstOrDefault(i => Convert.ToInt32(i.Key) == r.From);
            OnInputChanged(this, null);

            _lastPostReport = result.Done.PostReport;
            _lastPostTitle = "Timekeeper reassignment — " + r.FromId + " to " + r.ToId;
            PostReportButton.IsEnabled = true;
            Frame.ShowSuccess("Changed " + TimekeeperReassignmentService.Describe(result.Done.Changed) + " from " + r.FromId + " to " + r.ToId + "."
                              + (r.AddNoteCards ? " Added " + result.Done.NoteCards.ToString("N0") + " note card(s)." : ""));
            ReportWindow.Show(this, _lastPostTitle, _lastPostReport, "The clients and matters that were changed, as they are now.");
        }

        private void OnPostReport(object sender, RoutedEventArgs e)
        {
            if (_lastPostReport != null)
                ReportWindow.Show(this, _lastPostTitle, _lastPostReport, "The clients and matters that were changed, as they are now.");
        }
    }
}
