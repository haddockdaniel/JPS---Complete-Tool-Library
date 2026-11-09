using System;
using System.Collections.Generic;
using System.Linq;
using Jps.Core.Data;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.FeeScheduleCopy
{
    /// <summary>How many rate rows a fee schedule has (also used as the result of a copy).</summary>
    public sealed class RateCounts
    {
        public int Timekeepers { get; set; }
        public int PersonnelTypes { get; set; }
        public int TaskCodes { get; set; }
        public override string ToString() =>
            Timekeepers + " timekeeper rate(s), " + PersonnelTypes + " personnel type rate(s) and " + TaskCodes + " task code rate(s)";
    }

    /// <summary>Database work for Fee Schedule Copy. Every method must be called through ToolFrame.RunAsync.</summary>
    public sealed class FeeScheduleCopyService
    {
        /// <summary>Juris' own fee schedule folder; the original tool used it when the source has no tree node.</summary>
        private const int DefaultFeeScheduleFolder = 17;
        private const int FeeScheduleDocClass = 2700;

        private readonly JurisSession _session;

        public FeeScheduleCopyService(JurisSession session) => _session = session;

        public List<LookupItem> LoadSources() =>
            Lookup.Load(_session, "select FeeSchCode, FeeSchCode as Code, FeeSchDesc from FeeSchedule where FeeSchActive = 'Y' order by FeeSchCode");

        /// <summary>Returns a plain-English problem with the typed code, or null when it's usable (format only, no DB).</summary>
        public static string CheckCodeFormat(string code)
        {
            if (string.IsNullOrEmpty(code)) return "Type a code for the new fee schedule.";
            if (code.Length > 4) return "The code can be at most 4 characters.";
            if (code.Any(ch => char.IsWhiteSpace(ch) || ch == '\'' || ch == '"'))
                return "The code can't contain spaces or quote marks.";
            return null;
        }

        public bool CodeExists(string code) =>
            _session.Scalar("select count(*) from FeeSchedule where FeeSchCode = @code", Sql.P("@code", code)) is int n && n > 0;

        public RateCounts CountRates(string feeSch) => new RateCounts
        {
            Timekeepers = Count("select count(*) from TkprRate where TKRFeeSch = @fs", feeSch),
            PersonnelTypes = Count("select count(*) from PersTypRate where PTRFeeSch = @fs", feeSch),
            TaskCodes = Count("select count(*) from TaskCodeRate where TCRFeeSch = @fs", feeSch),
        };

        private int Count(string sql, string feeSch) => Convert.ToInt32(_session.Scalar(sql, Sql.P("@fs", feeSch)) ?? 0);

        /// <summary>
        /// Creates the new fee schedule (header, Juris explorer node, all three rate tables) in one transaction.
        /// Returns the number of rate rows copied.
        /// </summary>
        public RateCounts Copy(string fromCode, string newCode, string newDesc)
        {
            return _session.InTransaction(() =>
            {
                // Re-check inside the transaction: someone may have added it since the user clicked.
                if (CodeExists(newCode))
                    throw new InvalidOperationException("Fee schedule " + newCode + " already exists. Nothing was changed.");

                _session.Execute("insert into FeeSchedule (FeeSchCode, FeeSchDesc, FeeSchActive) values (@code, @desc, 'Y')",
                    Sql.P("@code", newCode), Sql.P("@desc", newDesc));

                // Explorer node, created the way Juris' JDocumentTree.AddToDocTree does it:
                // id from SysParam 'LastSysNbrDocTree' (above every id in use), system-created 'Y', type 'R', title max 30, DTKeyL 0.
                var docId = JurisCounters.NextDocTreeId(_session);
                var parent = _session.Scalar(
                    "select top 1 DTParentID from DocumentTree where DTDocClass = @cls and DTKeyT = @from order by DTDocID",
                    Sql.P("@cls", FeeScheduleDocClass), Sql.P("@from", fromCode));
                _session.Execute(
                    @"insert into DocumentTree (DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)
                      values (@id, 'Y', @cls, 'R', @parent, @title,
                              -- same DTKeyL as the source schedule's node (Juris leaves it NULL on some books, 0 on others)
                              (select top 1 DTKeyL from DocumentTree where DTDocClass = @cls and DTKeyT = @from order by DTDocID), @code)",
                    Sql.P("@id", docId), Sql.P("@cls", FeeScheduleDocClass),
                    Sql.P("@parent", parent == null ? DefaultFeeScheduleFolder : Convert.ToInt32(parent)),
                    Sql.P("@title", newDesc.Length > 30 ? newDesc.Substring(0, 30) : newDesc), Sql.P("@code", newCode), Sql.P("@from", fromCode));

                // Set-based copies keep every rate exactly as stored (no rounding, no culture formatting).
                var counts = new RateCounts
                {
                    Timekeepers = _session.ExecuteRows(
                        "insert into TkprRate (TKRFeeSch, TKREmp, TKRRate) select @code, TKREmp, TKRRate from TkprRate where TKRFeeSch = @from",
                        Sql.P("@code", newCode), Sql.P("@from", fromCode)),
                    PersonnelTypes = _session.ExecuteRows(
                        "insert into PersTypRate (PTRFeeSch, PTRPrsTyp, PTRRate) select @code, PTRPrsTyp, PTRRate from PersTypRate where PTRFeeSch = @from",
                        Sql.P("@code", newCode), Sql.P("@from", fromCode)),
                    TaskCodes = _session.ExecuteRows(
                        @"insert into TaskCodeRate (TCRFeeSch, TCRTaskCd, TCRHrs, TCRRate, TCRAmt)
                          select @code, TCRTaskCd, TCRHrs, TCRRate, TCRAmt from TaskCodeRate where TCRFeeSch = @from",
                        Sql.P("@code", newCode), Sql.P("@from", fromCode)),
                };

                _session.WriteUtilityLog("JPS Fee Schedule Copy: created fee schedule " + newCode + " (" + newDesc + ") from " + fromCode
                                         + " with " + counts + ".");
                return counts;
            });
        }
    }
}
