using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MHG_BoxPrint.Print
{
    public class InkLevelControl : Control
    {
        private double _value = 100;

        [Category("Ink")]
        public double Value
        {
            get => _value;
            set
            {
                _value = Math.Max(0, Math.Min(100, value));
                Invalidate();
            }
        }

        public Color InkColor { get; set; } = Color.Black;
        public bool ShowText { get; set; } = true;

        public InkLevelControl()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(150, 25);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // Khung
            g.DrawRectangle(Pens.Gray, rect);

            // Fill mực
            float fillWidth = (int)((Width - 2) * (_value / 100.0));

            using (Brush b = new SolidBrush(GetColor()))
            {
                g.FillRectangle(b, 1, 1, fillWidth, Height - 2);
            }

            // Text %
            if (ShowText)
            {
                string text = $"{_value}%";
                var size = g.MeasureString(text, Font);

                g.DrawString(text, Font, Brushes.Black,
                    (Width - size.Width) / 2,
                    (Height - size.Height) / 2);
            }
        }

        private Color GetColor()
        {
            if (_value > 60) return InkColor;
            if (_value > 30) return Color.Orange;
            return Color.Red;
        }
    }
}
