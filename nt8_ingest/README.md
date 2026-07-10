# nt8_ingest — single-vendor NT8/Tradovate 1-minute OHLCV archive

Builds `nt8_ohlcv_bars` in the Railway `tradehub` Postgres from **one vendor end to end**
(NT8/Tradovate), provenance-tagged on every row. It exists because the legacy `ohlcv_bars`
table is a silent multi-vendor composite (Databento → tastytrade/TV) with no `source` column —
which corrupted a feed-sensitive z-score strategy (PF 4.89 → ~1 depending on vendor). This
archive is a faithful record of the exact feed we compute signals on and execute against.

**`ohlcv_bars` is never modified.** This table is built fresh, alongside it, for later cutover.

## Data invariants
- **1-minute, full ETH** (24h Globex), never RTH-only.
- **Real, NON-back-adjusted prices.** Never `MergeBackAdjusted` (it shifts historical prices by
  cumulative roll gaps — e.g. ES 2025-05-06 12:00 becomes 5909 vs the real 5629.75 — which
  corrupts any ratio/spread signal).
- **Per-contract storage.** Each real front-month contract is pulled over its life and tagged
  with a `contract` id (e.g. `GCQ20`), **keeping roll-overlap bars** (both contracts around each
  roll). A continuous series is reconstructed downstream by dedup (keep the newer contract on
  overlapping `ts`) — exactly how `data_tt.py` builds its feed. Reproducible rolls, maximum info.
- **No cross-vendor fill.** A missing span stays missing and is recorded; never patched from
  tastytrade/TV/Databento.

## Symbol map (verified against `SELECT DISTINCT symbol FROM ohlcv_bars`)
```
CL  -> NYMEX:CL1!    GC  -> COMEX:GC1!    SI  -> COMEX:SI1!
ES  -> CME:ES1!      NQ  -> CME:NQ1!      RTY -> CME:RTY1!
```

## Timestamp convention (critical — matched to `ohlcv_bars`)
`ohlcv_bars` stores **UTC, bar-OPEN** timestamps. NT8 exports **US-Central (America/Chicago),
bar-CLOSE**. The conversion, verified **bit-perfect** against `ohlcv_bars`' RTY `2020-01-01`
Globex reopen (volume, range, and price matched to the unit):

```
ohlcv_ts (UTC, bar-open) = America/Chicago(nt8_close) - 1 minute   (DST-aware)
```
The two offsets are independent and were disentangled on a multi-day holiday gap: **−1 minute**
(close→open) and **+6h/+5h** (CST/CDT→UTC).

## Schema
```
nt8_ohlcv_bars(
  symbol text, ts timestamptz, open/high/low/close double precision, volume bigint,
  contract text, source text = 'nt8_tradovate', merge_policy text = 'non_back_adjusted',
  PRIMARY KEY (symbol, contract, ts), INDEX (symbol, ts))
nt8_ingest_checkpoint(canonical, nt8_symbol, contract, from_date, to_date, rows_loaded, status)
```

## Running
```
python ingest.py gc-pilot   # GC pilot: Slice A (2020, 7 even-month contracts) + Slice B (2026-06)
python ingest.py status     # checkpoint progress + table summary
python qa.py gc              # the 4 QA checks
```
- **Idempotent:** `INSERT ... ON CONFLICT (symbol,contract,ts) DO NOTHING`.
- **Resumable:** each (contract, window) checkpointed `done`; a restart skips completed chunks.
- **Chunked:** one contract-window per bridge call; never one giant pull.
- Requires the NT8 MCP bridge live on `localhost:7890` and `DATABASE_URL` in `../.env`.
- GC follows the **even-month** roll cycle (Feb/Apr/Jun/Aug/Oct/Dec); windows overlap ~1 month
  each side so overlap bars are captured for downstream dedup.

## Known data gaps — recorded, never patched
Single-vendor means real feed holes are **surfaced, not filled**. Every genuine missing span is
logged in `nt8_data_gaps(symbol, gap_start, gap_end, note)`; downstream treats these as known-missing.

- **COMEX:GC1! 2023-04-06 → 2023-04-15** — genuine NT8/Tradovate 1-min hole (verified empty on
  both the GCM23 front and the expiring GCJ23). The legacy `ohlcv_bars` *had* this week (from
  Databento) — i.e. the composite silently cross-vendor-patched it. Left missing on purpose.

QA1's "missing weekdays" list is the gap detector: everything else in it is a market holiday
(Good Friday, Christmas, New Year's).

## Status
- **GC — COMPLETE (2020-01-01 → 2026-07-10):** 2,908,798 raw rows across 34 contracts
  (2,340,101 continuous 1-min bars + 568,697 roll-overlap bars). All 4 QA checks passed except
  the lineage gate (DEFERRED). One recorded gap (above).
- CL / SI / ES / NQ / RTY — pending (next).

## QA (mandatory before declaring done)
1. **Density** — rows/trading-day/year; flag gaps; confirm coverage reaches 2020-01-01.
2. **Roll continuity** — 2–3 roll boundaries; no phantom price step (proves non-back-adjusted);
   correct overlap dedup.
3. **Lineage gate** — diff NT8 *historical* re-pull vs NT8/Tradovate *live-captured* bars for a
   recent month. **Currently DEFERRED**: no genuine NT8/Tradovate live capture exists yet.
   Comparing to `ohlcv_bars` is NOT this test (its recent tail is tastytrade/TV — cross-vendor).
4. **Spot-check** — a few known bars against reference values.
