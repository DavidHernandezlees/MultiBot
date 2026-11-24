using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace MultiAiRobot.Feed
{
    internal sealed class BreakingBetFeed
    {
        readonly Map books, sports, groups, markets;
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal BreakingBetFeed(string json)
        {
            var labels = Feed.Serializer().DeserializeObject(json) as Map;
            books = Child(labels, "bookmakers");
            sports = Child(labels, "sports");
            groups = Child(labels, "odds_groups");
            markets = Child(labels, "odds_types");
            if (books == null || sports == null || groups == null || markets == null)
                throw new InvalidDataException("Invalid BreakingBet dictionaries response.");
        }

        static object Get(Map map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        static Map Child(Map map, string key) { return Get(map, key) as Map; }

        static string Field(Map map, string key) { return Feed.Field(map, key); }

        // The spec shows sub_events keyed by ID in examples and as an array in the schema.
        static Map SubEvent(Map ev, string id)
        {
            var entries = Get(ev, "sub_events");
            var keyed = entries as Map;
            if (keyed != null) {
                var sub = Child(keyed, id);
                var actual = Field(sub, "id");
                return actual == "" || actual == id ? sub : null;
            }
            var array = entries as object[];
            return array == null ? null : array.OfType<Map>().FirstOrDefault(s => Field(s, "id") == id);
        }

        static long Time(string value)
        {
            DateTimeOffset time;
            if (DateTimeOffset.TryParse(value, Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
                return time.ToUnixTimeSeconds();
            return 0;
        }

        static bool Recent(long value, long now, int maxAge)
        {
            return value > 0 && value <= now + 5 && now - value <= maxAge;
        }

        internal List<Quote> Parse(string json, string mode, long now, int maxAge)
        {
            var js = Feed.Serializer();
            var root = js.DeserializeObject(json) as Map;
            var items = Get(root, "items") as object[];
            var events = Get(root, "events") as object[];
            if (items == null || events == null || !(Get(root, "masked") is bool))
                throw new InvalidDataException("Expected a BreakingBet ItemList with items, events and masked.");
            bool masked = (bool)root["masked"];
            if (masked) Console.Error.WriteLine("BreakingBet returned masked data. Check Valuebets access for this feed.");
            long snapshot = Feed.Epoch(Field(root, "timestamp"));
            var eventIndex = new Dictionary<string, Map>();
            foreach (var ev in events.OfType<Map>()) eventIndex[Field(ev, "id")] = ev;
            var result = new List<Quote>();
            foreach (var item in items.OfType<Map>()) {
                var id = Field(item, "id");
                if (id == "") { Console.Error.WriteLine("Skipped BreakingBet item without an ID."); continue; }
                var eventId = Field(item, "event_id");
                Map ev;
                eventIndex.TryGetValue(eventId, out ev);
                var legs = Get(item, "odds") as object[];
                // The item supplies one edge. Never assign that edge to an ambiguous leg.
                var odd = legs != null && legs.Length == 1 ? legs[0] as Map : null;
                var subId = Field(odd, "sub_event_id");
                var sub = SubEvent(ev, subId);
                var bookId = Field(sub, "bookmaker_id");
                var marketId = Field(odd, "type_id");
                var market = Child(markets, marketId);
                var line = Field(odd, "index");
                var marketName = Field(market, "title_full");
                var name = marketName + (line.Length > 0 ? " " + line : "");
                var groupValues = Get(item, "groups_ids") as object[];
                var groupIds = (groupValues ?? new object[0]).Select(v => Convert.ToString(v, Invariant)).ToArray();
                var groupNames = groupIds.Select(g => Field(groups, g)).ToArray();
                var book = Field(books, bookId);
                var sport = Field(sports, Field(ev, "sport_id"));
                var home = Field(ev, "team_1");
                var away = Field(ev, "team_2");
                long start = Time(Field(ev, "start"));
                var updatedText = Field(odd, "updated");
                long updated = updatedText == "" ? snapshot : Time(updatedText);
                decimal price, edge;
                bool priceOK = decimal.TryParse(Field(odd, "value"), NumberStyles.Float, Invariant, out price) && price > 1 && price <= 1000000;
                bool edgeOK = decimal.TryParse(Field(item, "overvaluation"), NumberStyles.Float, Invariant, out edge) && edge > 0;
                bool live = mode == "live";
                var tip = new WireTip {
                    arbId = id, bookmaker = book, sport = sport == "Football" ? "Soccer" : sport,
                    eventTitle = home + " vs " + away, homeTeam = home, awayTeam = away, league = Field(ev, "league"),
                    period = string.Join(", ", groupNames), formulaKey = marketId, formula = name, outcome = name,
                    odds = (double)price, ROI = (double)edge, percent = edge, created = Feed.Iso(Time(Field(item, "created"))),
                    started = Feed.Iso(start), updated = Feed.Iso(updated), eventUrl = "", siteUrl = "", direct_link = "",
                    isLive = live, isLay = live // The 1.2.0 receiver reads its feed mode from isLay.
                };
                string status = "display only";
                if (masked) status = "masked";
                else if (odd == null) status = "missing or ambiguous odds";
                else if (ev == null || sub == null || home == "" || away == "" || book == "" || sport == "")
                    status = "unmatched event or bookmaker";
                else if (!object.Equals(Get(ev, "live"), live)) status = "feed mode mismatch";
                else if (marketName == "" || !object.Equals(Get(market, "lay"), false) || groupNames.Any(g => g == ""))
                    status = "lay or unmapped market";
                else if (!priceOK || !edgeOK) status = "invalid price or edge";
                else if (!Recent(snapshot, now, maxAge) || !Recent(updated, now, maxAge)) status = "stale quote";
                else if (!live && start <= now) status = "missing or elapsed start";
                var target = js.Serialize(new object[] { bookId, eventId, subId, marketId, line, groupIds.OrderBy(g => g, StringComparer.Ordinal).ToArray() });
                result.Add(new Quote { Mode = mode, Id = id, BookId = bookId, Target = target, Updated = updated, Snapshot = snapshot,
                    Status = status, Tip = tip, Json = js.Serialize(item), EventJson = ev == null ? "{}" : js.Serialize(ev) });
            }
            return result;
        }
    }
}
