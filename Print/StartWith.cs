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
    public partial class StartWith : Form
    {
        public StartWith()
        {
            InitializeComponent();
            AcceptButton = button1;
            this.DialogResult = DialogResult.Cancel;
        }

        
        public int Index => (int)numericUpDown1.Value;

        private void button1_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.OK; 
            this.Close();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
