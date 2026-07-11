#!/usr/bin/env python3
"""Generalized QA for nt8_ohlcv_bars — runs the density/gap + roll-continuity checks
for any (or all) of the newly loaded instruments. Robust to variable-length roots
(GC/CL/SI/ES/NQ are 2-char, RTY is 3-char) by parsing the contract tag from the RIGHT.

Usage:
    python qa_all.py            # all five: cl si es nq rty
    python qa_all.py rty        # one instrument
"""
import sys, datetime as dt
import ingest

SYMS = {
    "cl":  "NYMEX:CL1!", "si": "COMEX:SI1!", "es": "CME:ES1!",
    "nq":  "CME:NQ1!",   "rty": "CME:RTY1!", "gc": "COMEX:GC1!",
}

# Continuous (dedup) view: keep the NEWER contract per ts. Parse tag from the RIGHT so
# it works for 2- and 3-char roots: YY = right(tag,2), month code = char at len-2.
CONT = """
with ranked as (
  select *, (right(contract,2))::int*100 +
    case substring(contract from length(contract)-2 for 1)
      when 'F' then 1 when 'G' then 2 when 'H' then 3 when 'J' then 4 when 'K' then 5 when 'M' then 6
      when 'N' then 7 when 'Q' then 8 when 'U' then 9 when 'V' then 10 when 'X' then 11 when 'Z' then 12 end as expkey
  from nt8_ohlcv_bars where symbol=%s)
select distinct on (ts) ts, open, high, low, close, volume, contract from ranked order by ts, expkey desc
"""

def q1_density(cx, sym):
    print(f"\n=== {sym} DENSITY ===")
    c = cx.cursor()
    c.execute("select min(ts), max(ts), count(*), count(distinct contract) from nt8_ohlcv_bars where symbol=%s", (sym,))
    mn, mx, n, nc = c.fetchone()
    if not n:
        print("  NO ROWS"); return []
    print(f"raw: {n} rows, {nc} contracts, {mn} -> {mx}")
    print(f"reaches 2020-01-01? {'YES' if mn and mn.date() <= dt.date(2020,1,1) else 'NO — '+str(mn)}")
    end = mx.date()
    c.execute(f"""
      with cont as ({CONT}),
      perday as (select ts::date d, count(*) bars from cont group by d),
      allwd as (select gs::date d from generate_series('2020-01-01'::date, %s::date, interval '1 day') gs
                where extract(isodow from gs) < 6)
      select extract(year from coalesce(p.d,w.d))::int y,
             count(p.d) trading_days, round(avg(p.bars)) avg_day, min(p.bars) thin_day,
             sum((p.bars<400)::int) thin_lt400, count(*) filter (where p.d is null) missing_wd
      from allwd w left join perday p on p.d=w.d
      group by y order by y""", (sym, end))
    print(f"{'year':>6} {'tdays':>6} {'avg/day':>8} {'thin_day':>9} {'#<400':>6} {'missWD':>7}")
    for y, td, avg, thin, lt, miss in c.fetchall():
        print(f"{y:>6} {td:>6} {avg:>8} {thin:>9} {lt:>6} {miss:>7}")
    c.execute(f"""
      with cont as ({CONT}), perday as (select ts::date d from cont group by 1),
      allwd as (select gs::date d from generate_series('2020-01-01'::date, %s::date, interval '1 day') gs
                where extract(isodow from gs) < 6)
      select w.d from allwd w left join perday p on p.d=w.d where p.d is null order by w.d""", (sym, end))
    miss = [r[0] for r in c.fetchall()]
    print(f"missing weekdays ({len(miss)}): {miss}")
    return miss

def q2_roll_continuity(cx, sym):
    print(f"\n=== {sym} ROLL CONTINUITY (dominant-volume front) ===")
    c = cx.cursor()
    c.execute("""
      with byday as (select ts::date d, contract, sum(volume) vol
                     from nt8_ohlcv_bars where symbol=%s group by d, contract),
           front as (select distinct on (d) d, contract, vol from byday order by d, vol desc),
           rolls as (select d, contract, lag(contract) over (order by d) prev from front)
      select d, prev, contract from rolls where prev is not null and prev<>contract order by d""", (sym,))
    rolls = c.fetchall()
    print(f"{len(rolls)} volume rolls; first 3 with calendar spread:")
    for d, prev, new in rolls[:3]:
        c.execute("""select contract, round(avg(close)::numeric,2)
                     from nt8_ohlcv_bars where symbol=%s and ts::date=%s and contract in (%s,%s)
                     group by contract""", (sym, d, prev, new))
        rows = {r[0]: r[1] for r in c.fetchall()}
        sp = round(float(rows[new]-rows[prev]),2) if prev in rows and new in rows else None
        print(f"  {d}: {prev} -> {new}  spread={sp}")
    c.execute("""
      with j as (select contract, ts, close, lag(close) over (partition by contract order by ts) p
                 from nt8_ohlcv_bars where symbol=%s)
      select contract, ts, p, close, round((close-p)::numeric,2) jump from j
      where p is not null order by abs(close-p) desc limit 2""", (sym,))
    print("  largest intra-contract 1-min jumps (huge => phantom/back-adj artifact):")
    for r in c.fetchall(): print("   ", r)

if __name__ == "__main__":
    keys = [sys.argv[1]] if len(sys.argv) > 1 else ["cl","si","es","nq","rty"]
    cx = ingest.connect()
    for k in keys:
        sym = SYMS[k]
        q1_density(cx, sym)
        q2_roll_continuity(cx, sym)
    cx.close()
