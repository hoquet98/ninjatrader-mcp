// McpBridgeAddOn.cs — NinjaTrader 8 AddOn, HTTP API on port 7890
// Compile in NT8: File → Utilities → NinjaScript Editor → right-click → Compile
// Or: copy to Documents\NinjaTrader 8\bin\Custom\AddOns\

#region Using declarations
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Core;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class McpBridgeAddOn : AddOnBase
    {
        private HttpListener _listener;
        private Thread _serverThread;
        private bool _running;

        protected override void OnStartUp()
        {
            _running = true;
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:7890/");
            _listener.Start();

            _serverThread = new Thread(HandleRequests);
            _serverThread.IsBackground = true;
            _serverThread.Start();

            Log("McpBridgeAddOn started on http://localhost:7890", LogLevel.Information);
        }

        protected override void OnShutDown()
        {
            _running = false;
            _listener?.Stop();
            _listener?.Close();
            Log("McpBridgeAddOn stopped", LogLevel.Information);
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
                catch (Exception ex)
                {
                    Log($"Error: {ex.Message}", LogLevel.Error);
                }
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
                WriteResponse(context, 500, new { error = ex.Message });
            }
        }

        private object RouteRequest(string path, string method, string body, System.Collections.Specialized.NameValueCollection query)
        {
            switch (path)
            {
                case "/api/health":
                    return new { status = "ok", timestamp = DateTime.UtcNow, version = "0.1.0" };

                case "/api/account":
                    return GetAccountInfo();

                case "/api/positions":
                    return GetPositions();

                case "/api/orders":
                    return GetOrders();

                case "/api/quote":
                    return GetQuote(query["symbol"]);

                case "/api/bars":
                    return GetBars(
                        query["symbol"],
                        query["period"] ?? "Minute",
                        int.Parse(query["periodValue"] ?? "1"),
                        int.Parse(query["count"] ?? "100")
                    );

                case "/api/search":
                    return SearchInstruments(query["query"]);

                case "/api/order":
                    if (method == "POST") return PlaceOrder(body);
                    return new { error = "method not allowed" };

                case "/api/order/cancel":
                    if (method == "POST") return CancelOrder(body);
                    return new { error = "method not allowed" };

                case "/api/orders/cancel-all":
                    if (method == "POST") return CancelAllOrders();
                    return new { error = "method not allowed" };

                case "/api/strategy/start":
                    if (method == "POST") return StartStrategy(body);
                    return new { error = "method not allowed" };

                default:
                    throw new Exception($"Unknown endpoint: {path}");
            }
        }

        // ─── Account ──────────────────────────────────────────────────
        private object GetAccountInfo()
        {
            var accounts = new List<object>();
            foreach (Account account in Account.All)
            {
                accounts.Add(new
                {
                    name = account.Name,
                    accountType = account.AccountType.ToString(),
                    cashValue = account.Get(AccountItem.CashValue),
                    unrealizedPnL = account.Get(AccountItem.UnrealizedPnL),
                    buyingPower = account.Get(AccountItem.BuyingPower),
                });
            }
            return accounts;
        }

        // ─── Positions ────────────────────────────────────────────────
        private object GetPositions()
        {
            var positions = new List<object>();
            foreach (Account account in Account.All)
            {
                foreach (Position pos in account.Positions)
                {
                    if (pos.Instrument == null) continue;
                    positions.Add(new
                    {
                        account = account.Name,
                        symbol = pos.Instrument.FullName,
                        quantity = pos.Quantity,
                        avgPrice = pos.AveragePrice,
                        unrealizedPnL = pos.UnrealizedPnL,
                        marketValue = pos.MarketValue,
                    });
                }
            }
            return positions;
        }

        // ─── Orders ───────────────────────────────────────────────────
        private object GetOrders()
        {
            var orders = new List<object>();
            foreach (Account account in Account.All)
            {
                foreach (Order order in account.Orders)
                {
                    if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled)
                        continue;
                    orders.Add(new
                    {
                        id = order.Id,
                        name = order.Name,
                        account = account.Name,
                        symbol = order.Instrument?.FullName,
                        action = order.OrderAction.ToString(),
                        orderType = order.OrderType.ToString(),
                        quantity = order.Quantity,
                        limitPrice = order.LimitPrice,
                        stopPrice = order.StopPrice,
                        state = order.OrderState.ToString(),
                        filled = order.Filled,
                        time = order.Time,
                    });
                }
            }
            return orders;
        }

        // ─── Place Order ──────────────────────────────────────────────
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

            var orderAction = actionStr.Equals("buy", StringComparison.OrdinalIgnoreCase)
                ? OrderAction.Buy : OrderAction.Sell;
            var orderType = (OrderType)Enum.Parse(typeof(OrderType), orderTypeStr, true);

            var order = account.CreateOrder(instrument, orderAction, orderType, quantity, 0, 0, string.Empty, "McpBridge");
            account.Submit(order);

            return new { status = "submitted", orderId = order.Id, orderName = order.Name };
        }

        // ─── Cancel Order ─────────────────────────────────────────────
        private object CancelOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            var orderId = req.GetValueOrDefault("orderId")?.ToString();

            foreach (Account account in Account.All)
            {
                foreach (Order order in account.Orders)
                {
                    if (order.Id == orderId || order.Name == orderId)
                    {
                        account.Cancel(order);
                        return new { status = "cancelled", orderId };
                    }
                }
            }
            return new { error = $"order not found: {orderId}" };
        }

        // ─── Cancel All ───────────────────────────────────────────────
        private object CancelAllOrders()
        {
            int count = 0;
            foreach (Account account in Account.All)
            {
                var toCancel = account.Orders
                    .Where(o => o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled)
                    .ToList();
                foreach (var order in toCancel)
                {
                    account.Cancel(order);
                    count++;
                }
            }
            return new { status = "cancelled", count };
        }

        // ─── Quote ────────────────────────────────────────────────────
        private object GetQuote(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            // Try to get it from MarketData
            try
            {
                var md = MarketData.GetMarketData(instrument);
                return new
                {
                    symbol = instrument.FullName,
                    last = md.Last?.Price ?? 0,
                    bid = md.Bid?.Price ?? 0,
                    ask = md.Ask?.Price ?? 0,
                    bidSize = md.Bid?.Size ?? 0,
                    askSize = md.Ask?.Size ?? 0,
                    volume = md.DailyVolume,
                    high = md.High?.Price ?? 0,
                    low = md.Low?.Price ?? 0,
                    time = md.Last?.Time ?? DateTime.MinValue,
                };
            }
            catch
            {
                return new { symbol, error = "no market data" };
            }
        }

        // ─── Bars ─────────────────────────────────────────────────────
        private object GetBars(string symbol, string periodStr, int periodValue, int count)
        {
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            var periodType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), periodStr, true);
            var barsPeriod = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };

            var bars = instrument.GetBars(barsPeriod, DateTime.MinValue, DateTime.MaxValue, count);

            if (bars == null || bars.Count == 0)
                return new { symbol, bars = new List<object>() };

            var result = new List<object>();
            for (int i = Math.Max(0, bars.Count - count); i < bars.Count; i++)
            {
                result.Add(new
                {
                    time = bars.GetTime(i),
                    open = bars.GetOpen(i),
                    high = bars.GetHigh(i),
                    low = bars.GetLow(i),
                    close = bars.GetClose(i),
                    volume = bars.GetVolume(i),
                });
            }

            return new { symbol, period = periodStr, periodValue, count = result.Count, bars = result };
        }

        // ─── Search Instruments ─────────────────────────────────────────
        private object SearchInstruments(string query)
        {
            if (string.IsNullOrEmpty(query)) return new List<object>();

            var results = new List<object>();
            foreach (var inst in Instrument.GetAllInstruments()
                .Where(i => i.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(20))
            {
                results.Add(new
                {
                    name = inst.FullName,
                    symbol = inst.MasterInstrument?.Symbol ?? inst.Name,
                    exchange = inst.MasterInstrument?.Exchange?.Name,
                    type = inst.MasterInstrument?.InstrumentType.ToString(),
                });
            }
            return results;
        }

        // ─── Start Strategy ─────────────────────────────────────────────
        private object StartStrategy(string body)
        {
            // Stub — for Phase 2
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            return new
            {
                status = "not implemented",
                message = "Strategy execution will be available in Phase 2",
                requested = req.GetValueOrDefault("strategyName"),
            };
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
        {
            NinjaTrader.Code.Output.Process(message, PrintTo.Log);
        }
    }
}

public static class DictionaryExtensions
{
    public static object GetValueOrDefault(this Dictionary<string, object> dict, string key, object defaultValue = null)
    {
        return dict.TryGetValue(key, out var val) ? val : defaultValue;
    }
}
