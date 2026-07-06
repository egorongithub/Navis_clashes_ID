using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClashIdFixer.UI
{
    /// <summary>
    /// Small always-on-top progress window. Navisworks plugins run on the UI
    /// thread and the model API is not thread-safe, so instead of a background
    /// worker the long loops pump pending window messages via DoEvents on every
    /// update - enough to keep the bar moving and the app painting instead of
    /// the "black screen" effect.
    /// </summary>
    public sealed class ProgressForm : Form
    {
        private readonly Label _label;
        private readonly ProgressBar _bar;

        public ProgressForm(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 90);

            _label = new Label
            {
                Left = 15,
                Top = 12,
                Width = 430,
                Height = 32,
                Text = ""
            };
            _bar = new ProgressBar
            {
                Left = 15,
                Top = 48,
                Width = 430,
                Height = 24,
                Minimum = 0,
                Maximum = 100
            };

            Controls.Add(_label);
            Controls.Add(_bar);
        }

        /// <summary>For steps of unknown length (recomputing tests etc.).</summary>
        public void SetMarquee(string text)
        {
            _label.Text = text;
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
            Refresh();
            Application.DoEvents();
        }

        public void SetProgress(string text, int current, int total)
        {
            _label.Text = text;
            _bar.Style = ProgressBarStyle.Blocks;
            _bar.Maximum = Math.Max(1, total);
            _bar.Value = Math.Min(Math.Max(0, current), _bar.Maximum);
            Application.DoEvents();
        }
    }
}
