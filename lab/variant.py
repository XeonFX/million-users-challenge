"""variant.py <name> <entry dir>: turns a copy of this branch's entry into a lab variant, by exact replacements that
must each match once. The names can be joined with '+'.

  four      one loop with the connections a 4-core box gives it (120 of the 480)
  steady    batches never shrink
  calm      nudges as #114 sent them: one after 50 ms of silence, then doubling
  nothin    no TCP_THIN_LINEAR_TIMEOUTS on the clients' sockets (they stay on Postgres's)
  quick     requests are acknowledged at once, not with their response
"""
import sys

PATCHES = {
    "four": [("Program.cs", "const int Connections = 480;", "const int Connections = 120;")],
    "steady": [("Loop.Postgres.cs", "internal const int SmallestBatch = 4;", "internal const int SmallestBatch = 64;")],
    "calm": [
        ("Pg.cs", "public const int ReadNudgeAfterMs = 5, WriteNudgeAfterMs = 20;",
         "public const int ReadNudgeAfterMs = 50, WriteNudgeAfterMs = 50;"),
        ("Pg.cs", "public const int SteadyNudges = 4, NudgeCapMs = 80;", "public const int SteadyNudges = 1, NudgeCapMs = 1 << 20;"),
    ],
    "nothin": [("Native.cs", "        setsockopt(fd, IpProtoTcp, TcpNoDelay, &one, sizeof(int));\n        Thin(fd);\n    }\n\n    /// <summary>The options that only hold once the socket listens",
                "        setsockopt(fd, IpProtoTcp, TcpNoDelay, &one, sizeof(int));\n    }\n\n    /// <summary>The options that only hold once the socket listens")],
    "quick": [
        ("Native.cs", "        if (setsockopt(fd, IpProtoTcp, TcpQuickAck, &zero, sizeof(int)) < 0)\n            System.Console.Error.WriteLine($\"TCP_QUICKACK refused (errno {Marshal.GetLastSystemError()})\");\n", ""),
        ("Native.cs", "        int zero = 0;\n        setsockopt(fd, IpProtoTcp, TcpQuickAck, &zero, sizeof(int));\n    }", "    }"),
        ("Native.cs", "        int seconds = 1, zero = 0;", "        int seconds = 1;"),
    ],
}

names, entry = sys.argv[1].split("+"), sys.argv[2]
for name in names:
    for file, old, new in PATCHES[name]:
        path = f"{entry}/{file}"
        text = open(path).read()
        assert text.count(old) == 1, (name, file, text.count(old))
        open(path, "w").write(text.replace(old, new))
print("variant:", "+".join(names))
