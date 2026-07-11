You are a highly capable data engineer (Claude Opus 4.8) with access to an NT8 MCP bridge and
a Postgres database. Your job is to build a NEW, single-vendor historical market-data archive
sourced ENTIRELY from NT8/Tradovate. Read this whole brief before acting — the caveats are the
point, not decoration.

════════════════════════════════════════════════════════════════════════════
WHY THIS EXISTS (read first — this is the reasoning behind every rule below)
════════════════════════════════════════════════════════════════════════════
The existing `ohlcv_bars` table is a COMPOSITE of multiple vendors stitched together —
Databento history through ~2026-03-12, then tastytrade + TradingView filling the tail — with
NO source column to tell them apart. That contamination silently destroyed a feed-sensitive
strategy: its signal is a z-score of log(RTY/ES), whose small denominator makes it a high-gain
amplifier. Bar differences of ~0.01% between vendors (a tick here, a slightly different
bucket there) got magnified into 32% sign-flips in the signal, swinging profit factor from 4+
to ~1 depending on which vendor's bars it ran on. Concretely: the same strategy scored PF 4.89
on the validated feed, 1.08 on TradingView's continuous, ~2 on Tradovate — same market, same
minutes, different bar construction.

The lesson, now firm policy: the historical archive must be a faithful record of the EXACT
feed we will compute live signals on and execute against — ONE vendor, end to end, provenance
tagged on every row, no cross-vendor backfill, ever. A missing span stays marked missing; it
is never patched from another source.

════════════════════════════════════════════════════════════════════════════
OBJECTIVE
════════════════════════════════════════════════════════════════════════════
Populate a NEW table `nt8_ohlcv_bars` in the Railway Postgres DB (connection = DATABASE_URL in
the repo .env — the same DB that holds `ohlcv_bars`) with 1-minute OHLCV bars from
NT8/Tradovate, 2020-01-01 → today, for six instruments:

  CL  -> NYMEX:CL1!     GC  -> COMEX:GC1!     SI  -> COMEX:SI1!
  ES  -> CME:ES1!       NQ  -> CME:NQ1!       RTY -> CME:RTY1!

FIRST verify these exact canonical symbol strings against `SELECT DISTINCT symbol FROM
ohlcv_bars` so the new table is a drop-in for downstream loaders.

DO NOT modify, drop, truncate, or write to `ohlcv_bars`. Build `nt8_ohlcv_bars` fresh,
alongside it. The operator decides cutover later, after validation.

════════════════════════════════════════════════════════════════════════════
PILOT PHASE — DO THIS FIRST, THEN STOP FOR OPERATOR GO/NO-GO
════════════════════════════════════════════════════════════════════════════
Prove the mechanics and the data provenance on ONE instrument before spending the ~12M-row
full pull. Pilot instrument: GC (COMEX:GC1!). Gold is a good stress test because its liquidity
rolls on the EVEN-MONTH cycle (Feb/Apr/Jun/Aug/Oct/Dec) — ~6 rolls in 2020, more chances to
catch a roll/dedup/adjustment bug than a quarterly contract. Follow GC's ACTIVE-month roll;
do not assume quarterly.

Ingest exactly TWO slices, applying the full spec below:
  SLICE A — GC 2020-01-01 → 2020-12-31 (depth, roll continuity across ~6 rolls, density,
            dedup, mechanics end to end).
  SLICE B — GC most-recent completed month, e.g. 2026-06 (the ONLY slice where the lineage
            gate can run).

Then run ALL FOUR QA checks (below) and STOP. Report the results + a go/no-go recommendation.
Do NOT proceed to 2021+ or the other five instruments until the operator approves.

════════════════════════════════════════════════════════════════════════════
DATA SPEC — INVARIANTS (non-negotiable)
════════════════════════════════════════════════════════════════════════════
- Resolution: 1-minute. Session: FULL ETH (24h Globex), never RTH-only — strategies trade all
  hours.
- Adjustment: REAL, NON-BACK-ADJUSTED prices ONLY. Never MergeBackAdjusted. You already proved
  why: back-adjustment shifted ES 2025-05-06 12:00 from the real 5629.75 to 5909.00 (+279pt of
  cumulative-roll fiction). That is additive-in-price, which corrupts any ratio/spread signal.
- Storage method (preferred): pull each real front-month CONTRACT over its life and tag every
  bar with its `contract` id, KEEPING the roll-overlap bars (both contracts around each roll).
  This lets any continuous series be reconstructed downstream by dedup (keep the newer contract
  on overlapping ts), exactly how data_tt.py builds its feed. Reproducible rolls, maximum info.
  Fallback (only if per-contract enumeration is impractical via the MCP): MergeNonBackAdjusted
  continuous, anchored on a CONCRETE month (e.g. "GC 08-26" — the generic "GC ##-##"/base
  symbol does not work). If you use the fallback, you MUST store the exact roll rule NT8 used
  (trigger: volume vs open-interest, and the days-before-expiry offset) as a documented param.
- Timezone / timestamp convention: store ts in UTC. NT8 stamps bars at bar CLOSE in its
  configured timezone. BEFORE loading, verify whether the existing `ohlcv_bars` ts is bar-OPEN
  or bar-CLOSE (compare a few overlapping recent bars) and MATCH it exactly, so data_tt.py /
  rty_common read the new table unchanged. Document the convention you chose in the script and
  the README.
- No cross-vendor fill. If NT8/Tradovate lacks a span, leave the hole and record it — do not
  reach for tastytrade/TV/Databento.

════════════════════════════════════════════════════════════════════════════
SCHEMA (match ohlcv_bars column types where it already has them)
════════════════════════════════════════════════════════════════════════════
  symbol       text            -- canonical, e.g. 'CME:ES1!'
  ts           timestamptz     -- UTC; convention matching ohlcv_bars (document open vs close)
  open,high,low,close  double precision
  volume       bigint
  contract     text            -- specific contract the bar came from, e.g. 'GCM20'
                                   (null only if unavoidable in the continuous fallback)
  source       text            -- 'nt8_tradovate' on EVERY row (explicit provenance — the
                                   missing source column is how the old contamination hid)
  merge_policy text            -- 'non_back_adjusted' (+ roll-rule note if continuous fallback)
  PRIMARY KEY (symbol, contract, ts)  [or (symbol, ts) if continuous fallback]
  INDEX on (symbol, ts).

════════════════════════════════════════════════════════════════════════════
ENGINEERING
════════════════════════════════════════════════════════════════════════════
- Full archive is ~12M+ rows; NT8 MCP export throughput will likely be the bottleneck. CHUNK
  the ingestion (per instrument, per contract or per month). Make it IDEMPOTENT
  (INSERT ... ON CONFLICT DO NOTHING/UPDATE) and RESUMABLE (checkpoint loaded chunks so a
  restart doesn't re-pull). Log progress per chunk. Never one giant call.
- Commit the ingestion script + a README to the repo in a SINGLE folder. README documents:
  symbol map, timestamp convention, merge policy, roll rule (if any), schema, and how to
  resume. Repo git norms: NO git stash, committing/pushing to main is expected — push your work.

════════════════════════════════════════════════════════════════════════════
QA — MANDATORY BEFORE DECLARING EITHER THE PILOT OR THE FULL RUN DONE
════════════════════════════════════════════════════════════════════════════
1. DENSITY: rows per trading-day, per instrument, per year. Flag gaps and suspiciously thin
   spans. Confirm the instrument actually reaches 2020-01-01 with continuous coverage. (Known
   depths: ES ~2006, GC ~2008, SI/CL/NQ pre-2020, RTY mini from 2017 — all six should reach
   2020, but VERIFY, and watch RTY for early thinness on the full run.)
2. ROLL CONTINUITY: show 2-3 roll boundaries; confirm NO phantom price step (proves real,
   non-back-adjusted prices) and correct dedup on the overlap bars.
3. LINEAGE GATE (the most important, and the subtle one): NT8's deep history is a SOURCED /
   rebuilt dataset, not Tradovate's own live capture from 2020 (Tradovate didn't stream it
   then). If NT8's historical re-pull of a date differs from what NT8/Tradovate STREAMS live,
   the archive can't predict live trading — the exact "measuring the warehouse, not the market"
   trap that has already bitten this project once. Test: diff SLICE B (or a recent month)
   between NT8's HISTORICAL pull and NT8/Tradovate's LIVE-captured bars for the same dates.
   Report tick-match %. IMPORTANT: comparing against the existing `ohlcv_bars` is NOT this test
   — that table's recent tail is tastytrade/TV, so differences there are cross-vendor and
   EXPECTED, not the failure signal. If you have no genuine NT8/Tradovate live capture yet, say
   so and mark the gate DEFERRED — do NOT declare it passed off a cross-vendor comparison.
4. SPOT-CHECK: a few known bars against reference values (e.g. GC bars against a trusted source;
   for ES, the 2025-05-06 12:00 = 5629.75 non-back-adjusted value).

════════════════════════════════════════════════════════════════════════════
DELIVERABLES
════════════════════════════════════════════════════════════════════════════
Pilot: GC slices A+B loaded, the four QA results, and a go/no-go recommendation.
Full run (only after approval): nt8_ohlcv_bars populated 2020-01-01→now for all six
instruments; idempotent/resumable ingestion script + README committed; QA report with the
lineage gate called out explicitly per instrument.

════════════════════════════════════════════════════════════════════════════
STOP AND ASK THE OPERATOR IF:
════════════════════════════════════════════════════════════════════════════
- The pilot QA is complete (always stop here for go/no-go).
- The lineage gate fails (NT8-historical != NT8-live) or must be deferred.
- Any instrument can't cleanly reach 2020-01-01.
- You are tempted to deviate from per-contract / non-back-adjusted, or to fill a gap from any
  other vendor (don't — surface it instead).