using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>SCRAM-SHA-256 against a server written from RFC 5802 here: it must accept our proof, and we must refuse a
/// server that doesn't know the password.</summary>
public sealed class ScramTests
{
    sealed class Server(string password)
    {
        readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
        const int Iterations = 4096;
        string _clientFirstBare = "", _serverFirst = "";
        byte[] _salted = [];

        public string First(string clientFirst, string? nonceOverride = null)
        {
            Assert.StartsWith("n,,", clientFirst, StringComparison.Ordinal);
            _clientFirstBare = clientFirst[3..];
            string clientNonce = _clientFirstBare.Split(',').Single(p => p.StartsWith("r=", StringComparison.Ordinal))[2..];
            _serverFirst = $"r={nonceOverride ?? clientNonce + "srv0123"},s={Convert.ToBase64String(_salt)},i={Iterations}";
            _salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), _salt, Iterations, HashAlgorithmName.SHA256, 32);
            return _serverFirst;
        }

        /// <summary>The server-final-message, or null when the proof is wrong.</summary>
        public string? Final(string clientFinal)
        {
            int p = clientFinal.LastIndexOf(",p=", StringComparison.Ordinal);
            string withoutProof = clientFinal[..p];
            byte[] proof = Convert.FromBase64String(clientFinal[(p + 3)..]);
            byte[] auth = Encoding.UTF8.GetBytes(_clientFirstBare + "," + _serverFirst + "," + withoutProof);
            byte[] storedKey = SHA256.HashData(HMACSHA256.HashData(_salted, "Client Key"u8));
            byte[] signature = HMACSHA256.HashData(storedKey, auth);
            byte[] clientKey = proof.Zip(signature, (a, b) => (byte)(a ^ b)).ToArray();
            if (!SHA256.HashData(clientKey).SequenceEqual(storedKey)) return null;
            return "v=" + Convert.ToBase64String(HMACSHA256.HashData(HMACSHA256.HashData(_salted, "Server Key"u8), auth));
        }
    }

    static string Ascii(byte[] b) => Encoding.ASCII.GetString(b);

    [Theory]
    [InlineData("feedpass")]
    [InlineData("")]
    [InlineData("pä$$ wörd,=")]
    public void TheRightPasswordLogsIn(string password)
    {
        var server = new Server(password);
        var client = new Scram(password);
        string serverFirst = server.First(Ascii(client.ClientFirst()));
        string? serverFinal = server.Final(Ascii(client.ClientFinal(Encoding.ASCII.GetBytes(serverFirst))));
        Assert.NotNull(serverFinal);
        client.VerifyServerFinal(Encoding.ASCII.GetBytes(serverFinal));
    }

    [Fact]
    public void AWrongPasswordIsRefusedByTheServer()
    {
        var server = new Server("right");
        var client = new Scram("wrong");
        string serverFirst = server.First(Ascii(client.ClientFirst()));
        Assert.Null(server.Final(Ascii(client.ClientFinal(Encoding.ASCII.GetBytes(serverFirst)))));
    }

    [Fact]
    public void AServerThatDoesNotKnowThePasswordIsRefused()
    {
        var real = new Server("right");
        var client = new Scram("right");
        string serverFirst = real.First(Ascii(client.ClientFirst()));
        client.ClientFinal(Encoding.ASCII.GetBytes(serverFirst));
        string forged = "v=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<InvalidOperationException>(() => client.VerifyServerFinal(Encoding.ASCII.GetBytes(forged)));
        Assert.ThrowsAny<Exception>(() => client.VerifyServerFinal("e=other-error"u8));
    }

    [Fact]
    public void AServerNonceThatDoesNotExtendOursIsRefused()
    {
        var server = new Server("p");
        var client = new Scram("p");
        string serverFirst = server.First(Ascii(client.ClientFirst()), nonceOverride: "someoneelsesnonce");
        Assert.Throws<InvalidOperationException>(() => client.ClientFinal(Encoding.ASCII.GetBytes(serverFirst)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("s=abc,i=1")]
    public void AMalformedServerFirstIsRefused(string serverFirst) =>
        Assert.Throws<InvalidOperationException>(() => new Scram("p").ClientFinal(Encoding.ASCII.GetBytes(serverFirst)));

    [Fact]
    public void EachLoginUsesAFreshNonce() => Assert.NotEqual(Ascii(new Scram("p").ClientFirst()), Ascii(new Scram("p").ClientFirst()));
}

public sealed class PgHelperTests
{
    static byte[] Error(params (char Field, string Value)[] fields) =>
        [.. fields.SelectMany(f => new[] { (byte)f.Field }.Concat(Encoding.UTF8.GetBytes(f.Value)).Append((byte)0)), 0];

    [Fact]
    public void ErrorFieldsAreFoundByCode()
    {
        byte[] e = Error(('S', "ERROR"), ('V', "ERROR"), ('C', "23503"), ('M', "insert violates foreign key"), ('D', "Key (post_id)=(9) is not present"));
        Assert.Equal("23503", Encoding.ASCII.GetString(Pg.ErrorField(e, (byte)'C')));
        Assert.Equal("insert violates foreign key", Pg.ErrorText(e));
        Assert.True(Pg.ErrorField(e, (byte)'H').IsEmpty);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { (byte)'M' })]
    [InlineData(new byte[] { (byte)'M', (byte)'x' })]
    public void ATruncatedErrorHasNoFields(byte[] body) => Assert.True(Pg.ErrorField(body, (byte)'M').IsEmpty);

    [Fact]
    public void TheContestUrlIsRead()
    {
        var s = PgSettings.Parse("postgres://feed:s3cret@10.0.0.5:5432/feed");
        Assert.Equal(new PgSettings("10.0.0.5", 5432, "feed", "s3cret", "feed"), s);
    }

    [Fact]
    public void EscapesAreDecodedAndThePortDefaults()
    {
        var s = PgSettings.Parse("postgres://us%40er:p%3Aw%2Fd@db.local/my%20db");
        Assert.Equal(new PgSettings("db.local", 5432, "us@er", "p:w/d", "my db"), s);
    }

    [Fact]
    public void NoPasswordIsEmpty() => Assert.Equal("", PgSettings.Parse("postgres://postgres@localhost:55432/feed").Password);
}
