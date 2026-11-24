using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace MultiAiRobot.Feed
{
    internal sealed class BreakingBetClient
    {
        readonly string mode, secret, tokenUrl, filterUrl, filterBody, cachePath;
        readonly Func<DateTimeOffset> clock;
        string token;
        DateTimeOffset renewAt, refreshLabelsAt, lastFilterCreation;
        bool createFilter;
        internal BreakingBetFeed Labels { get; private set; }

        internal BreakingBetClient(string mode, string id, string secret, string filter, string cachePath, Func<DateTimeOffset> clock)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
                throw new ArgumentException("BreakingBet access key ID and secret key are required.");
            if (mode != "prematch" && mode != "live") throw new ArgumentException("Invalid BreakingBet feed mode.");
            this.mode = mode;
            this.secret = secret.Trim();
            this.cachePath = cachePath;
            this.clock = clock;
            var key = Uri.EscapeDataString(id.Trim());
            tokenUrl = "https://breaking-bet.com/access_keys/" + key + "/access_tokens";
            filterUrl = "https://valuebets." + mode + ".api.breaking-bet.com/v1/users/" + key + "/filter";
            filterBody = filter ?? "{}";
            if (!(Feed.Serializer().DeserializeObject(filterBody) is Dictionary<string,object>))
                throw new ArgumentException("BreakingBet filter file must contain a JSON object.");
            createFilter = filter != null;
            if (File.Exists(cachePath)) {
                try {
                    Labels = new BreakingBetFeed(File.ReadAllText(cachePath));
                    refreshLabelsAt = File.GetLastWriteTimeUtc(cachePath).AddHours(6);
                } catch (ArgumentException) { } catch (InvalidDataException) { } catch (InvalidOperationException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        static HttpResponseMessage Send(HttpClient http, HttpMethod method, string url, string scheme, string credential,
                                        string json, CancellationToken stop)
        {
            using (var request = new HttpRequestMessage(method, url)) {
                request.Headers.Authorization = new AuthenticationHeaderValue(scheme, credential);
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return http.SendAsync(request, stop).GetAwaiter().GetResult();
            }
        }

        static string ReadToken(string body)
        {
            string text = body.Trim();
            string result = null;
            if (text.StartsWith("{")) {
                var json = Feed.Serializer().DeserializeObject(text) as Dictionary<string,object>;
                result = Feed.Field(json, "access_token");
                if (result == "") result = Feed.Field(json, "token");
            } else if (text.StartsWith("\"")) {
                result = Feed.Serializer().Deserialize<string>(text);
            } else if (text.Split('.').Length == 3) result = text;
            if (string.IsNullOrEmpty(result) || result.IndexOfAny(new[] { ' ', '\r', '\n', '\t', '<', '>' }) >= 0)
                throw new InvalidDataException("BreakingBet returned an unrecognized access token response.");
            return result;
        }

        void CacheLabels(string body)
        {
            var labels = new BreakingBetFeed(body);
            string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                if (File.Exists(cachePath)) {
                    File.WriteAllText(temporary, body);
                    File.Replace(temporary, cachePath, null);
                } else {
                    // Exclusive create also works in directories where file renames are restricted.
                    using (var file = new FileStream(cachePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(file)) writer.Write(body);
                }
            } catch (IOException) {
                Console.Error.WriteLine("Dictionary cache is unavailable. Using current labels in memory.");
            } catch (UnauthorizedAccessException) {
                Console.Error.WriteLine("Dictionary cache is read-only. Using current labels in memory.");
            } finally {
                if (File.Exists(temporary)) {
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            Labels = labels;
            refreshLabelsAt = clock().AddHours(6);
        }

        // A returned response belongs to the caller. Retry authorization once per poll.
        internal HttpResponseMessage Poll(HttpClient http, CancellationToken stop)
        {
            for (int attempt = 0; ; attempt++) {
                if (token == null || clock() >= renewAt) {
                    var issuedAt = clock();
                    var auth = Send(http, HttpMethod.Post, tokenUrl, "ApiKey", secret, "{\"service\":\"valuebets_" + mode + "\"}", stop);
                    if (!auth.IsSuccessStatusCode) return auth;
                    using (auth) token = ReadToken(auth.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    renewAt = issuedAt.AddMinutes(4);
                }
                var response = PollAuthorized(http, stop);
                if (attempt != 0 || (response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden))
                    return response;
                response.Dispose();
                token = null;
            }
        }

        HttpResponseMessage PollAuthorized(HttpClient http, CancellationToken stop)
        {
            if (Labels == null || clock() >= refreshLabelsAt) {
                var dict = Send(http, HttpMethod.Get, "https://dicts.api.breaking-bet.com/v1/en", "Bearer", token, null, stop);
                if (!dict.IsSuccessStatusCode) return dict;
                using (dict) CacheLabels(dict.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            if (createFilter) {
                // Limit filter creation to 20/hour, even if a missing filter persists.
                var remaining = lastFilterCreation.AddMinutes(3) - clock();
                if (remaining > TimeSpan.Zero) {
                    var waiting = new HttpResponseMessage((HttpStatusCode)429);
                    waiting.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)));
                    return waiting;
                }
                lastFilterCreation = clock();
                var created = Send(http, HttpMethod.Post, filterUrl, "Bearer", token, filterBody, stop);
                if (!created.IsSuccessStatusCode) return created;
                created.Dispose();
                createFilter = false;
            }
            var items = Send(http, HttpMethod.Get, filterUrl + "/items", "Bearer", token, null, stop);
            if (items.StatusCode != HttpStatusCode.NotFound) return items;
            items.Dispose();
            createFilter = true;
            // Wait one normal poll before initializing a missing or expired filter.
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
