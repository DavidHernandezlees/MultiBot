using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MultiAiRobot.Feed
{
    internal static class Program
    {
        static readonly CancellationTokenSource Stop=new CancellationTokenSource();
        static readonly string Base=AppDomain.CurrentDomain.BaseDirectory;
        static string Env(string name,string fallback) { return Environment.GetEnvironmentVariable("BETBURGER_"+name) ?? fallback; }
        static int Number(string name,int initial,int min,int max)
        {
            int n;
            if(!int.TryParse(Env(name,initial.ToString()),out n) || n<min || n>max) throw new ArgumentException("BETBURGER_"+name+" must be between "+min+" and "+max+".");
            return n;
        }
        static string Prompt(string message,bool secret)
        {
            Console.Write(message);
            if(!secret || Console.IsInputRedirected) return (Console.ReadLine() ?? "").Trim();
            var buffer=new StringBuilder();
            while(true) {
                var key=Console.ReadKey(true);
                if(key.Key==ConsoleKey.Enter) {Console.WriteLine(); return buffer.ToString().Trim();}
                if(key.Key==ConsoleKey.Backspace && buffer.Length>0) buffer.Length--;
                else if(!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
            }
        }
        static string Mode(string value)
        {
            string mode=value.Trim().ToLowerInvariant();
            if(mode=="") return "prematch";
            if(mode!="live" && mode!="prematch") throw new ArgumentException("Choose prematch or live.");
            return mode;
        }
        internal static FormUrlEncodedContent Request(string token,string filters)
        {
            var fields=new List<KeyValuePair<string,string>> {
                new KeyValuePair<string,string>("access_token",token), new KeyValuePair<string,string>("per_page","30"),
                new KeyValuePair<string,string>("grouped","false") };
            foreach(string id in filters.Split(',').Select(s=>s.Trim()).Distinct()) {
                int n;
                if(!int.TryParse(id,out n) || n<=0) throw new ArgumentException("Enter positive BetBurger filter IDs, separated by commas.");
                fields.Add(new KeyValuePair<string,string>("search_filter[]",n.ToString(CultureInfo.InvariantCulture)));
            }
            return new FormUrlEncodedContent(fields);
        }
        internal static Uri Endpoint(string mode)
        {
            return new Uri("https://rest-api-"+(Mode(mode)=="live" ? "lv" : "pr")+".betburger.com/api/v2/valuebets/bot_pro_search");
        }
        static void Save(Store store,List<Quote> rows,string stamp)
        {
            store.Run("BEGIN IMMEDIATE");
            try { foreach(var q in rows) store.Save(q,stamp); store.Run("COMMIT"); }
            catch { store.Run("ROLLBACK"); throw; }
        }
        internal static int Deliver(Store store,List<Quote> rows,Stream socket,string stamp,int maxAge)
        {
            int count=0;
            foreach(var q in rows.Where(item=>item.Status=="ready").OrderByDescending(item=>item.Updated)) {
                var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if(now-q.Updated>maxAge) continue;
                byte[] packet=Feed.Packet(q.Tip);
                if(!store.Claim(q,stamp)) continue;
                // The receiver has no acknowledgement. Commit first to prevent duplicate wagering on reconnect.
                socket.Write(packet,0,packet.Length);
                count++;
            }
            return count;
        }

        static bool Gone(TcpClient client) {return client.Client.Poll(0,SelectMode.SelectRead) && client.Available==0;}
        static int Serve(Store store)
        {
            string mode=Mode(Env("MODE",null) ?? Prompt("Feed mode [prematch/live, Enter = prematch]: ",false));
            string filters=Env("FILTER_IDS",null) ?? Prompt("BetBurger multifilter ID(s): ",false);
            string token=Env("API_TOKEN",null) ?? Prompt("BetBurger API token (hidden): ",true);
            if(string.IsNullOrWhiteSpace(token)) throw new ArgumentException("An active BetBurger API token is required.");
            using(Request(token,filters)) {}
            int port=Number("PORT",14010,1,65535), interval=Number("POLL_SECONDS",5,1,3600);
            int maxAge=Number("MAX_AGE_SECONDS",mode=="live"?30:300,1,86400);
            var listener=new TcpListener(IPAddress.Loopback,port);
            listener.Server.ExclusiveAddressUse=true;
            listener.Start(4);
            var handler=new HttpClientHandler {AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate};
            using(var http=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=8*1024*1024}) {
                TcpClient client=null;
                int failures=0;
                try {
                    Console.WriteLine("Tip store listening on 127.0.0.1:"+port+" ("+mode+"). Ctrl+C to stop.");
                    while(!Stop.IsCancellationRequested) {
                        int delay=interval;
                        try {
                            if(client!=null && Gone(client)) {client.Close(); client=null;}
                            if(client==null && listener.Pending()) {
                                client=listener.AcceptTcpClient(); client.NoDelay=true; client.SendTimeout=2000;
                                Console.WriteLine("Desktop connected.");
                            }
                            using(var form=Request(token,filters))
                            using(var response=http.PostAsync(Endpoint(mode),form,Stop.Token).GetAwaiter().GetResult()) {
                                int code=(int)response.StatusCode;
                                if(code==401 || code==402 || code==403 || code==400) {
                                    Console.Error.WriteLine("BetBurger returned HTTP "+code+". Check the token, subscription, feed and filter IDs."); return 3;
                                }
                                if(!response.IsSuccessStatusCode) {
                                    if(code!=429 && (code<500 || code>=600)) throw new InvalidDataException("Unexpected BetBurger HTTP status "+code+".");
                                    failures=Math.Min(failures+1,7); delay=Math.Max(interval,Math.Min(120,1<<failures));
                                    var retry=response.Headers.RetryAfter;
                                    if(retry!=null) {
                                        var after=retry.Delta ?? (retry.Date.HasValue ? retry.Date.Value-DateTimeOffset.UtcNow : TimeSpan.Zero);
                                        delay=(int)Math.Max(delay,Math.Min(2000000,Math.Ceiling(after.TotalSeconds)));
                                    }
                                    Console.Error.WriteLine("BetBurger HTTP "+code+". Retrying in "+delay+" seconds.");
                                } else {
                                    string body=response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                    var now=DateTimeOffset.UtcNow;
                                    var rows=Feed.Parse(body,mode,now.ToUnixTimeSeconds(),maxAge);
                                    string stamp=now.ToString("o");
                                    Save(store,rows,stamp);
                                    int sent=0;
                                    if(client!=null) {
                                        try { sent=Deliver(store,rows,client.GetStream(),stamp,maxAge); }
                                        catch(IOException) {client.Close(); client=null; Console.Error.WriteLine("Desktop disconnected during delivery.");}
                                    }
                                    Console.WriteLine("{0:u}  received {1}, eligible {2}, forwarded {3}",now,rows.Count,rows.Count(q=>q.Status=="ready"),sent);
                                    failures=0;
                                }
                            }
                        } catch(OperationCanceledException) {
                            if(Stop.IsCancellationRequested) break;
                            failures=Math.Min(failures+1,7); delay=Math.Max(interval,Math.Min(120,1<<failures));
                            Console.Error.WriteLine("BetBurger request timed out. Retrying in "+delay+" seconds.");
                        } catch(HttpRequestException) {
                            failures=Math.Min(failures+1,7); delay=Math.Max(interval,Math.Min(120,1<<failures));
                            Console.Error.WriteLine("Could not reach the BetBurger API. Retrying in "+delay+" seconds.");
                        } catch(SocketException) {
                            if(client!=null){client.Close();client=null;}
                            Console.Error.WriteLine("Desktop connection lost. Waiting for reconnect.");
                        }
                        Stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay));
                    }
                } finally {if(client!=null) client.Close(); listener.Stop();}
            }
            return 0;
        }
        static int Main(string[] args)
        {
            try {
                if(args.Length>0 && args[0]=="--help") {
                    Console.WriteLine("BetBurgerTipStore [--history | --import RESPONSE.json prematch|live]");
                    Console.WriteLine("Start with no arguments to collect Valuebets V2 and serve MultiAiRobot. See BETBURGER_SETUP.md."); return 0;
                }
                ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
                Console.CancelKeyPress+=(sender,e)=>{e.Cancel=true;Stop.Cancel();};
                string dir=Env("DATA_DIR",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MultiAiRobot","BetBurger"));
                Directory.CreateDirectory(dir);
                string path=Path.Combine(dir,"tips.sqlite3");
                Feed.ConfigureLabels(Path.Combine(Base,"labels.json"));
                Console.WriteLine("Tip database: "+path);
                using(var store=new Store(path)) {
                    if(args.Length==0) return Serve(store);
                    if(args.Length==1 && args[0]=="--history") {
                        Console.WriteLine("Observed (UTC) | Feed | Valuebet ID | Event | Selection | Odds | Edge % | Last observed status");
                        store.History(row=>Console.WriteLine(string.Join(" | ",row))); return 0;
                    }
                    if(args.Length==3 && args[0]=="--import") {
                        var now=DateTimeOffset.UtcNow;
                        var rows=Feed.Parse(File.ReadAllText(args[1]),Mode(args[2]),now.ToUnixTimeSeconds(),Number("MAX_AGE_SECONDS",Mode(args[2])=="live"?30:300,1,86400));
                        Save(store,rows,now.ToString("o")); Console.WriteLine("Imported "+rows.Count+" valuebet(s)."); return 0;
                    }
                    throw new ArgumentException("Invalid command. Run --help for usage.");
                }
            } catch(Exception ex) {Console.Error.WriteLine(ex.Message); return 1;}
        }
    }
}
