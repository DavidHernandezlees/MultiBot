using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MultiAiRobot.Feed
{
    internal static class Checks
    {
        static void Assert(bool ok,string name) { if(!ok) throw new Exception(name); Console.WriteLine("PASS "+name); }
        internal static int Main(string[] args)
        {
            var js=Feed.Serializer(); long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var row=new Dictionary<string,object> {
                {"valuebet_id","example-1"},{"bookmaker_id",10},{"target_bookmaker_bet_id","selection-22"},
                {"bookmaker_event_id",81},{"market_and_bet_type",19},{"market_and_bet_type_param",2.5},
                {"period_identifier",0},{"odds",2.15},{"percent",3.75},{"updated_at",now},{"created_at",now-60},
                {"direct_link","FI=1234567&ID=7654321&OD=23/20"},{"paused",false},{"lay",false}
            };
            var ev=new Dictionary<string,object> {
                {"bookmaker_event_id",81},{"bookmaker_id",10},{"bookmaker_event_name","Demo United vs Démo City"},
                {"sport_id",7},{"started_at",now+3600},{"bookmaker_event_direct_link","/event/1234567"}
            };
            var root=new Dictionary<string,object> {{"events",new Dictionary<string,object>{{"81",ev}}},{"valuebets",new[]{row}}};
            Func<string,List<Quote>> parse=mode=>Feed.Parse(js.Serialize(root),mode,now,300);
            var quote=parse("prematch").Single();
            Assert(quote.Status=="ready" && quote.Tip.percent==3.75m && quote.Tip.odds==2.15 && quote.Tip.outcome=="Over 2.5" && quote.Tip.bookmaker=="Bet365" && quote.Tip.sport=="Soccer","V2 event, market, odds and bookmaker join");
            var wire=Feed.Packet(quote.Tip);
            Assert(wire[0]==134 && wire[1]==123 && wire[2]==3 && BitConverter.ToInt32(wire,4)==wire.Length-8 && BitConverter.ToUInt16(wire,12)==wire.Length-14,"desktop frame lengths");
            File.WriteAllBytes(Path.Combine(args[0],"prematch.packet"),wire);
            File.WriteAllBytes(Path.Combine(args[0],"live.packet"),Feed.Packet(parse("live").Single().Tip));
            File.WriteAllText(Path.Combine(args[0],"response.json"),js.Serialize(root));
            string db=Path.Combine(args[0],"tests.sqlite3");
            using(var store=new Store(db)) {
                store.Save(quote,"a");
                using(var output=new MemoryStream()) {
                    Assert(Program.Deliver(store,new List<Quote>{quote},output,"a",300)==1 && output.Length>0,"store to desktop stream");
                    Assert(Program.Deliver(store,new List<Quote>{quote},output,"b",300)==0,"duplicate poll suppressed");
                }
                row["odds"]=2.17; var update=parse("prematch").Single(); store.Save(update,"b");
                row["updated_at"]=now-100; store.Save(parse("prematch").Single(),"c"); row["updated_at"]=now;
                var history=new List<string[]>(); store.History(history.Add);
                Assert(history.Count==1 && history[0][5]=="2.17" && history[0][0]=="b","price update and out-of-order poll");
            }
            using(var store=new Store(db)) {
                Assert(!store.Claim(quote,"d"),"reopen without replay");
                row["valuebet_id"]="second-reference"; var second=parse("prematch").Single(); store.Save(second,"e");
                Assert(!store.Claim(second,"e"),"same target across reference bookmakers");
            }
            row["paused"]=true; Assert(parse("prematch")[0].Status=="paused","paused quote");
            row["paused"]=false; row["lay"]=true; Assert(parse("live")[0].Status=="lay quote","lay quote");
            row["lay"]=false; row["updated_at"]=now-301; Assert(parse("live")[0].Status=="stale quote","stale quote");
            row["updated_at"]=now; ev["started_at"]=now-5; Assert(parse("prematch")[0].Status=="missing or elapsed start" && parse("live")[0].Status=="ready","feed mode and elapsed event");
            ev["bookmaker_id"]=11; Assert(parse("live")[0].Status.StartsWith("unmatched"),"wrong bookmaker event is not joined");
            ev["bookmaker_id"]=10; row["direct_link"]=""; Assert(parse("live")[0].Status=="incomplete selection","missing selection link");
            using(var form=Program.Request("example token","123,456")) {
                string fields=form.ReadAsStringAsync().Result;
                Assert(fields.Contains("search_filter%5B%5D=123") && fields.Contains("search_filter%5B%5D=456") && fields.Contains("access_token=example+token"),"official POST form");
            }
            Assert(Program.Endpoint("live").Host=="rest-api-lv.betburger.com" && Program.Endpoint("prematch").AbsolutePath=="/api/v2/valuebets/bot_pro_search","API routes");
            BreakingBetChecks.Run(args[0], args[1]);
            return 0;
        }
    }
}
