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
    description: 'Проверить соединение с NinjaTrader 8',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_accounts',
    description: 'Список счетов и балансов',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_positions',
    description: 'Список открытых позиций',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_orders',
    description: 'Список работающих ордеров',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_place_order',
    description: 'Разместить ордер',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:    { type: 'string', description: 'Тикер (NQ, ES, MES)' },
        action:    { type: 'string', enum: ['buy', 'sell'], description: 'Направление' },
        quantity:  { type: 'number', description: 'Количество контрактов' },
        orderType: { type: 'string', enum: ['Market', 'Limit', 'StopMarket', 'StopLimit'], description: 'Тип ордера' },
        price:     { type: 'number', description: 'Цена (для Limit/Stop)' },
        stopPrice: { type: 'number', description: 'Стоп-цена (для StopMarket/StopLimit)' },
        timeInForce: { type: 'string', enum: ['Day', 'GTC'], description: 'Время жизни' },
      },
      required: ['symbol', 'action', 'quantity'],
    },
  },
  {
    name: 'nt_cancel_order',
    description: 'Отменить ордер по ID',
    inputSchema: {
      type: 'object',
      properties: {
        orderId: { type: 'string', description: 'ID ордера' },
      },
      required: ['orderId'],
    },
  },
  {
    name: 'nt_cancel_all_orders',
    description: 'Отменить все работающие ордеры',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_quote',
    description: 'Получить текущую котировку',
    inputSchema: {
      type: 'object',
      properties: {
        symbol: { type: 'string', description: 'Тикер' },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_bars',
    description: 'Получить исторические бары (OHLCV)',
    inputSchema: {
      type: 'object',
      properties: {
        symbol: { type: 'string', description: 'Тикер' },
        period: { type: 'string', enum: ['Minute', 'Day', 'Tick', 'Volume', 'Range'], description: 'Период' },
        periodValue: { type: 'number', description: 'Значение периода (например 5 для 5m)', default: 1 },
        count: { type: 'number', description: 'Количество баров', default: 100 },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_search',
    description: 'Поиск инструментов по имени',
    inputSchema: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Поисковый запрос' },
      },
      required: ['query'],
    },
  },
  {
    name: 'nt_execute_strategy',
    description: 'Запустить стратегию на чарте (локально)',
    inputSchema: {
      type: 'object',
      properties: {
        strategyName: { type: 'string', description: 'Имя стратегии (класс)' },
        symbol:       { type: 'string', description: 'Тикер' },
        chartPeriod:  { type: 'string', description: 'Период графика', default: 'Minute' },
        chartPeriodValue: { type: 'number', description: 'Значение периода', default: 1 },
      },
      required: ['strategyName', 'symbol'],
    },
  },
];

// ─── HTTP Client to NT8 AddOn ──────────────────────────────────────────
function ntFetch(endpoint, method = 'GET', body = null) {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint, NT8_BASE);
    const options = {
      method,
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      headers: { 'Accept': 'application/json' },
      timeout: 10000,
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

    case 'nt_execute_strategy': {
      const res = await ntFetch('/api/strategy/start', 'POST', args);
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
