using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Jps.Core.Catalog;
using Jps.Core.Data;
using Jps.Core.Licensing;
using Jps.Core.Modules;
using Jps.Core.Templates;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Jps.Shell.ViewModels
{
    public sealed class ToolCard
    {
        public ToolCard(ToolDefinition definition, ToolAccess access, ICommand open)
        {
            Definition = definition;
            Access = access;
            Open = open;
        }

        public ToolDefinition Definition { get; }
        public ToolAccess Access { get; }
        public ICommand Open { get; }
        public SymbolRegular Icon => Icons.Parse(Definition.Icon);
        public bool IsLicensed => Access == ToolAccess.Licensed;
    }

    public sealed class ToolGroup
    {
        public ToolGroup(string name, IReadOnlyList<ToolCard> tools)
        {
            Name = name;
            Tools = tools;
        }

        public string Name { get; }
        public IReadOnlyList<ToolCard> Tools { get; }
    }

    public sealed class HomePage : ObservableObject
    {
        private readonly IReadOnlyList<ToolCard> _all;
        private string _search = "";
        private IReadOnlyList<ToolGroup> _groups;

        public HomePage(IReadOnlyList<ToolCard> all, int licensedCount, string licenseWarning)
        {
            _all = all;
            LicensedCount = licensedCount;
            LicenseWarning = licenseWarning;
            Filter();
        }

        public int LicensedCount { get; }
        /// <summary>Why tools are locked across the board (no license, or a license for another firm). Null when fine.</summary>
        public string LicenseWarning { get; }
        public bool HasLicenseWarning => LicenseWarning != null;
        public int TotalCount => _all.Count;

        public string Search
        {
            get => _search;
            set { if (Set(ref _search, value ?? "")) Filter(); }
        }

        public IReadOnlyList<ToolGroup> Groups { get => _groups; private set => Set(ref _groups, value); }
        public bool NothingFound => Groups.Count == 0;

        private void Filter()
        {
            var terms = _search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Groups = _all
                .Where(c => terms.All(t => c.Definition.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                                        || c.Definition.Description.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                                        || c.Definition.Group.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                .GroupBy(c => c.Definition.Group)
                .OrderBy(g => Array.IndexOf(ToolCatalog.GroupOrder, g.Key))
                .Select(g => new ToolGroup(g.Key, g.ToList()))
                .ToList();
            OnPropertyChanged(nameof(NothingFound));
        }
    }

    /// <summary>A licensed tool. Shows the module's own UI once it is connected; otherwise a placeholder.</summary>
    public sealed class ToolPage : ObservableObject, IToolContext
    {
        private string _notice;

        public ToolPage(ToolDefinition definition, JurisSession session, IJurisTool module)
        {
            Definition = definition;
            Session = session;
            if (module != null && session != null)
                ModuleView = module.CreateView(this);
            HasModule = module != null;
            SaveTemplateCommand = new RelayCommand(SaveTemplate);
        }

        public ToolDefinition Definition { get; }
        public JurisSession Session { get; }
        public SymbolRegular Icon => Icons.Parse(Definition.Icon);
        public FrameworkElement ModuleView { get; }
        public bool HasModule { get; }
        public bool IsConnected => Session != null;
        public bool ShowConnectPrompt => HasModule && !IsConnected;
        public bool ShowComingSoon => !HasModule;
        public ICommand SaveTemplateCommand { get; }
        public string TemplateKind => Definition.HasTemplate && Path.GetExtension(Definition.Template).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV file" : "Excel workbook";

        public string Notice { get => _notice; private set => Set(ref _notice, value); }

        public void SaveTemplate()
        {
            if (!Definition.HasTemplate)
                return;
            var dialog = new SaveFileDialog
            {
                FileName = Definition.Template,
                Filter = TemplateStore.FileFilter(Definition),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog() != true)
                return;
            try
            {
                TemplateStore.Save(Definition, dialog.FileName);
                Notice = "Template saved to " + dialog.FileName;
            }
            catch (IOException ex)
            {
                Notice = "Couldn't save the template: " + ex.Message;
            }
            catch (UnauthorizedAccessException)
            {
                Notice = "You don't have permission to save to that folder. Pick another location.";
            }
        }
    }

    public sealed class LockedPage
    {
        public LockedPage(ToolDefinition definition, ToolAccess access, LicenseService license, JurisSession session)
        {
            Definition = definition;
            if (access == ToolAccess.WrongFirm)
            {
                Title = "This license doesn't cover " + session.FirmName;
                Body = "Your license file was issued for " + license.License.Firm + " (" + string.Join(", ", license.License.Databases)
                     + "). Contact JPS for a license that includes database " + session.JurisDbName + ".";
            }
            else
            {
                Title = definition.Name + " isn't part of your license";
                Body = "Add it to your license and it unlocks here right away, with no reinstall.";
            }
            Reference = "Tool ID: " + definition.Id + (session != null ? "   ·   Firm: " + session.FirmName + "   ·   Database: " + session.JurisDbName : "");
        }

        public ToolDefinition Definition { get; }
        public SymbolRegular Icon => Icons.Parse(Definition.Icon);
        public string Title { get; }
        public string Body { get; }
        public string Reference { get; }
    }

    public sealed class LicenseRow
    {
        public string Group { get; set; }
        public string Name { get; set; }
        public string Id { get; set; }
        public bool Owned { get; set; }
    }

    public sealed class LicensePage : ObservableObject
    {
        private readonly LicenseService _license;
        private readonly Action _reloaded;
        private string _notice;

        public LicensePage(LicenseService license, JurisSession session, Action reloaded)
        {
            _license = license;
            _reloaded = reloaded;
            Session = session;
            ImportCommand = new RelayCommand(Import);
        }

        public JurisSession Session { get; }
        public bool IsValid => _license.IsValid;
        public string Firm => _license.License?.Firm ?? "No license";
        public string Status => _license.IsValid
            ? (Session == null ? "Valid license." : _license.MatchesFirm(Session) ? "Valid license for the connected firm." : "Valid license, but not for the connected firm or database.")
            : _license.Problem;
        public string LicenseId => _license.License?.LicenseId;
        public string Databases => _license.License == null ? "" : string.Join(", ", _license.License.Databases);
        public string Issued => _license.License?.Issued;
        public string LoadedFrom => _license.LoadedFrom;
        public string OwnedSummary => (_license.License?.Modules.Count ?? 0) + " of " + ToolCatalog.All.Count + " tools";

        public IReadOnlyList<LicenseRow> Rows => ToolCatalog.All
            .OrderBy(t => Array.IndexOf(ToolCatalog.GroupOrder, t.Group))
            .Select(t => new LicenseRow { Group = t.Group, Name = t.Name, Id = t.Id, Owned = _license.AccessFor(t.Id, null) == ToolAccess.Licensed })
            .ToList();

        public ICommand ImportCommand { get; }
        public string Notice { get => _notice; private set => Set(ref _notice, value); }

        private void Import()
        {
            var dialog = new OpenFileDialog { Filter = "JPS license (*.license)|*.license|All files (*.*)|*.*" };
            if (dialog.ShowDialog() != true)
                return;
            try
            {
                if (_license.Import(dialog.FileName, out var problem))
                {
                    Notice = "License installed.";
                    _reloaded();
                }
                else
                {
                    Notice = problem;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Notice = "Windows blocked saving the license. Ask IT to run JPS Tool Library as administrator once and import it again.";
            }
            catch (IOException ex)
            {
                Notice = "Couldn't install the license: " + ex.Message;
            }
        }
    }
}
