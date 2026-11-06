# JPS Tool Library

One WPF app that hosts every JPS Juris utility. Each tool is still sold separately: a signed license file decides which tools unlock for a firm.

- .NET Framework 4.8, x86 (the Juris libraries in `lib\Juris` are 32-bit and depend on .NET Framework-only components). 4.8 ships with Windows 10/11, so firms install nothing extra.
- UI: [WPF-UI](https://github.com/lepoco/wpfui) (MIT, free). No paid libraries.
- The old WinForms base (`JurisUtilityBase\`) is left untouched for reference.

## Layout

| Path | What it is |
|---|---|
| `src\Jps.Core` | Shared core: `JurisSession` (company list, connection, FirmName, last backup, UtilityLog), `Sql` helpers, `SpreadsheetReader`, Excel export, the tool catalog, licensing, embedded import templates, `IJurisTool` |
| `src\Jps.Core\Catalog\tools.json` | Every tool: id, group, name, description, icon, template. Generated from *Application consolidation.xlsx*. **Never change an id once a license has shipped with it.** |
| `src\Jps.Core\Templates` | Import templates embedded in the exe (no internet needed). Rebuild with `build\New-Templates.ps1` (needs Excel on the build PC only). |
| `src\Jps.Tools` | **Every tool, in one assembly** (`Jps.Tools.dll`, built next to the exe). One folder per tool under `Tools\<Tool>`; shared UI and SQL helpers in `Common\` (below). Each tool is still licensed separately by its catalog id. |
| `src\Jps.Shell` | The app: top bar (company + connection), nav rail, home, tool, locked and license pages. Loads the tools from `Jps.Tools` (and any hotfix `Modules\*.dll`, which overrides a built-in tool with the same id). |
| `tools\Jps.LicenseGenerator` | `JpsLicense.exe`, internal only. Issues licenses. |

### Shared helpers (use these; don't copy them into a tool)

| Where | What |
|---|---|
| `JurisSession.Query / Scalar / Execute` | Parameterised SQL on the one shared connection. `InTransaction` joins an outer transaction, so helpers compose. |
| `JurisSession.ExecuteRows(dml)` | Runs ONE insert/update/delete/merge and returns that statement's own `@@ROWCOUNT`. **Use it whenever a row count matters** ("exactly one row", counts shown to users or written to UtilityLog). Juris tables have audit triggers and NOCOUNT is off, so `Execute`'s return value also counts the trigger's log rows (a 1-row delete can return 4). Ignore `Execute`'s return value. |
| `Sql.P`, `Sql.InList`, `Sql.Text`, `.Rows()` | Parameters, a short "in (@x0, @x1...)" list, trimmed text, LINQ over a DataTable. |
| `Sql.IdList(name, ids)` + `Sql.IdSelect(name)` | Any number of int ids as ONE xml parameter: `"where MatSysNbr in " + Sql.IdSelect("@Mats")` with `Sql.IdList("@Mats", ids)`. A statement with `option (hash join)` can't use `.nodes()`: copy the ids into a table variable first (see Delete Zero Batches). |
| `SpreadsheetReader.Read` / `SourceRow(table, i)` / `FirstUsedColumn(table)` | .csv/.xlsx (native) and .xls (via Excel) into a DataTable of strings. Blank rows are dropped, so report row numbers with `SourceRow`, never `index + 2`. |
| `Common\ToolFrame`, `ReportWindow`, `Dialogs`, `CheckList`, `Lookup`, `ToolStyles.xaml` | The page frame (`RunAsync`: background work, errors shown), report grid with Open in Excel / Save as CSV, confirm and file dialogs, searchable multi-select, lookup lists, card/step styles. |
| `Common\FolderPicker` | The Windows "Select folder" dialog. |
| `Common\TempKeyTable.With(...)` | A `#temp` table of int keys (any number, parameterised) for set-based SQL inside a transaction. `MatterCloser\MatterIdTable` is the #JpsMatterIds instance the close rules use. |
| `Common\JurisCounters` | Numbers from Juris' SysParam counters (batch numbers, voucher numbers, `NextDocTreeId`): locked until commit, above every number already in use, never lowers a counter. |
| `Common\JurisBatchTree.AddBatch(...)` | Files a new batch in the Juris batch tree, creating the user / period folders the firm option (CfgMiscOpts) asks for. |
| `Tools\MatterCloser\MatterCloseRules` | The one set of matter close rules (Matter Closer, Matter Status and Lock Updater, Split Matter Closer). |
| `Tools\CodeReassignment\TriggerSafeDml` | `SnapshotEntries` / `RestoreEntries`: updating TimeBatchDetail / ExpBatchDetail fires MyJuris triggers that rewrite the linked entries; these put them back. |

The shell shows an **Import template / Download template** card on the page of every tool that has a template; tools don't add their own download button.

## Build

```
dotnet build "JPS Tool Library.sln" -c Release
```

Output: `src\Jps.Shell\bin\Release\net48\JPS Tool Library.exe`, `Jps.Tools.dll`, `Jps.Core.dll` plus the Juris DLLs. Put a firm's `JPS.license` next to the exe in the installer.

ActiveReports 6 (Archive Bill Bulk Print renders through Juris' JurisSVR21) needs its runtime license as an embedded resource named `<exe file name>.licenses` in the entry exe. `src\Jps.Shell\Licensing\JPS Tool Library.exe.licenses` is the original tool's compiled licenses.licx re-keyed to "JPS TOOL LIBRARY.EXE" and embedded directly (LC.exe isn't available to `dotnet build`). **If the shell's `AssemblyName` ever changes, regenerate that resource** or Archive Bill Bulk Print fails at render time. See the comment in `Jps.Shell.csproj`.

## Licenses

Private key: `%USERPROFILE%\JPS Licensing\jps-license-private.xml`. **Back it up.** If it is lost, no new licenses can be issued that existing installs accept. Never ship it or put it in source control. The matching public key is `src\Jps.Core\Licensing\license-public-key.xml`.

```
JpsLicense modules
JpsLicense sign --firm "Smith and Lane LLP" --firmkey "<SysParam FirmName exactly>" --db Juris1 --module JPS.MATCLOSER --module JPS.VCHIMPORT --out JPS.license
JpsLicense verify JPS.license
```

- `--firmkey` must match `SELECT sptxtvalue FROM sysparam WHERE spname = 'FirmName'` (case and outer spaces ignored).
- `--db` is the Juris database name (`JurisDbName`); repeat it for each company database the firm owns.
- Licenses are perpetual: no dates. To add a tool, issue a new file with the extra `--module`. The firm can import it from the License page, which stores it in `C:\ProgramData\JPS\Tool Library\` and takes priority over the installed copy.

## Moving a tool into the library

1. Add a folder `src\Jps.Tools\Tools\<Tool>` (namespace `Jps.Tools.Tools.<Tool>`). No new project: every tool lives in `Jps.Tools`.
2. Add a public `IJurisTool` class with `Id` equal to the tool's id in `tools.json`, returning the tool's WPF `UserControl` from `CreateView`. The shell finds it by reflection; until it exists the tool shows a "being moved into the Tool Library" placeholder.
3. Wrap the view in `ToolFrame` and run database work through `Frame.RunAsync`. Use `context.Session` for all SQL: parameterised `Query` / `Scalar` / `Execute`, `ExecuteRows` for any count you compare, show or log, `InTransaction` around every change, and `WriteUtilityLog` once per run.
4. Before writing a helper, check the shared helpers above (and `Common\`); if two tools need the same thing, put it in `Common\` (or `Jps.Core` when it isn't UI or tool-specific) instead of copying it.
5. Templates: put the file in `src\Jps.Core\Templates` and its name in `tools.json`; the shell's template card offers it. Read it with `SpreadsheetReader`.
6. Hotfix only: a separate class library built into `Modules\` next to the exe overrides the built-in tool with the same id.

## Screenshots without opening a window

```
"JPS Tool Library.exe" --snapshot out.png [--page JPS.MATCLOSER|home|license] [--theme dark] [--size 1320x840]
```
