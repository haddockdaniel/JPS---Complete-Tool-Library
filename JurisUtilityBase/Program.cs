using System;
using System.Threading;
using System.Windows.Forms;

namespace JurisUtilityBase
{
    static class Program
    {
        private static UtilityBaseMain _mainForm;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var splashScreen = new SplashScreen {Status = "Creating Server Components ..."};
            splashScreen.Show();
            Application.DoEvents();
            Thread.Sleep(1000);
            Application.DoEvents();
            splashScreen.Status = "Loading Companies ...";
            _mainForm = new UtilityBaseMain();
            _mainForm.LoadCompanies();
            Thread.Sleep(500);
            Application.DoEvents();
            splashScreen.Hide();
            splashScreen.Dispose();
            splashScreen = null;
            Application.Run(_mainForm);
        }
    }
}
