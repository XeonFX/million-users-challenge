#!/usr/bin/env bash
# staircase.sh <name> <entry dir> <cap packets/s> <users...>: the most users an entry holds when what comes in to
# it past <cap> packets a second is dropped (loss.sh, PPS_CAP). One run per user count, upward, until two in a row
# fail. Prints each run's line and the most users that passed.
set -uo pipefail
name=$1 entry=$2 cap=$3; shift 3
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
best=0 fails=0
for users in "$@"; do
  line=$(PPS_CAP=$cap bash "$ROOT/lab/loss.sh" "$name-cap$cap-u$users" "$entry" "$users" "${THINK:-0.25}" "${HOLD:-60}" 0 0 "${CPUS:-0}" | tail -1)
  echo "$line"
  if echo "$line" | cut -f10 | grep -q '^pass$'; then best=$users; fails=0; else fails=$((fails + 1)); fi
  [ "$fails" -ge 2 ] && break
done
printf '%s\t%s\t%s\n' "$name" "$cap" "$best" | tee -a "$ROOT/lab/out/held.tsv"
