using System.Collections.Generic;
using System.Windows;
using Jps.Core.Modules;
using Jps.Tools.Tools.CodeReassignment;

namespace Jps.Tools.Tools.TaskCode
{
    /// <summary>
    /// Task Code Reassignment: replaces one task code with another in every Juris table so the original can be deleted
    /// (optionally in the same transaction), plus a "last activity date" report per task code. Ported from
    /// "JPS - Task Code Reassignment v10" onto the shared code-reassignment engine. Fixed here on top of the shared fixes:
    /// CRFeeAlloc and FeeSumByPrd merges lost the From amounts; From = To deleted the code's own rates/cross-references;
    /// the promised last-activity report didn't exist; favourites/most-recent entries were deleted instead of moved;
    /// selecting the first item of an empty list crashed; WriteLog was never called.
    /// </summary>
    public sealed class TaskCodeReassignment : IJurisTool
    {
        public string Id => "JPS.TASKCODE";

        public FrameworkElement CreateView(IToolContext context) => new CodeReassignmentView(context, Map);

        private const string S1 = "Updating billed time and receivables…";
        private const string S2 = "Updating cash receipt allocations…";
        private const string S3 = "Updating fee totals by period…";
        private const string S4 = "Updating matter budgets…";
        private const string S5 = "Updating time and expense batches…";
        private const string S6 = "Updating time entries…";
        private const string S7 = "Updating unbilled time…";

        internal static readonly CodeReassignmentMap Map = new CodeReassignmentMap
        {
            Noun = "Task Code",
            ToolName = "Task Code Reassignment",
            CodeTable = "TaskCode",
            CodeColumn = "TaskCdCode",
            DescColumn = "TaskCdDesc",
            References = new[]
            {
                CodeRef.Merge(S1, "ARFTaskAlloc", "ARFTTaskCd",
                    new[] { "ARFTBillNbr", "ARFTMatter", "ARFTTkpr", "ARFTActivityCd?" },
                    new[] { "ARFTWorkedHrsBld", "ARFTHrsBld", "ARFTStdValueBld", "ARFTActualValueBld", "ARFTActualAmtBld", "ARFTRcvd", "ARFTAdj", "ARFTPend" }),
                CodeRef.Update(S1, "BilledTime", "BTTaskCd"),
                CodeRef.Update(S1, "BilledExpenses", "BEBudgTaskCd"),
                CodeRef.Update(S1, "ExpenseEntry", "TaskCode"),
                CodeRef.Update(S1, "ExpenseEntryTemplate", "TaskCode"),
                CodeRef.Update(S1, "MobilityTemplates", "TaskCode"),
                // Not MobilityTokens: its TaskCode column holds a field-name token ("TaskCode"), not a code.
                CodeRef.KeepTarget(S1, "TaskCodeMostRecent", "Code", "EmployeeID"),
                CodeRef.KeepTarget(S1, "TaskCodeFavorite", "Code", "EmployeeID"),
                CodeRef.Merge(S2, "CRFeeAlloc", "CRFTaskCd",
                    new[] { "CRFBatch", "CRFRecNbr", "CRFBillNbr", "CRFMatter", "CRFTkpr", "CRFActivityCd?" },
                    new[] { "CRFPrePost", "CRFAmount" }),
                CodeRef.Merge(S3, "FeeSumByPrd", "FSPTaskCd",
                    new[] { "FSPMatter", "FSPTkpr", "FSPActivityCd?", "FSPPrdYear", "FSPPrdNbr" },
                    new[]
                    {
                        "FSPWorkedHrsEntered", "FSPNonBilHrsEntered", "FSPBilHrsEntered", "FSPFeeEnteredStdValue", "FSPFeeEnteredActualValue",
                        "FSPWorkedHrsBld", "FSPHrsBilled", "FSPFeeBldStdValue", "FSPFeeBldActualValue", "FSPFeeBldActualAmt",
                        "FSPFeeReceived", "FSPFeeAdjusted",
                    }),
                CodeRef.Update(S4, "MatterFeeBudget", "MFBTaskCode"),
                CodeRef.Update(S4, "MatterExpBudget", "MEBTaskCode"),
                CodeRef.Update(S5, "ExpBatchDetail", "EBDBudgTaskCd").Protecting(EntryMirror.ExpenseEntry),
                CodeRef.Update(S5, "TimeBatchDetail", "TBDTaskCd").Protecting(EntryMirror.TimeEntry),
                CodeRef.Update(S6, "TimeEntry", "TaskCode"), // after TimeBatchDetail: its trigger's restore puts the old code back
                CodeRef.Update(S6, "TimeEntryTemplate", "TaskCode"),
                CodeRef.Update(S6, "VoucherBatchMatDist", "VBMBudgTaskCd"),
                CodeRef.Update(S6, "VoucherMatDist", "VMBudgTaskCd"),
                CodeRef.Update(S6, "UnbilledExpense", "UEBudgTaskCd"),
                CodeRef.KeepTarget(S6, "TaskCodeRate", "TCRTaskCd", "TCRFeeSch"),
                CodeRef.KeepTarget(S6, "TaskCodeXref", "TCXFirmsCode", "TCXList"),
                CodeRef.Update(S7, "UnbilledTime", "UTTaskCd"),
            },
            DeleteChecks = new[]
            {
                new KeyValuePair<string, string>("Saved time and expense entry filters that use it - edit or delete those filters first",
                    "select count(*) from Filter where Definition like '%Column=\"TaskCode%' and charindex('DataValue=\"' + @From + '\"', cast(Definition as varchar(max))) > 0"),
            },
            // Only this code's leaf entry in the Tables view (class 2400, under the Task Codes folder) - not every orphan.
            AfterDeleteSql = new[]
            {
                @"delete from DocumentTree
                  where DTDocClass = 2400 and DTDocType = 'R' and DTKeyT = @From
                    and DTParentID in (select p.DTDocID from DocumentTree p where p.DTDocClass = 2400 and p.DTDocType = 'F')
                    and not exists (select 1 from DocumentTree c where c.DTParentID = DocumentTree.DTDocID)",
            },

            ReportButtonText = "Last activity report",
            ReportTitle = "Task code last activity",
            ReportSubtitle = "Latest date each task code was used on unbilled, billed or unposted time and expenses. Blank = never used.",
            ReportSql = @"
select t.TaskCdCode as [Task Code], t.TaskCdDesc as [Description], max(a.LastDate) as [Last Activity Date]
from TaskCode t
left join (
            select UTTaskCd      as Code, max(UTDate)  as LastDate from UnbilledTime    group by UTTaskCd
  union all select BTTaskCd,      max(BTDate)  from BilledTime      group by BTTaskCd
  union all select TBDTaskCd,     max(TBDDate) from TimeBatchDetail group by TBDTaskCd
  union all select UEBudgTaskCd,  max(UEDate)  from UnbilledExpense group by UEBudgTaskCd
  union all select BEBudgTaskCd,  max(BEDate)  from BilledExpenses  group by BEBudgTaskCd
  union all select EBDBudgTaskCd, max(EBDDate) from ExpBatchDetail  group by EBDBudgTaskCd
) a on a.Code = t.TaskCdCode
group by t.TaskCdCode, t.TaskCdDesc
order by t.TaskCdCode",
        };
    }
}
