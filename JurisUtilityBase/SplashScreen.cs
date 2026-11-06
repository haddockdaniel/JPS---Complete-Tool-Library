using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JurisUtilityBase
{
    public partial class SplashScreen : Form
    {
        public string Status
        {
            get { return labelStatus.Text; }
            set
            {
                labelStatus.Text = value; 
                Refresh();
            }
        }

        public SplashScreen()
        {
            InitializeComponent();
        }

        private void SplashScreen_Load(object sender, EventArgs e)
        { string VerLabel = "";
           
            if (System.Deployment.Application.ApplicationDeployment.IsNetworkDeployed)
            {
                Version ver = System.Deployment.Application.ApplicationDeployment.CurrentDeployment.CurrentVersion;
                VerLabel= string.Format("Version: {0}.{1}.{2}.{3}", ver.Major, ver.Minor, ver.Build, ver.Revision);
            }
            else
            {
                var ver2 = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                VerLabel = string.Format("Version: {0}.{1}.{2}.{3}", ver2.Major, ver2.Minor, ver2.Build, ver2.Revision);
            }
            
            this.labelVersion.Text = VerLabel;
            this.labelCopyright.Text = @"Copyright © 1996-" + DateTime.Now.Year;
            this.labelAppName.Text = "Moore - Task Cd";
            this.labelCompany.Text = Application.CompanyName;
            this.Refresh();
        }

        private void labelCopyright_Click(object sender, EventArgs e)
        {

        }
    }
}
