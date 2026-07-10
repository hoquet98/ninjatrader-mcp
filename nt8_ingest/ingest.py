#!/usr/bin/env python3
"""
nt8_ingest — single-vendor (NT8/Tradovate) 1-minute OHLCV archive builder.

Pulls REAL, NON-BACK-ADJUSTED front-month contract bars from the NT8 MCP bridge,
converts NT8's (Central, bar-CLOSE) timestamps to (UTC, bar-OPEN) to match ohlcv_bars,
tags provenance on every row, and loads into nt8_ohlcv_bars idempotently + resumably.

Timestamp convention (verified bit-perfect against ohlcv_bars' RTY 2020-01-01 reopen):
    ohlcv_ts(UTC, bar-open) = America/Chicago(nt8_close) - 1 minute   (DST-aware)

Storage: per-contract, roll-overlap bars KEPT (both contracts around each roll);
downstream reconstructs a continuous series by dedup (keep newer contract on overlap).
NEVER back-adjusted. NEVER cross-vendor filled. source='nt8_tradovate' on every row.

Usage:
    python ingest.py gc-pilot        # GC slice A (2020) + slice B (2026-06)
    python ingest.py status          # show checkpoint progress
"""
import sys, os, csv, json, time, datetime as dt, urllib.request
from zoneinfo import ZoneInfo

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
BRIDGE = os.environ.get("NT8_BRIDGE", "http://localhost:7890")
# NT8 writes exports here; we read the CSV directly (same machine).
NT8_DATADIR = r"C:\Users\Administrator\Documents\NinjaTrader 8"
CT = ZoneInfo("America/Chicago"); UTC = ZoneInfo("UTC")
MERGE_POLICY = "non_back_adjusted"
SOURCE = "nt8_tradovate"

MONTH_CODE = {1:"F",2:"G",3:"H",4:"J",5:"K",6:"M",7:"N",8:"Q",9:"U",10:"V",11:"X",12:"Z"}

def db_url():
    for line in open(os.path.join(REPO, ".env"), encoding="utf-8"):
        if line.startswith("DATABASE_URL="):
            return line.split("=", 1)[1].strip().strip('"').strip("'")
    raise SystemExit("DATABASE_URL not in .env")

def connect():
    import psycopg2
    return psycopg2.connect(db_url())

def contract_tag(root, mm, yy):
    """('GC', 8, 26) -> 'GCQ26'."""
    return f"{root}{MONTH_CODE[mm]}{yy:02d}"

def to_ohlcv_ts(naive_ct):
    """NT8 Central bar-CLOSE (naive) -> aware UTC bar-OPEN (close-1min, DST-aware)."""
    close_ct = naive_ct.replace(tzinfo=CT)
    open_ct = close_ct - dt.timedelta(minutes=1)
    return open_ct.astimezone(UTC)

def bridge_export(nt8_symbol, frm, to, timeout_sec=300):
    """Ask the bridge to export a date-range CSV; return the on-disk path."""
    body = json.dumps({
        "symbol": nt8_symbol, "period": "Minute", "periodValue": 1,
        "from": frm, "to": to, "merge": "DoNotMerge", "timeoutSec": timeout_sec,
    }).encode()
    req = urllib.request.Request(BRIDGE + "/api/bars/export", data=body,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout_sec + 40) as r:
        res = json.loads(r.read())
    if "error" in res:
        raise RuntimeError(f"export {nt8_symbol}: {res['error']}")
    return os.path.join(NT8_DATADIR, res["file"]), res

def read_csv(path):
    out = []
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            t = dt.datetime.strptime(row["time"], "%Y-%m-%dT%H:%M:%S")
            out.append((t, float(row["open"]), float(row["high"]),
                        float(row["low"]), float(row["close"]), int(row["volume"])))
    return out

def load_chunk(cx, canonical, nt8_symbol, tag, frm, to, timeout_sec=300):
    """Export one contract window, convert, load ON CONFLICT DO NOTHING, checkpoint."""
    from psycopg2.extras import execute_values
    c = cx.cursor()
    c.execute("""select status, rows_loaded from nt8_ingest_checkpoint
                 where canonical=%s and contract=%s and from_date=%s and to_date=%s""",
              (canonical, tag, frm, to))
    ck = c.fetchone()
    if ck and ck[0] == "done":
        print(f"  [skip] {tag} {frm}->{to} already done ({ck[1]} rows)")
        return ck[1]

    t0 = time.time()
    path, meta = bridge_export(nt8_symbol, frm, to, timeout_sec)
    bars = read_csv(path)
    rows = [(canonical, to_ohlcv_ts(t), o, h, l, cl, v, tag, SOURCE, MERGE_POLICY)
            for (t, o, h, l, cl, v) in bars]
    c.execute("select count(*) from nt8_ohlcv_bars where symbol=%s and contract=%s", (canonical, tag))
    before = c.fetchone()[0]
    execute_values(c, """
        insert into nt8_ohlcv_bars (symbol,ts,open,high,low,close,volume,contract,source,merge_policy)
        values %s on conflict (symbol,contract,ts) do nothing""", rows, page_size=5000)
    c.execute("select count(*) from nt8_ohlcv_bars where symbol=%s and contract=%s", (canonical, tag))
    n = c.fetchone()[0] - before
    c.execute("""insert into nt8_ingest_checkpoint
                 (canonical,nt8_symbol,contract,from_date,to_date,rows_loaded,status,updated_at)
                 values (%s,%s,%s,%s,%s,%s,'done',now())
                 on conflict (canonical,contract,from_date,to_date)
                 do update set rows_loaded=excluded.rows_loaded, status='done', updated_at=now()""",
              (canonical, nt8_symbol, tag, frm, to, len(rows)))
    cx.commit()
    print(f"  [ok]   {tag} {frm}->{to}: {len(rows)} bars pulled, {n} new "
          f"(first {bars[0][0] if bars else '-'} last {bars[-1][0] if bars else '-'}) {time.time()-t0:.0f}s")
    return len(rows)

# ── GC pilot chunk definitions ──────────────────────────────────────────────
# Even-month gold contracts; wide windows (~overlap a month each side) so roll-
# overlap bars are captured on both contracts. Slice A covers all of 2020.
GC_PILOT = [
    # (canonical, nt8_symbol, tag, from, to)
    ("COMEX:GC1!", "GC 02-20", contract_tag("GC",2,20),  "2019-11-15", "2020-01-31"),
    ("COMEX:GC1!", "GC 04-20", contract_tag("GC",4,20),  "2020-01-01", "2020-03-31"),
    ("COMEX:GC1!", "GC 06-20", contract_tag("GC",6,20),  "2020-03-01", "2020-05-31"),
    # Gold liquidity rolls Aug -> Dec (Oct is NOT a listed front month). Overlap Aug<->Dec so the
    # Aug window is covered on both contracts for downstream dedup.
    ("COMEX:GC1!", "GC 08-20", contract_tag("GC",8,20),  "2020-05-01", "2020-08-31"),
    ("COMEX:GC1!", "GC 12-20", contract_tag("GC",12,20), "2020-07-15", "2020-11-30"),
    ("COMEX:GC1!", "GC 02-21", contract_tag("GC",2,21),  "2020-11-01", "2021-01-31"),
    # Slice B — most-recent completed month (2026-06); front gold month = GC 08-26
    ("COMEX:GC1!", "GC 08-26", contract_tag("GC",8,26),  "2026-05-15", "2026-07-01"),
]

# Full GC history generator. Gold rolls G(Feb)->J(Apr)->M(Jun)->Q(Aug)->Z(Dec), skipping
# V(Oct) (verified in the pilot). Windows overlap ~1 month each side so roll-overlap bars land
# on both contracts; they intentionally match the pilot windows so completed chunks skip.
#   month -> (from_MM-DD, to_MM-DD, year_offset_for_from)
GC_MONTH_WIN = {
    2:  ("11-15", "01-31", -1),   # Feb: prior-year Nov-15 -> Jan-31
    4:  ("01-01", "03-31",  0),   # Apr
    6:  ("03-01", "05-31",  0),   # Jun
    8:  ("05-01", "08-31",  0),   # Aug (extends into Aug for the expiring tail)
    12: ("07-15", "11-30",  0),   # Dec (starts Jul-15 to overlap the Aug roll)
}
def gc_full_chunks(start_year=2020, end="2026-07-10"):
    chunks = []
    for yy in range(start_year, int(end[:4]) + 1):
        for mm in (2, 4, 6, 8, 12):
            fmd, tmd, yoff = GC_MONTH_WIN[mm]
            frm = f"{yy+yoff:04d}-{fmd}"
            to  = f"{yy:04d}-{tmd}"
            if frm > end:                 # contract's window starts after our cutoff -> skip
                continue
            if to > end:                  # clip the trailing contract to the cutoff day
                to = end
            tag = contract_tag("GC", mm, yy % 100)
            chunks.append(("COMEX:GC1!", f"GC {mm:02d}-{yy % 100:02d}", tag, frm, to))
    return chunks

def run(chunks):
    cx = connect()
    total = 0
    for i, (canon, sym, tag, frm, to) in enumerate(chunks, 1):
        print(f"[{i}/{len(chunks)}] {sym} ({tag}) {frm} -> {to}")
        try:
            total += load_chunk(cx, canon, sym, tag, frm, to)
        except Exception as e:
            print(f"  [FAIL] {tag}: {e}")
    cx.close()
    print(f"done; {total} rows processed.")

def status():
    cx = connect(); c = cx.cursor()
    c.execute("""select canonical, contract, from_date, to_date, rows_loaded, status
                 from nt8_ingest_checkpoint order by canonical, from_date""")
    for r in c.fetchall(): print("  ", *r)
    c.execute("select symbol, count(*), min(ts), max(ts), count(distinct contract) from nt8_ohlcv_bars group by symbol")
    print("table:", c.fetchall())
    cx.close()

if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "gc-pilot": run(GC_PILOT)
    elif cmd == "gc-full": run(gc_full_chunks(2020, "2026-07-10"))
    elif cmd == "status": status()
    else: print(__doc__)
