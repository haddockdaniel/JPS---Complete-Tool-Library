using System;
using System.IO;
using System.Reflection;

namespace Jps.BillRenderer
{
    /// <summary>
    /// Renders archived Juris bills to PDF in its own process, through Juris's own JurisWrapper.dll (shipped next to this exe,
    /// 4.32 set, backwards compatible). Keeps the memory-heavy rendering out of the Tool Library, so a crash here can't take
    /// the app down.
    ///
    ///   JPS Bill Renderer.exe --company &lt;code&gt;
    /// Prints "READY" once logged on (or "LOGON-FAILED\t&lt;message&gt;" and exits 3). Then reads lines "&lt;bill number&gt;\t&lt;pdf path&gt;"
    /// from stdin and answers each with "OK" or "FAIL\t&lt;message&gt;". An empty line or end of input ends the run.
    /// </summary>
    internal static class Program
    {
        private static readonly string Here = AppDomain.CurrentDomain.BaseDirectory;

        [STAThread]
        private static int Main(string[] args)
        {
            var company = Arg(args, "--company");
            if (company == null || !File.Exists(Path.Combine(Here, "JurisWrapper.dll")))
            {
                Console.WriteLine("LOGON-FAILED\tThe Juris bill printing files are missing from the JPS Tool Library folder. Reinstall the tool library.");
                return 2;
            }
            Directory.SetCurrentDirectory(Here);
            return Run(company);
        }

        private static int Run(string company)
        {
            dynamic wrapper;
            try
            {
                wrapper = Activator.CreateInstance(Assembly.LoadFrom(Path.Combine(Here, "JurisWrapper.dll")).GetType("JurisWrapper.Wrapper", true));
                if (!(bool)wrapper.LogonCompany(company))
                {
                    Exception why = wrapper.WrapperException;
                    Say("LOGON-FAILED\t" + Clean(why?.Message ?? "Juris didn't accept the company logon."));
                    return 3;
                }
            }
            catch (Exception ex)
            {
                Say("LOGON-FAILED\t" + Clean((ex.InnerException ?? ex).Message));
                return 3;
            }

            Say("READY");
            try
            {
                string line;
                while (!string.IsNullOrEmpty(line = Console.ReadLine()))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2 || !int.TryParse(parts[0], out var bill))
                    {
                        Say("FAIL\tBad request.");
                        continue;
                    }
                    var path = parts[1];
                    try
                    {
                        wrapper.WrapperException = null;
                        var result = (int)wrapper.GetBillImage(bill, path);
                        Exception error = wrapper.WrapperException;
                        if (result == 0 && File.Exists(path) && new FileInfo(path).Length > 0)
                            Say("OK");
                        else
                            Say("FAIL\t" + Clean(error?.Message ?? (result == 0 ? "Juris returned no PDF for this invoice." : "Juris couldn't load the archived invoice (code " + result + ").")));
                    }
                    catch (Exception ex)
                    {
                        Say("FAIL\t" + Clean((ex.InnerException ?? ex).Message));
                    }
                    finally
                    {
                        // Juris/ActiveReports build large report objects per invoice; release them before the next one.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                }
                return 0;
            }
            finally
            {
                try { ((IDisposable)wrapper).Dispose(); } catch (Exception) { /* closing down anyway */ }
            }
        }

        private static void Say(string s)
        {
            Console.WriteLine(s);
            Console.Out.Flush();
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static string Arg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
