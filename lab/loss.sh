#!/usr/bin/env bash
# loss.sh <label> <entry dir> <users> <think scale> <hold s> <pg loss %> <client loss %> [cpus] [NAME=value ...]
#
# One load run against a built entry with packets dropped on purpose, as the scoring box's network drops them near
# its limit: <pg loss> of the packets between the server and Postgres (each way) and <client loss> of the packets
# between the clients and the server (each way). Prints one TSV line and appends it to lab/out/loss.tsv.
#
# Postgres runs in a container on a bridge, so its link is a real one with its own TCP stack at the far end. Packets
# are dropped where they arrive (the receiver's INPUT), after the sender's stack has sent them: a drop on the sender's
# OUTPUT would tell its TCP the packet never left, which is not what a lost packet looks like.
set -euo pipefail
label=$1 entry=$2 users=$3 think=$4 hold=$5 pg_loss=$6 cl_loss=$7 cpus=${8:-0}
extra="${*:9}"   # the server's environment on top of the challenge's: this branch's lab switches
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
[[ "$entry" = /* ]] || entry="$ROOT/$entry"
OUT="$ROOT/lab/out"; mkdir -p "$OUT"
DB=feed_lab

cid=$(docker ps -q --filter ancestor=postgres:18 | head -1)
pgip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$cid")
pgpid=$(docker inspect -f '{{.State.Pid}}' "$cid")
inpg() { sudo nsenter -t "$pgpid" -n "$@"; }

# removes every rule of ours from the built-in chains, then our chains, here and in Postgres's namespace
clear_in() {
  { "$@" iptables -S 2>/dev/null | grep -E '^-A (INPUT|OUTPUT) .*-j MU_' || true; } | sed 's/^-A/-D/' |
    while read -r rule; do "$@" iptables $rule || true; done
  for c in MU_COUNT MU_LOSS; do "$@" iptables -F $c 2>/dev/null || true; "$@" iptables -X $c 2>/dev/null || true; done
}
clear_rules() { clear_in sudo; clear_in sudo nsenter -t "$pgpid" -n; }
trap clear_rules EXIT
clear_rules

# the Postgres link as a wire: one packet per segment (no segmentation offload, which would make a drop a burst of
# up to 45 segments), and a datacenter's delay each way where the kernel has netem
bridge=$(ip -o route get "$pgip" | grep -o 'dev [^ ]*' | cut -d' ' -f2)
for i in $(ls /sys/class/net | grep -E '^(veth|br-|docker)' || true); do
  sudo ethtool -K "$i" tso off gso off gro off >/dev/null 2>&1 || true
done
inpg ethtool -K eth0 tso off gso off gro off >/dev/null 2>&1 || true
delay=${PG_DELAY_US:-100}
if [ "$delay" != 0 ]; then
  if sudo tc qdisc replace dev "$bridge" root netem delay "${delay}us" limit 100000 2>/dev/null &&
     inpg tc qdisc replace dev eth0 root netem delay "${delay}us" limit 100000 2>/dev/null; then
    echo "Postgres link: ${delay} us each way"
  else
    echo "no netem here: the Postgres link has no added delay"
  fi
fi

psql -qX -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" -c "CREATE DATABASE $DB TEMPLATE feed_seed"

# counters, on what is offered (before any drop)
sudo iptables -N MU_COUNT
count() { sudo iptables -A MU_COUNT -p tcp "$@" -j RETURN; }
count -s "$pgip" --sport 5432                                  # 1 Postgres -> app
count -d "$pgip" --dport 5432                                  # 2 app -> Postgres
count -i lo --sport 3000:3001 -m length --length 0:80          # 3 server -> clients, no payload (ACKs)
count -i lo --sport 3000:3001                                  # 4 server -> clients, responses
count -i lo --dport 3000:3001 -m length --length 0:80          # 5 clients -> server, no payload
count -i lo --dport 3000:3001                                  # 6 clients -> server, requests
sudo iptables -I INPUT -j MU_COUNT
sudo iptables -I OUTPUT -o "$bridge" -j MU_COUNT

sudo bash -c "ulimit -n 2000000; cd '$entry' && exec taskset -c $cpus env $extra DATABASE_URL=postgres://postgres:postgres@$pgip:5432/$DB \
  JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=3000 EXTRA_PORT=3001 bash start.sh" > "$OUT/$label.server.log" 2>&1 &
server=$!
for _ in $(seq 60); do curl -sf http://127.0.0.1:3000/health >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:3000/health >/dev/null || { echo "server didn't come up"; cat "$OUT/$label.server.log"; exit 1; }

# the drops, once the server is up and logged in
prob() { awk -v p="$1" 'BEGIN { printf "%.4f", p / 100 }'; }
sudo iptables -N MU_LOSS; inpg iptables -N MU_LOSS
if [ "$pg_loss" != 0 ]; then
  p=$(prob "$pg_loss")
  sudo iptables -A MU_LOSS -p tcp -s "$pgip" --sport 5432 -m statistic --mode random --probability "$p" -j DROP
  inpg iptables -A MU_LOSS -p tcp --dport 5432 -m statistic --mode random --probability "$p" -j DROP
fi
if [ "$cl_loss" != 0 ]; then
  p=$(prob "$cl_loss")
  sudo iptables -A MU_LOSS -i lo -p tcp --dport 3000:3001 -m statistic --mode random --probability "$p" -j DROP
  sudo iptables -A MU_LOSS -i lo -p tcp --sport 3000:3001 -m statistic --mode random --probability "$p" -j DROP
fi
# after the counters: INPUT is MU_COUNT, then MU_LOSS
sudo iptables -I INPUT 2 -j MU_LOSS
inpg iptables -I INPUT -j MU_LOSS

sudo iptables -Z MU_COUNT
nstat -az > "$OUT/$label.nstat.app0" 2>/dev/null || true
inpg nstat -az > "$OUT/$label.nstat.pg0" 2>/dev/null || true
targets=$(seq -s, -f "127.0.0.%g" 1 8)
sudo bash -c "ulimit -n 2000000; exec '$ROOT/bench/mload/mload' -target $targets -ports 3000,3001 -users $users \
  -ramp 15s -hold ${hold}s -gzip -think-scale $think -tokens '$ROOT/seed/tokens.json' -out '$OUT/$label.json'" \
  > "$OUT/$label.mload.log" 2>&1 || true
counters=$(sudo iptables -L MU_COUNT -v -x -n | awk 'NR>2 {print $1, $2}')
nstat -az > "$OUT/$label.nstat.app1" 2>/dev/null || true
inpg nstat -az > "$OUT/$label.nstat.pg1" 2>/dev/null || true
# the CPU seconds of the server and whatever it forked
cpu=$(ps -eo pid=,ppid=,cputimes= | awk -v root="$server" '{ pp[$1] = $2; t[$1] = $3 }
  END { for (p in pp) { q = p; while (q in pp && q != root && q != 1) q = pp[q]; if (q == root) s += t[p] } print s + 0 }')
sudo kill "$server" 2>/dev/null || true   # sudo passes the signal on to the server
wait "$server" 2>/dev/null || true
for _ in $(seq 20); do curl -s -o /dev/null --max-time 1 http://127.0.0.1:3000/health || break; sleep 0.5; done

grep RESULT "$OUT/$label.mload.log" || { tail -20 "$OUT/$label.mload.log"; exit 1; }
python3 "$ROOT/lab/loss_summary.py" "$label" "$pg_loss" "$cl_loss" "$OUT/$label.json" "$counters" "$OUT/$label.nstat" "$cpu" \
  | tee -a "$OUT/loss.tsv"
