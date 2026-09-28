using System;
using System.Drawing;
using System.Windows.Forms;

namespace AuroraAssistantApi
{
    /// <summary>Saved as Patches/AuroraAssistantApi/settings.json.</summary>
    public class Settings
    {
        public const int DefaultPort = 47100;

        /// <summary>Listen on all network interfaces (LAN, Tailscale) instead of only this computer.</summary>
        public bool Lan { get; set; }

        public int Port { get; set; } = DefaultPort;

        /// <summary>Optional command run when Aurora starts, e.g. to launch the assistant bridge.</summary>
        public string LaunchCommand { get; set; } = "";
    }

    /// <summary>Dialog behind AuroraPatch's "Change settings" button.</summary>
    internal class SettingsForm : Form
    {
        private readonly RadioButton rdoLocal = new RadioButton { Text = "This computer only (127.0.0.1)", AutoSize = true };
        private readonly RadioButton rdoLan = new RadioButton { Text = "LAN and Tailscale (all network interfaces)", AutoSize = true };
        private readonly NumericUpDown numPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Width = 90 };
        private readonly TextBox txtLaunch = new TextBox { Width = 440 };

        public SettingsForm(Settings settings, string version)
        {
            Text = "Aurora Assistant API " + version;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            layout.Controls.Add(new Label { Text = "Accept API connections from:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            layout.Controls.Add(rdoLocal);
            layout.Controls.Add(rdoLan);
            layout.Controls.Add(new Label
            {
                Text = "The API has no authentication: anyone who can reach the port can read and operate the game.",
                AutoSize = true, MaximumSize = new Size(460, 0), ForeColor = Color.DimGray,
            });

            var portRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            portRow.Controls.Add(new Label { Text = "Port:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) });
            portRow.Controls.Add(numPort);
            layout.Controls.Add(portRow);

            layout.Controls.Add(new Label { Text = "Command to run when Aurora starts (optional):", AutoSize = true, Margin = new Padding(0, 10, 0, 0) });
            layout.Controls.Add(txtLaunch);
            layout.Controls.Add(new Label
            {
                Text = "Runs via cmd.exe. Windows example: aurora-assistant\r\nWine/Proton example: start /unix /home/me/.local/bin/aurora-assistant",
                AutoSize = true, ForeColor = Color.DimGray,
            });

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0), Width = 460 };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);
            layout.Controls.Add(new Label { Text = "Settings take effect when Aurora starts (restart Aurora if it is already running).", AutoSize = true, ForeColor = Color.DimGray });

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;

            rdoLan.Checked = settings.Lan;
            rdoLocal.Checked = !settings.Lan;
            numPort.Value = Math.Max(numPort.Minimum, Math.Min(numPort.Maximum, settings.Port));
            txtLaunch.Text = settings.LaunchCommand ?? "";
        }

        public Settings Result => new Settings
        {
            Lan = rdoLan.Checked,
            Port = (int)numPort.Value,
            LaunchCommand = txtLaunch.Text.Trim(),
        };
    }
}
