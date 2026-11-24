using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiBot.Desk
{
    internal sealed partial class MainWindow : Form
    {
        readonly string directory;
        readonly Func<HttpMessageHandler> createHandler;
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 5000 };
        readonly TextBox key = new TextBox { Name = "KeyId" };
        readonly TextBox secret = new TextBox { Name = "Secret", UseSystemPasswordChar = true };
        readonly ComboBox mode = new ComboBox { Name = "FeedMode", DropDownStyle = ComboBoxStyle.DropDownList };
        readonly NumericUpDown port = new NumericUpDown { Name = "Port", Minimum = 1, Maximum = 65535 };
        readonly Button start = new Button { Name = "StartStop", Text = "Start server" };
        readonly Label status = new Label { Name = "ServerStatus", Text = "Stopped", AutoSize = true };
        readonly Label feedback = new Label { Name = "Feedback", AutoSize = true, MaximumSize = new Size(210, 0) };
        readonly Label count = new Label { AutoSize = true, Text = "Latest 50 tips" };
        readonly Label empty = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        readonly DataGridView tips = new DataGridView { Name = "TipHistory" };
        readonly ListBox log = new ListBox { Name = "Activity", HorizontalScrollbar = true, IntegralHeight = false };
        readonly List<string[]> previous = new List<string[]>();
        CancellationTokenSource session;
        bool reading, closing;

        internal MainWindow(Func<HttpMessageHandler> handler = null)
        {
            createHandler = handler;
            directory = DesktopApp.Setting("DATA_DIR", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiAiRobot", "BreakingBet"));
            BuildLayout();
            mode.Items.AddRange(new object[] { "Pre-match", "Live" });
            mode.SelectedIndex = string.Equals(DesktopApp.Setting("MODE", ""), "live", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            key.Text = DesktopApp.Setting("ACCESS_KEY_ID", "");
            secret.Text = DesktopApp.Setting("SECRET_KEY", "");
            try { port.Value = DesktopApp.Number("PORT", 14010, 1, 65535); }
            catch (ArgumentException ex) { port.Value = 14010; feedback.Text = ex.Message; }
            start.Click += async (sender, e) => {
                if (session == null) await StartServer();
                else StopServer();
            };
            Shown += async (sender, e) => { await ReadHistory(); if (!closing) refresh.Start(); };
            refresh.Tick += async (sender, e) => await ReadHistory();
            FormClosing += (sender, e) => {
                closing = true;
                refresh.Stop();
                if (session != null) { e.Cancel = true; StopServer(); }
            };
            FormClosed += (sender, e) => refresh.Dispose();
        }

        async Task StartServer()
        {
            if (closing || session != null) return;
            string id = key.Text.Trim(), credential = secret.Text.Trim();
            if (id == "" || credential == "") {
                feedback.Text = "Enter your access key ID and secret key.";
                (id == "" ? key : secret).Focus();
                return;
            }
            var active = new CancellationTokenSource();
            session = active;
            var selected = mode.SelectedIndex == 1 ? "live" : "prematch";
            var localPort = (int)port.Value;
            SetState("Starting", false);
            feedback.Text = "";
            start.Text = "Stop server";
            var ui = SynchronizationContext.Current;
            Action<string> report = line => {
                string safe = line.Replace(credential, "[hidden]");
                ui.Post(ignored => { if (session == active && !closing) AddLog(safe); }, null);
            };
            Action listening = () => ui.Post(ignored => {
                if (session == active && !active.IsCancellationRequested) {
                    SetState("Listening", false);
                    feedback.Text = "Listening on 127.0.0.1:" + localPort;
                }
            }, null);
            try {
                int result = await Task.Run(() => DesktopApp.Run(directory, selected, id, credential, localPort,
                    active.Token, report, listening, createHandler == null ? null : createHandler()));
                if (result != 0 && !active.IsCancellationRequested)
                    throw new InvalidOperationException("Check your BreakingBet credentials and feed subscription. See server activity for details.");
                SetState("Stopped", true);
                feedback.Text = "";
                AddLog("Server stopped.");
            } catch (Exception ex) {
                SetState("Stopped", true);
                var socket = ex as SocketException;
                feedback.Text = socket != null && socket.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "This port is already in use. Stop the other server or choose another port."
                    : ex.Message.Replace(credential, "[hidden]");
                AddLog(feedback.Text);
            } finally {
                session = null;
                active.Dispose();
            }
            if (closing) Close();
            else await ReadHistory();
        }

        void SetState(string text, bool idle)
        {
            status.Text = text;
            status.ForeColor = text == "Listening" ? Color.LightGreen : Color.WhiteSmoke;
            key.Enabled = secret.Enabled = port.Enabled = mode.Enabled = idle;
            start.Enabled = true;
            if (idle) start.Text = "Start server";
        }

        void StopServer()
        {
            if (session == null || session.IsCancellationRequested) return;
            status.Text = "Stopping";
            start.Enabled = false;
            session.Cancel();
        }

        void AddLog(string message)
        {
            if (log.Items.Count >= 180) log.Items.RemoveAt(0);
            log.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + message);
            log.TopIndex = log.Items.Count - 1;
        }

        async Task ReadHistory()
        {
            if (reading || closing) return;
            reading = true;
            try {
                var rows = await Task.Run(() => DesktopApp.History(directory));
                if (closing) return;
                count.Text = rows.Count + (rows.Count == 1 ? " saved tip" : " saved tips") + "  /  latest 50";
                empty.Text = "No saved tips yet.\nStart the server to collect your first tip.";
                empty.Visible = rows.Count == 0;
                bool changed = previous.Count != rows.Count || previous.Where((r, i) => !r.SequenceEqual(rows[i])).Any();
                if (!changed) return;
                int top = tips.FirstDisplayedScrollingRowIndex;
                string selected = tips.CurrentRow == null ? null : tips.CurrentRow.Tag as string;
                tips.Rows.Clear();
                foreach (var row in rows) {
                    int n = tips.Rows.Add(row[0], row[1], row[8], row[3], row[4], row[5], row[6], row[7]);
                    string identity = row[1] + "/" + row[2];
                    tips.Rows[n].Tag = identity;
                    if (identity == selected) tips.CurrentCell = tips.Rows[n].Cells[0];
                }
                if (top > 0 && top < tips.Rows.Count) tips.FirstDisplayedScrollingRowIndex = top;
                previous.Clear();
                previous.AddRange(rows);
            } catch (Exception ex) {
                if (!closing) {
                    count.Text = "History unavailable";
                    empty.Text = "Could not read saved tips.\n" + ex.Message;
                    empty.Visible = true;
                }
            } finally { reading = false; }
        }
    }
}
