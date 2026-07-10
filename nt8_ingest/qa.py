#!/usr/bin/env python3
"""QA for nt8_ohlcv_bars — the 4 mandated checks. Usage: python qa.py gc"""
import sys, datetime as dt
import ingest

SYM = "COMEX:GC1!"

# Continuous (dedup) view: for each ts keep the NEWER contract (higher expiry key derived
# from the tag month-code + year), exactly the downstream reconstruction rule.
CONT = """
with ranked as (
  select *, (substring(contract from 4 for 2))::int*100 +
    case substring(contract from 3 for 1)
      when 'F' then 1 when 'G' then 2 when 'H' then 3 when 'J' then 4 when 'K' then 5 when 'M' then 6
      when 'N' then 7 when 'Q' then 8 when 'U' then 9 when 'V' then 10 when 'X' then 11 when 'Z' then 12 end as expkey
  from nt8_ohlcv_bars where symbol=%s)
select distinct on (ts) ts, open, high, low, close, volume, contract from ranked order by ts, expkey desc
"""

def q1_density(cx):
    print("\n=== QA1 DENSITY ===")
    c = cx.cursor()
    c.execute("select min(ts), max(ts), count(*), count(distinct contract) from nt8_ohlcv_bars where symbol=%s", (SYM,))
    mn, mx, n, nc = c.fetchone()
    print(f"raw: {n} rows, {nc} contracts, {mn} -> {mx}")
    print(f"reaches 2020-01-01? {'YES' if mn and mn.date() <= dt.date(2020,1,1) else 'NO — '+str(mn)}")
    # per-YEAR density over the full loaded range; trading days, avg/day, thin days (<600),
    # missing weekdays (gaps). Range = 2020-01-01 .. max(ts).
    end = mx.date()
    c.execute(f"""
      with cont as ({CONT}),
      perday as (select ts::date d, count(*) bars from cont group by d),
      allwd as (select gs::date d from generate_series('2020-01-01'::date, %s::date, interval '1 day') gs
                where extract(isodow from gs) < 6)
      select extract(year from coalesce(p.d,w.d))::int y,
             count(p.d) trading_days, round(avg(p.bars)) avg_day, min(p.bars) thin_day,
             sum((p.bars<600)::int) thin_lt600, count(*) filter (where p.d is null) missing_wd
      from allwd w left join perday p on p.d=w.d
      group by y order by y""", (SYM, end))
    print(f"{'year':>6} {'tdays':>6} {'avg/day':>8} {'thin_day':>9} {'#<600':>6} {'missWD':>7}")
    for y, td, avg, thin, lt6, miss in c.fetchall():
        print(f"{y:>6} {td:>6} {avg:>8} {thin:>9} {lt6:>6} {miss:>7}")
    # list the actual missing weekdays (true gaps, holidays aside)
    c.execute(f"""
      with cont as ({CONT}), perday as (select ts::date d from cont group by 1),
      allwd as (select gs::date d from generate_series('2020-01-01'::date, %s::date, interval '1 day') gs
                where extract(isodow from gs) < 6)
      select w.d from allwd w left join perday p on p.d=w.d where p.d is null order by w.d""", (SYM, end))
    miss = [r[0] for r in c.fetchall()]
    print(f"missing weekdays ({len(miss)}, expect only holidays): {miss[:25]}{'...' if len(miss)>25 else ''}")

def q2_roll_continuity(cx):
    print("\n=== QA2 ROLL CONTINUITY (dominant-volume front contract) ===")
    c = cx.cursor()
    # front contract per day = highest total volume that day; roll = day it changes (~6/yr for GC)
    c.execute("""
      with byday as (select ts::date d, contract, sum(volume) vol
                     from nt8_ohlcv_bars where symbol=%s group by d, contract),
           front as (select distinct on (d) d, contract, vol from byday order by d, vol desc),
           rolls as (select d, contract, lag(contract) over (order by d) prev from front)
      select d, prev, contract from rolls where prev is not null and prev<>contract order by d""", (SYM,))
    rolls = c.fetchall()
    print(f"{len(rolls)} volume rolls in 2019-11..2021-01:")
    for d, prev, new in rolls:
        # calendar spread at the roll: mean close of each contract that day (real carry => non-back-adj)
        c.execute("""select contract, round(avg(close)::numeric,2), sum(volume)
                     from nt8_ohlcv_bars where symbol=%s and ts::date=%s and contract in (%s,%s)
                     group by contract order by contract""", (SYM, d, prev, new))
        rows = {r[0]:(r[1],r[2]) for r in c.fetchall()}
        sp = round(float(rows[new][0]-rows[prev][0]),2) if prev in rows and new in rows else None
        print(f"  {d}: {prev} -> {new}  | {prev} avg={rows.get(prev)}  {new} avg={rows.get(new)}  calendar_spread={sp}")
    # internal continuity: largest 1-min close jump WITHIN a single contract (phantom-step detector)
    c.execute("""
      with j as (select contract, ts, close, lag(close) over (partition by contract order by ts) p
                 from nt8_ohlcv_bars where symbol=%s)
      select contract, ts, p, close, round((close-p)::numeric,2) jump from j
      where p is not null order by abs(close-p) desc limit 3""", (SYM,))
    print("  largest intra-contract 1-min jumps (should be small; a huge one = phantom/back-adj artifact):")
    for r in c.fetchall(): print("   ", r)

def q3_lineage(cx):
    print("\n=== QA3 LINEAGE GATE ===")
    print("  DEFERRED. NT8's deep history is a sourced/rebuilt dataset; the gate requires diffing")
    print("  NT8's HISTORICAL re-pull against NT8/Tradovate's LIVE-captured bars for the same dates.")
    print("  No genuine NT8/Tradovate live capture exists yet, so this gate cannot be run.")
    print("  NOT declared passed. (Comparing to ohlcv_bars is invalid — its recent tail is")
    print("  tastytrade/TV, so any diff there is cross-vendor and expected.)")

def q4_spotcheck(cx):
    print("\n=== QA4 SPOT-CHECK (real, non-back-adjusted values) ===")
    c = cx.cursor()
    # Gold 2020 sanity: price ~1450 (Jan) rising to the ~2075 ATH (2020-08-06). Back-adjusted
    # values would be shifted; real front-month should sit in this band.
    for label, ts in [("2020-01-02 open-ish", dt.datetime(2020,1,2,15,0,tzinfo=dt.timezone.utc)),
                       ("2020-08-06 ~ATH day noon", dt.datetime(2020,8,6,16,0,tzinfo=dt.timezone.utc))]:
        c.execute(f"with cont as ({CONT}) select ts,close,contract from cont where ts=%s", (SYM, ts))
        print(f"  {label}: {c.fetchone()}")
    c.execute(f"with cont as ({CONT}) select min(close), max(close) from cont where ts >= '2020-01-01' and ts < '2021-01-01'", (SYM,))
    lo, hi = c.fetchone()
    print(f"  2020 continuous close range: {lo} .. {hi}  (real gold 2020 ~1450..2075 -> {'PLAUSIBLE' if lo and 1300<lo<1600 and 1900<hi<2200 else 'CHECK'})")

if __name__ == "__main__":
    cx = ingest.connect()
    q1_density(cx); q2_roll_continuity(cx); q3_lineage(cx); q4_spotcheck(cx)
    cx.close()
