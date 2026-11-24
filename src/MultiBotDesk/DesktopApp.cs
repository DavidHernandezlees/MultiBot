using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using MultiAiRobot.Feed;

namespace MultiBot.Desk
{
    internal static class DesktopApp
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.Run(new MainWindow());
        }

        internal static string Setting(string name, string fallback)
        {
            return Environment.GetEnvironmentVariable("BREAKINGBET_" + name) ?? fallback;
        }

        internal static int Number(string name, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(Setting(name, fallback.ToString()), out value) || value < min || value > max)
                throw new ArgumentException("BREAKINGBET_" + name + " must be between " + min + " and " + max + ".");
            return value;
        }

        internal static int Run(string directory, string mode, string id, string secret, int port, CancellationToken stop,
            Action<string> log, Action listening, System.Net.Http.HttpMessageHandler handler)
        {
            int poll = Number("POLL_SECONDS", 5, 2, 3600);
            int maxAge = Number("MAX_AGE_SECONDS", mode == "live" ? 30 : 300, 1, 86400);
            var filter = Setting("FILTER_FILE", null);
            var settings = string.IsNullOrEmpty(filter) ? null : File.ReadAllText(filter);
            Directory.CreateDirectory(directory);
            var api = new BreakingBetClient(mode, id, secret, settings, Path.Combine(directory, "dictionaries.en.json"), () => DateTimeOffset.UtcNow);
            using (var store = new Store(Path.Combine(directory, "tips.sqlite3")))
                return Program.RunServer(store, mode, port, poll, maxAge, api, api.Poll, stop, log, log, listening, handler);
        }

        internal static List<string[]> History(string directory)
        {
            var rows = new List<string[]>();
            var db = Path.Combine(directory, "tips.sqlite3");
            if (!File.Exists(db)) return rows;
            Dictionary<string,object> books = null;
            var cache = Path.Combine(directory, "dictionaries.en.json");
            if (File.Exists(cache)) {
                try {
                    var data = Feed.Serializer().DeserializeObject(File.ReadAllText(cache)) as Dictionary<string,object>;
                    object values;
                    if (data != null && data.TryGetValue("bookmakers", out values)) books = values as Dictionary<string,object>;
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
                  catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
            using (var store = new Store(db, false)) {
                store.History(row => {
                    DateTimeOffset seen;
                    if (DateTimeOffset.TryParse(row[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out seen))
                        row[0] = seen.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var book = Feed.Field(books, row[8]);
                    row[8] = book.Length == 0 ? row[8] : book;
                    rows.Add(row);
                }, true);
            }
            return rows;
        }
    }
}
