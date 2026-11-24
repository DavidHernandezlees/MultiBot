using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Web.Script.Serialization;

namespace MultiAiRobot.Feed
{
    // Backing-field names are part of the published 1.2.0 receiver's contract.
    [Serializable]
    public sealed class WireTip
    {
        public string bookmaker { get; set; }
        public string sport { get; set; }
        public string eventTitle { get; set; }
        public string homeTeam { get; set; }
        public string awayTeam { get; set; }
        public string league { get; set; }
        public string period { get; set; }
        public string formula { get; set; }
        public string outcome { get; set; }
        public string formulaKey { get; set; }
        public string arbId { get; set; }
        public string direct_link { get; set; }
        public string eventUrl { get; set; }
        public string siteUrl { get; set; }
        public double odds { get; set; }
        public double ROI { get; set; }
        public decimal percent { get; set; }
        public string created { get; set; }
        public string started { get; set; }
        public string updated { get; set; }
        public bool isLive { get; set; }
        public bool isLay { get; set; }
    }

    internal sealed class Quote
    {
        public string Mode, Id, Target, Status, BookId, Json, EventJson;
        public long Updated;
        public long? Snapshot;
        public long Version { get { return Snapshot ?? Updated; } }
        public WireTip Tip;
        public bool CanDeliver { get { return Status == "ready" || Status == "display only"; } }
    }

    internal static class Feed
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Dictionary<string,string> Books = new Dictionary<string,string> {
            {"9","Bwin"},{"10","Bet365"},{"53","PaddyPower"},{"61","Goldenpalace"},{"65","Betvictor"},{"74","TonyBet"},{"76","Stoiximan"},
            {"729","BetanoPE"},{"730","BwinIT"},{"798","BetanoPT"},{"799","BwinPT"}
        };
        static readonly Dictionary<string,string> Sports = new Dictionary<string,string> {
            {"1","Baseball"},{"2","Basketball"},{"4","Futsal"},{"5","Handball"},{"6","Hockey"},{"7","Soccer"},{"8","Tennis"},
            {"9","Volleyball"},{"10","Am. Football"},{"11","Snooker"},{"12","Darts"},{"13","Table Tennis"},{"14","Badminton"},{"15","Rugby League"},{"16","Water Polo"},{"17","Bandy"},
            {"18","Martial arts"},{"19","Field Hockey"},{"20","AFL"},{"21","E-Sports"},{"22","Chess"},{"23","Gaelic Sport"},
            {"25","Formula 1"},{"27","Motorsport"},{"28","Cycling"},{"29","Beach Volley"},{"30","Horse Racing"},{"31","Biathlon"},{"32","Curling"},{"33","Squash"},
            {"34","Netball"},{"35","Beach Soccer"},{"36","Floorball"},{"37","Hurling"},{"38","Kung Volleyball"},
            {"43","Rugby Union"},{"44","Boxing"},{"45","MMA"}
        };
        static readonly Dictionary<string,string> Markets = new Dictionary<string,string> {
            {"1","Team 1 to win"},{"2","Team 2 to win"},{"3","Team 1 draw no bet"},{"4","Team 2 draw no bet"},
            {"5","European handicap, team 1 {line}"},{"6","European handicap, draw {line}"},{"7","European handicap, team 2 {line}"},
            {"8","Both teams to score: yes"},{"9","Both teams to score: no"},{"11","1X2: team 1"},{"12","1X2: draw"},{"13","1X2: team 2"},
            {"14","Double chance: 1X"},{"15","Double chance: X2"},{"16","Double chance: 12"},{"17","Asian handicap, team 1 {line}"},
            {"18","Asian handicap, team 2 {line}"},{"19","Over {line}"},{"20","Under {line}"},{"21","Team 1 over {line}"},
            {"22","Team 1 under {line}"},{"23","Team 2 over {line}"},{"24","Team 2 under {line}"},{"25","Odd"},{"26","Even"}
        };

        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=8*1024*1024, RecursionLimit=48 }; }
        internal static string Field(IDictionary<string,object> row, string name)
        {
            object v;
            return row != null && row.TryGetValue(name,out v) && v != null && (v is string || v is ValueType)
                ? Convert.ToString(v,Inv).Trim() : "";
        }
        static string First(params string[] xs) { foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x; return ""; }
        static string Label(Dictionary<string,string> dict,string key,string fallback)
        { string name; return dict.TryGetValue(key,out name) ? name : fallback; }
        internal static long Epoch(string text)
        {
            long n;
            if (!long.TryParse(text,NumberStyles.Integer,Inv,out n) || n < 1) return 0;
            if (n >= 1000000000000L) n /= 1000;
            return n <= 253402300799L ? n : 0;
        }
        internal static string Iso(long n) { return n == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(n).ToString("u",Inv); }
        static bool Flag(string value, out bool result)
        {
            if (value=="0" || value=="1") {result=value=="1"; return true;}
            return bool.TryParse(value,out result);
        }
        static string Selection(string market,string line)
        {
            string label;
            if (!Markets.TryGetValue(market,out label)) return "BetBurger market " + market + (line=="" ? "" : " ("+line+")");
            return label.Replace("{line}",line=="" ? "?" : line);
        }
        internal static void ConfigureLabels(string path)
        {
            if (!File.Exists(path)) return;
            var sections=Serializer().Deserialize<Dictionary<string, Dictionary<string,string>>>(File.ReadAllText(path));
            foreach(var pair in sections)
            {
                var dest = pair.Key=="bookmakers" ? Books : pair.Key=="sports" ? Sports : pair.Key=="markets" ? Markets : null;
                if(dest==null) throw new InvalidDataException("Unknown mapping section: "+pair.Key);
                foreach(var entry in pair.Value) {
                    int id;
                    if(!int.TryParse(entry.Key,out id) || id<0 || string.IsNullOrWhiteSpace(entry.Value)) throw new InvalidDataException("Invalid label in "+pair.Key);
                    dest[entry.Key]=entry.Value.Trim();
                }
            }
        }

        public static List<Quote> Parse(string json,string mode,long now,int maxAge)
        {
            var js=Serializer();
            var root=js.DeserializeObject(json) as Dictionary<string,object>;
            object listValue,eventsValue;
            if(root==null || !root.TryGetValue("valuebets",out listValue) || !(listValue is object[]))
                throw new InvalidDataException("Expected a BetBurger V2 response containing a valuebets array.");
            var events=new Dictionary<string,Dictionary<string,object>>();
            if(root.TryGetValue("events",out eventsValue) && eventsValue is Dictionary<string,object>)
                foreach(var pair in (Dictionary<string,object>)eventsValue) {
                    var ev=pair.Value as Dictionary<string,object>;
                    if(ev==null) continue;
                    var eventId=First(Field(ev,"bookmaker_event_id"),pair.Key);
                    events[Field(ev,"bookmaker_id")+"/"+eventId]=ev;
                }
            var result=new List<Quote>();
            int missingIds=0;
            foreach(object raw in (object[])listValue) {
                var row=raw as Dictionary<string,object>;
                string id=Field(row,"valuebet_id");
                if(string.IsNullOrEmpty(id)) {missingIds++; continue;}
                string bookId=Field(row,"bookmaker_id"),eventId=Field(row,"bookmaker_event_id");
                Dictionary<string,object> ev;
                events.TryGetValue(bookId+"/"+eventId,out ev);
                string sportId=Field(ev,"sport_id"),market=Field(row,"market_and_bet_type"),line=Field(row,"market_and_bet_type_param");
                string target=Field(row,"target_bookmaker_bet_id"), direct=Field(row,"direct_link");
                string home=First(Field(ev,"bookmaker_home_team_name"),Field(ev,"home_team_name"));
                string away=First(Field(ev,"bookmaker_away_team_name"),Field(ev,"away_team_name"));
                long updated=Epoch(Field(row,"updated_at")),start=Epoch(First(Field(ev,"started_at"),Field(row,"started_at")));
                decimal edge,price;
                bool edgeOK=decimal.TryParse(Field(row,"percent"),NumberStyles.Float,Inv,out edge);
                bool oddsOK=decimal.TryParse(Field(row,"odds"),NumberStyles.Float,Inv,out price) && price>1 && price<=1000000;
                bool paused,lay;
                bool pausedKnown=Flag(Field(row,"paused"),out paused),layKnown=Flag(Field(row,"lay"),out lay);
                var tip=new WireTip {
                    bookmaker=Label(Books,bookId,""),sport=Label(Sports,sportId,""),eventTitle=First(Field(ev,"bookmaker_event_name"),Field(ev,"event_name"),Field(ev,"canonical_event_name"),home!="" && away!="" ? home+" vs "+away : ""),
                    homeTeam=home,awayTeam=away,league=First(Field(ev,"bookmaker_league_name"),Field(ev,"league_name"),Field(ev,"league")),
                    formulaKey=market,formula=Selection(market,line),outcome=Selection(market,line),period="Period "+Field(row,"period_identifier"),
                    arbId=id,direct_link=direct,eventUrl=Field(ev,"bookmaker_event_direct_link"),siteUrl=Field(ev,"bookmaker_event_direct_link"),
                    odds=(double)price,percent=edge,ROI=(double)edge,created=Iso(Epoch(Field(row,"created_at"))),started=Iso(start),updated=Iso(updated),
                    isLive=mode=="live",isLay=mode=="live"
                };
                // 1.2.0 decodes both booleans from isLay. Only back quotes pass below.
                string status="ready";
                if(!pausedKnown || !layKnown) status="unknown quote flags";
                else if(paused) status="paused";
                else if(lay) status="lay quote";
                else if(!oddsOK || !edgeOK || edge<=0) status="invalid price or edge";
                else if(tip.bookmaker=="") status="unmapped bookmaker "+bookId;
                else if(ev==null || tip.eventTitle=="" || tip.sport=="") status="unmatched event or sport "+sportId;
                else if(target=="" || market=="" || direct=="" || tip.outcome.Contains("?")) status="incomplete selection";
                else if(updated==0 || updated>now+5 || now-updated>maxAge) status="stale quote";
                else if(mode=="prematch" && (start==0 || start<=now)) status="missing or elapsed start";
                result.Add(new Quote {Mode=mode,Id=id,BookId=bookId,Target=js.Serialize(new[]{bookId,eventId,target}),Updated=updated,
                    Status=status,Tip=tip,Json=js.Serialize(row),EventJson=ev==null ? "{}" : js.Serialize(ev)});
            }
            if(missingIds>0) Console.Error.WriteLine("Skipped "+missingIds+" malformed valuebet(s) without a provider ID.");
            return result;
        }

        public static byte[] Packet(WireTip tip)
        {
            // Serialize only this fixed, locally constructed DTO. Incoming JSON never specifies runtime types.
            byte[] compressed;
            using(var buffer=new MemoryStream()) {
                using(var gzip=new GZipStream(buffer,CompressionMode.Compress,true))
                    new BinaryFormatter().Serialize(gzip,new List<WireTip>{tip});
                compressed=buffer.ToArray();
            }
            if(compressed.Length>ushort.MaxValue) throw new InvalidDataException("Tip exceeds the desktop packet size.");
            using(var buffer=new MemoryStream())
            using(var binary=new BinaryWriter(buffer)) {
                binary.Write((byte)0x86); binary.Write((byte)0x7B); binary.Write((ushort)3); binary.Write(6+compressed.Length);
                binary.Write((ushort)1); binary.Write((ushort)9); binary.Write((ushort)compressed.Length); binary.Write(compressed);
                return buffer.ToArray();
            }
        }
    }
}
