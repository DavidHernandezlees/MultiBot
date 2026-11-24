using System.Drawing;
using System.Windows.Forms;

namespace MultiBot.Desk
{
    internal sealed partial class MainWindow
    {
        static readonly Color Ink = Color.FromArgb(23, 43, 50);
        static readonly Color Teal = Color.FromArgb(12, 107, 93);
        static readonly Color Muted = Color.FromArgb(82, 99, 102);
        static readonly Color Paper = Color.FromArgb(246, 248, 246);

        static Label Heading(string text, float size, Color color)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = color,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size, FontStyle.Bold), Margin = Padding.Empty };
        }

        static void Field(FlowLayoutPanel panel, string caption, Control input)
        {
            panel.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Ink, Margin = new Padding(0, 13, 0, 4) });
            input.Width = 201;
            input.AccessibleName = caption;
            input.Margin = Padding.Empty;
            panel.Controls.Add(input);
        }

        void BuildLayout()
        {
            Text = "MultiBot | BreakingBet";
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1160, 680);
            MinimumSize = new Size(820, 565);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Paper;
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Ink, ColumnCount = 3, RowCount = 1,
                Padding = new Padding(22, 0, 24, 0), Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = Heading("MultiBot", 21, Color.White);
            title.Anchor = AnchorStyles.Left;
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(new Label { Text = "BreakingBet tip server", AutoSize = true, Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(194, 218, 214), Margin = new Padding(16, 4, 0, 0) }, 1, 0);
            status.Anchor = AnchorStyles.Right;
            status.ForeColor = Color.WhiteSmoke;
            header.Controls.Add(status, 2, 0);
            shell.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 247));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, WrapContents = false,
                FlowDirection = FlowDirection.TopDown, AutoScroll = true, Margin = Padding.Empty, Padding = new Padding(20, 22, 12, 12) };
            sidebar.Controls.Add(Heading("Connection", 14, Ink));
            Field(sidebar, "Feed", mode);
            Field(sidebar, "Access key ID", key);
            Field(sidebar, "Secret key", secret);
            Field(sidebar, "Local port", port);
            start.BackColor = Teal;
            start.ForeColor = Color.White;
            start.FlatStyle = FlatStyle.Flat;
            start.FlatAppearance.BorderSize = 0;
            start.Size = new Size(201, 39);
            start.Margin = new Padding(0, 22, 0, 8);
            sidebar.Controls.Add(start);
            feedback.ForeColor = Muted;
            feedback.Margin = new Padding(0, 4, 0, 6);
            sidebar.Controls.Add(feedback);
            body.Controls.Add(sidebar, 0, 0);

            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1,
                Padding = new Padding(19, 16, 18, 16), Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
            var historyHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            historyHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            historyHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var historyTitle = Heading("Tip history", 16, Ink);
            historyTitle.Anchor = AnchorStyles.Left;
            historyHeader.Controls.Add(historyTitle, 0, 0);
            count.ForeColor = Muted;
            count.Anchor = AnchorStyles.Right;
            historyHeader.Controls.Add(count, 1, 0);
            content.Controls.Add(historyHeader, 0, 0);
            var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
            tips.Dock = DockStyle.Fill;
            tips.ReadOnly = true;
            tips.AllowUserToAddRows = tips.AllowUserToDeleteRows = tips.AllowUserToResizeRows = false;
            tips.RowHeadersVisible = tips.MultiSelect = false;
            tips.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tips.BackgroundColor = Color.White;
            tips.BorderStyle = BorderStyle.None;
            tips.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            tips.GridColor = Color.FromArgb(226, 233, 231);
            tips.EnableHeadersVisualStyles = false;
            tips.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            tips.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(225, 236, 232);
            tips.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            tips.ColumnHeadersHeight = 32;
            tips.RowTemplate.Height = 32;
            tips.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 230, 221);
            tips.DefaultCellStyle.SelectionForeColor = Ink;
            string[] headers = { "Seen (UTC)", "Feed", "Bookmaker", "Event", "Selection", "Odds", "Edge %", "Status" };
            int[] widths = { 145, 71, 88, 145, 110, 54, 58, 100 };
            for (int i = 0; i < headers.Length; i++) {
                var column = new DataGridViewTextBoxColumn { HeaderText = headers[i], Width = widths[i],
                    MinimumWidth = widths[i], SortMode = DataGridViewColumnSortMode.NotSortable };
                if (i == 3 || i == 4) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                tips.Columns.Add(column);
            }
            host.Controls.Add(tips);
            empty.BackColor = Color.White;
            empty.ForeColor = Muted;
            empty.Text = "Loading saved tips…";
            host.Controls.Add(empty);
            empty.BringToFront();
            content.Controls.Add(host, 0, 1);
            var activity = Heading("Server activity", 13, Ink);
            activity.Anchor = AnchorStyles.Left;
            content.Controls.Add(activity, 0, 2);
            log.Dock = DockStyle.Fill;
            log.Margin = Padding.Empty;
            log.BackColor = Ink;
            log.ForeColor = Color.FromArgb(223, 237, 233);
            log.BorderStyle = BorderStyle.None;
            content.Controls.Add(log, 0, 3);
            body.Controls.Add(content, 1, 0);
            shell.Controls.Add(body, 0, 1);
            Controls.Add(shell);
        }
    }
}
