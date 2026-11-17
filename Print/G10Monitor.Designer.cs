namespace MHG_BoxPrint.Print
{
    partial class G10Monitor
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.ucG10Monitor1 = new ucG10Monitor();
            this.SuspendLayout();
            // 
            // ucG10Monitor1
            // 
            this.ucG10Monitor1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ucG10Monitor1.Location = new System.Drawing.Point(0, 0);
            this.ucG10Monitor1.Name = "ucG10Monitor1";
            this.ucG10Monitor1.Size = new System.Drawing.Size(800, 450);
            this.ucG10Monitor1.TabIndex = 0;
            // 
            // G10Monitor
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.ucG10Monitor1);
            this.Name = "G10Monitor";
            this.Text = "G10Monitor";
            this.Load += new System.EventHandler(this.G10Monitor_Load);
            this.ResumeLayout(false);

        }

        #endregion

        private ucG10Monitor ucG10Monitor1;
    }
}