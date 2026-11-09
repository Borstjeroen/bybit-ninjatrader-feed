// BybitBridge v3 - sends live Bybit ticks (trades + best bid/ask) to NinjaTrader through the ATI (External connection).
//
// Usage:  BybitBridge.exe [--dry] [--config symbols.txt]
//   By default it reads  machines\<computername>\symbols.txt  (one line per coin:  BYBITSYMBOL[,NT_INSTRUMENT_NAME];  # = comment)
//   --dry: Bybit side only (no NinjaTrader needed), handy for testing.
//
// symbols.txt is checked every 5 seconds. After a change (and 3 seconds of quiet, because cloud sync sometimes writes in parts)
// new coins are subscribed live and removed coins are unsubscribed - without a restart.
//
// The volume scale per coin is determined automatically from Bybit's qtyStep (smallest lot = 1),
// because the ATI only accepts whole numbers.  E.g. BTCUSDT qtyStep 0.001 -> scale 1000.
//
// Writes to machines\<computername>\:
//   instruments.csv  - bybit;nt;tickSize;qtyStep;scale   (the live coins; used by the AddOn)
//   state.csv        - bybit;lastTickUtcMs;tradeCount     (every 30s)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BybitBridge
{
	class Sym
	{
		public string Bybit, Nt;
		public double Scale = 1, TickSize, QtyStep;
		public long Trades, Quotes;
		public double Bid, Ask;
		public long LastTickMs;
	}

	class Program
	{
		static bool dry;
		static string dir, mdir, configPath;
		static NinjaTrader.Client.Client ati;
		static readonly Dictionary<string, Sym> syms = new Dictionary<string, Sym>();
		static readonly object symLock = new object();   // protects syms against concurrent reading (status timer) and changing (reload)
		static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1); // a WebSocket may only have 1 sender at a time
		static long reconnects;
		static Timer statusTimer, cfgTimer; // keep static, otherwise the GC collects the timers
		static Mutex instanceLock;           // prevents a second instance
		static DateTime cfgStamp = DateTime.MinValue, cfgPendingStamp = DateTime.MinValue, cfgChangeSeen = DateTime.MinValue;
		static volatile bool reloadDue;
		static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

		static void Log(string m) { Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + m); }

		static int Main(string[] args)
		{
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
			bool firstInstance;
			instanceLock = new Mutex(true, "Global\\BybitBridgeNinjaTrader", out firstInstance);
			if (!firstInstance)
			{
				Console.WriteLine("The BybitBridge is already running. This second instance will close (otherwise NinjaTrader would receive every tick twice).");
				Thread.Sleep(6000);
				return 3;
			}
			dir = AppDomain.CurrentDomain.BaseDirectory;
			// One folder per computer (the project folder may be synced to several PCs): machines\<computername>\
			mdir = Path.Combine(dir, "machines", Environment.MachineName);
			Directory.CreateDirectory(mdir);
			configPath = Path.Combine(mdir, "symbols.txt");
			if (!File.Exists(configPath) && File.Exists(Path.Combine(dir, "symbols.txt")))
			{
				File.Copy(Path.Combine(dir, "symbols.txt"), configPath); // template from the root folder
				Console.WriteLine("New PC: copied symbols.txt from the root folder to " + mdir);
			}
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "--dry") dry = true;
				else if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
			}

			if (!LoadSymbols()) return 1;

			if (!dry) ConnectAti(); else Log("DRY-RUN: no NinjaTrader connection, only reading Bybit data.");

			statusTimer = new Timer(delegate { Status(); }, null, 30000, 30000);
			cfgTimer = new Timer(delegate { CheckConfig(); }, null, 5000, 5000);
			RunForever().GetAwaiter().GetResult();
			return 0;
		}

		// ---------- configuration ----------
		static List<string[]> ParseConfig(string path)
		{
			var list = new List<string[]>();
			var seen = new HashSet<string>();
			foreach (string raw in File.ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line.StartsWith("#")) continue;
				string[] p = line.Split(',');
				string bybit = p[0].Trim().ToUpperInvariant();
				string nt = p.Length > 1 && p[1].Trim().Length > 0 ? p[1].Trim() : bybit;
				if (bybit.Length > 0 && seen.Add(bybit)) list.Add(new string[] { bybit, nt });
			}
			return list;
		}

		// Fetches tickSize/qtyStep from Bybit; null for an unknown symbol or on error.
		static Sym BuildSym(string bybit, string nt)
		{
			var s = new Sym { Bybit = bybit, Nt = nt };
			try
			{
				string body = new WebClient().DownloadString("https://api.bybit.com/v5/market/instruments-info?category=linear&symbol=" + bybit);
				var root = (Dictionary<string, object>)json.DeserializeObject(body);
				var list = (System.Collections.IList)((Dictionary<string, object>)root["result"])["list"];
				if (list.Count == 0) { Log("Unknown Bybit symbol, skipped: " + bybit); return null; }
				var item = (Dictionary<string, object>)list[0];
				s.TickSize = D(((Dictionary<string, object>)item["priceFilter"])["tickSize"]);
				s.QtyStep = D(((Dictionary<string, object>)item["lotSizeFilter"])["qtyStep"]);
				s.Scale = s.QtyStep >= 1 ? 1 : Math.Round(1.0 / s.QtyStep);
			}
			catch (Exception ex) { Log("Could not fetch instrument info for " + bybit + ": " + ex.Message); return null; }
			Log(string.Format("Coin {0} -> NT '{1}', tickSize={2}, qtyStep={3}, volumeScale={4}", s.Bybit, s.Nt, s.TickSize, s.QtyStep, s.Scale));
			return s;
		}

		static void WriteCsv()
		{
			var csv = new StringBuilder();
			lock (symLock)
				foreach (var s in syms.Values)
					csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0};{1};{2};{3};{4}", s.Bybit, s.Nt, s.TickSize, s.QtyStep, s.Scale));
			File.WriteAllText(Path.Combine(mdir, "instruments.csv"), csv.ToString());
		}

		static bool LoadSymbols()
		{
			if (!File.Exists(configPath)) { Log("Config not found: " + configPath); return false; }
			cfgStamp = File.GetLastWriteTimeUtc(configPath);
			foreach (string[] e in ParseConfig(configPath))
			{
				Sym s = BuildSym(e[0], e[1]);
				if (s != null) lock (symLock) syms[s.Bybit] = s;
			}
			if (syms.Count == 0) { Log("No valid coins in " + configPath); return false; }
			WriteCsv();
			return true;
		}

		// Every 5s: has symbols.txt changed and then stayed unchanged for 3s (cloud sync sometimes writes in parts)?
		static void CheckConfig()
		{
			try
			{
				DateTime t = File.GetLastWriteTimeUtc(configPath);
				if (t == cfgStamp) return;
				if (t != cfgPendingStamp) { cfgPendingStamp = t; cfgChangeSeen = DateTime.UtcNow; }
				else if ((DateTime.UtcNow - cfgChangeSeen).TotalSeconds >= 3) reloadDue = true;
			}
			catch { }
		}

		// Runs on the receive thread (between two messages), so syms is never used concurrently.
		static async Task ApplyReload(ClientWebSocket ws)
		{
			reloadDue = false;
			DateTime stamp = File.GetLastWriteTimeUtc(configPath);
			List<string[]> entries;
			try { entries = ParseConfig(configPath); }
			catch (Exception ex) { Log("symbols.txt could not be read (" + ex.Message + "); will try again."); return; }
			cfgStamp = stamp;
			if (entries.Count == 0) { Log("symbols.txt contains no coins; change ignored (current list stays in effect)."); return; }

			var want = new Dictionary<string, string>();
			foreach (string[] e in entries) want[e[0]] = e[1];

			var added = new List<Sym>();     // really new coins: subscribe
			var removed = new List<Sym>();   // disappeared coins: unsubscribe
			var renamed = new List<Sym>();   // same coin, other NT name: just replace
			foreach (var kv in want)
			{
				Sym cur;
				bool exists = syms.TryGetValue(kv.Key, out cur);
				if (exists && cur.Nt == kv.Value) continue;
				Sym s = BuildSym(kv.Key, kv.Value);
				if (s == null) continue;
				if (exists) renamed.Add(s); else added.Add(s);
			}
			foreach (Sym s in new List<Sym>(syms.Values)) if (!want.ContainsKey(s.Bybit)) removed.Add(s);

			if (added.Count == 0 && removed.Count == 0 && renamed.Count == 0) { Log("symbols.txt changed, but the coin list stayed the same."); return; }

			lock (symLock)
			{
				foreach (Sym s in added) syms[s.Bybit] = s;
				foreach (Sym s in renamed) syms[s.Bybit] = s;
				foreach (Sym s in removed) syms.Remove(s.Bybit);
			}
			await SubscribeChunks(ws, "subscribe", added);
			await SubscribeChunks(ws, "unsubscribe", removed);
			WriteCsv();

			var an = new List<string>(); foreach (Sym s in added) an.Add(s.Bybit);
			var rn = new List<string>(); foreach (Sym s in removed) rn.Add(s.Bybit);
			Log("symbols.txt reloaded: +[" + string.Join(",", an.ToArray()) + "] -[" + string.Join(",", rn.ToArray()) + "] (" + renamed.Count + " renamed); now " + syms.Count + " coins.");
		}

		// ---------- NinjaTrader ATI ----------
		// The ATI library shows a modal error window by itself when NinjaTrader is unreachable. We never want to
		// trigger that: we only call the ATI when port 36973 is demonstrably open, and watch that every 2s.
		static readonly object atiLock = new object();
		static volatile bool atiUp;
		static Timer atiWatch; // keep static (GC)

		static bool AtiPortOpen()
		{
			try
			{
				using (var tc = new System.Net.Sockets.TcpClient())
				{
					var ar = tc.BeginConnect("127.0.0.1", 36973, null, null);
					if (!ar.AsyncWaitHandle.WaitOne(500)) return false;
					tc.EndConnect(ar);
					return true;
				}
			}
			catch { return false; }
		}

		static void WatchAti()
		{
			lock (atiLock)
			{
				bool open = AtiPortOpen();
				if (!open)
				{
					if (atiUp) Log("ATI/NinjaTrader no longer reachable - ticks are skipped until NinjaTrader is back.");
					atiUp = false;
					return;
				}
				if (atiUp) return; // already connected and port open
				try
				{
					if (ati != null) { try { ati.TearDown(); } catch { } try { ati.Dispose(); } catch { } }
					ati = new NinjaTrader.Client.Client();
					int rc = ati.SetUp("127.0.0.1", 36973);
					if (rc == 0) { atiUp = true; Log("ATI connected to NinjaTrader."); }
					else Log("ATI port open but SetUp failed (rc=" + rc + "), will try again.");
				}
				catch (Exception ex) { Log("ATI error: " + ex.Message); }
			}
		}

		static void ConnectAti()
		{
			WatchAti();
			if (!atiUp) Log("Waiting for NinjaTrader (ATI on, port 36973)...");
			atiWatch = new Timer(delegate { WatchAti(); }, null, 2000, 2000);
		}

		// All ATI calls go through here
		static void AtiLast(string nt, double price, int size) { lock (atiLock) { if (atiUp) ati.Last(nt, price, size); } }
		static void AtiBid(string nt, double price, int size) { lock (atiLock) { if (atiUp) ati.Bid(nt, price, size); } }
		static void AtiAsk(string nt, double price, int size) { lock (atiLock) { if (atiUp) ati.Ask(nt, price, size); } }

		// ---------- Bybit WebSocket ----------
		static async Task RunForever()
		{
			while (true)
			{
				try { await RunOnce(); }
				catch (Exception ex) { Log("ERROR: " + ex.GetType().Name + ": " + ex.Message + (ex.InnerException != null ? " | inner: " + ex.InnerException.Message : "")); }
				reconnects++;
				Log("Connection lost, retrying in 3s...");
				await Task.Delay(3000);
			}
		}

		static async Task SendText(ClientWebSocket ws, string text)
		{
			await sendLock.WaitAsync();
			try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None); }
			finally { sendLock.Release(); }
		}

		static async Task SubscribeChunks(ClientWebSocket ws, string op, List<Sym> list)
		{
			if (list.Count == 0) return;
			var topics = new List<string>();
			foreach (var s in list) { topics.Add("\"publicTrade." + s.Bybit + "\""); topics.Add("\"orderbook.1." + s.Bybit + "\""); }
			for (int i = 0; i < topics.Count; i += 10)
			{
				int n = Math.Min(10, topics.Count - i);
				await SendText(ws, "{\"op\":\"" + op + "\",\"args\":[" + string.Join(",", topics.GetRange(i, n).ToArray()) + "]}");
				if (topics.Count > 10) await Task.Delay(100);
			}
		}

		static async Task RunOnce()
		{
			using (var ws = new ClientWebSocket())
			{
				await ws.ConnectAsync(new Uri("wss://stream.bybit.com/v5/public/linear"), CancellationToken.None);
				List<Sym> snapshot;
				lock (symLock) snapshot = new List<Sym>(syms.Values);
				Log("Bybit WebSocket connected (" + snapshot.Count + " coins).");
				await SubscribeChunks(ws, "subscribe", snapshot);

				DateTime lastMsg = DateTime.UtcNow;
				var cts = new CancellationTokenSource();
				Task pinger = Task.Run(async delegate
				{
					while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
					{
						await Task.Delay(15000);
						if (ws.State != WebSocketState.Open) break;
						if ((DateTime.UtcNow - lastMsg).TotalSeconds > 45) { Log("No data for >45s, closing the connection."); try { ws.Abort(); } catch { } break; }
						try { await SendText(ws, "{\"op\":\"ping\"}"); } catch { break; }
					}
				});

				try
				{
					var buf = new byte[65536];
					while (ws.State == WebSocketState.Open)
					{
						var sb = new StringBuilder();
						WebSocketReceiveResult r;
						do
						{
							r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
							if (r.MessageType == WebSocketMessageType.Close) return;
							sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
						} while (!r.EndOfMessage);
						lastMsg = DateTime.UtcNow;
						Handle(sb.ToString());
						if (reloadDue)
						{
							try { await ApplyReload(ws); }
							catch (Exception ex) { Log("Reload failed: " + ex.Message); }
						}
					}
				}
				finally { cts.Cancel(); }
			}
		}

		static void Handle(string text)
		{
			var msg = json.DeserializeObject(text) as Dictionary<string, object>;
			if (msg == null || !msg.ContainsKey("topic")) return;
			string topic = (string)msg["topic"];
			string symbol = topic.Substring(topic.LastIndexOf('.') + 1);
			Sym s;
			if (!syms.TryGetValue(symbol, out s)) return;
			object data = msg["data"];

			if (topic.StartsWith("publicTrade."))
			{
				foreach (object o in (System.Collections.IEnumerable)data)
				{
					var t = (Dictionary<string, object>)o;
					double price = D(t["p"]), qty = D(t["v"]);
					s.Trades++;
					s.LastTickMs = Convert.ToInt64(t["T"]);
					if (!dry) AtiLast(s.Nt, price, Size(s, qty));
					else if (s.Trades <= 3) Log(s.Bybit + " TRADE " + t["S"] + " " + price + " x " + qty + " -> size " + Size(s, qty));
				}
			}
			else if (topic.StartsWith("orderbook."))
			{
				var d = (Dictionary<string, object>)data;
				double bp, bq, ap, aq;
				bool changed = false;
				if (d.ContainsKey("b") && FirstLevel(d["b"], out bp, out bq)) { s.Bid = bp; changed = true; if (!dry) AtiBid(s.Nt, bp, Size(s, bq)); }
				if (d.ContainsKey("a") && FirstLevel(d["a"], out ap, out aq)) { s.Ask = ap; changed = true; if (!dry) AtiAsk(s.Nt, ap, Size(s, aq)); }
				if (changed) s.Quotes++;
			}
		}

		static bool FirstLevel(object levels, out double price, out double qty)
		{
			price = 0; qty = 0;
			var arr = levels as System.Collections.IList;
			if (arr == null || arr.Count == 0) return false;
			var lvl = arr[0] as System.Collections.IList;
			if (lvl == null || lvl.Count < 2) return false;
			price = D(lvl[0]); qty = D(lvl[1]);
			return qty > 0; // qty 0 = level removed (delta); skip
		}

		// ---------- status ----------
		static void Status()
		{
			try
			{
				Sym[] snap;
				lock (symLock) snap = new List<Sym>(syms.Values).ToArray();
				var sb = new StringBuilder();
				var line = new StringBuilder();
				foreach (var s in snap)
				{
					sb.AppendLine(s.Bybit + ";" + s.LastTickMs + ";" + s.Trades);
					line.Append(" " + s.Bybit + "[t=" + s.Trades + " q=" + s.Quotes + "]");
				}
				File.WriteAllText(Path.Combine(mdir, "state.csv"), sb.ToString());
				Log("status: reconnects=" + reconnects + " coins=" + snap.Length + line);
			}
			catch (Exception ex) { Log("Status error: " + ex.Message); }
		}

		static double D(object o) { return double.Parse(o.ToString(), System.Globalization.CultureInfo.InvariantCulture); }
		static int Size(Sym s, double qty) { return (int)Math.Max(1, Math.Round(qty * s.Scale)); }
	}
}
