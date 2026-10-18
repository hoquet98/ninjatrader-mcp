#!/usr/bin/env python3
"""Pull 2 months of NQ last-trade ticks per front-month contract -> Postgres nt8_nq_ticks.
NT8 emits Central time; the window is entirely CDT (UTC-5), stamped as offset for UTC
storage (consistent with nt8_ohlcv_bars). Ticks are instantaneous (no bar close->open shift).
Front contract picked per session; the roll window keeps whichever contract is more liquid."""
import sys, json, urllib.request, datetime as dt, io, time, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ingest

BRIDGE = "http://localhost:7890"
START, END = dt.date(2026, 7, 23), dt.date(2026, 9, 23)

def export_ticks(sym, d):
    body = json.dumps({"symbol": sym, "period": "Tick", "periodValue": 1,
        "from": f"{d}T00:00:00", "to": f"{d}T23:59:59", "timeoutSec": 180}).encode()
    req = urllib.request.Request(BRIDGE + "/api/bars/export", data=body,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=230) as r:
        res = json.loads(r.read())
    if "error" in res: return None, res.get("error")
    return res["path"], res.get("rows", 0)

def pick(d):
    if d <= dt.date(2026, 9, 4):  return ["NQ 09-26"]
    if d >= dt.date(2026, 9, 18): return ["NQ 12-26"]
    return ["NQ 09-26", "NQ 12-26"]

def read_ticks(path):
    out = []
    with open(path, newline="") as f:
        next(f)
        for i, line in enumerate(f):
            p = line.rstrip("\r\n").split(",")
            out.append((p[0] + "-05:00", p[4], p[5], i))
    return out

cx = ingest.connect(); c = cx.cursor()
c.execute("drop table if exists nt8_nq_ticks")
c.execute("""create table nt8_nq_ticks(
  ts timestamptz not null, price double precision not null,
  size bigint not null, contract text not null, seq integer not null)""")
cx.commit()

total = 0; d = START; t0 = time.time()
while d <= END:
    if d.weekday() < 5:
        best = None
        for sym in pick(d):
            path, info = export_ticks(sym, d)
            if path is None: print(f"{d} {sym}: SKIP {info}", flush=True); continue
            tk = read_ticks(path)
            if best is None or len(tk) > len(best[1]): best = (sym, tk)
        if best and best[1]:
            sym, tk = best
            buf = io.StringIO()
            for tsv, price, size, seq in tk: buf.write(f"{tsv}\t{price}\t{size}\t{sym}\t{seq}\n")
            buf.seek(0)
            c.copy_expert("copy nt8_nq_ticks(ts,price,size,contract,seq) from stdin", buf)
            cx.commit(); total += len(tk)
            print(f"{d} -> {sym}: {len(tk):,} ticks (cum {total:,})", flush=True)
        else:
            print(f"{d}: no data", flush=True)
    d += dt.timedelta(days=1)

print("building indexes...", flush=True)
c.execute("create index nt8_nq_ticks_ts_idx on nt8_nq_ticks(ts)")
c.execute("create index nt8_nq_ticks_ct_idx on nt8_nq_ticks(contract, ts)")
cx.commit()
print(f"DONE: {total:,} ticks in {time.time()-t0:.0f}s", flush=True)
