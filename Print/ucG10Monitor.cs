using MSTSCLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MHG_BoxPrint.Print
{
    public partial class ucG10Monitor : UserControl
    {
        public ucG10Monitor()
        {
            InitializeComponent();
        }
        private AxMSTSCLib.AxMsRdpClient9NotSafeForScripting rdp;

        public string IP = "192.168.0.3";
        public void LOAD()
        {
            if (tableLayoutPanel4.InvokeRequired)
            {
                tableLayoutPanel4.Invoke(new Action(LOAD));
                return;
            }

            try
            {
                // Hủy RDP cũ
                if (rdp != null)
                {
                    try
                    {
                        rdp.Disconnect();
                        tableLayoutPanel4.Controls.Remove(rdp);
                        rdp.Dispose();
                    }
                    catch { }

                    rdp = null;
                }

                // Tạo ActiveX
                rdp = new AxMSTSCLib.AxMsRdpClient9NotSafeForScripting();

                ((System.ComponentModel.ISupportInitialize)rdp).BeginInit();

                rdp.Dock = DockStyle.Fill;
                rdp.Margin = Padding.Empty;

                tableLayoutPanel4.Controls.Add(rdp);

                ((System.ComponentModel.ISupportInitialize)rdp).EndInit();

                // Tạo Handle ActiveX
                rdp.CreateControl();

                // Cấu hình Remote Desktop
                rdp.Server = IP;
                rdp.UserName = "odroid";

                rdp.DesktopWidth = 1920;
                rdp.DesktopHeight = 1080;

                IMsTscNonScriptable secured =
                    (IMsTscNonScriptable)rdp.GetOcx();

                secured.ClearTextPassword = "f8ea0cb3-9801-4005-937f-3ddf2fce25d0";

                rdp.AdvancedSettings7.SmartSizing = true;
                rdp.AdvancedSettings7.EnableCredSspSupport = true;
                rdp.AdvancedSettings7.AuthenticationLevel = 0;

                rdp.Width = 960;
                rdp.Height = 540;

                // Kết nối
                rdp.Connect();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Remote Desktop initialization failed:\n{ex.Message}",
                    "RDP Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
