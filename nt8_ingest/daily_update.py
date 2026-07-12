#!/usr/bin/env python3
"""Daily incremental updater for nt8_ohlcv_bars.

Pulls a short TRAILING window of every currently-trading contract (+ the next
contract in each roll cycle) for the six instruments from the NT8 bridge and
upserts them idempotently. Meant to run once a day during the CME maintenance
break (16:00-17:00 CT), when the Globex trade-date has just closed at 16:00 CT
so the session is complete and no bar is half-formed.

Design:
  - Active contracts are read from the DB (contracts with bars in the last
    LOOKBACK_DAYS), i.e. ground truth of what is trading -- no per-instrument
    expiry math. The next contract in each cycle is seeded so a roll is caught
    early. On a cold DB it falls back to the schedule's nearest upcoming months.
  - TRAIL_DAYS trailing window => a missed run self-heals on the next day, and
    roll-overlap bars keep landing on BOTH contracts (matches the archive rule).
  - Idempotent: INSERT ... ON CONFLICT (symbol,contract,ts) DO NOTHING.
  - Single-vendor: only adds bars NT8/Tradovate returns; never fills gaps.

Usage:
    python daily_update.py            # run the update (what the scheduler calls)
    python daily_update.py --dry      # show which contracts/window, pull nothing
"""
import sys, os, datetime as dt, urllib.request
from zoneinfo import ZoneInfo
from psycopg2.extras import execute_values
import ingest

CT = ZoneInfo("America/Chicago")
TRAIL_DAYS = 5        # trailing pull window (calendar days); spans a weekend
LOOKBACK_DAYS = 10    # "currently trading" = had a bar within this many days
INSTR_KEYS = ("cl", "gc", "si", "es", "nq", "rty")
LOGDIR = os.path.join(ingest.HERE, "logs")
INV_CODE = {v: k for k, v in ingest.MONTH_CODE.items()}   # 'Q' -> 8


def log(msg):
    os.makedirs(LOGDIR, exist_ok=True)
    line = f"{dt.datetime.now(CT):%Y-%m-%d %H:%M:%S %Z}  {msg}"
    print(line, flush=True)
    with open(os.path.join(LOGDIR, "daily_update.log"), "a", encoding="utf-8") as f:
        f.write(line + "\n")


def sym_from_tag(tag):
    """'GCQ26'/'RTYH26' -> ('GC 08-26'/'RTY 03-26', mm, yy). Root may be 2 or 3 chars."""
    root, code, yy = tag[:-3], tag[-3], int(tag[-2:])
    mm = INV_CODE[code]
    return f"{root} {mm:02d}-{yy:02d}", mm, yy


def next_contract(key, mm, yy):
    """Next listed contract in this instrument's roll cycle after (mm,yy)."""
    _, root, months, _, _ = ingest.INSTRUMENTS[key]
    ms = sorted(months)
    later = [m for m in ms if m > mm]
    nm, ny = (later[0], yy) if later else (ms[0], yy + 1)
    tag = ingest.contract_tag(root, nm, ny % 100)
    return tag, f"{root} {nm:02d}-{ny % 100:02d}"


def candidates(cx, key, today):
    """Return (canonical, {tag: (symbol, is_seed)}) to refresh: every recently-traded
    contract (ground truth), plus the ONE next contract after the current front (by
    recent volume) to seed the upcoming roll early. `is_seed` lets a not-yet-listed
    seed be skipped quietly instead of logged as a failure. Cold DB (no recent bars)
    falls back to the schedule's nearest upcoming contracts."""
    canonical, root, months, off, to_day = ingest.INSTRUMENTS[key]
    c = cx.cursor()
    c.execute("""select distinct contract from nt8_ohlcv_bars
                 where symbol=%s and ts >= now() - make_interval(days => %s)""",
              (canonical, LOOKBACK_DAYS))
    active = [r[0] for r in c.fetchall()]

    out = {}   # tag -> (symbol, is_seed)
    if active:
        for tag in active:
            sym, _, _ = sym_from_tag(tag)
            out[tag] = (sym, False)
        # front = highest-volume contract in the last few days; seed the next AFTER it
        # (not after the furthest-dated stored contract, which would chase a contract
        # that has not been listed yet and fail every run).
        c.execute("""select contract from nt8_ohlcv_bars
                     where symbol=%s and ts >= now() - make_interval(days => 5)
                     group by contract order by sum(volume) desc nulls last limit 1""", (canonical,))
        fr = c.fetchone()
        if fr:
            _, fmm, fyy = sym_from_tag(fr[0])
            ntag, nsym = next_contract(key, fmm, fyy)
            out.setdefault(ntag, (nsym, True))
    else:
        # cold DB: take the two nearest upcoming contracts by the roll schedule
        seq = []
        for yy in range(today.year - 1, today.year + 2):
            for mm in months:
                try:
                    exp = dt.date(yy, mm, min(to_day, 28))
                except ValueError:
                    continue
                seq.append((exp, ingest.contract_tag(root, mm, yy % 100),
                            f"{root} {mm:02d}-{yy % 100:02d}"))
        seq.sort()
        for exp, tag, sym in [r for r in seq if r[0] >= today - dt.timedelta(days=7)][:2]:
            out[tag] = (sym, False)
    return canonical, out


def upsert(cx, canonical, tag, sym, frm, to):
    path, meta = ingest.bridge_export(sym, frm, to)
    bars = ingest.read_csv(path)
    # Drop the still-forming bar: NT8 stamps at bar-CLOSE, so keep only bars whose
    # close time is already in the past. (At the scheduled 16:20 CT run the market
    # is in its 16:00-17:00 break, so nothing is forming anyway -- this is defense
    # for manual/off-hours runs.)
    now_ct = dt.datetime.now(CT)
    bars = [b for b in bars if b[0].replace(tzinfo=CT) <= now_ct]
    if not bars:
        return 0, 0, None
    rows = [(canonical, ingest.to_ohlcv_ts(t), o, h, l, cl, v, tag, ingest.SOURCE, ingest.MERGE_POLICY)
            for (t, o, h, l, cl, v) in bars]
    c = cx.cursor()
    c.execute("select count(*) from nt8_ohlcv_bars where symbol=%s and contract=%s", (canonical, tag))
    before = c.fetchone()[0]
    # DO UPDATE (not DO NOTHING as in the historical backfill): on the live tail a
    # bar re-pulled later may be the completed version of an earlier partial, so
    # refresh OHLCV. Re-pulled historical minutes are identical, so this only ever
    # corrects a partial -- never corrupts a settled bar.
    execute_values(c, """insert into nt8_ohlcv_bars
        (symbol,ts,open,high,low,close,volume,contract,source,merge_policy)
        values %s on conflict (symbol,contract,ts) do update set
          open=excluded.open, high=excluded.high, low=excluded.low,
          close=excluded.close, volume=excluded.volume""", rows, page_size=5000)
    c.execute("select count(*) from nt8_ohlcv_bars where symbol=%s and contract=%s", (canonical, tag))
    new = c.fetchone()[0] - before
    cx.commit()
    return len(rows), new, bars[-1][0]


def main():
    dry = "--dry" in sys.argv
    today = dt.datetime.now(CT).date()
    frm = (today - dt.timedelta(days=TRAIL_DAYS)).isoformat()
    to = today.isoformat()
    log(f"=== daily_update {'(dry) ' if dry else ''}start; window {frm} -> {to} CT ===")

    try:
        urllib.request.urlopen(ingest.BRIDGE + "/api/health", timeout=15).read()
    except Exception as e:
        log(f"[FATAL] NT8 bridge unreachable at {ingest.BRIDGE}: {e}  (is NT8 running?)")
        return 2

    cx = ingest.connect()
    grand_new = 0
    for key in INSTR_KEYS:
        canonical, cand = candidates(cx, key, today)
        if dry:
            log(f"  {key.upper():4} {canonical}: would refresh {sorted(cand)}")
            continue
        ins_new = 0
        for tag, (sym, is_seed) in sorted(cand.items()):
            try:
                pulled, new, last = upsert(cx, canonical, tag, sym, frm, to)
                ins_new += new
                log(f"  {key.upper():4} {sym} ({tag}){' [seed]' if is_seed else ''}: "
                    f"{pulled} pulled, {new} new" + (f", last bar {last}" if last else ""))
            except Exception as e:
                cx.rollback()
                # a seed contract with no history yet is expected, not a failure
                if is_seed and "no bars" in str(e).lower():
                    log(f"  {key.upper():4} {sym} ({tag}) [seed]: not listed yet, skipped")
                else:
                    log(f"  [FAIL] {key.upper()} {sym} ({tag}): {e}")
        log(f"  {key.upper():4} total new: {ins_new}")
        grand_new += ins_new
    cx.close()
    log(f"=== daily_update done; {grand_new} new bars total ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
