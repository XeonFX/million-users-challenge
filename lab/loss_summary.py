"""One TSV line for a loss.sh run: the result, each endpoint's tail, packets per request by kind, and what each
side's TCP had to do about the drops (the app's side shares its counters with the load generator, Postgres's is its
own)."""
import json
import sys

HEADER = ["label", "pg%", "cl%", "req/s", "p50", "p95", "p99", "max", "err%", "pass",
          "feed95", "feed99", "post95", "post99", "like95", "like99",
          "pg_in/r", "pg_out/r", "pgB_in/r", "srv_ack/r", "resp/r", "cl_ack/r", "req/r", "pkts/r",
          "pg_rto/s", "pg_tlp/s", "pg_rexmit/s", "app_rto/s", "app_tlp/s", "app_rexmit/s", "cpu_s"]


def counters(path):
    out = {}
    try:
        for line in open(path):
            parts = line.split()
            if len(parts) >= 2 and parts[1].lstrip("-").isdigit():
                out[parts[0]] = int(parts[1])
    except OSError:
        pass
    return out


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "header":
        print("\t".join(HEADER))
        return
    label, pg_loss, cl_loss, result_path, raw, nstat, cpu = sys.argv[1:8]
    r = json.load(open(result_path))
    rows = [tuple(map(int, line.split())) for line in raw.split("\n") if line.strip()]
    (pg_in, pg_in_b), (pg_out, _), (srv_ack, _), (resp, _), (cl_ack, _), (req, _) = rows
    n = max(r["reqs"], 1)
    secs = max(r["duration_s"], 1)
    ep = {e["name"]: e for e in r["endpoints"]}

    def delta(side, key):
        return counters(f"{nstat}.{side}1").get(key, 0) - counters(f"{nstat}.{side}0").get(key, 0)

    def tail(name):
        e = ep.get(name, {})
        return [f"{e.get('p95_ms', 0):.0f}", f"{e.get('p99_ms', 0):.0f}"]

    total = pg_in + pg_out + srv_ack + resp + cl_ack + req
    print("\t".join([
        label, pg_loss, cl_loss, f"{r['req_per_s']:.0f}", f"{r['p50_ms']:.1f}", f"{r['p95_ms']:.0f}",
        f"{r['p99_ms']:.0f}", f"{r['max_ms']:.0f}", f"{r['err_pct']:.3f}", "pass" if r["pass"] else "FAIL",
        *tail("GET /feed"), *tail("GET /posts/:id"), *tail("POST /posts/:id/like"),
        f"{pg_in / n:.2f}", f"{pg_out / n:.2f}", f"{pg_in_b / n:.0f}", f"{srv_ack / n:.2f}", f"{resp / n:.2f}",
        f"{cl_ack / n:.2f}", f"{req / n:.2f}", f"{total / n:.2f}",
        f"{delta('pg', 'TcpExtTCPTimeouts') / secs:.1f}", f"{delta('pg', 'TcpExtTCPLossProbes') / secs:.1f}",
        f"{delta('pg', 'TcpRetransSegs') / secs:.0f}",
        f"{delta('app', 'TcpExtTCPTimeouts') / secs:.1f}", f"{delta('app', 'TcpExtTCPLossProbes') / secs:.1f}",
        f"{delta('app', 'TcpRetransSegs') / secs:.0f}", cpu.strip(),
    ]))


main()
