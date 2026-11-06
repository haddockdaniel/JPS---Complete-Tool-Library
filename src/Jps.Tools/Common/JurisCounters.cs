using System;
using System.Linq;
using Jps.Core.Data;

namespace Jps.Tools.Common
{
    /// <summary>
    /// Numbers from Juris' own SysParam counters (LastBatchTrust, LastBatchVoucher, LastSysNbrVoucher, LastSysNbrDocTree...),
    /// claimed inside the caller's transaction. The counter row is read with UPDLOCK/HOLDLOCK and every table that holds
    /// numbers from that counter is read the same way, so Juris and other users wait until commit rather than issue the
    /// same number. The numbers handed out are above both the counter and every number already in those tables (older
    /// tools inserted max+1 without advancing the counter), and the counter only ever moves forward.
    /// Call only inside Session.InTransaction.
    /// </summary>
    public static class JurisCounters
    {
        /// <summary>
        /// Reserves <paramref name="count"/> numbers from SysParam counter <paramref name="counter"/> and returns the base:
        /// the numbers are base+1 .. base+count. <paramref name="inUse"/> lists the "Table.Column"s that already hold numbers
        /// from this counter (fixed Juris names from the calling code, never user input).
        /// </summary>
        public static int Claim(JurisSession session, string counter, int count, string what, params string[] inUse)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var maxima = string.Concat(inUse.Select(tc =>
            {
                var parts = tc.Split('.');
                if (parts.Length != 2 || parts.Any(p => p.Length == 0 || !p.All(char.IsLetterOrDigit)))
                    throw new ArgumentException("Expected Table.Column, got " + tc, nameof(inUse));
                return "\nselect @max = isnull(max(" + parts[1] + "), 0) from " + parts[0] + " with (updlock, holdlock)"
                       + "\nif @max > @base set @base = @max";
            }));
            var value = session.Scalar(
                @"declare @counter int, @max int, @base int
select @counter = SpNbrValue from SysParam with (updlock, holdlock, rowlock) where SpName = @name
if @counter is null
begin
    raiserror(@missing, 16, 1)
    return
end
set @base = @counter" + maxima + @"
update SysParam set SpNbrValue = @base + @count where SpName = @name
select @base",
                Sql.P("@name", counter), Sql.P("@count", count),
                Sql.P("@missing", "Juris has no next-number setting for " + what + " in this company, so no new " + what +
                                  " can be issued and nothing was changed. Ask your Juris support contact to check the company's system settings."));
            if (value == null) throw new InvalidOperationException("Juris did not hand out a new " + what + ", so nothing was changed. Please try again.");
            return Convert.ToInt32(value);
        }

        /// <summary>The next single number from <paramref name="counter"/> (see <see cref="Claim"/>).</summary>
        public static int Next(JurisSession session, string counter, string what, params string[] inUse) =>
            Claim(session, counter, 1, what, inUse) + 1;

        /// <summary>The next DocumentTree id (SysParam LastSysNbrDocTree), for a batch-tree or Tables-view entry.</summary>
        public static int NextDocTreeId(JurisSession session) =>
            Next(session, "LastSysNbrDocTree", "Juris explorer entry number", "DocumentTree.DTDocID");
    }
}
