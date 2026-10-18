#!/usr/bin/env python3
"""Convert the addon's daily tick CSVs -> per-UTC-day Parquet (zstd), backtest-ready.

Source : C:\\Users\\Administrator\\tick_capture\\{nq,es}\\t_YYYYMMDD.csv   (written by the NT8 recorder)
Output : C:\\Users\\Administrator\\tick_capture\\parquet\\{nq,es}_ticks_ba\\t_YYYYMMDD.parquet
         + checks.csv + README.md per folder.

Schema (one row per trade, with top-of-book at the trade):
  ms int64 (UTC epoch ms) | seq int32 (within-ms order) | px int32 (price*4, 0.25 tick)
  sz int32 | bid/ask Int32 (nullable) | bsz/asz int32 | side int8 (+1>=ask,-1<=bid,0 else) | ct string

Idempotent: rewrites every day-file each run (cheap); re-run after the session to pick up growth.
Partitions by the UTC day of each tick's ms, so CSV day-rollover seams are handled correctly.
"""
import os, glob
import pandas as pd, pyarrow as pa, pyarrow.parquet as pq

SRC = r"C:\Users\Administrator\tick_capture"
OUT = os.path.join(SRC, "parquet")
INSTR = {"nq": "nq_ticks_ba", "es": "es_ticks_ba"}

README = """# {name}

NQ/ES **trades with top-of-book** captured live from NinjaTrader 8 (VPS), one row per trade.

- **Source:** NT8 Level-1 market data (prop feed), recorded forward by the McpBridge addon.
- **Scope:** last-trade prints + the best bid/ask in force at each trade. NOT full L2/DOM
  (the feed doesn't serve depth). Aggressor `side` is inferred (px vs bid/ask), not feed-provided.
- **Prices** are in ticks: index price * 4 (0.25 tick). Divide by 4 for points.
- **Times** are UTC epoch ms, converted from the feed's Central time (DST-aware).
- **Partitioning:** one file per UTC day, sorted by (ms, seq).

## Known gaps
- Daily ~16:00-17:00 CT CME maintenance + NT8 restart → a short gap each day (little/no trading then).
- Feed disconnects disable capture until reconnect; low-row days flagged in checks.csv.
- Contract roll: files tagged by `ct`; the recorder follows the configured front month
  (update the recorder config at each quarterly roll).
"""


def convert():
    os.makedirs(OUT, exist_ok=True)
    for sub, name in INSTR.items():
        src_dir = os.path.join(SRC, sub)
        if not os.path.isdir(src_dir):
            continue
        out_dir = os.path.join(OUT, name)
        os.makedirs(out_dir, exist_ok=True)
        checks = []
        for csvp in sorted(glob.glob(os.path.join(src_dir, "t_*.csv"))):
            df = pd.read_csv(csvp)
            if df.empty:
                continue
            df["utcday"] = pd.to_datetime(df["ms"], unit="ms", utc=True).dt.strftime("%Y%m%d")
            for day, g in df.groupby("utcday"):
                g = g.sort_values(["ms", "seq"], kind="stable").reset_index(drop=True)
                g["seq"] = g.groupby("ms").cumcount().astype("int32")
                out = g[["ms", "seq", "px", "sz", "bid", "ask", "bsz", "asz", "side", "ct"]].copy()
                out["ms"] = out["ms"].astype("int64")
                for c in ("px", "sz", "bsz", "asz"):
                    out[c] = out[c].fillna(0).astype("int32")
                out["bid"] = out["bid"].astype("Int32")
                out["ask"] = out["ask"].astype("Int32")
                out["side"] = out["side"].astype("int8")
                path = os.path.join(out_dir, f"t_{day}.parquet")
                pq.write_table(pa.Table.from_pandas(out, preserve_index=False), path, compression="zstd")
                checks.append(dict(
                    day=day, rows=len(out),
                    ba_present=round(float(out["bid"].notna().mean()), 4),
                    side0=round(float((out["side"] == 0).mean()), 4),
                    bid_gt_ask=int(((out["bid"].notna()) & (out["ask"].notna()) & (out["bid"] > out["ask"])).sum()),
                    bytes=os.path.getsize(path),
                ))
        if checks:
            cdf = pd.DataFrame(checks)
            cdf.to_csv(os.path.join(out_dir, "checks.csv"), index=False)
            with open(os.path.join(out_dir, "README.md"), "w", encoding="utf-8") as f:
                f.write(README.format(name=name))
            bpt = cdf["bytes"].sum() / max(1, cdf["rows"].sum())
            print(f"{name}: {len(cdf)} day-file(s), {int(cdf['rows'].sum()):,} rows, "
                  f"{cdf['bytes'].sum()/1e6:.2f} MB  (~{bpt:.1f} bytes/tick)")
        else:
            print(f"{name}: no data yet")


if __name__ == "__main__":
    convert()
