using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using Jps.Core.Data;
using Jps.Core.Import;

namespace Jps.Tools.Tools.CoreBudgetImporter
{
    /// <summary>The matter settings chosen in the tool; they apply to every matter in the file.</summary>
    internal sealed class BudgetSettings
    {
        /// <summary>Phase given to lines whose Phase cell is blank (0-999).</summary>
        public int DefaultPhase { get; set; }

        /// <summary>The matter's new current phase (Matter.MatBudgetPhase); null = keep it (0 for a matter whose budgets were off).</summary>
        public int? CurrentPhase { get; set; }

        /// <summary>Matter.MatReqPhaseOnTrans: true = 'Y', false = 'N', null = keep each matter's setting.</summary>
        public bool? RequirePhase { get; set; }
    }

    /// <summary>
    /// One new budget line from the spreadsheet: a MatterFeeBudget line (IsFee) or a MatterExpBudget line. A null code
    /// column means "any" when Juris posting picks the line (see <see cref="BudgetMatch"/>).
    /// </summary>
    internal sealed class BudgetLine
    {
        public int Row { get; set; }
        public object[] Cells { get; set; }  // the row as shown in the problems report
        public bool IsFee { get; set; }
        public int? FilePhase { get; set; }  // null = the tool's "phase for lines with no Phase"
        public int Phase { get; set; }       // resolved by Evaluate
        public string TaskCode { get; set; }
        public string ActivityCode { get; set; }
        public int? Timekeeper { get; set; } // EmpSysNbr
        public string TimekeeperId { get; set; }
        public string PersonnelType { get; set; }
        public string ExpenseCode { get; set; }
        public decimal Hours { get; set; }
        public decimal Amount { get; set; }

        // Posted actuals carried over from the old lines Juris posting would have put on this line.
        public decimal HrsWrk, FeeWrk, HrsBld, FeeBld, ExpIncur, ExpBld;

        public void ClearCarried() => HrsWrk = FeeWrk = HrsBld = FeeBld = ExpIncur = ExpBld = 0;

        public string Key =>
            IsFee
                ? "F|" + Phase + "|" + TaskCode + "|" + ActivityCode + "|" + Timekeeper + "|" + PersonnelType
                : "E|" + Phase + "|" + TaskCode + "|" + ExpenseCode;
    }

    /// <summary>Everything that will happen to one matter: its whole fee and expense budget is replaced by these lines.</summary>
    internal sealed class MatterBudget
    {
        public int MatSysNbr { get; set; }
        public int CliSysNbr { get; set; }
        public string Client { get; set; }
        public string Matter { get; set; }
        public string Name { get; set; }

        // The matter's settings now (re-read under lock when importing).
        public bool BudgetsWereOn { get; set; }
        public int OldPhase { get; set; }
        public bool OldRequirePhase { get; set; }

        // ...and after the import (set by Evaluate).
        public int NewPhase { get; set; }
        public bool NewRequirePhase { get; set; }

        public List<BudgetLine> Fees { get; } = new List<BudgetLine>();
        public List<BudgetLine> Expenses { get; } = new List<BudgetLine>();

        public int OldFeeLines { get; set; }
        public decimal OldFeeHours { get; set; }
        public decimal OldFeeAmount { get; set; }
        public int OldExpenseLines { get; set; }
        public decimal OldExpenseAmount { get; set; }

        /// <summary>The matter's current budget lines, with the actuals Juris has accumulated on them.</summary>
        public List<OldBudgetLine> OldLines { get; set; } = new List<OldBudgetLine>();

        /// <summary>Unbilled and unposted time / expense entries, grouped by the columns Juris posting matches on.</summary>
        public List<EntryGroup> Entries { get; } = new List<EntryGroup>();

        public int TimeToCatchAll { get; set; }
        public int ExpensesToCatchAll { get; set; }
        public List<OldBudgetLine> Lost { get; } = new List<OldBudgetLine>();
    }

    /// <summary>A group of unbilled / unposted entries of one matter with the same phase and matching columns.</summary>
    internal sealed class EntryGroup
    {
        public bool IsTime;
        public int Phase;
        public string TaskCode, ActivityCode, ExpenseCode, PersonnelType;
        public int Timekeeper;
        public int Count;
    }

    /// <summary>
    /// An existing MatterFeeBudget / MatterExpBudget line. Juris accumulates actuals on these lines as time, expenses and
    /// bills post (MFBActual* / MEBExpIncur / MEBExpBld) and has no procedure that rebuilds them, so a line's actuals exist
    /// nowhere else: they are carried onto the new line Juris' posting would pick for the same work (same phase, best match
    /// by <see cref="BudgetMatch"/> using this line's own codes), and the import is blocked when no new line would.
    /// </summary>
    internal sealed class OldBudgetLine
    {
        public bool IsFee { get; set; }
        public int Line { get; set; }
        public int Phase { get; set; }
        public string TaskCode { get; set; }
        public string ExpenseCode { get; set; }
        public string ActivityCode { get; set; }
        public string PersonnelType { get; set; }
        public int? Timekeeper { get; set; }
        public decimal BudHrs { get; set; }
        public decimal BudAmt { get; set; }
        public decimal HrsWrk { get; set; }
        public decimal FeeWrk { get; set; }
        public decimal HrsBld { get; set; }
        public decimal FeeBld { get; set; }
        public decimal ExpIncur { get; set; }
        public decimal ExpBld { get; set; }

        public bool HasActuals => HrsWrk != 0 || FeeWrk != 0 || HrsBld != 0 || FeeBld != 0 || ExpIncur != 0 || ExpBld != 0;

        public string Describe(IDictionary<int, string> empIds)
        {
            if (IsFee)
            {
                string tkpr = null;
                if (Timekeeper != null) tkpr = empIds.TryGetValue(Timekeeper.Value, out var id) ? id : "(unknown)";
                return "Fee budget line " + Line + " (Budget Phase " + Phase + ", Task Code " + (TaskCode ?? "any") + ", Activity Code " + (ActivityCode ?? "any")
                       + ", Timekeeper " + (tkpr ?? "any") + ", Personnel Type " + (PersonnelType ?? "any") + "): actual "
                       + HrsWrk.ToString("0.##", CultureInfo.InvariantCulture) + " hours / " + FeeWrk.ToString("N2", CultureInfo.InvariantCulture)
                       + " worked, " + FeeBld.ToString("N2", CultureInfo.InvariantCulture) + " billed";
            }
            return "Expense budget line " + Line + " (Budget Phase " + Phase + ", Task Code " + (TaskCode ?? "any") + ", Expense Code " + (ExpenseCode ?? "any") + "): actual "
                   + ExpIncur.ToString("N2", CultureInfo.InvariantCulture) + " incurred, " + ExpBld.ToString("N2", CultureInfo.InvariantCulture) + " billed";
        }
    }

    /// <summary>
    /// How Juris posting picks the budget line for a time or expense entry, copied from Juris: ModFees.JGetMFBRow and
    /// ModExp.JGetMEBRow. Only the matter's lines in the ENTRY's phase are candidates (in line order). Each column scores:
    /// line value equals the entry's = match points; line column null = "any" (full points when the entry's is blank too,
    /// a small amount otherwise); line value differs = a large negative. Fee priority: task (128/8) > activity (64/4) >
    /// timekeeper (32/2) > personnel type of the entry's timekeeper (16/1), mismatch -256. Expense priority: task (8/2) >
    /// expense code (4/1), mismatch -16. The highest score above 0 wins (the first line on a tie); with no such line Juris
    /// inserts a new catch-all line (all codes null) in that phase.
    /// </summary>
    internal static class BudgetMatch
    {
        private static int Col(string line, string entry, int match, int any, int notMatch) =>
            line == null ? (string.IsNullOrEmpty(entry) ? match : any) : (string.Equals(line, entry ?? "", StringComparison.Ordinal) ? match : notMatch);

        public static BudgetLine Fee(IEnumerable<BudgetLine> lines, int phase, string task, string activity, int timekeeper, string personnelType)
        {
            BudgetLine best = null;
            var max = 0;
            foreach (var l in lines)
            {
                if (l.Phase != phase) continue;
                var score = Col(l.TaskCode, task, 128, 8, -256)
                            + Col(l.ActivityCode, activity, 64, 4, -256)
                            + (l.Timekeeper == null ? (timekeeper == 0 ? 32 : 2) : (l.Timekeeper == timekeeper ? 32 : -256))
                            + Col(l.PersonnelType, personnelType, 16, 1, -256);
                if (score > max) { max = score; best = l; }
            }
            return best;
        }

        public static BudgetLine Expense(IEnumerable<BudgetLine> lines, int phase, string task, string expenseCode)
        {
            BudgetLine best = null;
            var max = 0;
            foreach (var l in lines)
            {
                if (l.Phase != phase) continue;
                var score = Col(l.TaskCode, task, 8, 2, -16) + Col(l.ExpenseCode, expenseCode, 4, 1, -16);
                if (score > max) { max = score; best = l; }
            }
            return best;
        }
    }

    /// <summary>The result of reading and checking a file. Only a plan with no errors can be imported.</summary>
    internal sealed class ImportPlan
    {
        public string FilePath { get; set; }
        public DataTable FileRows { get; set; }
        public DataTable FileErrors { get; set; }  // problems in the rows themselves
        public DataTable Errors { get; set; }      // FileErrors + problems that depend on the matter settings
        public bool OldLayout { get; set; }
        public bool HasMatterNumberColumn { get; set; }
        public bool Loaded { get; set; }           // existing budgets and entries were read (only when the rows are valid)
        public List<MatterBudget> Matters { get; } = new List<MatterBudget>();
        public Dictionary<int, string> EmpIds { get; set; } = new Dictionary<int, string>();
        public Dictionary<int, string> EmpPrsTyp { get; set; } = new Dictionary<int, string>();
        public BudgetSettings Settings { get; set; } = new BudgetSettings();
        public int FeeLines => Matters.Sum(m => m.Fees.Count);
        public int ExpenseLines => Matters.Sum(m => m.Expenses.Count);
        public int LostCount => Matters.Sum(m => m.Lost.Count);
        public bool CanImport => Loaded && Errors.Rows.Count == 0 && Matters.Count > 0;
    }

    internal sealed class ImportResult
    {
        public int Matters { get; set; }
        public int FeeLines { get; set; }
        public int ExpenseLines { get; set; }
        public int DeletedLines { get; set; }
    }

    /// <summary>Reads, checks and applies a matter budget spreadsheet. No SQL is ever built from spreadsheet text.</summary>
    internal sealed class CoreBudgetImportService
    {
        public const string SheetName = "Sheet1";
        public const int MaxPhase = 999;
        private const int MaxLinesPerMatter = short.MaxValue;      // MFBLine / MEBLine are smallint
        private const decimal MaxHours = 9999999999.99m;          // decimal(12,2)
        private const decimal MaxAmount = 99999999999.99m;        // well inside money

        private readonly JurisSession _session;

        public CoreBudgetImportService(JurisSession session) => _session = session;

        // ---------- reading ----------

        private sealed class Columns
        {
            public string Client, Matter, TaskType, TaskExp, TaskCode, ExpenseCode, ActivityCode, Timekeeper, PersonnelType, Phase, Desc, Hours, Amount, MatSysNbr;

            /// <summary>The original template: no Expense_Code column, so Task_Code holds the expense code on Expense rows.</summary>
            public bool OldLayout => ExpenseCode == null;
        }

        /// <summary>Reads Sheet1 (or the first sheet when there is no Sheet1) and checks the headers.</summary>
        public static DataTable ReadFile(string path)
        {
            DataTable table;
            try
            {
                table = SpreadsheetReader.Read(path, SheetName);
            }
            catch (Exception first)
            {
                try
                {
                    table = SpreadsheetReader.Read(path);
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("The file could not be read. It must be a .xlsx, .xls or .csv file with the template's columns on a sheet named Sheet1. " + first.Message, first);
                }
            }
            FindColumns(table); // throws a plain-English message when headers are missing
            if (table.Rows.Count == 0)
                throw new InvalidOperationException("The file does not contain any budget rows. Fill in the rows under the header row and try again.");
            return table;
        }

        private static string Normalise(string header) => new string(header.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        private static Columns FindColumns(DataTable table)
        {
            var byName = new Dictionary<string, string>();
            foreach (DataColumn column in table.Columns)
            {
                var key = Normalise(column.ColumnName);
                if (key.Length > 0 && !byName.ContainsKey(key))
                    byName[key] = column.ColumnName;
            }
            string Find(params string[] names)
            {
                foreach (var name in names)
                    if (byName.TryGetValue(Normalise(name), out var actual)) return actual;
                return null;
            }

            // Current template headers first, then the original template's (Client, Matter, Task_Type, TaskExp, Task_Code,
            // Task_Desc, Hours, Amount, MatSysNbr) so files made from it still import. Matching ignores case, spaces and underscores.
            var cols = new Columns
            {
                Client = Find("Client Code", "Client"),
                Matter = Find("Matter Code", "Matter"),
                TaskType = Find("Task_Type"),
                TaskExp = Find("Fee or Expense", "TaskExp", "Task_Exp"),
                TaskCode = Find("Task Code"),
                ExpenseCode = Find("Expense Code"),
                ActivityCode = Find("Activity Code"),
                Timekeeper = Find("Timekeeper ID", "Timekeeper"),
                PersonnelType = Find("Personnel Type"),
                Phase = Find("Budget Phase", "Phase"),
                Desc = Find("Description", "Task_Desc"),
                Hours = Find("Budgeted Hours", "Hours"),
                Amount = Find("Budgeted Amount", "Amount"),
                MatSysNbr = Find("MatSysNbr", "Matter System Number"),
            };

            // The original read column I by position whatever its header said; keep that for files in the original layout.
            var original = cols.ExpenseCode == null && cols.ActivityCode == null && cols.Timekeeper == null && cols.PersonnelType == null && cols.Phase == null;
            if (original && cols.MatSysNbr == null && table.Columns.Count >= 9)
            {
                var ninth = table.Columns[8].ColumnName;
                var known = new[] { cols.Client, cols.Matter, cols.TaskType, cols.TaskExp, cols.TaskCode, cols.Desc, cols.Hours, cols.Amount };
                if (!known.Contains(ninth))
                    cols.MatSysNbr = ninth;
            }

            var missing = new List<string>();
            if (cols.Client == null) missing.Add("Client Code");
            if (cols.Matter == null) missing.Add("Matter Code");
            if (cols.TaskExp == null) missing.Add("Fee or Expense");
            if (cols.OldLayout && cols.TaskCode == null) missing.Add("Task Code and Expense Code");
            if (cols.Amount == null) missing.Add("Budgeted Amount");
            if (missing.Count > 0)
                throw new InvalidOperationException("The file does not have the expected column headings. Missing: " + string.Join(", ", missing)
                    + ". Use the template (Download template) and keep its header row.");
            return cols;
        }

        // ---------- checking ----------

        private sealed class MatterInfo
        {
            public int MatSysNbr, CliSysNbr, Phase;
            public bool BudgetsOn, RequirePhase;
            public string Client, Matter, Name;
        }

        private sealed class Employee
        {
            public int SysNbr;
            public string Id, PrsTyp;
            public bool ValidAsTkpr;
        }

        private static DataTable NewErrorTable()
        {
            var errors = new DataTable();
            errors.Columns.Add("Row", typeof(int));
            errors.Columns.Add("Client Code", typeof(string));
            errors.Columns.Add("Matter Code", typeof(string));
            errors.Columns.Add("Fee or Expense", typeof(string));
            errors.Columns.Add("Budget Phase", typeof(string));
            errors.Columns.Add("Task Code", typeof(string));
            errors.Columns.Add("Expense Code", typeof(string));
            errors.Columns.Add("Activity Code", typeof(string));
            errors.Columns.Add("Timekeeper ID", typeof(string));
            errors.Columns.Add("Personnel Type", typeof(string));
            errors.Columns.Add("Budgeted Hours", typeof(string));
            errors.Columns.Add("Budgeted Amount", typeof(string));
            errors.Columns.Add(MatterNumberColumn, typeof(string));
            errors.Columns.Add("Problem", typeof(string));
            return errors;
        }

        /// <summary>Checks every row (all problems, not just the first), reads the matters' current budgets, then <see cref="Evaluate"/>s with <paramref name="settings"/>.</summary>
        public ImportPlan Check(string path, DataTable file, BudgetSettings settings)
        {
            var cols = FindColumns(file);
            var fileErrors = NewErrorTable();
            var plan = new ImportPlan { FilePath = path, FileRows = file, FileErrors = fileErrors, OldLayout = cols.OldLayout, HasMatterNumberColumn = cols.MatSysNbr != null };

            var taskCodes = CodeSet("select TaskCdCode from TaskCode");
            var expenseCodes = CodeSet("select ExpCdCode from ExpenseCode");
            var activityCodes = CodeSet("select ActyCdCode from ActivityCode");
            var personnelTypes = CodeSet("select PrsTypCode from PersonnelType");
            var employees = _session.Query("select EmpSysNbr, EmpID, EmpValidAsTkpr, EmpPrsTyp from Employee").Rows()
                .Select(r => new Employee
                {
                    SysNbr = Convert.ToInt32(r["EmpSysNbr"]),
                    Id = Sql.Text(r, "EmpID"),
                    ValidAsTkpr = Sql.Text(r, "EmpValidAsTkpr") == "Y",
                    PrsTyp = Sql.Text(r, "EmpPrsTyp").Length == 0 ? null : Sql.Text(r, "EmpPrsTyp"),
                }).ToList();
            plan.EmpIds = employees.ToDictionary(e => e.SysNbr, e => e.Id);
            plan.EmpPrsTyp = employees.ToDictionary(e => e.SysNbr, e => e.PrsTyp);

            var matters = _session.Query(
                @"select m.MatSysNbr, c.CliSysNbr, m.MatBudgetPhase, m.MatBudgetOption, m.MatReqPhaseOnTrans, m.MatNickName, c.CliNickName,
                         dbo.jfn_FormatClientCode(c.CliCode) as Cli, dbo.jfn_FormatMatterCode(m.MatCode) as Mat
                  from Matter m inner join Client c on c.CliSysNbr = m.MatCliNbr").Rows()
                .Select(r => new MatterInfo
                {
                    MatSysNbr = Convert.ToInt32(r["MatSysNbr"]),
                    CliSysNbr = Convert.ToInt32(r["CliSysNbr"]),
                    Phase = r["MatBudgetPhase"] == DBNull.Value ? 0 : Convert.ToInt32(r["MatBudgetPhase"]),
                    BudgetsOn = Sql.Text(r, "MatBudgetOption") == "1",
                    RequirePhase = Sql.Text(r, "MatReqPhaseOnTrans").Equals("Y", StringComparison.OrdinalIgnoreCase),
                    Client = Sql.Text(r, "Cli"),
                    Matter = Sql.Text(r, "Mat"),
                    Name = (Sql.Text(r, "CliNickName") + " / " + Sql.Text(r, "MatNickName")).Trim(' ', '/'),
                }).ToList();
            var bySysNbr = matters.ToDictionary(m => m.MatSysNbr);
            var byClient = matters.GroupBy(m => m.Client, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var plans = new Dictionary<int, MatterBudget>();

            for (var i = 0; i < file.Rows.Count; i++)
            {
                var r = file.Rows[i];
                var rowNumber = SpreadsheetReader.SourceRow(file, i); // the real sheet row (header = row 1; blank rows counted)
                string Cell(string column) => column == null ? "" : Sql.Text(r, column);

                var client = Cell(cols.Client);
                var matter = Cell(cols.Matter);
                var type = Cell(cols.TaskExp);
                var taskText = Cell(cols.TaskCode).ToUpperInvariant();
                var expText = Cell(cols.ExpenseCode).ToUpperInvariant();
                var actyText = Cell(cols.ActivityCode).ToUpperInvariant();
                var tkprText = Cell(cols.Timekeeper);
                var prsText = Cell(cols.PersonnelType).ToUpperInvariant();
                var phaseText = Cell(cols.Phase);
                var hoursText = Cell(cols.Hours);
                var amountText = Cell(cols.Amount);
                var sysText = Cell(cols.MatSysNbr);
                var cells = new object[] { rowNumber, client, matter, type, phaseText, taskText, expText, actyText, tkprText, prsText, hoursText, amountText, sysText };
                var problems = new List<string>();

                bool? isTask = null;
                if (type.Equals("Fee", StringComparison.OrdinalIgnoreCase) || type.Equals("Task", StringComparison.OrdinalIgnoreCase)) isTask = true;
                else if (type.Equals("Expense", StringComparison.OrdinalIgnoreCase)) isTask = false;
                else problems.Add(type.Length == 0 ? "Fee or Expense is blank (enter Fee or Expense)" : "Fee or Expense \"" + type + "\" must be Fee or Expense");

                var line = new BudgetLine { Row = rowNumber, Cells = cells, IsFee = isTask == true };

                // Phase: blank = the phase chosen in the tool.
                if (phaseText.Length > 0)
                {
                    if (decimal.TryParse(phaseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var ph) && ph == Math.Floor(ph) && ph >= 0 && ph <= MaxPhase)
                        line.FilePhase = (int)ph;
                    else
                        problems.Add("Budget Phase \"" + phaseText + "\" must be a whole number from 0 to " + MaxPhase + " (or blank)");
                }

                if (isTask == true)
                {
                    // A blank code = the line applies to any value of that column.
                    if (taskText.Length == 0)
                    {
                        if (cols.OldLayout) problems.Add("Task Code is blank");
                    }
                    else if (taskCodes.TryGetValue(taskText, out var t)) line.TaskCode = t;
                    else problems.Add("Task Code " + taskText + " does not exist in Juris");

                    if (actyText.Length > 0)
                    {
                        if (activityCodes.TryGetValue(actyText, out var a)) line.ActivityCode = a;
                        else problems.Add("Activity Code " + actyText + " does not exist in Juris");
                    }

                    if (tkprText.Length > 0)
                    {
                        var emp = FindEmployee(tkprText, employees);
                        if (emp == null) problems.Add("Timekeeper ID " + tkprText + " is not an employee in Juris");
                        else if (!emp.ValidAsTkpr) problems.Add("Employee " + emp.Id + " is not set up as a timekeeper in Juris");
                        else { line.Timekeeper = emp.SysNbr; line.TimekeeperId = emp.Id; }
                    }

                    if (prsText.Length > 0)
                    {
                        if (personnelTypes.TryGetValue(prsText, out var p)) line.PersonnelType = p;
                        else problems.Add("Personnel Type " + prsText + " does not exist in Juris");
                    }

                    // Juris compares the line's personnel type with the entry timekeeper's: a line with both can only ever match if they agree.
                    if (line.Timekeeper != null && line.PersonnelType != null
                        && !string.Equals(plan.EmpPrsTyp[line.Timekeeper.Value], line.PersonnelType, StringComparison.Ordinal))
                        problems.Add("Timekeeper " + line.TimekeeperId + " has Personnel Type " + (plan.EmpPrsTyp[line.Timekeeper.Value] ?? "(none)")
                                     + ", not " + line.PersonnelType + ", so Juris would never post any time to this line. Leave one of them blank");

                    if (expText.Length > 0) problems.Add("Expense Code is only used on Expense rows; leave it blank on Fee rows");
                }
                else if (isTask == false)
                {
                    var code = cols.OldLayout ? taskText : expText;
                    if (code.Length == 0)
                    {
                        if (cols.OldLayout) problems.Add("Task Code (the expense code in this older layout) is blank");
                    }
                    else if (expenseCodes.TryGetValue(code, out var e)) line.ExpenseCode = e;
                    else problems.Add("Expense Code " + code + " does not exist in Juris");

                    if (!cols.OldLayout && taskText.Length > 0)
                    {
                        if (taskCodes.TryGetValue(taskText, out var t)) line.TaskCode = t;
                        else problems.Add("Task Code " + taskText + " does not exist in Juris");
                    }

                    if (actyText.Length > 0 || tkprText.Length > 0 || prsText.Length > 0)
                        problems.Add("Activity Code, Timekeeper ID and Personnel Type are only used on Fee rows; leave them blank on Expense rows");
                }

                // Hours only matter for fee lines; expense budgets have no hours, so they're ignored there.
                decimal hours = 0;
                if (isTask != false)
                {
                    if (!TryNumber(hoursText, out hours)) problems.Add("Budgeted Hours \"" + hoursText + "\" is not a number");
                    else if (hours < 0) problems.Add("Budgeted Hours can't be negative");
                    else if (hours > MaxHours) problems.Add("Budgeted Hours is too large");
                }
                if (!TryNumber(amountText, out var amount)) problems.Add("Budgeted Amount \"" + amountText + "\" is not a number");
                else if (amount < 0) problems.Add("Budgeted Amount can't be negative");
                else if (amount > MaxAmount) problems.Add("Budgeted Amount is too large");
                line.Hours = isTask == true ? Math.Round(hours, 2) : 0;
                line.Amount = Math.Round(amount, 2);

                var found = ResolveMatter(client, matter, sysText, bySysNbr, byClient, problems);

                if (problems.Count > 0)
                {
                    AddError(fileErrors, cells, string.Join("; ", problems));
                    continue;
                }

                if (!plans.TryGetValue(found.MatSysNbr, out var mp))
                {
                    mp = new MatterBudget
                    {
                        MatSysNbr = found.MatSysNbr,
                        CliSysNbr = found.CliSysNbr,
                        Client = found.Client,
                        Matter = found.Matter,
                        Name = found.Name,
                        BudgetsWereOn = found.BudgetsOn,
                        OldPhase = found.Phase,
                        OldRequirePhase = found.RequirePhase,
                    };
                    plans.Add(found.MatSysNbr, mp);
                    plan.Matters.Add(mp);
                }
                (line.IsFee ? mp.Fees : mp.Expenses).Add(line);
            }

            foreach (var mp in plan.Matters)
                if (mp.Fees.Count > MaxLinesPerMatter || mp.Expenses.Count > MaxLinesPerMatter)
                    AddMatterError(fileErrors, mp, "Juris allows at most " + MaxLinesPerMatter.ToString("N0") + " fee and " + MaxLinesPerMatter.ToString("N0") + " expense budget lines per matter");

            if (fileErrors.Rows.Count == 0 && plan.Matters.Count > 0)
            {
                LoadExistingBudgets(plan.Matters);
                LoadEntries(plan);
                plan.Loaded = true;
            }
            Evaluate(plan, settings);
            return plan;
        }

        private static void AddError(DataTable errors, object[] cells, string problem)
        {
            var values = new object[cells.Length + 1];
            cells.CopyTo(values, 0);
            values[cells.Length] = problem;
            errors.Rows.Add(values);
        }

        private static void AddMatterError(DataTable errors, MatterBudget m, string problem) =>
            errors.Rows.Add(DBNull.Value, m.Client, m.Matter, "", "", "", "", "", "", "", "", "", "", problem);

        private static Employee FindEmployee(string id, List<Employee> employees)
        {
            var exact = employees.Where(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            // Excel drops leading zeros from numeric IDs; accept them when they still identify one employee.
            var loose = employees.Where(e => StripZeros(e.Id).Equals(StripZeros(id), StringComparison.OrdinalIgnoreCase)).ToList();
            return loose.Count == 1 ? loose[0] : null;
        }

        /// <summary>
        /// Applies the matter settings to a checked plan, in memory: resolves each line's phase, rejects duplicate lines,
        /// works out each matter's new current phase and require-phase flag, checks that a required phase can be used, and
        /// (when the existing budgets were read) where each old line's actuals go and which entries would post to a
        /// catch-all line. Fast: call it again whenever a setting changes.
        /// </summary>
        public static void Evaluate(ImportPlan plan, BudgetSettings settings)
        {
            plan.Settings = settings;
            var errors = plan.FileErrors.Copy();
            plan.Errors = errors;

            foreach (var m in plan.Matters)
            {
                var keys = new Dictionary<string, BudgetLine>();
                foreach (var l in m.Fees.Concat(m.Expenses).OrderBy(x => x.Row))
                {
                    l.Phase = l.FilePhase ?? settings.DefaultPhase;
                    if (keys.TryGetValue(l.Key, out var first))
                        AddError(errors, l.Cells, "Same " + (l.IsFee ? "fee" : "expense") + " budget line as row " + first.Row + " (same matter, Budget Phase " + l.Phase
                            + (l.IsFee ? ", Task Code, Activity Code, Timekeeper and Personnel Type" : ", Task Code and Expense Code")
                            + "). Juris would only ever post to one of them: combine them into one row");
                    else
                        keys.Add(l.Key, l);
                }

                m.NewPhase = settings.CurrentPhase ?? (m.BudgetsWereOn ? m.OldPhase : 0);
                m.NewRequirePhase = settings.RequirePhase ?? m.OldRequirePhase;
                if (m.NewRequirePhase)
                    foreach (var problem in RequirePhaseProblems(m))
                        AddMatterError(errors, m, problem);

                CarryActuals(m, plan.EmpPrsTyp);
                CountCatchAll(m);
            }

            if (!plan.HasMatterNumberColumn)
                errors.Columns.Remove(MatterNumberColumn); // only files made from the old template have it
        }

        private const string MatterNumberColumn = "Matter number (old template)";

        /// <summary>
        /// With "require phase" on, Juris accepts a time entry only in a phase that has a fee budget line, and an expense entry
        /// only in a phase that has an expense budget line (JTimeBatchEntry / JExpenseBatchEntry.BudgetPhase), and new entries
        /// start in the matter's current phase. Juris' matter screen therefore insists on both kinds of line ("You must set up
        /// budgets for both expense and fees"). Every phase the budget uses, and the current phase, need both.
        /// </summary>
        private static IEnumerable<string> RequirePhaseProblems(MatterBudget m)
        {
            var feePhases = new HashSet<int>(m.Fees.Select(l => l.Phase));
            var expPhases = new HashSet<int>(m.Expenses.Select(l => l.Phase));
            var phases = new SortedSet<int>(feePhases.Concat(expPhases)) { m.NewPhase };
            foreach (var p in phases)
            {
                var what = p == m.NewPhase ? "the matter's current Budget Phase " + p + " (new entries start in it)" : "Budget Phase " + p;
                if (!feePhases.Contains(p))
                    yield return "\"Require phase on transactions\" will be on, but " + what + " has no Fee line, so Juris would reject time entries in that phase. "
                                 + "Add a Fee row for Budget Phase " + p + " (blank codes and a 0 amount is fine), or change the matter settings in the tool";
                if (!expPhases.Contains(p))
                    yield return "\"Require phase on transactions\" will be on, but " + what + " has no Expense line, so Juris would reject expense entries in that phase. "
                                 + "Add an Expense row for Budget Phase " + p + " (blank codes and a 0 amount is fine), or change the matter settings in the tool";
            }
        }

        /// <summary>Moves each old line's actuals onto the new line Juris posting would choose for it; lines with no such new line are Lost.</summary>
        private static void CarryActuals(MatterBudget m, IDictionary<int, string> empPrsTyp)
        {
            foreach (var l in m.Fees.Concat(m.Expenses)) l.ClearCarried();
            m.Lost.Clear();
            foreach (var o in m.OldLines.Where(x => x.HasActuals))
            {
                BudgetLine target;
                if (o.IsFee)
                {
                    var tkpr = o.Timekeeper ?? 0;
                    // Posting compares the line's personnel type with the entry timekeeper's; the old line's own value stands in for it.
                    var prs = o.PersonnelType ?? (o.Timekeeper != null && empPrsTyp.TryGetValue(tkpr, out var p) ? p : null);
                    target = BudgetMatch.Fee(m.Fees, o.Phase, o.TaskCode, o.ActivityCode, tkpr, prs);
                }
                else
                    target = BudgetMatch.Expense(m.Expenses, o.Phase, o.TaskCode, o.ExpenseCode);

                if (target == null)
                {
                    m.Lost.Add(o);
                    continue;
                }
                target.HrsWrk += o.HrsWrk; target.FeeWrk += o.FeeWrk; target.HrsBld += o.HrsBld; target.FeeBld += o.FeeBld;
                target.ExpIncur += o.ExpIncur; target.ExpBld += o.ExpBld;
            }
        }

        private static void CountCatchAll(MatterBudget m)
        {
            m.TimeToCatchAll = 0;
            m.ExpensesToCatchAll = 0;
            foreach (var e in m.Entries)
            {
                if (e.IsTime)
                {
                    if (BudgetMatch.Fee(m.Fees, e.Phase, e.TaskCode, e.ActivityCode, e.Timekeeper, e.PersonnelType) == null)
                        m.TimeToCatchAll += e.Count;
                }
                else if (BudgetMatch.Expense(m.Expenses, e.Phase, e.TaskCode, e.ExpenseCode) == null)
                    m.ExpensesToCatchAll += e.Count;
            }
        }

        private Dictionary<string, string> CodeSet(string sql)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow r in _session.Query(sql).Rows)
            {
                var code = Convert.ToString(r[0]).Trim();
                if (code.Length > 0 && !result.ContainsKey(code))
                    result[code] = Convert.ToString(r[0]); // insert Juris' own spelling of the code
            }
            return result;
        }

        private static bool TryNumber(string text, out decimal value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return true; // blank counts as zero
            var t = text.Trim();
            var negative = t.StartsWith("(") && t.EndsWith(")");
            if (negative) t = t.Substring(1, t.Length - 2);
            if (!decimal.TryParse(t, NumberStyles.Currency | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out value)
                && !decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            if (negative) value = -value;
            return true;
        }

        private static string StripZeros(string code) => code.TrimStart('0');

        /// <summary>
        /// Finds the row's matter. With MatSysNbr: that matter, which must belong to the row's client (and match the Matter code
        /// when one is given). Without: the client and matter codes as Juris displays them; codes whose leading zeros were
        /// dropped by Excel are accepted when they still identify exactly one client / matter.
        /// </summary>
        private static MatterInfo ResolveMatter(string client, string matter, string sysText,
            Dictionary<int, MatterInfo> bySysNbr, Dictionary<string, List<MatterInfo>> byClient, List<string> problems)
        {
            if (client.Length == 0)
            {
                problems.Add("Client Code is blank");
                return null;
            }

            var clientMatters = FindClient(client, byClient);
            if (clientMatters == null)
            {
                problems.Add("Client " + client + " was not found in Juris");
                return null;
            }

            if (sysText.Length > 0)
            {
                // Excel can hand back "1234.0"; accept any whole number.
                if (!decimal.TryParse(sysText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    || d != Math.Floor(d) || d <= 0 || d > int.MaxValue)
                {
                    problems.Add("The matter number \"" + sysText + "\" (old template column I) is not a whole number");
                    return null;
                }
                var sys = (int)d;
                if (!bySysNbr.TryGetValue(sys, out var bySys))
                {
                    problems.Add("The matter number " + sys + " (old template column I) was not found in Juris");
                    return null;
                }
                if (bySys.CliSysNbr != clientMatters[0].CliSysNbr)
                {
                    problems.Add("The matter number " + sys + " (old template column I) belongs to client " + bySys.Client + ", not " + client);
                    return null;
                }
                if (matter.Length > 0 && !SameCode(matter, bySys.Matter))
                {
                    problems.Add("The matter number " + sys + " (old template column I) is matter " + bySys.Client + "/" + bySys.Matter + ", not " + client + "/" + matter);
                    return null;
                }
                return bySys;
            }

            if (matter.Length == 0)
            {
                problems.Add("Matter Code is blank");
                return null;
            }
            var exact = clientMatters.Where(m => string.Equals(m.Matter, matter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var loose = clientMatters.Where(m => SameCode(matter, m.Matter)).ToList();
            if (loose.Count == 1) return loose[0];
            problems.Add(loose.Count > 1
                ? "Matter " + client + "/" + matter + " matches more than one matter; enter the matter code exactly as Juris shows it, including leading zeros"
                : "Matter " + client + "/" + matter + " was not found in Juris");
            return null;
        }

        private static List<MatterInfo> FindClient(string client, Dictionary<string, List<MatterInfo>> byClient)
        {
            if (byClient.TryGetValue(client, out var exact))
                return exact;
            var loose = byClient.Where(kv => StripZeros(kv.Key).Equals(StripZeros(client), StringComparison.OrdinalIgnoreCase)).ToList();
            return loose.Count == 1 ? loose[0].Value : null;
        }

        private static bool SameCode(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(StripZeros(a), StripZeros(b), StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<List<T>> Chunks<T>(IEnumerable<T> items, int size)
        {
            var chunk = new List<T>(size);
            foreach (var item in items)
            {
                chunk.Add(item);
                if (chunk.Count == size) { yield return chunk; chunk = new List<T>(size); }
            }
            if (chunk.Count > 0) yield return chunk;
        }

        private static string NullText(DataRow r, string c) =>
            r[c] == DBNull.Value || Convert.ToString(r[c]).Trim().Length == 0 ? null : Convert.ToString(r[c]).Trim();

        private static decimal Dec(DataRow r, string c) => r[c] == DBNull.Value ? 0m : Convert.ToDecimal(r[c]);

        private void LoadExistingBudgets(List<MatterBudget> matters)
        {
            var map = matters.ToDictionary(m => m.MatSysNbr);
            foreach (var m in matters) m.OldLines = new List<OldBudgetLine>();
            foreach (var pair in ReadOldLines(map.Keys, false))
                map[pair.Key].OldLines.Add(pair.Value);
            SetOldTotals(matters);
        }

        private static void SetOldTotals(IEnumerable<MatterBudget> matters)
        {
            foreach (var m in matters)
            {
                var fees = m.OldLines.Where(o => o.IsFee).ToList();
                var exps = m.OldLines.Where(o => !o.IsFee).ToList();
                m.OldFeeLines = fees.Count;
                m.OldFeeHours = fees.Sum(o => o.BudHrs);
                m.OldFeeAmount = fees.Sum(o => o.BudAmt);
                m.OldExpenseLines = exps.Count;
                m.OldExpenseAmount = exps.Sum(o => o.BudAmt);
            }
        }

        /// <summary>
        /// Reads the current budget lines (with their actuals) of the given matters. <paramref name="lockRows"/> = read with
        /// (updlock, holdlock) inside the import transaction, so posting can't add actuals between the read and the delete.
        /// </summary>
        private List<KeyValuePair<int, OldBudgetLine>> ReadOldLines(IEnumerable<int> matterIds, bool lockRows)
        {
            var hint = lockRows ? " with (updlock, holdlock)" : "";
            var ids = matterIds.ToList();
            var result = new List<KeyValuePair<int, OldBudgetLine>>();
            foreach (var r in _session.Query(
                "select MFBMatter, MFBLine, MFBPhase, MFBTaskCode, MFBActivityCode, MFBPrsTyp, MFBTkpr, MFBBudHrsWrk, MFBBudFeeWrk, MFBActualHrsWrk, MFBActualFeeWrk, MFBActualHrsBld, MFBActualFeeBld"
                + " from MatterFeeBudget" + hint + " where MFBMatter in " + Sql.IdSelect("@Mats") + " order by MFBMatter, MFBLine",
                Sql.IdList("@Mats", ids)).Rows())
                result.Add(new KeyValuePair<int, OldBudgetLine>(Convert.ToInt32(r["MFBMatter"]), new OldBudgetLine
                {
                    IsFee = true,
                    Line = Convert.ToInt32(r["MFBLine"]),
                    Phase = Convert.ToInt32(r["MFBPhase"]),
                    TaskCode = NullText(r, "MFBTaskCode"),
                    ActivityCode = NullText(r, "MFBActivityCode"),
                    PersonnelType = NullText(r, "MFBPrsTyp"),
                    Timekeeper = r["MFBTkpr"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["MFBTkpr"]),
                    BudHrs = Dec(r, "MFBBudHrsWrk"),
                    BudAmt = Dec(r, "MFBBudFeeWrk"),
                    HrsWrk = Dec(r, "MFBActualHrsWrk"),
                    FeeWrk = Dec(r, "MFBActualFeeWrk"),
                    HrsBld = Dec(r, "MFBActualHrsBld"),
                    FeeBld = Dec(r, "MFBActualFeeBld"),
                }));

            foreach (var r in _session.Query(
                "select MEBMatter, MEBLine, MEBPhase, MEBTaskCode, MEBExpCode, MEBBudExp, MEBExpIncur, MEBExpBld from MatterExpBudget" + hint
                + " where MEBMatter in " + Sql.IdSelect("@Mats") + " order by MEBMatter, MEBLine",
                Sql.IdList("@Mats", ids)).Rows())
                result.Add(new KeyValuePair<int, OldBudgetLine>(Convert.ToInt32(r["MEBMatter"]), new OldBudgetLine
                {
                    IsFee = false,
                    Line = Convert.ToInt32(r["MEBLine"]),
                    Phase = Convert.ToInt32(r["MEBPhase"]),
                    TaskCode = NullText(r, "MEBTaskCode"),
                    ExpenseCode = NullText(r, "MEBExpCode"),
                    BudAmt = Dec(r, "MEBBudExp"),
                    ExpIncur = Dec(r, "MEBExpIncur"),
                    ExpBld = Dec(r, "MEBExpBld"),
                }));
            return result;
        }

        /// <summary>
        /// Unbilled time / expenses (UnbilledTime, UnbilledExpense) and unposted batch entries (TimeBatchDetail /
        /// ExpBatchDetail not yet posted, in batches that aren't posted) of the plan's matters, grouped by the columns Juris
        /// posting matches budget lines on. Billing and posting them will update the budget line Juris picks, or a new
        /// catch-all line when none matches.
        /// </summary>
        private void LoadEntries(ImportPlan plan)
        {
            var map = plan.Matters.ToDictionary(m => m.MatSysNbr);
            var mats = Sql.IdSelect("@Mats");
            var sql =
                "select 'T' as K, UTMatter as Mat, UTBudgPhase as Ph, UTTaskCd as Task, UTActivityCd as Acty, UTTkpr as Tkpr, cast(null as varchar(4)) as Exp, count(*) as N"
                + " from UnbilledTime where UTMatter in " + mats + " group by UTMatter, UTBudgPhase, UTTaskCd, UTActivityCd, UTTkpr"
                + " union all select 'T', d.TBDMatter, d.TBDBudgPhase, d.TBDTaskCd, d.TBDActivityCd, d.TBDTkpr, null, count(*)"
                + " from TimeBatchDetail d inner join TimeBatch b on b.TBBatchNbr = d.TBDBatch"
                + " where d.TBDPosted = 'N' and b.TBStatus not in ('P', 'D') and d.TBDMatter in " + mats
                + " group by d.TBDMatter, d.TBDBudgPhase, d.TBDTaskCd, d.TBDActivityCd, d.TBDTkpr"
                + " union all select 'E', UEMatter, UEBudgPhase, UEBudgTaskCd, null, 0, UEExpCd, count(*)"
                + " from UnbilledExpense where UEMatter in " + mats + " group by UEMatter, UEBudgPhase, UEBudgTaskCd, UEExpCd"
                + " union all select 'E', d.EBDMatter, d.EBDBudgPhase, d.EBDBudgTaskCd, null, 0, d.EBDExpCd, count(*)"
                + " from ExpBatchDetail d inner join ExpenseBatch b on b.EBBatchNbr = d.EBDBatch"
                + " where d.EBDPosted = 'N' and b.EBStatus not in ('P', 'D') and d.EBDMatter in " + mats
                + " group by d.EBDMatter, d.EBDBudgPhase, d.EBDBudgTaskCd, d.EBDExpCd";
            foreach (var r in _session.Query(sql, Sql.IdList("@Mats", map.Keys)).Rows())
            {
                var isTime = Convert.ToString(r["K"]) == "T";
                var tkpr = r["Tkpr"] == DBNull.Value ? 0 : Convert.ToInt32(r["Tkpr"]);
                map[Convert.ToInt32(r["Mat"])].Entries.Add(new EntryGroup
                {
                    IsTime = isTime,
                    Phase = Convert.ToInt32(r["Ph"]),
                    TaskCode = NullText(r, "Task"),
                    ActivityCode = NullText(r, "Acty"),
                    ExpenseCode = NullText(r, "Exp"),
                    Timekeeper = tkpr,
                    PersonnelType = isTime && plan.EmpPrsTyp.TryGetValue(tkpr, out var p) ? p : null,
                    Count = Convert.ToInt32(r["N"]),
                });
            }
        }

        // ---------- reports ----------

        /// <summary>One row per old budget line whose posted actuals the import would lose (empty = the import may run).</summary>
        public static DataTable LostActualsTable(ImportPlan plan)
        {
            var t = new DataTable();
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Name", typeof(string));
            t.Columns.Add("Current budget line with posted actuals", typeof(string));
            foreach (var m in plan.Matters)
                foreach (var o in m.Lost)
                    t.Rows.Add(m.Client, m.Matter, m.Name, o.Describe(plan.EmpIds));
            return t;
        }

        private static string Phases(MatterBudget m) =>
            string.Join(", ", m.Fees.Concat(m.Expenses).Select(l => l.Phase).Distinct().OrderBy(p => p));

        public static string CurrentPhaseChange(MatterBudget m)
        {
            if (!m.BudgetsWereOn) return "(budgets were off) → " + m.NewPhase;
            return m.OldPhase == m.NewPhase ? m.NewPhase + " (unchanged)" : m.OldPhase + " → " + m.NewPhase;
        }

        private static string YesNo(bool b) => b ? "Yes" : "No";

        private static string RequireChange(MatterBudget m) =>
            m.OldRequirePhase == m.NewRequirePhase ? YesNo(m.NewRequirePhase) + " (unchanged)" : YesNo(m.OldRequirePhase) + " → " + YesNo(m.NewRequirePhase);

        /// <summary>The pre-report: one row per matter whose whole budget is replaced, old against new.</summary>
        public static DataTable ReviewTable(ImportPlan plan)
        {
            var t = new DataTable();
            t.Columns.Add("Client Code", typeof(string));
            t.Columns.Add("Matter Code", typeof(string));
            t.Columns.Add("Name", typeof(string));
            t.Columns.Add("Fee lines now", typeof(int));
            t.Columns.Add("Budgeted hours now", typeof(decimal));
            t.Columns.Add("Budgeted fees now", typeof(decimal));
            t.Columns.Add("Expense lines now", typeof(int));
            t.Columns.Add("Budgeted expenses now", typeof(decimal));
            t.Columns.Add("New fee lines", typeof(int));
            t.Columns.Add("New budgeted hours", typeof(decimal));
            t.Columns.Add("New budgeted fees", typeof(decimal));
            t.Columns.Add("New expense lines", typeof(int));
            t.Columns.Add("New budgeted expenses", typeof(decimal));
            t.Columns.Add("Budget phases in new budget", typeof(string));
            t.Columns.Add("Current budget phase before → after", typeof(string));
            t.Columns.Add("Require phase on transactions before → after", typeof(string));
            t.Columns.Add("Actual hours worked kept", typeof(decimal));
            t.Columns.Add("Actual fees worked kept", typeof(decimal));
            t.Columns.Add("Actual fees billed kept", typeof(decimal));
            t.Columns.Add("Actual expenses incurred kept", typeof(decimal));
            t.Columns.Add("Actual expenses billed kept", typeof(decimal));
            t.Columns.Add("Unbilled/unposted time entries with no matching line", typeof(int));
            t.Columns.Add("Unbilled/unposted expense entries with no matching line", typeof(int));
            foreach (var m in plan.Matters)
                t.Rows.Add(m.Client, m.Matter, m.Name,
                    m.OldFeeLines, m.OldFeeHours, m.OldFeeAmount, m.OldExpenseLines, m.OldExpenseAmount,
                    m.Fees.Count, m.Fees.Sum(l => l.Hours), m.Fees.Sum(l => l.Amount),
                    m.Expenses.Count, m.Expenses.Sum(l => l.Amount),
                    Phases(m), CurrentPhaseChange(m), RequireChange(m),
                    m.Fees.Sum(l => l.HrsWrk), m.Fees.Sum(l => l.FeeWrk), m.Fees.Sum(l => l.FeeBld),
                    m.Expenses.Sum(l => l.ExpIncur), m.Expenses.Sum(l => l.ExpBld),
                    m.TimeToCatchAll, m.ExpensesToCatchAll);
            return t;
        }

        // ---------- writing ----------

        /// <summary>Replaces the fee and expense budget of every matter in the plan and sets the matter options, all in one transaction.</summary>
        public ImportResult Apply(ImportPlan plan)
        {
            if (!plan.CanImport)
                throw new InvalidOperationException("The file has problems; nothing was imported.");
            var settings = plan.Settings;

            return _session.InTransaction(() =>
            {
                var result = new ImportResult { Matters = plan.Matters.Count };
                var matterIds = plan.Matters.Select(m => m.MatSysNbr).ToList();
                var map = plan.Matters.ToDictionary(m => m.MatSysNbr);

                // Lock the matters (posting updates the matter row, so a posting in progress finishes first) and re-read their
                // settings and budget lines locked, so the checks and the actuals carried over are exactly what is replaced.
                foreach (var r in _session.Query(
                    "select MatSysNbr, MatBudgetOption, MatBudgetPhase, MatReqPhaseOnTrans from Matter with (updlock, holdlock) where MatSysNbr in " + Sql.IdSelect("@Mats"),
                    Sql.IdList("@Mats", matterIds)).Rows())
                {
                    var m = map[Convert.ToInt32(r["MatSysNbr"])];
                    m.BudgetsWereOn = Sql.Text(r, "MatBudgetOption") == "1";
                    m.OldPhase = Convert.ToInt32(r["MatBudgetPhase"]);
                    m.OldRequirePhase = Sql.Text(r, "MatReqPhaseOnTrans").Equals("Y", StringComparison.OrdinalIgnoreCase);
                }
                foreach (var m in plan.Matters) m.OldLines = new List<OldBudgetLine>();
                foreach (var pair in ReadOldLines(matterIds, true))
                    map[pair.Key].OldLines.Add(pair.Value);
                SetOldTotals(plan.Matters);

                Evaluate(plan, settings);
                if (plan.Errors.Rows.Count > 0)
                    throw new InvalidOperationException("The matters changed since the file was checked: "
                        + string.Join("; ", plan.Errors.Rows.Cast<DataRow>().Take(5).Select(r => r["Client Code"] + "/" + r["Matter Code"] + ": " + r["Problem"]))
                        + ". Nothing was changed. Click Check again.");
                var lost = plan.Matters.SelectMany(m => m.Lost.Select(o => m.Client + "/" + m.Matter + " " + o.Describe(plan.EmpIds))).ToList();
                if (lost.Count > 0)
                    throw new InvalidOperationException(lost.Count + " existing budget line(s) have posted actuals that no line in the file would receive, so they would be lost: "
                        + string.Join("; ", lost.Take(10)) + (lost.Count > 10 ? "; …" : "")
                        + ". Add a row in the same Budget Phase that this work would post to (a 0 amount is fine). Nothing was changed.");

                result.DeletedLines += _session.ExecuteRows("delete from MatterFeeBudget where MFBMatter in " + Sql.IdSelect("@Mats"), Sql.IdList("@Mats", matterIds));
                result.DeletedLines += _session.ExecuteRows("delete from MatterExpBudget where MEBMatter in " + Sql.IdSelect("@Mats"), Sql.IdList("@Mats", matterIds));

                // Budgets on, the current phase and require-phase, per matter.
                foreach (var chunk in Chunks(plan.Matters, 600)) // 3 parameters per matter
                {
                    var sql = new StringBuilder("update m set MatBudgetOption = '1', MatBudgetPhase = v.Ph, MatReqPhaseOnTrans = v.Req from Matter m inner join (values ");
                    var ps = new List<SqlParameter>();
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        if (i > 0) sql.Append(',');
                        sql.Append("(@m").Append(i).Append(",@p").Append(i).Append(",@r").Append(i).Append(')');
                        ps.Add(Sql.P("@m" + i, chunk[i].MatSysNbr));
                        ps.Add(new SqlParameter("@p" + i, SqlDbType.SmallInt) { Value = (short)chunk[i].NewPhase });
                        ps.Add(new SqlParameter("@r" + i, SqlDbType.Char, 1) { Value = chunk[i].NewRequirePhase ? "Y" : "N" });
                    }
                    sql.Append(") v (Mat, Ph, Req) on v.Mat = m.MatSysNbr");
                    var updated = _session.ExecuteRows(sql.ToString(), ps.ToArray());
                    if (updated != chunk.Count)
                        throw new InvalidOperationException("Expected to update " + chunk.Count + " matter(s) but updated " + updated + ". Nothing was changed.");
                }
                _session.ExecuteRows("update Client set CliBudgetOption = '1' where CliBudgetOption <> '1' and CliSysNbr in " + Sql.IdSelect("@Clis"),
                    Sql.IdList("@Clis", plan.Matters.Select(m => m.CliSysNbr).Distinct()));

                // Line numbers run 1..n per matter in file order (Juris checks lines in line order, first best match wins).
                var fees = plan.Matters.SelectMany(m => m.Fees.Select((l, i) => new { m.MatSysNbr, Line = i + 1, l })).ToList();
                foreach (var chunk in Chunks(fees, 150)) // 13 parameters per line: stays under SQL Server's 2,100
                {
                    var sql = new StringBuilder("insert into MatterFeeBudget (MFBMatter, MFBLine, MFBPhase, MFBTaskCode, MFBActivityCode, MFBPrsTyp, MFBTkpr, MFBBudHrsWrk, MFBBudFeeWrk, MFBActualHrsWrk, MFBActualFeeWrk, MFBActualHrsBld, MFBActualFeeBld) values ");
                    var ps = new List<SqlParameter>();
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        if (i > 0) sql.Append(',');
                        var n = i.ToString(CultureInfo.InvariantCulture);
                        sql.Append("(@m" + n + ",@l" + n + ",@p" + n + ",@t" + n + ",@a" + n + ",@pt" + n + ",@k" + n + ",@h" + n + ",@f" + n + ",@hw" + n + ",@fw" + n + ",@hb" + n + ",@fb" + n + ")");
                        var l = chunk[i].l;
                        ps.Add(Sql.P("@m" + n, chunk[i].MatSysNbr));
                        ps.Add(new SqlParameter("@l" + n, SqlDbType.SmallInt) { Value = (short)chunk[i].Line });
                        ps.Add(new SqlParameter("@p" + n, SqlDbType.SmallInt) { Value = (short)l.Phase });
                        ps.Add(new SqlParameter("@t" + n, SqlDbType.VarChar, 4) { Value = (object)l.TaskCode ?? DBNull.Value });
                        ps.Add(new SqlParameter("@a" + n, SqlDbType.VarChar, 4) { Value = (object)l.ActivityCode ?? DBNull.Value });
                        ps.Add(new SqlParameter("@pt" + n, SqlDbType.VarChar, 2) { Value = (object)l.PersonnelType ?? DBNull.Value });
                        ps.Add(new SqlParameter("@k" + n, SqlDbType.Int) { Value = (object)l.Timekeeper ?? DBNull.Value });
                        ps.Add(new SqlParameter("@h" + n, SqlDbType.Decimal) { Precision = 12, Scale = 2, Value = l.Hours });
                        ps.Add(new SqlParameter("@f" + n, SqlDbType.Money) { Value = l.Amount });
                        ps.Add(new SqlParameter("@hw" + n, SqlDbType.Decimal) { Precision = 12, Scale = 2, Value = l.HrsWrk });
                        ps.Add(new SqlParameter("@fw" + n, SqlDbType.Money) { Value = l.FeeWrk });
                        ps.Add(new SqlParameter("@hb" + n, SqlDbType.Decimal) { Precision = 12, Scale = 2, Value = l.HrsBld });
                        ps.Add(new SqlParameter("@fb" + n, SqlDbType.Money) { Value = l.FeeBld });
                    }
                    result.FeeLines += _session.ExecuteRows(sql.ToString(), ps.ToArray());
                }

                var expenses = plan.Matters.SelectMany(m => m.Expenses.Select((l, i) => new { m.MatSysNbr, Line = i + 1, l })).ToList();
                foreach (var chunk in Chunks(expenses, 250)) // 8 parameters per line
                {
                    var sql = new StringBuilder("insert into MatterExpBudget (MEBMatter, MEBLine, MEBPhase, MEBTaskCode, MEBExpCode, MEBBudExp, MEBExpIncur, MEBExpBld) values ");
                    var ps = new List<SqlParameter>();
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        if (i > 0) sql.Append(',');
                        var n = i.ToString(CultureInfo.InvariantCulture);
                        sql.Append("(@m" + n + ",@l" + n + ",@p" + n + ",@t" + n + ",@c" + n + ",@a" + n + ",@ei" + n + ",@eb" + n + ")");
                        var l = chunk[i].l;
                        ps.Add(Sql.P("@m" + n, chunk[i].MatSysNbr));
                        ps.Add(new SqlParameter("@l" + n, SqlDbType.SmallInt) { Value = (short)chunk[i].Line });
                        ps.Add(new SqlParameter("@p" + n, SqlDbType.SmallInt) { Value = (short)l.Phase });
                        ps.Add(new SqlParameter("@t" + n, SqlDbType.VarChar, 4) { Value = (object)l.TaskCode ?? DBNull.Value });
                        ps.Add(new SqlParameter("@c" + n, SqlDbType.VarChar, 4) { Value = (object)l.ExpenseCode ?? DBNull.Value });
                        ps.Add(new SqlParameter("@a" + n, SqlDbType.Money) { Value = l.Amount });
                        ps.Add(new SqlParameter("@ei" + n, SqlDbType.Money) { Value = l.ExpIncur });
                        ps.Add(new SqlParameter("@eb" + n, SqlDbType.Money) { Value = l.ExpBld });
                    }
                    result.ExpenseLines += _session.ExecuteRows(sql.ToString(), ps.ToArray());
                }

                var names = string.Join(", ", plan.Matters.Select(m => m.Client + "/" + m.Matter));
                if (names.Length > 3000) names = names.Substring(0, 3000) + "…";
                _session.WriteUtilityLog("JPS Juris Core Budget Importer: replaced the fee and expense budgets of " + result.Matters + " matter(s) with "
                    + result.FeeLines + " fee line(s) and " + result.ExpenseLines + " expense line(s), " + result.DeletedLines + " old line(s) removed"
                    + "; blank phase = " + settings.DefaultPhase
                    + ", current phase " + (settings.CurrentPhase == null ? "kept" : "set to " + settings.CurrentPhase)
                    + ", require phase " + (settings.RequirePhase == null ? "kept" : settings.RequirePhase.Value ? "Y" : "N")
                    + ". Matters: " + names);
                return result;
            });
        }
    }
}
