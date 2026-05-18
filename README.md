# NT8 MCP Bridge — Hermes → NinjaTrader 8

Связка MCP-сервера и NT8 AddOn для управления NinjaTrader 8 через Hermes/Claude.

## Архитектура

```
Hermes (я)
    ↓ MCP stdio (JSON-RPC)
nt-mcp-server.js (Node.js, zero deps)
    ↓ HTTP localhost:7890
McpBridgeAddOn.dll (C# NinjaScript, живёт внутри NT8)
    ↓
NT8: Account / Positions / Orders / MarketData / Bars / Strategy Analyzer
```

## Установка

### 1. NinjaTrader 8 AddOn

1. Открой **NinjaTrader 8**
2. `New → NinjaScript Editor` (F11)
3. В панели слева: правой кнопкой по `AddOns` → `New AddOn...`
4. Замени содержимое файла на `nt8-addon/McpBridgeAddOn.cs`
5. Нажми **F5** (Compile)
6. Перезапусти NT8

Или просто скопируй `McpBridgeAddOn.cs` в:
```
Documents\NinjaTrader 8\bin\Custom\AddOns\
```
И скомпилируй через NinjaScript Editor (F5).

После запуска NT8 — AddOn стартует автоматически на порту **7890**.

Проверка:
```powershell
curl http://localhost:7890/api/health
# → {"status":"ok","timestamp":"...","version":"0.1.0"}
```

### 2. MCP-сервер (Node.js)

```powershell
cd trading-scalping/nt-mcp
node nt-mcp-server.js
```

Должен написать в консоль:
```
[nt-mcp] Server started — NT8 at http://127.0.0.1:7890
[nt-mcp] Waiting for MCP messages on stdin...
```

### 3. Hermes Config

Добавить в `~/.hermes/config.yaml`:

```yaml
mcpServers:
  ninjatrader:
    command: node
    args:
      - C:\Users\YOUR_USER\Documents\projects\trading-scalping\nt-mcp\nt-mcp-server.js
    transport: stdio
```

После этого я смогу вызывать инструменты `nt_health`, `nt_orders`, `nt_positions` и т.д.

## Инструменты (Phase 1)

| Инструмент | Описание |
|-----------|----------|
| `nt_health` | Проверка соединения с NT8 |
| `nt_accounts` | Список счетов и балансов |
| `nt_positions` | Открытые позиции |
| `nt_orders` | Работающие ордеры |
| `nt_place_order` | Разместить ордер (Market/Limit/Stop) |
| `nt_cancel_order` | Отменить ордер по ID |
| `nt_cancel_all_orders` | Отменить все ордеры |
| `nt_quote` | Текущая котировка (bid/ask/last/volume) |
| `nt_bars` | Исторические бары OHLCV |
| `nt_search` | Поиск инструментов |
| `nt_execute_strategy` | Запуск стратегии (Phase 2) |

## Phase 2 (план)

- `nt_backtest` — запуск Strategy Analyzer, получение результатов
- `nt_strategy_performance` — метрики (PF, Sharpe, DD)
- `nt_chart_state` — состояние открытых чартов
- `nt_indicator_values` — значения индикаторов на чарте
- `nt_compile` — компиляция NinjaScript в памяти

## Requirements

- Node.js 18+
- NinjaTrader 8 (Lifetime или Trial)
- Windows (NT8 только Windows)
