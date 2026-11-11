#!/usr/bin/env bash
# measure.sh <label> <entry dir> <users> <think scale> <hold s>: one load run against a built entry on this machine,
# counting what crosses each kind of socket. Prints one TSV line of per-request figures and appends it to
# lab/out/summary.tsv.
#
# Loopback has no NIC, so packet sizes here aren't the wire's. Bytes are, and so are packets without payload (ACKs):
# Postgres's bytes per request (most of what the scoring box takes in), the clients' bytes, and the server's lone
# ACKs to clients.
set -euo pipefail
label=$1 entry=$2 users=$3 think=$4 hold=$5
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/lab/out"; mkdir -p "$OUT"
DB=feed_lab

psql -qX -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" -c "CREATE DATABASE $DB TEMPLATE feed_seed"

# counters on loopback's OUTPUT: every packet between two local sockets passes it once
sudo iptables -N MU 2>/dev/null || sudo iptables -F MU
sudo iptables -C OUTPUT -o lo -j MU 2>/dev/null || sudo iptables -I OUTPUT -o lo -j MU
rule() { sudo iptables -A MU -p tcp "$@" -j RETURN; }
rule --sport 5432                                    # 1 Postgres -> app
rule --dport 5432                                    # 2 app -> Postgres
rule --sport 3000:3001 -m length --length 0:80       # 3 server -> clients, no payload (ACKs)
rule --sport 3000:3001                               # 4 server -> clients, the rest (responses)
rule --dport 3000:3001                               # 5 clients -> server

sudo bash -c "ulimit -n 2000000; cd '$entry' && exec env DATABASE_URL=postgres://postgres:postgres@127.0.0.1:5432/$DB \
  JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=3000 EXTRA_PORT=3001 bash start.sh" > "$OUT/$label.server.log" 2>&1 &
server=$!
for _ in $(seq 60); do curl -sf http://127.0.0.1:3000/health >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:3000/health >/dev/null || { echo "server didn't come up"; cat "$OUT/$label.server.log"; exit 1; }

sudo iptables -Z MU
nstat -n
targets=$(seq -s, -f "127.0.0.%g" 1 8)
sudo bash -c "ulimit -n 2000000; exec '$ROOT/bench/mload/mload' -target $targets -ports 3000,3001 -users $users \
  -ramp 30s -hold ${hold}s -gzip -think-scale $think -tokens '$ROOT/seed/tokens.json' -out '$OUT/$label.json'" \
  > "$OUT/$label.mload.log" 2>&1 || true
counters=$(sudo iptables -L MU -v -x -n | awk 'NR>2 {print $1, $2}')
nstat > "$OUT/$label.nstat" || true
sudo kill "$server" || true   # sudo passes the signal on to the server
wait "$server" 2>/dev/null || true

grep RESULT "$OUT/$label.mload.log" || { tail -20 "$OUT/$label.mload.log"; exit 1; }
python3 - "$label" "$OUT/$label.json" "$counters" "$OUT/$label.nstat" <<'PY' | tee -a "$OUT/summary.tsv"
import json, sys
label, result, counters, nstat = sys.argv[1], json.load(open(sys.argv[2])), sys.argv[3].split("\n"), sys.argv[4]
c = [tuple(map(int, l.split())) for l in counters if l.strip()]
(pg_in_p, pg_in_b), (pg_out_p, pg_out_b), (ack_p, ack_b), (resp_p, resp_b), (cl_p, cl_b) = c
n = result["reqs"]
stats = {}
for line in open(nstat):
    parts = line.split()
    if len(parts) >= 2 and parts[1].isdigit(): stats[parts[0]] = int(parts[1])
def per(x): return f"{x / n:.2f}"
print("\t".join([label, str(n), f"{result['req_per_s']:.0f}", f"{result['p50_ms']:.1f}", f"{result['p95_ms']:.1f}",
                 f"{result['p99_ms']:.1f}", f"{result['err_pct']:.3f}",
                 per(pg_in_b), per(pg_out_b), per(resp_b), per(ack_p), per(cl_b),
                 per(stats.get("TcpExtDelayedACKs", 0)), per(stats.get("TcpExtTCPDeferAcceptDrop", 0))]))
PY
