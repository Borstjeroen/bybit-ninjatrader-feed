// BybitHistoryAddOn v4 - two layers
//   LIVE layer    : coins from instruments.csv (written by the BybitBridge = symbols.txt). Topped up every refreshMinutes.
//   HISTORY layer : all tradable Bybit USDT perpetuals (list re-fetched hourly = new listings/delistings).
//                   Topped up every refreshMinutesAll, within a time budget per cycle (budgetSeconds).
// Per coin: creates/repairs the NT instrument (Stock, 24x7, correct tick), first fill (minute + day), then top-ups.
// Top-ups look at the time of the last fill: after a standstill (NT closed) the whole gap is closed.
// Does NOTHING without 'addon_enabled.txt' (key=value, re-read every cycle; file gone = paused):
//   minuteDays=30          days of minute data on the first fill of LIVE coins
//   minuteDaysAll=14       same for the other coins (history layer)
//   dailyDays=400          daily candles on the first fill
//   refreshMinutes=5       LIVE coins
//   refreshMinutesAll=60   other coins
//   refreshWindowMinutes=120  minimum look-back window when topping up
//   universe=all|live      all = also all Bybit coins (history layer); live = only the bridge's coins
//   universeLimit=0        0 = all; N = only the first N coins (for a careful test)
//   budgetSeconds=240      max. working time per cycle for the history layer
//   symbols=A,B            optional filter (empty = no filter)
//   followBridge=true      AddOn only works while BybitBridge.exe is running (bridge closed = AddOn pauses immediately, also mid-cycle)
//   dailyLabelOffset=0     time label of daily candles: 0 = date of the UTC day, 1 = date of the next day (only change if
//                          the daily candles in NT look shifted by a day compared to TradingView; rewrites all daily candles afterwards)
// Trading hours: instruments get the template "Bybit 24 x 7 UTC" (UTC days, 4H and week like Bybit/TradingView). If that template does
// not exist in NT (yet), "Default 24 x 7" is used temporarily (setup.ps1 copies the template; restart NT once afterwards).
// Everything is logged to addon.log. Daily candles are UTC days (like Bybit/TradingView).
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	public class BybitHistoryAddOn : AddOnBase
	{
		private class Coin { public string Bybit, Nt; public double Tick, Scale; public bool Live; }

		private string root, dir, logPath, stateDir; // root = project folder; dir = machines\<computername> (own folder per PC)
		private int minuteDays = 30, minuteDaysAll = 14, dailyDays = 400;
		private int refreshMinutes = 5, refreshMinutesAll = 60, refreshWindowMinutes = 120;
		private int universeLimit = 0, budgetSeconds = 240;
		private bool universeAll = true;
		private string cleanupDesc = "";          // description of instruments that may be cleaned up (e.g. "Bybit spot")
		private bool cleanupConfirm = false;      // false = only log the list, true = really delete
		private string cleanupListedFor = null;   // prevents the list from being logged again every cycle
		private bool pausedLogged = false;        // log the active <-> paused transition only once
		private bool followBridge = false;        // true = AddOn only works while the BybitBridge is running
		private int dailyLabelOffset = 0;         // time label of daily candles: 0 = date of the UTC day itself, 1 = date of the next day
		private bool warnedTh = false;
		private const string TH_UTC = "Bybit 24 x 7 UTC";
		private const string TH_DEFAULT = "Default 24 x 7";
		private HashSet<string> only = new HashSet<string>();
		private Timer timer;
		private readonly object gate = new object();
		private volatile bool terminated;

		private List<Coin> universe = new List<Coin>();
		private DateTime universeAt = DateTime.MinValue;
		private int nextIdx = 0;

		private void L(string m)
		{
			try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + m + Environment.NewLine); }
			catch { }
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "BybitHistoryAddOn";
			}
			else if (State == State.Active)
			{
				try
				{
					root = FindRoot();
					if (root == null)
					{
						Log("BybitHistoryAddOn: project folder (with BybitBridge.exe) not found. Run setup.ps1 or set the environment variable BYBIT_FEED_DIR to its path (see README).", LogLevel.Warning);
						return;
					}
					dir = Path.Combine(root, "machines", Environment.MachineName);
					logPath = Path.Combine(dir, "addon.log");
					stateDir = Path.Combine(dir, "state");
					Directory.CreateDirectory(stateDir); // also creates machines\<pc>
					// The timer ALWAYS starts; every cycle checks whether addon_enabled.txt exists. That way both switching
					// off and on (renaming the file) work without compiling or restarting NT.
					bool enabled = File.Exists(Path.Combine(dir, "addon_enabled.txt"));
					L("AddOn v4 loaded (" + (enabled ? "active" : "PAUSED: addon_enabled.txt is missing") + "). Project folder: " + root + " | machine: " + Environment.MachineName);
					pausedLogged = !enabled;
					Task.Run(() => { Cycle(); StartTimer(); });
				}
				catch (Exception ex) { try { L("Startup error: " + ex); } catch { } }
			}
			else if (State == State.Terminated)
			{
				terminated = true;
				if (timer != null) { timer.Dispose(); timer = null; }
			}
		}

		// Finds the project folder: environment variable BYBIT_FEED_DIR, otherwise next to NT's folder, otherwise in the OneDrive/Documents folders.
		// A folder only counts if BybitBridge.exe is in it.
		private static string FindRoot()
		{
			const string rel1 = "3) VS Scripts", rel2 = "31) NinjaTrader Bybit Datafeed";
			var cands = new List<string>();
			string env = Environment.GetEnvironmentVariable("BYBIT_FEED_DIR");
			if (!string.IsNullOrEmpty(env)) cands.Add(env);
			try { cands.Add(Path.Combine(Directory.GetParent(Globals.UserDataDir.TrimEnd('\\', '/')).FullName, rel1, rel2)); } catch { }
			string[] bases = new string[] {
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				Environment.GetEnvironmentVariable("OneDrive") == null ? null : Path.Combine(Environment.GetEnvironmentVariable("OneDrive"), "Documenten"),
				Environment.GetEnvironmentVariable("OneDrive") == null ? null : Path.Combine(Environment.GetEnvironmentVariable("OneDrive"), "Documents"),
				Environment.GetEnvironmentVariable("OneDriveCommercial") == null ? null : Path.Combine(Environment.GetEnvironmentVariable("OneDriveCommercial"), "Documenten"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documenten"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents") };
			foreach (string b in bases) if (!string.IsNullOrEmpty(b)) cands.Add(Path.Combine(b, rel1, rel2));
			foreach (string c in cands)
				if (Directory.Exists(c) && File.Exists(Path.Combine(c, "BybitBridge.exe"))) return c;
			return null;
		}

		private void StartTimer()
		{
			if (terminated) return;
			int ms = Math.Max(1, refreshMinutes) * 60000;
			timer = new Timer(delegate { Cycle(); }, null, ms, ms);
		}

		// ---------------- settings ----------------
		private void ReadSettings(string path)
		{
			only.Clear();
			cleanupDesc = ""; cleanupConfirm = false; followBridge = false; dailyLabelOffset = 0;
			foreach (string raw in File.ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line.StartsWith("#") || line.IndexOf('=') < 0) continue;
				string k = line.Substring(0, line.IndexOf('=')).Trim().ToLowerInvariant();
				string v = line.Substring(line.IndexOf('=') + 1).Trim();
				int n;
				if (k == "minutedays" && int.TryParse(v, out n)) minuteDays = n;
				else if (k == "minutedaysall" && int.TryParse(v, out n)) minuteDaysAll = n;
				else if (k == "dailydays" && int.TryParse(v, out n)) dailyDays = n;
				else if (k == "refreshminutes" && int.TryParse(v, out n)) refreshMinutes = n;
				else if (k == "refreshminutesall" && int.TryParse(v, out n)) refreshMinutesAll = n;
				else if (k == "refreshwindowminutes" && int.TryParse(v, out n)) refreshWindowMinutes = n;
				else if (k == "universelimit" && int.TryParse(v, out n)) universeLimit = n;
				else if (k == "budgetseconds" && int.TryParse(v, out n)) budgetSeconds = n;
				else if (k == "universe") universeAll = v.ToLowerInvariant() != "live";
				else if (k == "cleanupdescription") cleanupDesc = v;
				else if (k == "cleanupconfirm") cleanupConfirm = v.ToLowerInvariant() == "true";
				else if (k == "followbridge") followBridge = v.ToLowerInvariant() == "true";
				else if (k == "dailylabeloffset" && int.TryParse(v, out n)) dailyLabelOffset = n;
				else if (k == "symbols")
					foreach (string s in v.Split(',')) { string t = s.Trim().ToUpperInvariant(); if (t.Length > 0) only.Add(t); }
			}
		}

		// ---------------- coins ----------------
		private List<Coin> ReadLive()
		{
			var list = new List<Coin>();
			string csv = Path.Combine(dir, "instruments.csv");
			if (!File.Exists(csv)) csv = Path.Combine(root, "instruments.csv"); // legacy fallback: an old bridge wrote into the root folder
			if (!File.Exists(csv)) return list;
			foreach (string raw in File.ReadAllLines(csv))
			{
				string[] p = raw.Trim().Split(';');
				if (p.Length < 5) continue;
				var c = new Coin { Bybit = p[0].Trim().ToUpperInvariant(), Nt = p[1].Trim(), Live = true };
				c.Tick = double.Parse(p[2], CultureInfo.InvariantCulture);
				c.Scale = double.Parse(p[4], CultureInfo.InvariantCulture);
				if (only.Count > 0 && !only.Contains(c.Bybit)) continue;
				list.Add(c);
			}
			return list;
		}

		// All tradable Bybit USDT perpetuals (status Trading). Same volume-scale formula as the bridge.
		private List<Coin> FetchUniverse()
		{
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
			var list = new List<Coin>();
			string cursor = "";
			var symRx = new Regex("\\{\"symbol\":\"([A-Z0-9]+)\"");
			do
			{
				string url = "https://api.bybit.com/v5/market/instruments-info?category=linear&limit=1000" + (cursor.Length > 0 ? "&cursor=" + cursor : "");
				string body = new WebClient().DownloadString(url);
				MatchCollection ms = symRx.Matches(body);
				for (int i = 0; i < ms.Count; i++)
				{
					int s = ms[i].Index, e = (i + 1 < ms.Count) ? ms[i + 1].Index : body.Length;
					string chunk = body.Substring(s, e - s);
					if (chunk.IndexOf("\"contractType\":\"LinearPerpetual\"") < 0 || chunk.IndexOf("\"status\":\"Trading\"") < 0
						|| chunk.IndexOf("\"quoteCoin\":\"USDT\"") < 0) continue;
					Match mt = Regex.Match(chunk, "\"tickSize\":\"([^\"]+)\""), mq = Regex.Match(chunk, "\"qtyStep\":\"([^\"]+)\"");
					if (!mt.Success || !mq.Success) continue;
					double step = D(mq.Groups[1].Value);
					string sym = ms[i].Groups[1].Value;
					list.Add(new Coin { Bybit = sym, Nt = sym, Tick = D(mt.Groups[1].Value), Scale = step >= 1 ? 1 : Math.Round(1.0 / step) });
				}
				Match cm = Regex.Match(body, "\"nextPageCursor\":\"([^\"]*)\"");
				cursor = cm.Success ? cm.Groups[1].Value : "";
			} while (cursor.Length > 0);
			return list;
		}

		private void RefreshUniverse()
		{
			if ((DateTime.UtcNow - universeAt).TotalMinutes < 60 && universe.Count > 0) return;
			try
			{
				List<Coin> fresh = FetchUniverse();
				if (fresh.Count == 0) { L("Universe: empty list received, previous list stays in effect."); return; }
				var old = new HashSet<string>(); foreach (Coin c in universe) old.Add(c.Bybit);
				var now = new HashSet<string>(); foreach (Coin c in fresh) now.Add(c.Bybit);
				var added = new List<string>(); foreach (string s in now) if (!old.Contains(s)) added.Add(s);
				var gone = new List<string>(); foreach (string s in old) if (!now.Contains(s)) gone.Add(s);
				universe = fresh; universeAt = DateTime.UtcNow;
				L("Universe: " + fresh.Count + " USDT perpetuals" + (old.Count > 0
					? "; new: " + (added.Count == 0 ? "-" : string.Join(",", added.ToArray())) + "; gone: " + (gone.Count == 0 ? "-" : string.Join(",", gone.ToArray())) : ""));
			}
			catch (Exception ex) { L("Fetching universe failed: " + ex.Message); }
		}

		// ---------------- link with the bridge / trading hours ----------------
		private static bool BridgeRunning()
		{
			try { return System.Diagnostics.Process.GetProcessesByName("BybitBridge").Length > 0; }
			catch { return true; } // when in doubt, do not stop
		}

		// Check between two coins: is the AddOn still on (file present) and, if so, is the bridge still running (with followBridge)?
		private bool StillRunning()
		{
			if (!File.Exists(Path.Combine(dir, "addon_enabled.txt"))) return false;
			if (followBridge && !BridgeRunning()) return false;
			return true;
		}

		// Wanted trading hours: own UTC template (days/4H/week then line up with Bybit/TradingView). If that template does not
		// exist (yet) in NT, 'Default 24 x 7' is used temporarily (restart NT after copying the template).
		private TradingHours WantedHours()
		{
			try { TradingHours t = TradingHours.Get(TH_UTC); if (t != null && t.Name == TH_UTC) return t; } catch { }
			if (!warnedTh) { L("Trading-hours template '" + TH_UTC + "' not found in NT (run setup and restart NT); using '" + TH_DEFAULT + "' for now."); warnedTh = true; }
			return TradingHours.Get(TH_DEFAULT);
		}

		// ---------------- cycle ----------------
		private void Cycle()
		{
			if (!Monitor.TryEnter(gate)) return; // previous cycle is still running
			try
			{
				string cfgPath = Path.Combine(dir, "addon_enabled.txt");
				string reason = null;
				if (!File.Exists(cfgPath)) reason = "addon_enabled.txt is missing";
				else
				{
					try { ReadSettings(cfgPath); } catch (Exception ex) { L("Reading settings failed: " + ex.Message); }
					if (followBridge && !BridgeRunning()) reason = "bridge is not running (followBridge=true)";
				}
				if (reason != null)
				{
					if (!pausedLogged) { L("AddOn PAUSED (" + reason + "). Nothing is written."); pausedLogged = true; }
					return;
				}
				if (pausedLogged) { L("AddOn RESUMED."); pausedLogged = false; }
				try { Cleanup(); } catch (Exception ex) { L("Cleanup failed: " + ex.Message); }

				List<Coin> live = ReadLive();
				var liveSet = new HashSet<string>(); foreach (Coin c in live) liveSet.Add(c.Bybit);

				foreach (Coin c in live) { if (terminated || !StillRunning()) return; Process(c); }

				if (!universeAll) return;
				RefreshUniverse();
				var others = new List<Coin>();
				foreach (Coin c in universe)
				{
					if (liveSet.Contains(c.Bybit)) continue;
					if (only.Count > 0 && !only.Contains(c.Bybit)) continue;
					if (universeLimit > 0 && others.Count >= universeLimit) break;
					others.Add(c);
				}
				if (others.Count == 0) return;
				if (nextIdx >= others.Count) nextIdx = 0;
				DateTime deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
				int worked = 0, visited = 0;
				for (; visited < others.Count; visited++)
				{
					if (terminated || !StillRunning()) return;
					int idx = (nextIdx + visited) % others.Count;
					if (DateTime.UtcNow > deadline) { nextIdx = idx; L("History layer: time budget used up after " + worked + " coins; next cycle continues at " + others[idx].Bybit); return; }
					if (Process(others[idx])) worked++;
				}
				nextIdx = 0;
				if (worked > 0) L("History layer: cycle done, " + worked + " coins updated.");
			}
			catch (Exception ex) { L("Cycle error: " + ex); }
			finally { Monitor.Exit(gate); }
		}

		// Cleans up instruments of an old connector, recognised by their description (cleanupDescription=...).
		// By default LIST ONLY (cleanupConfirm=false): nothing is deleted. Only with cleanupConfirm=true is anything deleted.
		// Close the charts of the instruments concerned first. Deleted coins (as far as they are in the
		// live or history list) are automatically re-created and completely filled by the AddOn.
		private void Cleanup()
		{
			if (string.IsNullOrEmpty(cleanupDesc)) { cleanupListedFor = null; return; }
			string want = cleanupDesc.Trim();
			var hits = new List<MasterInstrument>();
			foreach (MasterInstrument m in new List<MasterInstrument>(MasterInstrument.All))
			{
				string d = m.Description;
				if (d != null && d.Trim().Equals(want, StringComparison.OrdinalIgnoreCase)) hits.Add(m);
			}
			if (hits.Count == 0)
			{
				if (cleanupListedFor != want + "|0") { L("Cleanup: no instruments with description '" + want + "' found."); cleanupListedFor = want + "|0"; }
				return;
			}
			var names = new List<string>(); foreach (MasterInstrument m in hits) names.Add(m.Name);
			if (!cleanupConfirm)
			{
				if (cleanupListedFor != want + "|" + hits.Count)
				{
					L("Cleanup (LIST ONLY, nothing deleted): " + hits.Count + " instruments with description '" + want + "': " + string.Join(", ", names.ToArray()));
					L("Set cleanupConfirm=true in addon_enabled.txt to delete them.");
					cleanupListedFor = want + "|" + hits.Count;
				}
				return;
			}
			int deleted = 0;
			foreach (MasterInstrument m in hits)
			{
				try
				{
					Instrument.DbRemoveByMasterInstrumentID(m.Id);
					m.DbRemove();
					deleted++;
					try
					{
						foreach (string p in new string[] { "init_", "daily_", "last_", "dver_" })
						{
							string f = Path.Combine(stateDir, p + m.Name.ToUpperInvariant() + ".txt");
							if (File.Exists(f)) File.Delete(f);
						}
					}
					catch { }
				}
				catch (Exception ex) { L("Deleting " + m.Name + " failed: " + ex.Message); }
			}
			L("Cleanup: " + deleted + " of " + hits.Count + " instruments deleted (description '" + want + "'): " + string.Join(", ", names.ToArray()));
			cleanupListedFor = null;
		}

		// Returns true if work was actually done (for the time budget).
		private bool Process(Coin c)
		{
			try
			{
				string marker = Path.Combine(stateDir, "init_" + c.Bybit + ".txt");
				string lastFile = Path.Combine(stateDir, "last_" + c.Bybit + ".txt");
				string dver = Path.Combine(stateDir, "dver_" + c.Bybit + ".txt");
				// Format signature of the daily candles (version 3 = UTC days incl. running day, label according to dailyLabelOffset).
				// If it changes (or the file is missing), all daily candles of this coin are rewritten.
				string dsig = "3|" + dailyLabelOffset;
				Func<bool> dailyStale = delegate { try { return !File.Exists(dver) || File.ReadAllText(dver).Trim() != dsig; } catch { return true; } };

				// Non-live coins that are not due yet: skip without touching anything
				if (!c.Live && File.Exists(marker) && File.Exists(lastFile) && !dailyStale()
					&& (DateTime.UtcNow - File.GetLastWriteTimeUtc(lastFile)).TotalMinutes < refreshMinutesAll) return false;

				bool repaired, onlyHours;
				Instrument inst = EnsureInstrument(c.Nt, c.Tick, out repaired, out onlyHours);
				if (inst == null) { L(c.Nt + ": instrument not available, skipped."); return true; }
				if (repaired)
				{
					try
					{
						if (!onlyHours) { File.Delete(marker); File.Delete(lastFile); } // only for other changes also refill the minute data
						File.Delete(dver);                                                // daily candles always again
					}
					catch { }
				}

				int capDays = c.Live ? minuteDays : minuteDaysAll;
				double sinceMin = File.Exists(lastFile) ? (DateTime.UtcNow - File.GetLastWriteTimeUtc(lastFile)).TotalMinutes : 1e9;
				bool fullDaily = false;

				if (!File.Exists(marker) || sinceMin > capDays * 1440.0)
				{
					L("=== " + c.Bybit + (c.Live ? " (live)" : "") + ": first fill, " + capDays + " days");
					int m = Fill(inst, c, "1", 60000L, (long)capDays * 86400000L, BarsPeriodType.Minute, "day");
					File.WriteAllText(marker, DateTime.Now.ToString("o") + " minute=" + m);
					File.WriteAllText(lastFile, DateTime.UtcNow.ToString("o"));
					fullDaily = true; // new/completely filled coin: also all daily candles
				}
				else
				{
					// top-up: at least the window, but also the whole gap since the previous fill (+10 min margin), capped at capDays
					double lookMin = Math.Max(refreshWindowMinutes, sinceMin + 10);
					int mm = Fill(inst, c, "1", 60000L, (long)(lookMin * 60000.0), BarsPeriodType.Minute, "day");
					File.WriteAllText(lastFile, DateTime.UtcNow.ToString("o"));
					if (c.Live || sinceMin > refreshMinutesAll * 2) L(c.Bybit + ": topped up, " + mm + " minute candles (gap/window " + (int)lookMin + " min)");
				}

				// Daily candles: UTC days like Bybit/TradingView, INCLUDING the running day (so the last candle is right).
				if (fullDaily || dailyStale())
				{
					int d = FillDaily(inst, c, dailyDays);
					File.WriteAllText(dver, dsig);
					if (c.Live) L(c.Bybit + ": all daily candles written (" + d + ", label offset " + dailyLabelOffset + ")");
				}
				else FillDaily(inst, c, 3); // update the last days + the running day
				return true;
			}
			catch (Exception ex) { L("ERROR at " + c.Bybit + ": " + ex); return true; }
		}

		// repaired  = instrument was changed or newly created.
		// onlyHours = only the trading hours had to change: the minute data stays valid, only the daily candles are rewritten.
		private Instrument EnsureInstrument(string name, double tick, out bool repaired, out bool onlyHours)
		{
			repaired = false; onlyHours = false;
			TradingHours wanted = WantedHours();
			string wantedName = (wanted == null) ? TH_DEFAULT : wanted.Name;
			Instrument inst = Instrument.GetInstrument(name, false);
			if (inst != null)
			{
				// NT itself creates a default stock (tick 0.01, 'US Equities RTH') as soon as the bridge sends ticks for an
				// unknown instrument. Check and then repair the properties.
				MasterInstrument m0 = inst.MasterInstrument;
				string th = "";
				try { th = (m0.TradingHours == null) ? "" : m0.TradingHours.Name; } catch { }
				bool badHours = th != wantedName;
				bool badOther = Math.Abs(m0.TickSize - tick) > 1e-12 || m0.PointValue != 1;
				if (badHours || badOther)
				{
					L("Instrument " + name + " has different properties (trading hours '" + th + "', tick " + m0.TickSize + "); being repaired to '" + wantedName + "'.");
					m0.TradingHours = wanted;
					m0.TickSize = tick;
					m0.PointValue = 1;
					m0.Description = "Bybit USDT perpetual (BybitBridge)";
					string[] pn0 = m0.ProviderNames;
					int idx0 = (int)Provider.External;
					if (pn0 != null && pn0.Length > idx0) { pn0[idx0] = name; m0.ProviderNames = pn0; }
					m0.DbUpdate();
					repaired = true;
					onlyHours = badHours && !badOther;
				}
				return inst;
			}
			L("Instrument " + name + " does not exist, being created.");
			MasterInstrument mi = MasterInstrument.DbGet(name, InstrumentType.Stock, true);
			if (mi == null)
			{
				mi = new MasterInstrument();
				mi.Name = name;
				mi.InstrumentType = InstrumentType.Stock;
				mi.PointValue = 1;
				mi.TickSize = tick;
				mi.Currency = Currency.UsDollar;
				mi.Description = "Bybit USDT perpetual (BybitBridge)";
				mi.TradingHours = wanted;
				if (mi.Exchanges != null) mi.Exchanges.Add(Exchange.Default);
				string[] pn = mi.ProviderNames;
				int idx = (int)Provider.External;
				if (pn != null && pn.Length > idx) { pn[idx] = name; mi.ProviderNames = pn; }
				mi.DbAdd(true);
			}
			Instrument.DbRemoveByMasterInstrumentID(mi.Id);
			mi.CreateInstruments(0, OptionRight.Unknown);
			inst = Instrument.GetInstrument(name, false);
			if (inst == null) L("Creating " + name + " failed (instrument stays null).");
			repaired = true; // newly created: clear markers so the coin is (re)filled completely
			return inst;
		}

		// Fetches candles (interval "1" or "D") and writes them into NT. Returns the number of candles written.
		// IMPORTANT: Bars.Save() replaces the WHOLE NT file of every day (minute) or year (day) that is touched.
		// That is why 'startMs' always starts on a file boundary: align = "day" (local midnight) or "year" (1 January local).
		private int Fill(Instrument inst, Coin coin, string interval, long periodMs, long lookbackMs, BarsPeriodType type, string align)
		{
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long nowFloor = (nowMs / periodMs) * periodMs;       // start of the running candle
			long startMs = nowFloor - lookbackMs;
			if (align == "day" || align == "year")
			{
				DateTime lt = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime, TimeZoneInfo.Local);
				DateTime ls = align == "day" ? lt.Date : new DateTime(lt.Year, 1, 1);
				DateTime utcStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ls, DateTimeKind.Unspecified), TimeZoneInfo.Local);
				// NT label = END time of the candle; the candle that ends at 00:00 (local) belongs to the new day/year file
				// and therefore starts 1 period earlier. Without this correction that candle is lost on every top-up.
				startMs = new DateTimeOffset(utcStart, TimeSpan.Zero).ToUnixTimeMilliseconds() - periodMs;
			}
			var rows = new SortedDictionary<long, double[]>();
			long cursor = nowFloor;
			var rx = new Regex("\\[\"(\\d+)\",\"([^\"]+)\",\"([^\"]+)\",\"([^\"]+)\",\"([^\"]+)\",\"([^\"]+)\",\"[^\"]+\"\\]");
			while (cursor > startMs && !terminated)
			{
				string url = "https://api.bybit.com/v5/market/kline?category=linear&symbol=" + coin.Bybit + "&interval=" + interval + "&limit=1000&end=" + cursor;
				string body = new WebClient().DownloadString(url);
				MatchCollection ms = rx.Matches(body);
				if (ms.Count == 0) break;
				long oldest = long.MaxValue;
				foreach (Match m in ms)
				{
					long ts = long.Parse(m.Groups[1].Value);
					if (ts < oldest) oldest = ts;
					if (ts >= nowFloor || ts < startMs) continue; // leave out the running candle and ones that are too old
					rows[ts] = new double[] { D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value), D(m.Groups[5].Value), D(m.Groups[6].Value) };
				}
				if (oldest >= cursor) break;
				cursor = oldest - 1;
				Thread.Sleep(120);
			}
			if (rows.Count == 0) return 0;

			BarsPeriod bp = new BarsPeriod();
			bp.BarsPeriodType = type;
			bp.Value = 1;
			bp.MarketDataType = MarketDataType.Last;

			Func<long, DateTime> toLocalEnd = ms0 =>
				TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms0 + periodMs).UtcDateTime, TimeZoneInfo.Local);

			long first = long.MaxValue, last = long.MinValue;
			foreach (long k in rows.Keys) { if (k < first) first = k; if (k > last) last = k; }
			Bars bars = new Bars(inst, bp, toLocalEnd(first), toLocalEnd(last), inst.MasterInstrument.TradingHours);
			double tickSize = inst.MasterInstrument.TickSize;
			foreach (KeyValuePair<long, double[]> kv in rows)
			{
				double[] r = kv.Value;
				long vol = (long)Math.Max(0, Math.Round(r[4] * coin.Scale));
				bars.Add(r[0], r[1], r[2], r[3], toLocalEnd(kv.Key), vol, tickSize, true);
			}
			bars.Save();
			if (type == BarsPeriodType.Day || coin.Live || bars.Count > 5000)
				L(coin.Bybit + " " + type + ": " + bars.Count + " candles saved (" + bars.GetTime(0) + " .. " + bars.GetTime(bars.Count - 1) + ")");
			return bars.Count;
		}

		// Daily candles according to Bybit (UTC days), including the running day. Writes whole year files (NT keeps daily data per year,
		// and Bars.Save() replaces the whole year file): therefore always from 1 January (UTC) of the first year in the window.
		// Time label = date of the trading day (NT's 'TradingDay'), shiftable with dailyLabelOffset.
		private int FillDaily(Instrument inst, Coin coin, int lookbackDays)
		{
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
			const long dayMs = 86400000L;
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long today = (nowMs / dayMs) * dayMs;                                           // start of the running UTC day
			DateTime from = DateTimeOffset.FromUnixTimeMilliseconds(today - (long)lookbackDays * dayMs).UtcDateTime;
			long jan1 = new DateTimeOffset(new DateTime(from.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
			long startMs = jan1 - (long)dailyLabelOffset * dayMs;                           // with offset 1 the 31 Dec candle belongs to the new year
			var rows = new SortedDictionary<long, double[]>();
			long cursor = nowMs;                                                            // including the running day
			var rx = new Regex("\\[\"(\\d+)\",\"([^\"]+)\",\"([^\"]+)\",\"([^\"]+)\",\"([^\"]+)\",\"([^\"]+)\",\"[^\"]+\"\\]");
			while (cursor > startMs && !terminated)
			{
				string url = "https://api.bybit.com/v5/market/kline?category=linear&symbol=" + coin.Bybit + "&interval=D&limit=1000&end=" + cursor;
				string body = new WebClient().DownloadString(url);
				MatchCollection ms = rx.Matches(body);
				if (ms.Count == 0) break;
				long oldest = long.MaxValue;
				foreach (Match m in ms)
				{
					long ts = long.Parse(m.Groups[1].Value);
					if (ts < oldest) oldest = ts;
					if (ts < startMs) continue;
					rows[ts] = new double[] { D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value), D(m.Groups[5].Value), D(m.Groups[6].Value) };
				}
				if (oldest >= cursor) break;
				cursor = oldest - 1;
				Thread.Sleep(120);
			}
			if (rows.Count == 0) return 0;

			BarsPeriod bp = new BarsPeriod();
			bp.BarsPeriodType = BarsPeriodType.Day;
			bp.Value = 1;
			bp.MarketDataType = MarketDataType.Last;

			Func<long, DateTime> label = ts =>
			{
				DateTime u = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
				return new DateTime(u.Year, u.Month, u.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(dailyLabelOffset);
			};
			long first = long.MaxValue, last = long.MinValue;
			foreach (long k in rows.Keys) { if (k < first) first = k; if (k > last) last = k; }
			Bars bars = new Bars(inst, bp, label(first), label(last), inst.MasterInstrument.TradingHours);
			double tickSize = inst.MasterInstrument.TickSize;
			foreach (KeyValuePair<long, double[]> kv in rows)
			{
				double[] r = kv.Value;
				long vol = (long)Math.Max(0, Math.Round(r[4] * coin.Scale));
				bars.Add(r[0], r[1], r[2], r[3], label(kv.Key), vol, tickSize, true);
			}
			bars.Save();
			if (coin.Live && lookbackDays > 10)
				L(coin.Bybit + " Day: " + bars.Count + " candles saved (" + bars.GetTime(0) + " .. " + bars.GetTime(bars.Count - 1) + ")");
			return bars.Count;
		}

		private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
	}
}
