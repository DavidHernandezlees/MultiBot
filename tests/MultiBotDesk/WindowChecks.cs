using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiBot.Desk
{
    internal static class WindowChecks
    {
        sealed class Provider : HttpMessageHandler
        {
            readonly string fixtures;
            internal Provider(string fixtures) { this.fixtures = fixtures; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
            {
                string body;
                if (request.RequestUri.Host == "breaking-bet.com")
                    body = "{\"access_token\":\"fixture-token\"}";
                else if (request.RequestUri.Host == "dicts.api.breaking-bet.com")
                    body = File.ReadAllText(Path.Combine(fixtures, "breakingbet-dictionaries.json"));
                else if (request.RequestUri.AbsolutePath.EndsWith("/filter/items"))
                    body = File.ReadAllText(Path.Combine(fixtures, "breakingbet-items.json"))
                        .Replace("1795521600", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                else throw new Exception("Unexpected request " + request.RequestUri.Host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        }

        static void Until(Func<bool> condition, string name)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) {
                if (clock.ElapsedMilliseconds > 18000) throw new Exception("Timed out: " + name);
                Application.DoEvents();
                Thread.Sleep(20);
            }
            Console.WriteLine("PASS " + name);
        }

        static T Control<T>(Form window, string name) where T : Control
        {
            return (T)window.Controls.Find(name, true).Single();
        }

        [STAThread]
        static int Main(string[] args)
        {
            Environment.SetEnvironmentVariable("BREAKINGBET_DATA_DIR", args[0]);
            Environment.SetEnvironmentVariable("BREAKINGBET_ACCESS_KEY_ID", null);
            Environment.SetEnvironmentVariable("BREAKINGBET_SECRET_KEY", null);
            Environment.SetEnvironmentVariable("BREAKINGBET_FILTER_FILE", null);
            Environment.SetEnvironmentVariable("BREAKINGBET_MODE", "prematch");
            Environment.SetEnvironmentVariable("BREAKINGBET_POLL_SECONDS", "2");
            Environment.SetEnvironmentVariable("BREAKINGBET_MAX_AGE_SECONDS", "300");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Exception failure = null;
            EventHandler begin = null;
            begin = delegate {
                Application.Idle -= begin;
                try { Exercise(args); }
                catch (Exception ex) { failure = ex; }
                finally { Application.ExitThread(); }
            };
            Application.Idle += begin;
            Application.Run();
            if (failure == null) return 0;
            Console.Error.WriteLine(failure);
            return 1;
        }

        static void Exercise(string[] args)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            Environment.SetEnvironmentVariable("BREAKINGBET_PORT", port.ToString());
            try {
                using (var form = new MainWindow(() => new Provider(args[1]))) {
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    var start = Control<Button>(form, "StartStop");
                    var status = Control<Label>(form, "ServerStatus");
                    var feedback = Control<Label>(form, "Feedback");
                    var table = Control<DataGridView>(form, "TipHistory");
                    var secret = Control<TextBox>(form, "Secret");
                    start.PerformClick();
                    Until(() => feedback.Text.Contains("access key ID") && status.Text == "Stopped", "missing credentials keep server stopped");
                    Control<TextBox>(form, "KeyId").Text = "fixture";
                    secret.Text = "example-secret";
                    if (!secret.UseSystemPasswordChar) throw new Exception("Secret field is unmasked");
                    start.PerformClick();
                    Until(() => status.Text == "Stopped" && feedback.Text.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0,
                        "occupied port returns an actionable error");
                    probe.Stop();
                    start.PerformClick();
                    Until(() => status.Text == "Listening", "window starts localhost server");
                    using (var desktop = new TcpClient()) {
                        desktop.Connect(IPAddress.Loopback, port);
                        Until(() => desktop.Available > 14, "connected desktop receives a tip over TCP");
                        var data = desktop.GetStream();
                        if (data.ReadByte() != 0x86 || data.ReadByte() != 0x7b) throw new Exception("Invalid packet header");
                    }
                    Until(() => table.Rows.Count == 1 && Convert.ToString(table.Rows[0].Cells[2].Value) == "Bet365" &&
                        Convert.ToString(table.Rows[0].Cells[5].Value) == "2.15", "history shows bookmaker and decimal odds");
                    start.PerformClick();
                    Until(() => status.Text == "Stopped" && start.Enabled, "stop returns app to idle");
                    start.PerformClick();
                    Until(() => status.Text != "Starting", "second start completes");
                    if (status.Text != "Listening") throw new Exception("Restart state " + status.Text + " / " + feedback.Text + " / " + string.Join(" | ", Control<ListBox>(form, "Activity").Items.Cast<string>()));
                    Console.WriteLine("PASS restart reuses the same port");
                    form.Close();
                    Until(() => form.IsDisposed, "closing the window stops the server");
                }
                using (var reopened = new MainWindow()) {
                    reopened.Opacity = 0;
                    reopened.ShowInTaskbar = false;
                    reopened.Show();
                    var table = Control<DataGridView>(reopened, "TipHistory");
                    Until(() => table.Rows.Count == 1 && Control<TextBox>(reopened, "Secret").Text == "", "history survives restart without saved credentials");
                    reopened.Close();
                }
            } finally { probe.Stop(); }
        }
    }
}
