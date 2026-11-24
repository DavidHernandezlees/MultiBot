using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Map = System.Collections.Generic.Dictionary<string,object>;

namespace MultiAiRobot.Feed
{
    internal static class BreakingBetChecks
    {
        static void Check(bool value, string label)
        {
            if (!value) throw new Exception(label);
            Console.WriteLine("PASS BreakingBet " + label);
        }

        sealed class Handler : HttpMessageHandler
        {
            internal readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> Steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();

            internal void Expect(string method, string url, string auth, string body, int code, string response)
            {
                Steps.Enqueue(request => {
                    if (request.Method.Method != method || request.RequestUri.AbsoluteUri != url ||
                        request.Headers.Authorization.ToString() != auth ||
                        (request.Content == null ? null : request.Content.ReadAsStringAsync().Result) != body)
                        throw new Exception("Request did not match expected " + method + " " + url);
                    return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(response) };
                });
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
            {
                return Task.FromResult(Steps.Dequeue()(request));
            }
        }

        internal static void Run(string temp, string fixtures)
        {
            var js = Feed.Serializer();
            var dict = File.ReadAllText(Path.Combine(fixtures, "breakingbet-dictionaries.json"));
            var sample = File.ReadAllText(Path.Combine(fixtures, "breakingbet-items.json"));
            var labels = new BreakingBetFeed(dict);
            var data = (Map)js.DeserializeObject(sample);
            var now = DateTimeOffset.UtcNow;
            long epoch = now.ToUnixTimeSeconds();
            data["timestamp"] = epoch;
            var ev = (Map)((object[])data["events"])[0];
            var item = (Map)((object[])data["items"])[0];
            var odd = (Map)((object[])item["odds"])[0];
            ev["start"] = now.AddHours(1).ToString("o");
            Func<string,Quote> parse = mode => labels.Parse(js.Serialize(data), mode, epoch, 30).Single();
            var q = parse("prematch");
            Check(q.CanDeliver && q.Status == "display only" && q.Updated == epoch && q.BookId == "39" &&
                q.Tip.bookmaker == "Bet365" && q.Tip.sport == "Soccer" && q.Tip.period == "1 Half" &&
                q.Tip.outcome == "Total Over 2.5" && q.Tip.percent == 3.75m && q.Tip.odds == 2.15 && q.Tip.direct_link == "",
                "joins event, bookmaker, outcome and groups; keeps crumbs out of the betslip");
            using (var store = new Store(Path.Combine(temp, "breaking.sqlite3")))
            using (var output = new MemoryStream()) {
                store.Save(q, "a");
                Check(Program.Deliver(store, new List<Quote> { q }, output, "a", 30) == 1, "SQLite to desktop stream");
                File.WriteAllBytes(Path.Combine(temp, "breaking-prematch.packet"), output.ToArray());
            }
            using (var store = new Store(Path.Combine(temp, "breaking.sqlite3"))) {
                Check(!store.Claim(q, "b"), "restart does not replay a tip");
            }
            Check(!parse("live").CanDeliver, "mode mismatch");
            ev["live"] = true;
            var indexed = (Map)ev["sub_events"];
            ev["sub_events"] = indexed.Values.ToArray();
            var live = parse("live");
            Check(live.CanDeliver && live.Tip.isLive, "array sub-events and live mode");
            File.WriteAllBytes(Path.Combine(temp, "breaking-live.packet"), Feed.Packet(live.Tip));
            data["masked"] = true; Check(!parse("live").CanDeliver, "masked feed");
            data["masked"] = false;
            odd["updated"] = now.AddMinutes(-5).ToString("o"); Check(!parse("live").CanDeliver, "old odds");
            odd.Remove("updated"); data["timestamp"] = epoch - 31; Check(!parse("live").CanDeliver, "old snapshot");
            data["timestamp"] = epoch; odd["sub_event_id"] = "elsewhere"; Check(!parse("live").CanDeliver, "unmatched odds");
            odd["sub_event_id"] = "book-event-A";
            item["odds"] = new[] { odd, odd }; Check(!parse("live").CanDeliver, "ambiguous odds");
            item["odds"] = new[] { odd };
            odd["type_id"] = -1; Check(!parse("live").CanDeliver, "unknown market");

            var handler = new Handler();
            string cache = Path.Combine(temp, "dictionaries.en.json");
            const string auth = "https://breaking-bet.com/access_keys/demo/access_tokens";
            const string filter = "https://valuebets.live.api.breaking-bet.com/v1/users/demo/filter";
            const string dicts = "https://dicts.api.breaking-bet.com/v1/en";
            const string service = "{\"service\":\"valuebets_live\"}";
            using (var http = new HttpClient(handler)) {
                var api = new BreakingBetClient("live", "demo", "secret", null, cache, () => now);
                handler.Expect("POST", auth, "ApiKey secret", service, 200, "{\"access_token\":\"one\"}");
                handler.Expect("GET", dicts, "Bearer one", null, 200, dict);
                handler.Expect("GET", filter + "/items", "Bearer one", null, 404, "{}");
                using (var r = api.Poll(http, CancellationToken.None)) Check((int)r.StatusCode == 202, "missing filter schedules creation");
                handler.Expect("POST", filter, "Bearer one", "{}", 201, "{}");
                handler.Expect("GET", filter + "/items", "Bearer one", null, 202, "{}");
                using (var r = api.Poll(http, CancellationToken.None)) Check((int)r.StatusCode == 202, "asynchronous results");
                handler.Expect("GET", filter + "/items", "Bearer one", null, 403, "{}");
                handler.Expect("POST", auth, "ApiKey secret", service, 200, "{\"access_token\":\"two\"}");
                handler.Expect("GET", filter + "/items", "Bearer two", null, 200, sample);
                using (var r = api.Poll(http, CancellationToken.None))
                    Check(api.Labels.Parse(r.Content.ReadAsStringAsync().Result, "prematch", 1795521600, 300).Single().CanDeliver, "expired token recovers and response decodes");
                now = now.AddMinutes(4);
                handler.Expect("POST", auth, "ApiKey secret", service, 200, "{\"token\":\"three\"}");
                handler.Expect("GET", filter + "/items", "Bearer three", null, 429, "{}");
                using (var r = api.Poll(http, CancellationToken.None)) Check((int)r.StatusCode == 429, "proactive renewal and rate-limit propagation");
                const string custom = "{\"bookmakers\":{\"39\":{\"allowed\":true}}}";
                api = new BreakingBetClient("prematch", "demo", "secret", custom, cache, () => now);
                var pre = filter.Replace(".live.", ".prematch.");
                handler.Expect("POST", auth, "ApiKey secret", service.Replace("_live", "_prematch"), 200, "{\"token\":\"four\"}");
                handler.Expect("POST", pre, "Bearer four", custom, 201, "{}");
                handler.Expect("GET", pre + "/items", "Bearer four", null, 200, sample);
                using (var r = api.Poll(http, CancellationToken.None)) Check(r.IsSuccessStatusCode && handler.Steps.Count == 0, "prematch filter and cached dictionaries across restart");
            }
        }
    }
}
