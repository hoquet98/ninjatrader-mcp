// McpBridgeAddOn.cs — NinjaTrader 8 AddOn, HTTP API on port 7890
// Compile in NT8: File → Utilities → NinjaScript Editor → right-click → Compile (F5)
// Or: copy to Documents\NinjaTrader 8\bin\Custom\AddOns\ and compile via NinjaScript Editor.
//
// v0.2.0 — Phase 2: strategy authoring, in-process compile, Strategy Analyzer backtest.
//   New endpoints:
//     GET  /api/strategies              list NinjaScript strategy source files
//     GET  /api/strategy/source?name=   read one strategy's source
//     POST /api/strategy/create         write a strategy .cs into bin\Custom\Strategies
//     POST /api/compile                 recompile NinjaScript in-process (hot-swap, no restart)
//     POST /api/backtest                run a backtest via the Strategy Analyzer
//     POST /api/dev/reflect             DEV ONLY — reflection RPC for probing NT8 internals
//                                       (enabled only when env NT8_MCP_DEV=1)

#region Using declarations
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Collections;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class McpBridgeAddOn : AddOnBase
    {
        private const string Version = "0.2.1";

        private HttpListener _listener;
        private Thread _serverThread;
        private bool _running;

        // Dev-only reflection RPC: object handle registry so callers can chain calls
        // (e.g. construct a window → invoke methods on it → read results).
        // Gated dynamically (checked per request) on either env NT8_MCP_DEV=1 or the
        // presence of a marker file, so it can be toggled WITHOUT restarting NT8.
        private static string DevMarkerFile => Path.Combine(Globals.UserDataDir, "mcp_dev.on");
        private static bool DevMode =>
            Environment.GetEnvironmentVariable("NT8_MCP_DEV") == "1" || File.Exists(DevMarkerFile);
        private readonly Dictionary<string, object> _handles = new Dictionary<string, object>();
        private int _handleSeq;

        // NT8 AddOns are driven by OnStateChange (there is no OnStartUp/OnShutDown on
        // AddOnBase in NT8.1). Start the HTTP listener once at State.Configure and tear
        // it down at State.Terminated.
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "McpBridgeAddOn";
            }
            else if (State == State.Configure)
            {
                StartServer();
            }
            else if (State == State.Terminated)
            {
                StopServer();
            }
        }

        private void StartServer()
        {
            if (_running) return;
            _running = true;
            _listener = new HttpListener();

            // Bind address is configurable via the NT8_MCP_PREFIX environment variable.
            // Default: localhost only (safe — same-machine access).
            // For remote access over a PRIVATE network (e.g. Tailscale), set it to
            // "http://+:7890/" so the AddOn also listens on the VPN interface.
            // NEVER expose this on a public interface without auth + firewall.
            var prefix = Environment.GetEnvironmentVariable("NT8_MCP_PREFIX");
            if (string.IsNullOrEmpty(prefix)) prefix = "http://localhost:7890/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _serverThread = new Thread(HandleRequests) { IsBackground = true };
            _serverThread.Start();

            Log($"McpBridgeAddOn v{Version} started on {prefix}" + (DevMode ? " (DEV reflection RPC enabled)" : ""));
        }

        private void StopServer()
        {
            if (!_running) return;
            _running = false;
            // Do NOT close SA windows here — closing pops a blocking confirmation dialog, and on a
            // hot-swap the next addon instance adopts the existing window anyway (FindExistingSaWindow).
            _listener?.Stop();
            _listener?.Close();
            Log("McpBridgeAddOn stopped");
        }

        // Manual cleanup endpoint: report all open windows and close the SA ones.
        private object CloseSaWindows()
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { error = "no WPF dispatcher" };
            var all = new List<object>();
            int closed = 0;
            disp.Invoke((Action)(() =>
            {
                var app = System.Windows.Application.Current;
                var wins = new List<System.Windows.Window>();
                foreach (System.Windows.Window w in app.Windows) wins.Add(w);
                foreach (var w in wins)
                {
                    bool sa = IsSaWindow(w);
                    all.Add(new { type = w.GetType().FullName, title = SafeToString(w.Title), isSa = sa });
                    if (sa) { try { w.Close(); closed++; } catch { } }
                }
                _saWindow = null;
            }));
            return new { closed, openWindows = all };
        }

        private void HandleRequests()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Log($"Error: {ex.Message}", LogLevel.Error); }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var method = context.Request.HttpMethod;

                string body = null;
                if (method == "POST")
                {
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        body = reader.ReadToEnd();
                }

                var response = RouteRequest(path, method, body, context.Request.QueryString);
                WriteResponse(context, 200, response);
            }
            catch (Exception ex)
            {
                WriteResponse(context, 500, new { error = ex.Message, stack = ex.StackTrace });
            }
        }

        private object RouteRequest(string path, string method, string body, System.Collections.Specialized.NameValueCollection query)
        {
            switch (path)
            {
                case "/api/health":
                    return new { status = "ok", timestamp = DateTime.UtcNow, version = Version, dev = DevMode };

                // ─── Phase 1 (account / trading / data) ───────────────────
                case "/api/account":            return GetAccountInfo();
                case "/api/positions":          return GetPositions();
                case "/api/orders":             return GetOrders();
                case "/api/quote":              return GetQuote(query["symbol"]);
                case "/api/bars":
                    return GetBars(query["symbol"], query["period"] ?? "Minute",
                        int.Parse(query["periodValue"] ?? "1"), int.Parse(query["count"] ?? "100"));
                case "/api/search":             return SearchInstruments(query["query"]);
                case "/api/bars/export":        return Post(method, () => ExportBars(body));
                case "/api/export":             return ReadExportFile(query["name"]);
                case "/api/order":              return Post(method, () => PlaceOrder(body));
                case "/api/order/cancel":       return Post(method, () => CancelOrder(body));
                case "/api/orders/cancel-all":  return Post(method, () => CancelAllOrders());

                // ─── Phase 2 (strategy authoring / compile / backtest) ────
                case "/api/strategies":         return ListStrategies();
                case "/api/strategy/source":    return GetStrategySource(query["name"]);
                case "/api/strategy/create":    return Post(method, () => CreateStrategy(body));
                case "/api/compile":            return Post(method, () => Compile(body));
                case "/api/compile/result":     return ReadCompileResult();
                case "/api/backtest":           return Post(method, () => Backtest(body));
                case "/api/sa/close":           return Post(method, () => CloseSaWindows());
                case "/api/sa/inspect":         if (!DevMode) return new { error = "dev only" }; return SaInspect();

                // ─── Dev-only reflection RPC ──────────────────────────────
                case "/api/dev/reflect":
                    if (!DevMode) return new { error = "dev mode disabled (set NT8_MCP_DEV=1 and restart NT8)" };
                    return Post(method, () => DevReflect(body));

                default:
                    throw new Exception($"Unknown endpoint: {path}");
            }
        }

        private static object Post(string method, Func<object> fn)
            => method == "POST" ? fn() : new { error = "method not allowed" };

        // ═══════════════════════════════════════════════════════════════
        //  Strategy authoring (safe — pure file I/O)
        // ═══════════════════════════════════════════════════════════════

        private static string StrategiesDir =>
            Path.Combine(Globals.UserDataDir, "bin", "Custom", "Strategies");

        // Guard against path traversal — accept a bare class/file name only.
        private static string SafeStrategyPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("name required");
            name = name.Trim();
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 3);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..") || name.Contains("/") || name.Contains("\\"))
                throw new Exception($"invalid strategy name: {name}");
            return Path.Combine(StrategiesDir, name + ".cs");
        }

        private object ListStrategies()
        {
            var dir = StrategiesDir;
            if (!Directory.Exists(dir)) return new { dir, strategies = new List<object>() };
            var list = Directory.GetFiles(dir, "*.cs")
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Select(fi => new { name = Path.GetFileNameWithoutExtension(fi.Name), file = fi.Name, bytes = fi.Length, modified = fi.LastWriteTimeUtc })
                .ToList();
            return new { dir, count = list.Count, strategies = list };
        }

        private object GetStrategySource(string name)
        {
            var path = SafeStrategyPath(name);
            if (!File.Exists(path)) return new { error = $"strategy not found: {name}" };
            return new { name = Path.GetFileNameWithoutExtension(path), file = Path.GetFileName(path), source = File.ReadAllText(path) };
        }

        private object CreateStrategy(string body)
        {
            var req = JObject.Parse(body ?? "{}");
            var name = req.Str("name");
            var source = req.Str("source");
            var overwrite = req.Bool("overwrite", true);
            if (string.IsNullOrWhiteSpace(source)) return new { error = "source required" };

            var path = SafeStrategyPath(name);
            var existed = File.Exists(path);
            if (existed && !overwrite) return new { error = $"strategy exists (pass overwrite=true): {name}" };

            Directory.CreateDirectory(StrategiesDir);
            File.WriteAllText(path, source, new UTF8Encoding(false));
            return new { status = existed ? "updated" : "created", name = Path.GetFileNameWithoutExtension(path), file = path, bytes = source.Length,
                         note = "call /api/compile to build + hot-load this strategy" };
        }

        // ═══════════════════════════════════════════════════════════════
        //  Compile — invoke NT8's internal Roslyn compiler in-process.
        //  NinjaTrader.Code.Compiler is public but obfuscated; call via
        //  reflection so we don't take a compile-time dep on Microsoft.CodeAnalysis.
        // ═══════════════════════════════════════════════════════════════

        // A successful compile hot-swaps the NinjaScript AppDomain — the very domain
        // THIS addon runs in — so the addon (and its HttpListener) is torn down and
        // recreated moments after Compiler.Compile() returns. The in-flight HTTP
        // response usually dies with it. So we persist the result to disk immediately
        // and expose GET /api/compile/result as a reliable fallback.
        private static string CompileResultFile => Path.Combine(Globals.UserDataDir, "mcp_compile_result.json");

        private object Compile(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var debug = req.Bool("debug", false);
            return CompileCore(debug);
        }

        private object CompileCore(bool debug)
        {
            var compilerType = Type.GetType("NinjaTrader.Code.Compiler, NinjaTrader.Core");
            if (compilerType == null) return Persist(new { success = false, error = "NinjaTrader.Code.Compiler not found" });

            // EmitResult Compile(bool checkCompileOnly, bool debugBuild,
            //                    IEnumerable<string> filesToIgnore, IEnumerable<string> filesInTmp)
            var compile = compilerType.GetMethod("Compile",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(bool), typeof(bool), typeof(IEnumerable<string>), typeof(IEnumerable<string>) }, null);
            if (compile == null) return Persist(new { success = false, error = "Compiler.Compile(bool,bool,IEnumerable<string>,IEnumerable<string>) not found" });

            object emit;
            try
            {
                // Compile everything from disk: no ignores, no tmp overlay (files already written to Custom\Strategies).
                emit = compile.Invoke(null, new object[] { false, debug, new List<string>(), new List<string>() });
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                return Persist(new { success = false, error = "compile threw: " + inner.Message, stack = inner.StackTrace });
            }

            // EmitResult.Success (bool) + EmitResult.Diagnostics (IEnumerable<Diagnostic>)
            var success = (bool?)emit?.GetType().GetProperty("Success")?.GetValue(emit) ?? false;
            var diagnostics = ReadDiagnostics(emit);
            var assemblyToLoad = compilerType.GetProperty("AssemblyToLoad", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;

            // Persist BEFORE the imminent AppDomain hot-swap tears this addon down.
            return Persist(new
            {
                success,
                errorCount = diagnostics.Count(d => d.severity == "Error"),
                errors = diagnostics.Where(d => d.severity == "Error").ToList(),
                // CS1701/CS1702 are benign assembly-version-unification notices NT8 emits en masse
                // (thousands of them) — filter them out, and hard-cap the rest so the result file
                // can never balloon.
                warnings = diagnostics.Where(d => d.severity == "Warning" && d.id != "CS1701" && d.id != "CS1702").Take(25).ToList(),
                assemblyToLoad,
                note = "NinjaScript hot-swaps right after this; if the connection dropped, GET /api/compile/result",
                timestamp = DateTime.UtcNow,
            });
        }

        private object Persist(object result)
        {
            try { File.WriteAllText(CompileResultFile, JsonConvert.SerializeObject(result), new UTF8Encoding(false)); } catch { }
            return result;
        }

        private object ReadCompileResult()
        {
            if (!File.Exists(CompileResultFile)) return new { error = "no compile has run yet" };
            try { return JObject.Parse(File.ReadAllText(CompileResultFile)); }
            catch (Exception ex) { return new { error = ex.Message }; }
        }

        private class Diag { public string severity; public string id; public string message; public string location; }

        private List<Diag> ReadDiagnostics(object emit)
        {
            var result = new List<Diag>();
            if (emit == null) return result;
            var diags = emit.GetType().GetProperty("Diagnostics")?.GetValue(emit) as IEnumerable;
            if (diags == null) return result;
            foreach (var d in diags)
            {
                if (d == null) continue;
                var t = d.GetType();
                var sev = t.GetProperty("Severity")?.GetValue(d)?.ToString();
                if (sev == "Hidden" || sev == "Info") continue;
                var id = t.GetProperty("Id")?.GetValue(d)?.ToString();
                string loc = null;
                try { loc = t.GetProperty("Location")?.GetValue(d)?.ToString(); } catch { }
                // Diagnostic.ToString() = "file(line,col): error CSxxxx: message" — ideal for reporting.
                result.Add(new Diag { severity = sev, id = id, message = SafeToString(d), location = loc });
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Backtest — driven via a bridge-managed Strategy Analyzer window.
        //  Sequence (all on the WPF dispatcher):
        //    create+show(minimized) SA window (cached/reused) → configure the
        //    selected tab (Strategy, Instrument, BarsPeriod, params) →
        //    CheckSettingsValid → ViewModel.OnRun → poll SelectedResult.Results
        //    → extract SystemPerformance (metrics + trade list).
        // ═══════════════════════════════════════════════════════════════

        private object _saWindow; // reused across backtests

        private const string SaNs = "NinjaTrader.Gui.NinjaScript.StrategyAnalyzer.";

        private object Backtest(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string strategy = req.Str("strategy");
            string symbol = req.Str("symbol");
            string period = req.Str("period") ?? "Minute";
            int periodValue = req["periodValue"] != null ? (int)req["periodValue"] : 1;
            int timeoutSec = req["timeoutSec"] != null ? (int)req["timeoutSec"] : 180;
            int maxTrades = req["maxTrades"] != null ? (int)req["maxTrades"] : 50;
            var prms = req["params"] as JObject;
            DateTime fromDt = DateTime.MinValue, toDt = DateTime.MinValue;
            DateTime.TryParse(req.Str("from"), out fromDt);
            DateTime.TryParse(req.Str("to"), out toDt);
            if (string.IsNullOrEmpty(strategy) || string.IsNullOrEmpty(symbol))
                return new { error = "strategy and symbol are required" };

            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { error = "no WPF dispatcher (is NT8 UI up?)" };

            Exception cfgErr = null;
            bool valid = false;
            object tabRef = null;
            object baseline = null;

            disp.Invoke((Action)(() =>
            {
                try
                {
                    // Reuse a single SA window across runs/hot-swaps. Closing NT8 windows pops a
                    // blocking "are you sure?" dialog, so we NEVER close — we adopt any existing
                    // SA window (orphaned by a prior hot-swap) or create one if none exists.
                    _saWindow = FindExistingSaWindow();
                    if (_saWindow == null)
                    {
                        var saType = Type.GetType(SaNs + "StrategyAnalyzer, NinjaTrader.Gui");
                        _saWindow = Activator.CreateInstance(saType);
                        InvokeM(_saWindow, "Show");
                    }
                    try { SetP(_saWindow, "WindowState", System.Windows.WindowState.Minimized); } catch { }
                    var vm = GetP(_saWindow, "ViewModel");
                    var tab = GetP(vm, "SelectedTab");
                    tabRef = tab;
                    var props = GetP(tab, "TabStrategyProperties");

                    SetP(props, "Strategy", strategy);
                    SetP(props, "InstrumentOrInstrumentList", symbol);
                    var bp = GetP(props, "BarsPeriod");
                    var bpType = Type.GetType("NinjaTrader.Data.BarsPeriodType, NinjaTrader.Core");
                    SetP(bp, "BarsPeriodType", Enum.Parse(bpType, period, true));
                    SetP(bp, "Value", periodValue);

                    if (prms != null)
                    {
                        var tmpl = GetP(props, "StrategyTemplate");
                        if (tmpl != null)
                            foreach (var p in prms.Properties())
                            {
                                var pi = tmpl.GetType().GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
                                if (pi != null && pi.CanWrite && p.Value is JValue jv && jv.Value != null)
                                    try { pi.SetValue(tmpl, Convert.ChangeType(jv.Value, pi.PropertyType)); } catch { }
                            }
                    }

                    // Custom date range: the SA toolbar From/To are Infragistics XamDateTimeEditor
                    // controls; set their DateValue before running. (No config property exists.)
                    if (fromDt != DateTime.MinValue || toDt != DateTime.MinValue)
                        SetSaDateRange(_saWindow, fromDt, toDt);

                    baseline = GetP(tab, "SelectedResult");
                    valid = Convert.ToBoolean(InvokeM(vm, "CheckSettingsValid"));
                    if (valid) InvokeM(vm, "OnRun", null, null);
                }
                catch (Exception ex) { cfgErr = ex; }
            }));

            if (cfgErr != null) return new { error = "configure/fire failed: " + cfgErr.Message, stack = cfgErr.StackTrace };
            if (!valid) return new { error = "settings invalid — check strategy name, instrument, or that data exists for the range" };

            // Poll for a new completed result.
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            object entry = null;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1000);
                object sel = null, results = null;
                disp.Invoke((Action)(() =>
                {
                    sel = GetP(tabRef, "SelectedResult");
                    if (sel != null && !ReferenceEquals(sel, baseline)) results = GetP(sel, "Results");
                }));
                if (results != null) { entry = sel; break; }
            }
            if (entry == null) return new { status = "timeout", message = $"no result within {timeoutSec}s (backtest may still be running)" };

            object report = null;
            // Leave the (minimized) window open for reuse — closing pops a blocking dialog.
            disp.Invoke((Action)(() => { report = ExtractBacktest(entry, maxTrades); }));
            return report;
        }

        // DEV: walk the SA window's logical tree and report every DateTime-valued property,
        // to locate the toolbar From/To date controls. Also reports control types seen.
        private object SaInspect()
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { error = "no WPF dispatcher" };
            var dates = new List<object>();
            disp.Invoke((Action)(() =>
            {
                var win = FindExistingSaWindow() as System.Windows.DependencyObject;
                if (win == null)
                {
                    var saType = Type.GetType(SaNs + "StrategyAnalyzer, NinjaTrader.Gui");
                    var w = (System.Windows.Window)Activator.CreateInstance(saType);
                    w.Show(); w.WindowState = System.Windows.WindowState.Minimized;
                    win = w;
                }
                _walkSeen.Clear();
                WalkLogical(win, dates, 0);
            }));
            return new { dateProps = dates };
        }

        private readonly HashSet<object> _walkSeen = new HashSet<object>();
        private void WalkLogical(object node, List<object> dates, int depth)
        {
            if (node == null || depth > 80) return;
            var deo = node as System.Windows.DependencyObject;
            if (deo != null) { if (_walkSeen.Contains(node)) return; _walkSeen.Add(node); }
            var t = node.GetType();
            // For XamDateTimeEditor, report its Value binding source (reveals which VM drives it).
            if (t.Name == "XamDateTimeEditor" && deo != null)
            {
                string src = "?";
                try
                {
                    var dp = t.GetField("ValueProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as System.Windows.DependencyProperty;
                    var be = dp != null ? System.Windows.Data.BindingOperations.GetBindingExpression(deo, dp) : null;
                    var so = be?.GetType().GetProperty("ResolvedSource")?.GetValue(be);
                    var pth = be?.GetType().GetProperty("ResolvedSourcePropertyName")?.GetValue(be)?.ToString();
                    src = so != null ? so.GetType().Name + "." + pth : "unbound";
                }
                catch { }
                var dvv = GetP(node, "DateValue");
                dates.Add(new { control = t.Name, value = dvv is DateTime dd ? dd.ToString("yyyy-MM-dd HH:mm") : null, bindsTo = src });
            }
            // recurse both logical and visual trees (toolbar controls are often visual-only)
            if (deo != null)
            {
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(deo))
                    WalkLogical(child, dates, depth + 1);
                try
                {
                    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(deo);
                    for (int i = 0; i < n; i++) WalkLogical(System.Windows.Media.VisualTreeHelper.GetChild(deo, i), dates, depth + 1);
                }
                catch { }
            }
        }

        // Set the SA toolbar's From/To date pickers. The editors display the run-config dates via a
        // (OneWay) binding, so we resolve each editor's binding SOURCE object+property and set THAT
        // directly — that source is what the run actually reads.
        private object _dateNote;
        private void SetSaDateRange(object win, DateTime from, DateTime to)
        {
            try { InvokeM(win, "UpdateLayout"); } catch { }   // realize the property-grid tree first
            var editors = new List<object>();
            var seen = new HashSet<object>();
            CollectDateEditors(win, editors, seen);

            // The backtest run-input From/To are the date editors whose Value binds to a property-grid
            // PropertyItemValue (NOT the TradePerformanceReportViewModel report filter). Pick those,
            // ordered by current date → [From, To].
            var runEditors = editors
                .Select(e => new { e, dv = GetP(e, "DateValue") as DateTime?, src = EditorBindingSource(e) })
                .Where(x => x.dv.HasValue && x.dv.Value.Year >= 2000
                            && x.src != null && x.src.GetType().Name == "PropertyItemValue")
                .OrderBy(x => x.dv.Value)
                .ToList();

            string info;
            if (runEditors.Count >= 2)
            {
                if (from != DateTime.MinValue) SetP(runEditors[0].src, "Value", from);
                if (to != DateTime.MinValue) SetP(runEditors[1].src, "Value", to);
                info = $"set from={from:yyyy-MM-dd} to={to:yyyy-MM-dd} on {runEditors.Count} property-grid editors";
            }
            else info = $"run-input date editors not found (found {runEditors.Count}); range left at default";
            _dateNote = info;
        }

        // The binding source object behind an editor's Value property.
        private object EditorBindingSource(object editor)
        {
            try
            {
                var deo = editor as System.Windows.DependencyObject;
                var dp = editor.GetType().GetField("ValueProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
                         as System.Windows.DependencyProperty;
                if (deo == null || dp == null) return null;
                var be = System.Windows.Data.BindingOperations.GetBindingExpression(deo, dp);
                return be?.GetType().GetProperty("ResolvedSource")?.GetValue(be);
            }
            catch { return null; }
        }

        // Collect XamDateTimeEditor from both the logical and visual trees (property-grid editors
        // are often visual-only).
        private void CollectDateEditors(object node, List<object> acc, HashSet<object> seen)
        {
            if (node == null) return;
            var deo = node as System.Windows.DependencyObject;
            if (deo != null) { if (seen.Contains(node)) return; seen.Add(node); }
            if (node.GetType().Name == "XamDateTimeEditor") acc.Add(node);
            if (deo != null)
            {
                foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(deo)) CollectDateEditors(c, acc, seen);
                try { int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(deo);
                    for (int i = 0; i < n; i++) CollectDateEditors(System.Windows.Media.VisualTreeHelper.GetChild(deo, i), acc, seen); }
                catch { }
            }
        }

        private static bool IsSaWindow(System.Windows.Window w)
        {
            var fn = w.GetType().FullName ?? "";
            var title = w.Title ?? "";
            return fn.StartsWith(SaNs) || title.IndexOf("Strategy Analyzer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Adopt an existing Strategy Analyzer window if one is open (e.g. orphaned by a prior
        // hot-swap), so at most one exists and we never have to close (which would prompt).
        private object FindExistingSaWindow()
        {
            var app = System.Windows.Application.Current;
            if (app == null) return null;
            foreach (System.Windows.Window w in app.Windows)
                if (IsSaWindow(w)) return w;
            return null;
        }

        // Manual cleanup only (user-triggered). Closing pops NT8's confirmation dialog unless the
        // user has ticked "don't show again", so this is not used automatically.
        private int CloseAllSaWindows()
        {
            var app = System.Windows.Application.Current;
            if (app == null) return 0;
            var toClose = new List<System.Windows.Window>();
            foreach (System.Windows.Window w in app.Windows)
                if (IsSaWindow(w)) toClose.Add(w);
            foreach (var w in toClose) { try { w.Close(); } catch { } }
            _saWindow = null;
            return toClose.Count;
        }

        // Pull metrics + (capped) trade list out of a StrategyAnalyzerGridEntry's SystemPerformance.
        private object ExtractBacktest(object entry, int maxTrades)
        {
            var perf = GetP(entry, "Results");            // SystemPerformance
            var all = GetP(perf, "AllTrades");            // TradeCollection (IEnumerable<Trade>)
            var tp = GetP(all, "TradesPerformance");      // TradesPerformance
            var cur = tp != null ? GetP(tp, "Currency") : null; // TradesPerformanceValues

            var trades = new List<object>();
            int total = 0, winners = 0, losers = 0;
            // Aggregate P&L per distinct entry (scale-out exits share one entry) so we can report
            // entry-level win rate — comparable to research/kernel numbers, unlike per-partial-trade WR.
            var entryPnl = new Dictionary<string, double>();
            var exitReasons = new Dictionary<string, int>();   // per-partial-exit tally by exit order name
            DateTime firstEntry = DateTime.MaxValue, lastExit = DateTime.MinValue;
            if (all is IEnumerable en)
                foreach (var tr in en)
                {
                    total++;
                    var entryExec = GetP(tr, "Entry");         // Execution
                    var exitExec = GetP(tr, "Exit");
                    var pc = GetP(tr, "ProfitCurrency");
                    double pcd = pc is double dd ? dd : 0;
                    if (pcd > 0) winners++; else if (pcd < 0) losers++;
                    if (GetP(entryExec, "Time") is DateTime et)
                    {
                        if (et < firstEntry) firstEntry = et;
                        var ekey = et.Ticks + "|" + SafeToString(GetP(entryExec, "MarketPosition"));
                        entryPnl[ekey] = (entryPnl.TryGetValue(ekey, out var pv) ? pv : 0) + pcd;
                    }
                    if (GetP(exitExec, "Time") is DateTime xt && xt > lastExit) lastExit = xt;
                    // Exit-reason tally: the exit order's signal name ("bank","runner","flat","time",
                    // "Stop loss", ...). Falls back to the exit order's Name if Execution.Name is empty.
                    var xname = SafeToString(GetP(exitExec, "Name"));
                    if (string.IsNullOrWhiteSpace(xname) || xname == "<toString threw>")
                        xname = SafeToString(GetP(GetP(exitExec, "Order"), "Name"));
                    if (!string.IsNullOrWhiteSpace(xname))
                        exitReasons[xname] = (exitReasons.TryGetValue(xname, out var xc) ? xc : 0) + 1;
                    if (trades.Count >= maxTrades) continue;   // still count the rest
                    trades.Add(new
                    {
                        instrument = SafeToString(GetP(entryExec, "Instrument") is object inst ? GetP(inst, "FullName") : null),
                        marketPosition = SafeToString(GetP(entryExec, "MarketPosition")),
                        quantity = GetP(tr, "Quantity"),
                        entryPrice = GetP(entryExec, "Price"),
                        exitPrice = GetP(exitExec, "Price"),
                        entryTime = GetP(entryExec, "Time"),
                        exitTime = GetP(exitExec, "Time"),
                        profitCurrency = pc,
                        profitPoints = GetP(tr, "ProfitPoints"),
                        exitName = GetP(exitExec, "Name"),
                    });
                }

            double gp = D(tp, "GrossProfit") is double g1 ? g1 : 0;
            double gl = D(tp, "GrossLoss") is double g2 ? g2 : 0;
            int entries = entryPnl.Count;
            int winEntries = entryPnl.Values.Count(v => v > 0);
            // Per-ENTRY loss/win profile (kernel-comparable: the scale-out means a full position
            // outcome, not per-partial). This is the diagnostic for "are losers riding to the stop".
            var winVals = entryPnl.Values.Where(v => v > 0).ToList();
            var lossVals = entryPnl.Values.Where(v => v < 0).ToList();
            double? avgWinEntry = winVals.Count > 0 ? (double?)Math.Round(winVals.Average(), 2) : null;
            double? avgLossEntry = lossVals.Count > 0 ? (double?)Math.Round(lossVals.Average(), 2) : null;
            double? maxLossEntry = lossVals.Count > 0 ? (double?)Math.Round(lossVals.Min(), 2) : null;
            return new
            {
                summary = SafeToString(entry),
                metrics = new
                {
                    entries,
                    entryWinRatePct = entries > 0 ? Math.Round(100.0 * winEntries / entries, 1) : 0,   // per-entry (kernel-comparable)
                    avgWinEntry,
                    avgLossEntry,
                    maxLossEntry,
                    exitReasons,
                    totalTrades = D(tp, "TradesCount"),
                    winners,
                    losers,
                    tradeWinRatePct = total > 0 ? Math.Round(100.0 * winners / total, 1) : 0,           // per-NT8-trade (incl. scale-outs)
                    profitFactor = gl != 0 ? Math.Round(gp / Math.Abs(gl), 3) : (double?)null,
                    tradesPerDay = D(tp, "TradesPerDay"),
                    grossProfit = gp,
                    grossLoss = gl,
                    totalCommission = D(tp, "TotalCommission"),
                    maxConsecWinners = D(tp, "MaxConsecutiveWinner"),
                    maxConsecLosers = D(tp, "MaxConsecutiveLoser"),
                    netProfit = D(cur, "CumProfit"),
                    maxDrawdown = D(cur, "Drawdown"),
                    avgProfit = D(cur, "AverageProfit"),
                    largestWinner = D(cur, "LargestWinner"),
                    largestLoser = D(cur, "LargestLoser"),
                    stdDev = D(cur, "StdDev"),
                    firstEntry = firstEntry == DateTime.MaxValue ? null : (DateTime?)firstEntry,
                    lastExit = lastExit == DateTime.MinValue ? null : (DateTime?)lastExit,
                },
                tradeCount = total,
                tradesReturned = trades.Count,
                dateRange = _dateNote,
                trades,
            };
        }

        private static object D(object obj, string prop)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj); }
            catch { return null; }
        }

        // ── small reflection helpers (instance) ──
        private static object GetP(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            return t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
                   ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
        }
        private static void SetP(object o, string name, object val)
        {
            var pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null)
            {
                if (val != null && !pi.PropertyType.IsInstanceOfType(val))
                    try { val = pi.PropertyType.IsEnum ? Enum.ToObject(pi.PropertyType, Convert.ToInt64(val)) : Convert.ChangeType(val, pi.PropertyType); } catch { }
                pi.SetValue(o, val);
            }
            else o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(o, val);
        }
        private static object InvokeM(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == (args?.Length ?? 0));
            return m?.Invoke(o, args);
        }

        // ═══════════════════════════════════════════════════════════════
        //  DEV reflection RPC — probe/drive NT8 internals over HTTP so we
        //  can discover the Strategy Analyzer / compile behaviour without
        //  recompiling this addon on every iteration. Localhost + dev-gated.
        //
        //  Body: { "ops": [ { op, ... }, ... ] }  executed in order.
        //  Ops:
        //    listMembers { type, [assembly] }
        //    getStatic   { type, member, [assembly] }
        //    setStatic   { type, member, value, [assembly] }
        //    invokeStatic{ type, method, [args], [assembly] }
        //    getProp     { target(handle/ref), member }
        //    invoke      { target, method, [args] }
        //    new         { type, [args], [assembly] }
        //  Args accept literals or {"$ref":"h3"} / {"$type":"...","value":...} coercions.
        //  Any op result that is a non-primitive object is stored and returned as a handle
        //  ("h1", "h2", ...) reusable as a target/$ref in later ops.
        // ═══════════════════════════════════════════════════════════════

        private object DevReflect(string body)
        {
            var req = JObject.Parse(body ?? "{}");
            var ops = req["ops"] as JArray ?? new JArray();

            // WPF objects (windows, viewmodels) must be touched on the UI dispatcher.
            // Pass "ui": true to run the whole op batch on it.
            if (req.Bool("ui", false))
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null) return new { error = "no WPF dispatcher (Application.Current is null)" };
                object uiResult = null;
                disp.Invoke((Action)(() => { uiResult = RunOps(ops); }));
                return uiResult;
            }
            return RunOps(ops);
        }

        private object RunOps(JArray ops)
        {
            var results = new List<object>();
            _batchHandles = new List<string>();
            foreach (var opTok in ops)
            {
                var op = (JObject)opTok;
                var kind = op.Str("op");
                _lastHandle = null;
                try { var r = RunOp(kind, op); _batchHandles.Add(_lastHandle); results.Add(r); }
                catch (Exception ex) { _batchHandles.Add(null); results.Add(new { op = kind, error = ex.Message, stack = ex.StackTrace }); return new { results }; }
            }
            return new { results };
        }
        private string _lastHandle;
        private List<string> _batchHandles;

        private object RunOp(string kind, JObject op)
        {
            switch (kind)
            {
                case "listMembers":
                {
                    var t = ResolveType(op);
                    return new
                    {
                        type = t.FullName,
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(m => $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
                            .OrderBy(s => s).Distinct().ToList(),
                        properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(p => $"{p.PropertyType.Name} {p.Name}").OrderBy(s => s).ToList(),
                        fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(f => $"{f.FieldType.Name} {f.Name}").OrderBy(s => s).ToList(),
                    };
                }
                case "getStatic":
                {
                    var t = ResolveType(op);
                    var member = op.Str("member");
                    var val = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                              ?? t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                    return Describe(val);
                }
                case "setStatic":
                {
                    var t = ResolveType(op);
                    var member = op.Str("member");
                    var val = Coerce(op["value"]);
                    var pi = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (pi != null) pi.SetValue(null, val);
                    else t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, val);
                    return new { ok = true };
                }
                case "invokeStatic":
                {
                    var t = ResolveType(op);
                    var args = CoerceArgs(op["args"]);
                    var mi = FindMethod(t, op.Str("method"), args, true);
                    var val = mi.Invoke(null, args);
                    return Describe(val);
                }
                case "new":
                {
                    var t = ResolveType(op);
                    var args = CoerceArgs(op["args"]);
                    var val = Activator.CreateInstance(t, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                    return Describe(val);
                }
                case "getProp":
                {
                    var target = Coerce(op["target"]);
                    var member = op.Str("member");
                    var t = target.GetType();
                    var val = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
                              ?? t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
                    return Describe(val);
                }
                case "setProp":
                {
                    var target = Coerce(op["target"]);
                    var member = op.Str("member");
                    var t = target.GetType();
                    var val = Coerce(op["value"]);
                    var pi = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi != null)
                    {
                        // coerce numeric/enum to the property type when needed
                        if (val != null && !pi.PropertyType.IsInstanceOfType(val))
                        {
                            try { val = pi.PropertyType.IsEnum ? Enum.ToObject(pi.PropertyType, Convert.ToInt64(val)) : Convert.ChangeType(val, pi.PropertyType); }
                            catch { }
                        }
                        pi.SetValue(target, val);
                    }
                    else t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, val);
                    return new { ok = true, member };
                }
                case "invoke":
                {
                    var target = Coerce(op["target"]);
                    var args = CoerceArgs(op["args"]);
                    var mi = FindMethod(target.GetType(), op.Str("method"), args, false);
                    var val = mi.Invoke(target, args);
                    return Describe(val);
                }
                default:
                    throw new Exception($"unknown op: {kind}");
            }
        }

        private Type ResolveType(JObject op)
        {
            var typeName = op.Str("type");
            var asm = op.Str("assembly");
            Type t = asm != null ? Type.GetType($"{typeName}, {asm}") : null;
            if (t == null) t = Type.GetType(typeName);
            if (t == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { t = a.GetType(typeName); if (t != null) break; }
            if (t == null) throw new Exception($"type not found: {typeName}");
            return t;
        }

        private MethodInfo FindMethod(Type t, string name, object[] args, bool isStatic)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var candidates = t.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == (args?.Length ?? 0)).ToList();
            if (candidates.Count == 0) throw new Exception($"method not found: {t.FullName}.{name}/{args?.Length ?? 0}");
            return candidates[0];
        }

        private object[] CoerceArgs(JToken argsTok)
        {
            if (!(argsTok is JArray arr)) return new object[0];
            return arr.Select(Coerce).ToArray();
        }

        // Coerce a JSON token to a CLR value. Supports literals, {"$ref":"h1"} handles,
        // {"$type":"...","value":...} typed values, and {"$enum":"Type.Value"}.
        private object Coerce(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (tok is JObject o)
            {
                if (o["$ref"] != null) { var id = o.Str("$ref"); return _handles.TryGetValue(id, out var h) ? h : throw new Exception($"no handle {id}"); }
                if (o["$result"] != null)
                {
                    int n = (int)o["$result"];
                    var hid = (_batchHandles != null && n >= 0 && n < _batchHandles.Count) ? _batchHandles[n] : null;
                    if (hid == null || !_handles.TryGetValue(hid, out var hv)) throw new Exception($"$result {n} is not a handle");
                    return hv;
                }
                if (o["$enum"] != null)
                {
                    var s = o.Str("$enum");
                    var idx = s.LastIndexOf('.');
                    var et = ResolveType(new JObject { ["type"] = s.Substring(0, idx), ["assembly"] = o.Str("assembly") });
                    return Enum.Parse(et, s.Substring(idx + 1));
                }
                if (o["$type"] != null)
                {
                    var target = ResolveType(new JObject { ["type"] = o.Str("$type"), ["assembly"] = o.Str("assembly") });
                    return Convert.ChangeType(((JValue)o["value"]).Value, target);
                }
                if (o["$strlist"] != null) return o["$strlist"].Select(x => x.ToString()).ToList();
            }
            if (tok is JArray a) return a.Select(x => (object)((JValue)x).Value).ToList();
            var v = (JValue)tok;
            return v.Value;
        }

        // Turn a return value into a JSON-friendly description; register non-trivial
        // objects as reusable handles.
        private object Describe(object val)
        {
            if (val == null) return new { value = (object)null };
            var t = val.GetType();
            if (val is string || t.IsPrimitive || val is DateTime || val is decimal || t.IsEnum)
                return new { type = t.FullName, value = val.ToString() };
            if (val is IEnumerable en && !(val is IDictionary))
            {
                var items = new List<object>();
                int n = 0;
                foreach (var item in en) { items.Add(item?.ToString()); if (++n >= 50) break; }
                return new { type = t.FullName, count = items.Count, items };
            }
            var id = "h" + (++_handleSeq);
            _handles[id] = val;
            _lastHandle = id;
            return new { handle = id, type = t.FullName, toString = SafeToString(val) };
        }

        private static string SafeToString(object v) { try { return v.ToString(); } catch { return "<toString threw>"; } }

        // ═══════════════════════════════════════════════════════════════
        //  Phase 1 handlers (unchanged)
        // ═══════════════════════════════════════════════════════════════

        // Account.Get in NT8.1 requires the account's currency (Denomination).
        private static double AcctGet(Account a, AccountItem item)
        {
            try { return a.Get(item, a.Denomination); } catch { return 0; }
        }

        private object GetAccountInfo()
        {
            var accounts = new List<object>();
            foreach (Account account in Account.All)
                accounts.Add(new
                {
                    name = account.Name,
                    provider = account.Provider.ToString(),
                    denomination = account.Denomination.ToString(),
                    cashValue = AcctGet(account, AccountItem.CashValue),
                    netLiquidation = AcctGet(account, AccountItem.NetLiquidation),
                    realizedPnL = AcctGet(account, AccountItem.RealizedProfitLoss),
                    unrealizedPnL = AcctGet(account, AccountItem.UnrealizedProfitLoss),
                    buyingPower = AcctGet(account, AccountItem.BuyingPower),
                });
            return accounts;
        }

        private object GetPositions()
        {
            var positions = new List<object>();
            foreach (Account account in Account.All)
                foreach (Position pos in account.Positions)
                {
                    if (pos.Instrument == null || pos.MarketPosition == MarketPosition.Flat) continue;
                    double upnl = 0;
                    try { upnl = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency); } catch { }
                    positions.Add(new
                    {
                        account = account.Name,
                        symbol = pos.Instrument.FullName,
                        marketPosition = pos.MarketPosition.ToString(),
                        quantity = pos.Quantity,
                        avgPrice = pos.AveragePrice,
                        unrealizedPnL = upnl,
                    });
                }
            return positions;
        }

        private object GetOrders()
        {
            var orders = new List<object>();
            foreach (Account account in Account.All)
                foreach (Order order in account.Orders)
                {
                    if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled) continue;
                    orders.Add(new
                    {
                        id = order.Id.ToString(), orderId = order.OrderId, name = order.Name, account = account.Name,
                        symbol = order.Instrument?.FullName, action = order.OrderAction.ToString(),
                        orderType = order.OrderType.ToString(), quantity = order.Quantity,
                        limitPrice = order.LimitPrice, stopPrice = order.StopPrice,
                        state = order.OrderState.ToString(), filled = order.Filled, time = order.Time,
                    });
                }
            return orders;
        }

        private object PlaceOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            var account = Account.All.FirstOrDefault();
            if (account == null) return new { error = "no account available" };

            var symbol = req.GetValueOrDefault("symbol")?.ToString();
            var actionStr = req.GetValueOrDefault("action")?.ToString();
            var orderTypeStr = req.GetValueOrDefault("orderType")?.ToString() ?? "Market";
            var quantity = Convert.ToInt32(req.GetValueOrDefault("quantity", 1));

            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(actionStr))
                return new { error = "symbol and action required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            var orderAction = actionStr.Equals("buy", StringComparison.OrdinalIgnoreCase) ? OrderAction.Buy : OrderAction.Sell;
            var orderType = (OrderType)Enum.Parse(typeof(OrderType), orderTypeStr, true);

            var tifStr = req.GetValueOrDefault("timeInForce")?.ToString() ?? "Day";
            var tif = (TimeInForce)Enum.Parse(typeof(TimeInForce), tifStr, true);
            double limitPrice = Convert.ToDouble(req.GetValueOrDefault("price", 0));
            double stopPrice = Convert.ToDouble(req.GetValueOrDefault("stopPrice", 0));

            // NT8.1: CreateOrder(instrument, action, type, timeInForce, qty, limit, stop, oco, name, customOrder)
            var order = account.CreateOrder(instrument, orderAction, orderType, tif, quantity, limitPrice, stopPrice, string.Empty, "McpBridge", null);
            account.Submit(new[] { order });
            return new { status = "submitted", id = order.Id.ToString(), orderId = order.OrderId, orderName = order.Name };
        }

        private static bool OrderMatches(Order o, string key)
            => o.OrderId == key || o.Name == key || o.Id.ToString() == key;

        private object CancelOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            var orderId = req.GetValueOrDefault("orderId")?.ToString();
            foreach (Account account in Account.All)
                foreach (Order order in account.Orders)
                    if (OrderMatches(order, orderId))
                    {
                        account.Cancel(new[] { order });
                        return new { status = "cancelled", orderId };
                    }
            return new { error = $"order not found: {orderId}" };
        }

        private object CancelAllOrders()
        {
            int count = 0;
            foreach (Account account in Account.All)
            {
                var toCancel = account.Orders
                    .Where(o => o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled).ToList();
                if (toCancel.Count > 0) { account.Cancel(toCancel); count += toCancel.Count; }
            }
            return new { status = "cancelled", count };
        }

        private object GetQuote(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };
            try
            {
                // NT8.1: Level1 snapshot via instrument.MarketData (populated once subscribed).
                var md = instrument.MarketData;
                if (md == null) return new { symbol = instrument.FullName, error = "no market data (not subscribed)" };
                return new
                {
                    symbol = instrument.FullName,
                    last = md.Last?.Price ?? 0, bid = md.Bid?.Price ?? 0, ask = md.Ask?.Price ?? 0,
                    bidSize = md.Bid?.Volume ?? 0, askSize = md.Ask?.Volume ?? 0,
                    volume = md.DailyVolume?.Volume ?? 0,
                    high = md.DailyHigh?.Price ?? 0, low = md.DailyLow?.Price ?? 0,
                    time = md.Last?.Time ?? DateTime.MinValue,
                };
            }
            catch (Exception ex) { return new { symbol, error = $"no market data: {ex.Message}" }; }
        }

        private object GetBars(string symbol, string periodStr, int periodValue, int count)
        {
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            var periodType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), periodStr, true);
            var barsPeriod = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };

            // NT8.1: historical bars are fetched asynchronously via BarsRequest.
            string status = null;
            var done = new ManualResetEventSlim(false);
            Bars bars = null;
            using (var request = new BarsRequest(instrument, count) { BarsPeriod = barsPeriod })
            {
                request.Request((req, code, msg) =>
                {
                    status = code.ToString();
                    bars = req.Bars;
                    done.Set();
                });
                if (!done.Wait(TimeSpan.FromSeconds(30)))
                    return new { symbol, error = "bars request timed out" };

                if (bars == null || bars.Count == 0)
                    return new { symbol, period = periodStr, periodValue, status, bars = new List<object>() };

                var result = new List<object>();
                for (int i = Math.Max(0, bars.Count - count); i < bars.Count; i++)
                    result.Add(new
                    {
                        time = bars.GetTime(i), open = bars.GetOpen(i), high = bars.GetHigh(i),
                        low = bars.GetLow(i), close = bars.GetClose(i), volume = bars.GetVolume(i),
                    });
                return new { symbol, period = periodStr, periodValue, count = result.Count, bars = result };
            }
        }

        // Export historical OHLCV bars over a DATE RANGE to a CSV file (for large pulls that would be
        // impractical inline). Returns a summary + the file path/name; fetch the content via
        // GET /api/export?name=<file>. NT8 fetches missing history from the data provider on demand.
        private object ExportBars(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbol = req.Str("symbol");
            var periodStr = req.Str("period") ?? "Minute";
            int periodValue = req["periodValue"] != null ? (int)req["periodValue"] : 1;
            int timeoutSec = req["timeoutSec"] != null ? (int)req["timeoutSec"] : 180;
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            if (!DateTime.TryParse(req.Str("from"), out var from)) return new { error = "from (YYYY-MM-DD) required" };
            if (!DateTime.TryParse(req.Str("to"), out var to)) to = DateTime.Now;

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };
            var periodType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), periodStr, true);
            var bp = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };

            int pv = Math.Max(1, periodValue);
            // Continuous-contract merge policy. DoNotMerge = the resolved single contract (default).
            // MergeNonBackAdjusted = real front-month prices spliced at rolls, NO price adjustment
            // (matches a research-grade continuous series). MergeBackAdjusted shifts historical prices
            // by cumulative roll gaps — do NOT use it for log-ratio / spread work.
            var mergeStr = req.Str("merge") ?? "DoNotMerge";
            MergePolicy merge;
            if (!Enum.TryParse(mergeStr, true, out merge)) merge = MergePolicy.DoNotMerge;
            // Warm the subscription (harmless for live contracts; expired ones fall back to history).
            try { var _ = instrument.MarketData; } catch { }

            string status = null;
            var done = new ManualResetEventSlim(false);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var safe = System.Text.RegularExpressions.Regex.Replace(symbol, "[^A-Za-z0-9]", "_");
            var name = $"mcp_bars_{safe}_{periodStr}{pv}.csv";
            var path = Path.Combine(Globals.UserDataDir, name);

            // Direct DATE-RANGE request (from/to are local time). This downloads exactly the window
            // from the provider — no oversized count, no client-side filtering.
            using (var request = new BarsRequest(instrument, from, to) { BarsPeriod = bp, MergePolicy = merge })
            {
                request.Request((r, code, msg) => { status = code.ToString(); done.Set(); });
                if (!done.Wait(TimeSpan.FromSeconds(timeoutSec)))
                    return new { error = $"bars request timed out after {timeoutSec}s", symbol };

                // IMPORTANT: read Bars BEFORE the BarsRequest is disposed (dispose clears them).
                var bars = request.Bars;
                if (bars == null || bars.Count == 0)
                    return new { error = "no bars returned (provider may lack history for this range)", status, symbol };

                using (var w = new StreamWriter(path, false))
                {
                    w.WriteLine("time,open,high,low,close,volume");
                    for (int i = 0; i < bars.Count; i++)
                        w.WriteLine(string.Join(",",
                            bars.GetTime(i).ToString("yyyy-MM-ddTHH:mm:ss"),
                            bars.GetOpen(i).ToString(ci), bars.GetHigh(i).ToString(ci),
                            bars.GetLow(i).ToString(ci), bars.GetClose(i).ToString(ci),
                            bars.GetVolume(i).ToString(ci)));
                }
                return new
                {
                    symbol = instrument.FullName, period = periodStr, periodValue = pv,
                    merge = merge.ToString(),
                    rows = bars.Count,
                    first = bars.GetTime(0), last = bars.GetTime(bars.Count - 1),
                    timeNote = "bar CLOSE time in NT8's configured timezone",
                    file = name, path,
                    fetch = $"GET /api/export?name={name}",
                };
            }
        }

        // Return the content of an export CSV (whitelisted to mcp_*.csv in the NT8 user-data dir),
        // so exports/signal logs are pullable over the (private) network without file access.
        private object ReadExportFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new { error = "name required" };
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.StartsWith("mcp_") || !name.EndsWith(".csv"))
                return new { error = "only mcp_*.csv export files are readable" };
            var path = Path.Combine(Globals.UserDataDir, name);
            if (!File.Exists(path)) return new { error = $"not found: {name}" };
            return new { name, bytes = new FileInfo(path).Length, content = File.ReadAllText(path) };
        }

        private object SearchInstruments(string query)
        {
            if (string.IsNullOrEmpty(query)) return new List<object>();
            var results = new List<object>();
            // NT8.1: Instrument.All is the set of known/loaded instruments.
            foreach (var inst in Instrument.All
                .Where(i => i.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(20))
                results.Add(new
                {
                    name = inst.FullName, symbol = inst.MasterInstrument?.Name ?? inst.FullName,
                    exchange = inst.Exchange.ToString(), type = inst.MasterInstrument?.InstrumentType.ToString(),
                });
            return results;
        }

        // ─── Helpers ──────────────────────────────────────────────────
        private void WriteResponse(HttpListenerContext ctx, int code, object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var buffer = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }

        private void Log(string message, LogLevel level = LogLevel.Information)
            => NinjaTrader.Code.Output.Process("[McpBridge] " + message, PrintTo.OutputTab1);
    }
}

public static class DictionaryExtensions
{
    public static object GetValueOrDefault(this Dictionary<string, object> dict, string key, object defaultValue = null)
        => dict.TryGetValue(key, out var val) ? val : defaultValue;
}

// Safe JObject accessors. JObject.Value<T>(string) resolves to the IEnumerable<JToken>
// extension and throws "Cannot cast JObject to JToken", so use indexer access instead.
public static class JObjectExtensions
{
    public static string Str(this Newtonsoft.Json.Linq.JObject o, string key)
    {
        var t = o?[key];
        return (t == null || t.Type == Newtonsoft.Json.Linq.JTokenType.Null) ? null : t.ToString();
    }

    public static bool Bool(this Newtonsoft.Json.Linq.JObject o, string key, bool dflt = false)
    {
        var t = o?[key];
        if (t == null || t.Type == Newtonsoft.Json.Linq.JTokenType.Null) return dflt;
        try { return (bool)t; } catch { return dflt; }
    }
}
