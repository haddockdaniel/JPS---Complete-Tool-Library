using System.Collections.Generic;
using System.Windows;
using Jps.Core.Modules;
using Jps.Tools.Tools.CodeReassignment;

namespace Jps.Tools.Tools.ActivityCode
{
    /// <summary>
    /// Activity Code Reassignment: changes every reference from one Activity Code to another, optionally deleting the old
    /// code in the same transaction. Ported from "JPS - Activity Code Reassignment v10" onto the shared code-reassignment
    /// engine. Fixed here on top of the shared fixes: CRFeeAlloc and FeeSumByPrd merges lost the From amounts (they updated
    /// then deleted the From row); TimeEntryTemplate.ActivityCode (a real FK) was missed, so deleting the code crashed;
    /// MobilityTemplates/MobilityTokens rows and favourites/most-recent entries were deleted instead of moved; WriteLog was
    /// never called.
    /// </summary>
    public sealed class ActivityCodeReassignment : IJurisTool
    {
        public string Id => "JPS.ACTYCODE";

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
            Noun = "Activity Code",
            ToolName = "Activity Code Reassignment",
            CodeTable = "ActivityCode",
            CodeColumn = "ActyCdCode",
            DescColumn = "ActyCdDesc",
            References = new[]
            {
                CodeRef.Merge(S1, "ARFTaskAlloc", "ARFTActivityCd",
                    new[] { "ARFTBillNbr", "ARFTMatter", "ARFTTkpr", "ARFTTaskCd?" },
                    new[] { "ARFTWorkedHrsBld", "ARFTHrsBld", "ARFTStdValueBld", "ARFTActualValueBld", "ARFTActualAmtBld", "ARFTRcvd", "ARFTAdj", "ARFTPend" }),
                CodeRef.Update(S1, "BilledTime", "BTActivityCd"),
                CodeRef.KeepTarget(S1, "ActivityCodeMostRecent", "Code", "EmployeeID"),
                CodeRef.KeepTarget(S1, "ActivityCodeFavorite", "Code", "EmployeeID"),
                // Not MobilityTokens: its ActivityCode column holds a field-name token ("ActivityCode"), not a code.
                CodeRef.Update(S1, "MobilityTemplates", "ActivityCode"),
                CodeRef.Merge(S2, "CRFeeAlloc", "CRFActivityCd",
                    new[] { "CRFBatch", "CRFRecNbr", "CRFBillNbr", "CRFMatter", "CRFTkpr", "CRFTaskCd?" },
                    new[] { "CRFPrePost", "CRFAmount" }),
                CodeRef.Merge(S3, "FeeSumByPrd", "FSPActivityCd",
                    new[] { "FSPMatter", "FSPTkpr", "FSPTaskCd?", "FSPPrdYear", "FSPPrdNbr" },
                    new[]
                    {
                        "FSPWorkedHrsEntered", "FSPNonBilHrsEntered", "FSPBilHrsEntered", "FSPFeeEnteredStdValue", "FSPFeeEnteredActualValue",
                        "FSPWorkedHrsBld", "FSPHrsBilled", "FSPFeeBldStdValue", "FSPFeeBldActualValue", "FSPFeeBldActualAmt",
                        "FSPFeeReceived", "FSPFeeAdjusted",
                    }),
                CodeRef.Update(S4, "MatterFeeBudget", "MFBActivityCode"),
                CodeRef.UpdateWithNote(S5, "TimeBatchDetail", "TBDActivityCd", "TBDNarrative").Protecting(EntryMirror.TimeEntry),
                CodeRef.Update(S6, "TimeEntry", "ActivityCode"), // after TimeBatchDetail: its trigger's restore puts the old code back
                CodeRef.Update(S6, "TimeEntryTemplate", "ActivityCode"),
                CodeRef.Update(S7, "UnbilledTime", "UTActivityCd"),
            },
            DeleteChecks = new[]
            {
                new KeyValuePair<string, string>("Saved time entry filters that use it - edit or delete those filters first",
                    "select count(*) from Filter where Definition like '%Column=\"ActivityCode%' and charindex('DataValue=\"' + @From + '\"', cast(Definition as varchar(max))) > 0"),
            },
            // Its leaf entry in the Tables view (class 6500, under the Activity Codes folder). DocumentTree_Integrity only logs.
            AfterDeleteSql = new[]
            {
                @"delete from DocumentTree
                  where DTDocClass = 6500 and DTDocType = 'R' and DTKeyT = @From
                    and DTParentID in (select p.DTDocID from DocumentTree p where p.DTDocClass = 6500 and p.DTDocType = 'F')
                    and not exists (select 1 from DocumentTree c where c.DTParentID = DocumentTree.DTDocID)",
            },
        };
    }
}
