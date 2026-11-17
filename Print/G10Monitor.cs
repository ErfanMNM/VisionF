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
    public partial class G10Monitor : Form
    {
        public G10Monitor()
        {
            InitializeComponent();
        }
        public string IP = "";
        private void G10Monitor_Load(object sender, EventArgs e)
        {
            ucG10Monitor1.IP = IP;
            ucG10Monitor1.LOAD();
        }
    }
}
