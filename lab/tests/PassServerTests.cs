using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>The built server's loop passes: a lone request is answered after one pass's sleep and no later, and a
/// read that fails inside a big batch still has the reads after it sent again. Linux only. In ServerTests'
/// collection: both change the database under the other's server (revoking, killing sessions).</summary>
[Collection("Server")]
public sealed class PassServerTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    static readonly string Token = JwtTests.Sign(JwtTests.Payload(sub: "\"7\"", username: "\"tester_7\""));

    Client Connect()
    {
        Assert.SkipWhen(server.Skip != null, server.Skip ?? "");
        return new Client(server.Port, timeoutMs: 5_000);
    }

    static string Psql(string url, string sql)
    {
        using var psql = Process.Start(new ProcessStartInfo("psql", [url, "-qXAtc", sql]) { RedirectStandardOutput = true })!;
        string output = psql.StandardOutput.ReadToEnd().Trim();
        psql.WaitForExit();
        Assert.Equal(0, psql.ExitCode);
        return output;
    }

    [Fact]
    public void ALoneRequestOnAnIdleServerTakesOnePassAndNoLonger()
    {
        using Client c = Connect();
        var reads = new List<double>();
        var sw = new Stopwatch();
        for (int i = 0; i < 200; i++)
        {
            sw.Restart();
            Response r = i % 3 == 0 ? c.Get("/health") : i % 3 == 1 ? c.Get("/feed") : c.Get("/posts/" + (i + 1));
            reads.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(200, r.Status);
        }
        reads.Sort();
        // the pass that takes the request sends its read, then the loop sleeps out the pass (2 ms) before it looks for
        // the reply: no read comes back sooner, and the loop wakes for it (it isn't stuck)
        Assert.True(reads[0] >= 1.8, $"fastest read {reads[0]:F2} ms: the loop didn't sleep out its pass");
        Assert.True(reads[reads.Count / 2] < 20, $"median read {reads[reads.Count / 2]:F2} ms");
        Assert.True(reads[^1] < 1000, $"slowest read {reads[^1]:F2} ms");

        // a lone write leaves with its pass too, and comes back once committed
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            c.Send(Client.Request("POST", "/posts", Token, JsonSerializer.Serialize(new { body = "lone " + i })));
            Assert.Equal(201, c.Read().Status);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"a lone write took {sw.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void AReadFailingInsideABigBatchSendsTheRestAgain()
    {
        using Client probe = Connect();
        string? admin = Environment.GetEnvironmentVariable("MU_ADMIN_URL");
        Assert.SkipWhen(admin == null, "MU_ADMIN_URL isn't set");
        string url = Environment.GetEnvironmentVariable("MU_DATABASE_URL")!;
        string user = Uri.UnescapeDataString(new Uri(url).UserInfo.Split(':')[0]);
        Assert.SkipWhen(Psql(url, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user") == "t", "the server's role is a superuser: privileges don't apply");
        string role = "\"" + user.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        // privileges live in the server's database, not the one MU_ADMIN_URL names
        admin = new UriBuilder(admin!) { Path = new Uri(url).AbsolutePath }.Uri.ToString();

        // with likes unreadable, /feed and /posts/:id fail and /health (SELECT 1) doesn't: in a batch that mixes them,
        // every failure skips the reads after it up to the Sync, and those go again until each has its own answer
        var clients = new List<Client>();
        Psql(admin!, $"REVOKE SELECT ON likes FROM {role}");
        try
        {
            Assert.SkipWhen(Psql(url, "SELECT has_table_privilege('likes', 'SELECT')") == "t", "the server's role reads likes through another grant");
            for (int i = 0; i < 60; i++) clients.Add(Connect());
            for (int round = 0; round < 3; round++)
            {
                foreach (var (c, i) in clients.Select((c, i) => (c, i)))
                    c.Send(i % 2 == 0 ? Client.Request("GET", "/health") : Client.Request("GET", i % 4 == 1 ? "/feed" : "/posts/" + (i + 1)));
                foreach (var (c, i) in clients.Select((c, i) => (c, i)))
                {
                    Response r = c.Read();
                    Assert.Equal(i % 2 == 0 ? 200 : 500, r.Status);
                    if (i % 2 == 0) Assert.Equal("ok", r.Json.GetProperty("db").GetString());
                }
            }
        }
        finally
        {
            Psql(admin!, $"GRANT SELECT ON likes TO {role}");
        }

        // and back to normal, on the same connections
        foreach (var (c, i) in clients.Select((c, i) => (c, i)))
            c.Send(Client.Request("GET", "/posts/" + (i + 1)));
        foreach (var (c, i) in clients.Select((c, i) => (c, i)))
            Assert.Equal(i + 1, c.Read().Json.GetProperty("post").GetProperty("id").GetInt64());
        foreach (Client c in clients) c.Dispose();
        Assert.Equal(200, probe.Get("/feed").Status);
    }
}
