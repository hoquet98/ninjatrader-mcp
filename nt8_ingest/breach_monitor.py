#!/usr/bin/env python3
"""NT8 account-breach monitor -> AI-Gate webhook. Standalone always-on service.

Polls the NT8 bridge /api/account every poll_seconds. When an account's accountStatus
is 'LiquidationOnly' (the prop-firm breach / "liquidation mode only" state), POSTs the
AI-Gate breach webhook ONCE per breach episode. Decoupled from NT8 (survives addon
recompiles / NT8 restarts); boot-started via a Windows scheduled task.

Robustness: never exits on error; handles the bridge being down (NT8 not up yet);
de-dups via a state file; reloads config each cycle so the API key can be dropped in live.

Config (JSON): C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\mcp_breach.json
  { "api_key": "<AI-Gate bearer>", "poll_seconds": 60,
    "bridge_url": "http://localhost:7890",
    "webhook": "https://ai-gate.up.railway.app/webhook/ai/accounts/{account}/breach" }

Usage: breach_monitor.py            (run forever - the service)
       breach_monitor.py --once     (one cycle, for testing)
       breach_monitor.py --test-webhook <ACCOUNT>   (send a test breach to validate auth)
"""
import json, os, sys, time, urllib.request, urllib.parse, urllib.error, datetime, traceback

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG = os.environ.get("NT8_BREACH_CONFIG", r"C:\Users\Administrator\Documents\NinjaTrader 8\mcp_breach.json")
STATE = r"C:\Users\Administrator\Documents\NinjaTrader 8\mcp_breach_state.json"
LOG = os.path.join(HERE, "logs", "breach_monitor.log")

DEFAULTS = {
    "bridge_url": "http://localhost:7890",
    "webhook": "https://ai-gate.up.railway.app/webhook/ai/accounts/{account}/breach",
    "api_key": "",
    "poll_seconds": 60,
    "breach_status": "LiquidationOnly",
    "active_only": True,
}


def log(msg):
    line = f"{datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%SZ')} {msg}"
    print(line, flush=True)
    try:
        os.makedirs(os.path.dirname(LOG), exist_ok=True)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def load_cfg():
    cfg = dict(DEFAULTS)
    try:
        if os.path.exists(CONFIG):
            cfg.update(json.load(open(CONFIG, encoding="utf-8")))
    except Exception as e:
        log(f"[WARN] config load failed: {e}")
    return cfg


def load_state():
    try:
        return set(json.load(open(STATE, encoding="utf-8")).get("alerted", []))
    except Exception:
        return set()


def save_state(alerted):
    try:
        json.dump({"alerted": sorted(alerted)}, open(STATE, "w", encoding="utf-8"))
    except Exception as e:
        log(f"[WARN] state save failed: {e}")


def get_accounts(cfg):
    req = urllib.request.Request(cfg["bridge_url"].rstrip("/") + "/api/account")
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read())


def post_breach(cfg, acct, balance, observed_at):
    url = cfg["webhook"].replace("{account}", urllib.parse.quote(acct, safe=""))
    body = {"source": "nt8", "reason": "account entered liquidation mode", "observed_at": observed_at}
    if balance:
        body["balance"] = balance
    headers = {"Content-Type": "application/json"}
    if cfg.get("api_key"):
        headers["Authorization"] = "Bearer " + cfg["api_key"]
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=20) as r:
        return r.status, r.read().decode()[:200]


def send_with_retry(cfg, acct, balance):
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    if not cfg.get("api_key"):
        log(f"[BREACH] {acct} is LiquidationOnly but api_key is MISSING -> cannot POST (set it in {CONFIG})")
        return False
    for attempt in range(1, 5):
        try:
            st, resp = post_breach(cfg, acct, balance, now)
            log(f"[BREACH] POST {acct} -> HTTP {st} {resp}")
            if 200 <= st < 300:
                return True
        except urllib.error.HTTPError as e:
            body = ""
            try:
                body = e.read().decode()[:200]
            except Exception:
                pass
            log(f"[BREACH] POST {acct} -> HTTP {e.code} {body}")
            if e.code == 404:  # account not known to the gate -> retrying won't help
                log(f"[BREACH] {acct} unmapped at gate (404) -> not retrying")
                return True
        except Exception as e:
            log(f"[BREACH] POST {acct} attempt {attempt} failed: {e}")
        time.sleep(3 * attempt)
    return False


def cycle(cfg, alerted, watched):
    try:
        accts = get_accounts(cfg)
    except Exception as e:
        log(f"[WARN] bridge unreachable ({e}) -> NT8 down? skip cycle")
        return
    active_only = cfg.get("active_only", True)
    present = set()
    for a in accts:
        name, st = a.get("name"), a.get("accountStatus")
        if not name:
            continue
        present.add(name)
        # "active" = has a real balance; once seen active it stays watched for the session
        # (sticky), so a breach that zeroes the balance is still caught.
        val = (a.get("cashValue") or 0) or (a.get("netLiquidation") or 0)
        if val and name not in watched:
            watched.add(name)
            log(f"[WATCH] active account ({len(watched)}): {name} (value={val})")
        if active_only and name not in watched:
            continue  # skip the dead/zero-value shells NT8 still lists
        if st == cfg["breach_status"]:
            if name not in alerted:
                bal = a.get("netLiquidation")
                if send_with_retry(cfg, name, bal if (bal and bal > 0) else None):
                    alerted.add(name)
                    save_state(alerted)
        elif name in alerted:
            alerted.discard(name)
            save_state(alerted)
            log(f"[INFO] {name} no longer breached (status={st}) -> reset")
    for gone in list(alerted - present):
        alerted.discard(gone)
        save_state(alerted)
        log(f"[INFO] {gone} removed from NT8 (restart cleanup) -> reset")


def single_instance():
    """Windows named mutex -> prevents a duplicate service (held for process lifetime)."""
    try:
        import ctypes
        ctypes.windll.kernel32.CreateMutexW(None, False, "Global\\nt8_breach_monitor")
        return ctypes.windll.kernel32.GetLastError() != 183  # ERROR_ALREADY_EXISTS
    except Exception:
        return True


def main():
    if len(sys.argv) > 2 and sys.argv[1] == "--test-webhook":
        cfg = load_cfg()
        now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        try:
            st, resp = post_breach(cfg, sys.argv[2], None, now)
            log(f"[TEST] {sys.argv[2]} -> HTTP {st} {resp}")
        except Exception as e:
            log(f"[TEST] failed: {e}")
        return
    if len(sys.argv) > 1 and sys.argv[1] == "--once":
        cycle(load_cfg(), load_state(), set())
        return
    if not single_instance():
        log("[INFO] another breach_monitor instance already running -> exiting")
        return
    cfg = load_cfg()
    log(f"breach_monitor started; poll={cfg['poll_seconds']}s webhook={cfg['webhook']} key={'set' if cfg.get('api_key') else 'MISSING'}")
    alerted = load_state()
    watched = set()
    while True:
        try:
            cfg = load_cfg()
            cycle(cfg, alerted, watched)
        except Exception as e:
            log(f"[ERR] cycle crashed: {e}\n{traceback.format_exc()}")
        time.sleep(max(10, int(load_cfg().get("poll_seconds", 60))))


if __name__ == "__main__":
    main()
