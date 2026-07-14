#!/usr/bin/env node
/**
 * nt-mcp-server.js — MCP (Model Context Protocol) server for NinjaTrader 8
 *
 * Architecture:
 *   Claude/Hermes (MCP stdio)  →  nt-mcp-server.js  →  HTTP :7890  →  NT8 McpBridgeAddOn
 *
 * Zero npm dependencies. Uses only Node.js builtins.
 * Run: node nt-mcp-server.js
 */

import { createInterface } from 'node:readline';
import { request as httpRequest } from 'node:http';

// ─── Config ─────────────────────────────────────────────────────────────
const NT8_HOST = process.env.NT8_HOST || '127.0.0.1';
const NT8_PORT = parseInt(process.env.NT8_PORT || '7890', 10);
const NT8_BASE = `http://${NT8_HOST}:${NT8_PORT}`;

const SERVER_NAME = 'nt-mcp-server';
const SERVER_VERSION = '0.1.0';
const MCP_PROTOCOL_VERSION = '2024-11-05';

// ─── Tool Definitions ───────────────────────────────────────────────────
const TOOLS = [
  {
    name: 'nt_health',
    description: 'Check connection to NinjaTrader 8',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_accounts',
    description: 'List accounts and balances',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_positions',
    description: 'List open positions',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_orders',
    description: 'List working orders',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_place_order',
    description: 'Place an order',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:    { type: 'string', description: 'Ticker (NQ, ES, MES)' },
        action:    { type: 'string', enum: ['buy', 'sell'], description: 'Direction' },
        quantity:  { type: 'number', description: 'Number of contracts' },
        orderType: { type: 'string', enum: ['Market', 'Limit', 'StopMarket', 'StopLimit'], description: 'Order type' },
        price:     { type: 'number', description: 'Price (for Limit/Stop)' },
        stopPrice: { type: 'number', description: 'Stop price (for StopMarket/StopLimit)' },
        timeInForce: { type: 'string', enum: ['Day', 'GTC'], description: 'Time in force' },
      },
      required: ['symbol', 'action', 'quantity'],
    },
  },
  {
    name: 'nt_cancel_order',
    description: 'Cancel an order by ID',
    inputSchema: {
      type: 'object',
      properties: {
        orderId: { type: 'string', description: 'Order ID' },
      },
      required: ['orderId'],
    },
  },
  {
    name: 'nt_cancel_all_orders',
    description: 'Cancel all working orders',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_quote',
    description: 'Get the current quote',
    inputSchema: {
      type: 'object',
      properties: {
        symbol: { type: 'string', description: 'Ticker' },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_bars',
    description: 'Get historical bars (OHLCV)',
    inputSchema: {
      type: 'object',
      properties: {
        symbol: { type: 'string', description: 'Ticker' },
        period: { type: 'string', enum: ['Minute', 'Day', 'Tick', 'Volume', 'Range'], description: 'Period' },
        periodValue: { type: 'number', description: 'Period value (e.g. 5 for 5m)', default: 1 },
        count: { type: 'number', description: 'Number of bars', default: 100 },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_search',
    description: 'Search instruments by name',
    inputSchema: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Search query' },
      },
      required: ['query'],
    },
  },
  {
    name: 'nt_export_bars',
    description: 'Export historical OHLCV bars over a DATE RANGE to a CSV file on the NT8 machine (NT8 downloads missing history from the data provider on demand). Returns a summary (rows, actual range, filename). Fetch the CSV content with nt_get_export or GET /api/export?name=<file>.',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:      { type: 'string', description: 'Instrument (e.g. RTY 03-25, ES 09-26, M2K 09-26)' },
        from:        { type: 'string', description: 'Start date YYYY-MM-DD' },
        to:          { type: 'string', description: 'End date YYYY-MM-DD (default: now)' },
        period:      { type: 'string', enum: ['Minute', 'Day', 'Second', 'Tick', 'Volume', 'Range'], description: 'Bars period type', default: 'Minute' },
        periodValue: { type: 'number', description: 'Bars period value (e.g. 5 for 5m)', default: 1 },
        merge:       { type: 'string', enum: ['DoNotMerge', 'MergeNonBackAdjusted', 'MergeBackAdjusted'], description: 'DoNotMerge = the single anchored contract. MergeNonBackAdjusted = continuous series stitched across front months with NO price adjustment (anchor on any real contract like "ES 09-26"). NEVER use MergeBackAdjusted for spread/log-ratio work — it shifts historical prices by cumulative roll gaps.', default: 'DoNotMerge' },
        timeoutSec:  { type: 'number', description: 'Max seconds to wait for the provider download', default: 180 },
      },
      required: ['symbol', 'from'],
    },
  },
  {
    name: 'nt_get_export',
    description: 'Fetch the content of an export CSV created by nt_export_bars (or a signal log), by filename. WARNING: large files (100k+ bars) can be huge — prefer reading the file directly if on the NT8 machine.',
    inputSchema: {
      type: 'object',
      properties: { name: { type: 'string', description: 'Export filename, e.g. mcp_bars_RTY_03_25_Minute1.csv' } },
      required: ['name'],
    },
  },

  // ─── Phase 2: strategy authoring / compile / backtest ─────────────────
  {
    name: 'nt_list_strategies',
    description: 'List NinjaScript strategy source files in bin\\Custom\\Strategies (name, size, last modified)',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_strategy_source',
    description: 'Read the NinjaScript source of one strategy by class/file name',
    inputSchema: {
      type: 'object',
      properties: { name: { type: 'string', description: 'Strategy class/file name (no .cs)' } },
      required: ['name'],
    },
  },
  {
    name: 'nt_create_strategy',
    description: 'Write a NinjaScript strategy (.cs) into bin\\Custom\\Strategies. Pass full NinjaScript C# source. Call nt_compile afterward to build + hot-load it.',
    inputSchema: {
      type: 'object',
      properties: {
        name:      { type: 'string', description: 'Strategy class/file name (no .cs). Must match the class name in source.' },
        source:    { type: 'string', description: 'Full NinjaScript C# source (namespace NinjaTrader.NinjaScript.Strategies, class : Strategy)' },
        overwrite: { type: 'boolean', description: 'Overwrite if it already exists', default: true },
      },
      required: ['name', 'source'],
    },
  },
  {
    name: 'nt_compile',
    description: 'Recompile all NinjaScript in-process (Roslyn, hot-swap, no NT8 restart). Returns success + any compile errors/warnings. Run after nt_create_strategy.',
    inputSchema: {
      type: 'object',
      properties: { debug: { type: 'boolean', description: 'Emit a debug build', default: false } },
    },
  },
  {
    name: 'nt_backtest',
    description: 'Run a backtest of a compiled strategy via the NT8 Strategy Analyzer over a configurable symbol, date range, timeframe, and parameters. Returns performance metrics (net P&L, drawdown, gross P/L, trade count) + a capped trade list.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy:    { type: 'string', description: 'Strategy class name (must be compiled first)' },
        symbol:      { type: 'string', description: 'Instrument (e.g. GC 08-26, NQ, ES)' },
        from:        { type: 'string', description: 'Start date YYYY-MM-DD (defaults to the Strategy Analyzer range if omitted)' },
        to:          { type: 'string', description: 'End date YYYY-MM-DD (defaults to the Strategy Analyzer range if omitted)' },
        period:      { type: 'string', enum: ['Minute', 'Day', 'Tick', 'Second', 'Range', 'Volume'], description: 'Bars period type', default: 'Minute' },
        periodValue: { type: 'number', description: 'Bars period value (e.g. 5 for 5m)', default: 1 },
        params:      { type: 'object', description: 'Strategy parameter overrides { paramName: value }' },
        maxTrades:   { type: 'number', description: 'Max trades to include in the response (metrics always full)', default: 50 },
        timeoutSec:  { type: 'number', description: 'Server-side wait for the run to finish', default: 180 },
      },
      required: ['strategy', 'symbol'],
    },
  },
  {
    name: 'nt_strategy_status',
    description: 'List strategies NT8 is currently running (enabled on an account): type, state (Realtime/Historical/etc.), account, instrument, timeframe, market position and quantity. Read-only.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_deploy_strategy',
    description: 'Deploy a compiled strategy onto an OPEN chart and enable it (SIM-first). Adds the strategy to the chart for the given instrument, sets the account (default Sim101) + optional params, and enables it (Realtime). A live (non-sim) account requires confirmLive:true. Requires a chart already open for that instrument.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy:    { type: 'string', description: 'Compiled strategy class name (e.g. PathSignatureUnion)' },
        instrument:  { type: 'string', description: 'Instrument of an OPEN chart to deploy onto (e.g. NQ 09-26)' },
        account:     { type: 'string', description: 'Account name', default: 'Sim101' },
        params:      { type: 'object', description: 'Strategy parameter overrides { name: value }' },
        enable:      { type: 'boolean', description: 'Enable after adding', default: true },
        confirmLive: { type: 'boolean', description: 'Required to deploy to a non-sim (live) account', default: false },
      },
      required: ['strategy', 'instrument'],
    },
  },
  {
    name: 'nt_stop_strategy',
    description: 'Stop running strategies: disable and remove them from the chart, and (by default) flatten any open position with an offsetting market order. Filter by strategy class name and/or account (omit both to stop all).',
    inputSchema: {
      type: 'object',
      properties: {
        strategy: { type: 'string', description: 'Strategy class name to stop (omit = all)' },
        account:  { type: 'string', description: 'Limit to this account (omit = all)' },
        flatten:  { type: 'boolean', description: 'Flatten the stopped strategy\'s open position via an offsetting market order', default: true },
      },
    },
  },
  {
    name: 'nt_set_strategy_param',
    description: 'Change inputs on a RUNNING strategy live, with no restart. Examples: { "Qty": 2 } to resize; { "AllowLong": false, "AllowShort": false } to pause trading (it keeps calculating but opens nothing new — un-pause by setting them true). Only affects inputs the strategy re-reads each bar; startup-only inputs (instrument, account, session windows) need a disable/enable. Filter by strategy class name and/or account.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy: { type: 'string', description: 'Strategy class name (omit = all running)' },
        account:  { type: 'string', description: 'Limit to this account (omit = all)' },
        params:   { type: 'object', description: 'Inputs to set, e.g. { "Qty": 2 } or { "AllowLong": false, "AllowShort": false }' },
      },
      required: ['params'],
    },
  },
];

// ─── HTTP Client to NT8 AddOn ──────────────────────────────────────────
function ntFetch(endpoint, method = 'GET', body = null, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint, NT8_BASE);
    const options = {
      method,
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      headers: { 'Accept': 'application/json' },
      timeout: timeoutMs,
    };
    if (body) {
      const data = JSON.stringify(body);
      options.headers['Content-Type'] = 'application/json';
      options.headers['Content-Length'] = Buffer.byteLength(data);
    }

    const req = httpRequest(options, (res) => {
      let chunks = '';
      res.on('data', (chunk) => { chunks += chunk; });
      res.on('end', () => {
        try {
          const parsed = JSON.parse(chunks);
          resolve({ status: res.statusCode, data: parsed });
        } catch {
          resolve({ status: res.statusCode, data: chunks });
        }
      });
    });

    req.on('error', (err) => reject(new Error(`NT8 connection failed: ${err.message}`)));
    req.on('timeout', () => { req.destroy(); reject(new Error('NT8 timeout')); });

    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}

// ─── MCP Protocol ──────────────────────────────────────────────────────
const rl = createInterface({ input: process.stdin });

let messageId = 0;

function sendMessage(msg) {
  const str = JSON.stringify(msg);
  process.stdout.write(str + '\n');
}

function sendError(id, code, message) {
  sendMessage({ jsonrpc: '2.0', id, error: { code, message } });
}

function sendResult(id, result) {
  sendMessage({ jsonrpc: '2.0', id, result });
}

// ─── Tool Handlers ──────────────────────────────────────────────────────
async function handleToolCall(name, args) {
  switch (name) {
    case 'nt_health': {
      const res = await ntFetch('/api/health');
      return { status: res.status === 200 ? 'connected' : 'error', nt8: res.data };
    }

    case 'nt_accounts': {
      const res = await ntFetch('/api/account');
      return res.data;
    }

    case 'nt_positions': {
      const res = await ntFetch('/api/positions');
      return Array.isArray(res.data) ? res.data : [];
    }

    case 'nt_orders': {
      const res = await ntFetch('/api/orders');
      return Array.isArray(res.data) ? res.data : [];
    }

    case 'nt_place_order': {
      const res = await ntFetch('/api/order', 'POST', args);
      return res.data;
    }

    case 'nt_cancel_order': {
      const res = await ntFetch('/api/order/cancel', 'POST', { orderId: args.orderId });
      return res.data;
    }

    case 'nt_cancel_all_orders': {
      const res = await ntFetch('/api/orders/cancel-all', 'POST');
      return res.data;
    }

    case 'nt_quote': {
      const res = await ntFetch(`/api/quote?symbol=${encodeURIComponent(args.symbol)}`);
      return res.data;
    }

    case 'nt_bars': {
      const params = new URLSearchParams({
        symbol: args.symbol,
        period: args.period || 'Minute',
        periodValue: String(args.periodValue || 1),
        count: String(args.count || 100),
      });
      const res = await ntFetch(`/api/bars?${params}`);
      return res.data;
    }

    case 'nt_search': {
      const res = await ntFetch(`/api/search?query=${encodeURIComponent(args.query)}`);
      return res.data;
    }

    case 'nt_export_bars': {
      const timeoutMs = ((args.timeoutSec || 180) + 30) * 1000;
      const res = await ntFetch('/api/bars/export', 'POST', {
        symbol: args.symbol, from: args.from, to: args.to,
        period: args.period || 'Minute', periodValue: args.periodValue || 1,
        merge: args.merge || 'DoNotMerge',
        timeoutSec: args.timeoutSec || 180,
      }, timeoutMs);
      return res.data;
    }

    case 'nt_get_export': {
      const res = await ntFetch(`/api/export?name=${encodeURIComponent(args.name)}`, 'GET', null, 60000);
      return res.data;
    }

    case 'nt_strategy_status': {
      const res = await ntFetch('/api/strategy/running', 'GET', null, 30000);
      return res.data;
    }

    case 'nt_deploy_strategy': {
      const res = await ntFetch('/api/strategy/deploy', 'POST', args, 40000);
      return res.data;
    }

    case 'nt_stop_strategy': {
      const res = await ntFetch('/api/strategy/stop', 'POST', args, 40000);
      return res.data;
    }

    case 'nt_set_strategy_param': {
      const res = await ntFetch('/api/strategy/param', 'POST', args, 20000);
      return res.data;
    }

    // ─── Phase 2 ────────────────────────────────────────────────────────
    case 'nt_list_strategies': {
      const res = await ntFetch('/api/strategies');
      return res.data;
    }

    case 'nt_strategy_source': {
      const res = await ntFetch(`/api/strategy/source?name=${encodeURIComponent(args.name)}`);
      return res.data;
    }

    case 'nt_create_strategy': {
      const res = await ntFetch('/api/strategy/create', 'POST', {
        name: args.name,
        source: args.source,
        overwrite: args.overwrite !== false,
      });
      return res.data;
    }

    case 'nt_compile': {
      // A SUCCESSFUL compile hot-swaps the NinjaScript AppDomain, which tears down
      // the bridge's HTTP listener mid-response — the POST connection drops. That
      // dropped connection is actually the success signal. Either way, the authoritative
      // result is written to a durable file: read it back from /api/compile/result.
      try {
        await ntFetch('/api/compile', 'POST', { debug: !!args.debug }, 30000);
      } catch {
        // expected on success (connection reset by the hot-swap) — fall through to poll
      }
      // give the AppDomain a moment to reload, then fetch the durable result
      for (let i = 0; i < 15; i++) {
        await new Promise((r) => setTimeout(r, 1500));
        try {
          const res = await ntFetch('/api/compile/result', 'GET', null, 5000);
          if (res.status === 200 && res.data && typeof res.data === 'object') return res.data;
        } catch { /* bridge still reloading — retry */ }
      }
      return { error: 'compile result unavailable (bridge may still be reloading — try nt_compile result via /api/compile/result)' };
    }

    case 'nt_backtest': {
      // Backtests run synchronously server-side and can take a while; use a long timeout.
      const res = await ntFetch('/api/backtest', 'POST', args, 300000);
      return res.data;
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

// ─── Message Dispatch ──────────────────────────────────────────────────
rl.on('line', async (line) => {
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return; // invalid JSON, ignore
  }

  const { id, method, params } = msg;

  try {
    switch (method) {
      case 'initialize': {
        sendResult(id, {
          protocolVersion: MCP_PROTOCOL_VERSION,
          capabilities: { tools: {} },
          serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
        });
        break;
      }

      case 'notifications/initialized': {
        // no response needed
        break;
      }

      case 'tools/list': {
        sendResult(id, { tools: TOOLS });
        break;
      }

      case 'tools/call': {
        const { name, arguments: args } = params;
        const result = await handleToolCall(name, args || {});
        // Wrap result in content array as per MCP spec
        sendResult(id, {
          content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
        });
        break;
      }

      default: {
        sendError(id, -32601, `Method not found: ${method}`);
      }
    }
  } catch (err) {
    sendError(id, -32603, err.message);
  }
});

// ─── Startup ────────────────────────────────────────────────────────────
console.error(`[nt-mcp] Server started — NT8 at ${NT8_BASE}`);
console.error('[nt-mcp] Waiting for MCP messages on stdin...');
