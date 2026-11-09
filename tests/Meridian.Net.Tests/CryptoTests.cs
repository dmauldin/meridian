using System.Security.Cryptography;

namespace Meridian.Net.Tests;

public class SessionCipherTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static (SessionCipher Client, SessionCipher Server) Pair() =>
        (new SessionCipher(Key, PeerRole.Client), new SessionCipher(Key, PeerRole.Server));

    private static byte[] Seal(SessionCipher c, byte[] plain)
    {
        var buf = new byte[plain.Length + SessionCipher.Overhead];
        Assert.Equal(buf.Length, c.Seal(plain, buf));
        return buf;
    }

    private static byte[]? Open(SessionCipher c, byte[] sealedPacket)
    {
        var buf = new byte[sealedPacket.Length];
        var n = c.Open(sealedPacket, buf);
        return n < 0 ? null : buf[..n];
    }

    private static byte[] OpenOk(SessionCipher c, byte[] sealedPacket)
    {
        var opened = Open(c, sealedPacket);
        Assert.NotNull(opened);
        return opened;
    }

    [Fact]
    public void Round_trips_in_both_directions()
    {
        var (client, server) = Pair();
        Assert.Equal([1, 2, 3], OpenOk(server, Seal(client, [1, 2, 3])));
        Assert.Equal([9], OpenOk(client, Seal(server, [9])));
        Assert.Equal([], OpenOk(server, Seal(client, [])));
    }

    [Fact]
    public void Any_flipped_bit_is_rejected()
    {
        var (client, server) = Pair();
        var sealedPacket = Seal(client, [10, 20, 30, 40]);
        for (var i = 0; i < sealedPacket.Length * 8; i++)
        {
            var copy = (byte[])sealedPacket.Clone();
            copy[i / 8] ^= (byte)(1 << (i % 8));
            Assert.Null(Open(server, copy));
        }
        Assert.Equal(sealedPacket.Length * 8, server.Rejected);
        Assert.NotNull(Open(server, sealedPacket)); // the original still opens
    }

    [Fact]
    public void Replays_are_rejected()
    {
        var (client, server) = Pair();
        var p = Seal(client, [1]);
        Assert.NotNull(Open(server, p));
        Assert.Null(Open(server, p));
    }

    [Fact]
    public void Reordered_packets_within_the_window_are_accepted()
    {
        var (client, server) = Pair();
        var packets = Enumerable.Range(0, 10).Select(i => Seal(client, [(byte)i])).ToArray();
        foreach (var i in new[] { 3, 0, 9, 1, 2, 8, 4, 7, 5, 6 })
        {
            Assert.Equal([(byte)i], OpenOk(server, packets[i]));
        }
    }

    [Fact]
    public void Packets_older_than_the_window_are_rejected()
    {
        var (client, server) = Pair();
        var old = Seal(client, [0]);
        for (var i = 0; i < 64; i++)
        {
            Open(server, Seal(client, [1]));
        }
        Assert.Null(Open(server, old));
    }

    [Fact]
    public void A_side_cannot_open_its_own_packets()
    {
        // Reflection attack: bouncing a client packet back at the client must fail.
        var (client, _) = Pair();
        Assert.Null(Open(client, Seal(client, [1])));
    }

    [Fact]
    public void Wrong_key_is_rejected()
    {
        var client = new SessionCipher(Key, PeerRole.Client);
        var other = new SessionCipher(RandomNumberGenerator.GetBytes(32), PeerRole.Server);
        Assert.Null(Open(other, Seal(client, [1])));
    }

    [Fact]
    public void Short_or_empty_input_is_rejected_without_throwing()
    {
        var (_, server) = Pair();
        Assert.Null(Open(server, []));
        Assert.Null(Open(server, new byte[SessionCipher.Overhead - 1]));
        Assert.Null(Open(server, new byte[SessionCipher.Overhead]));
    }
}

public class ReplayWindowTests
{
    [Fact]
    public void Counter_zero_is_never_valid()
    {
        Assert.False(default(ReplayWindow).CanAccept(0));
    }

    [Fact]
    public void Large_jump_clears_history()
    {
        var w = default(ReplayWindow);
        w.Accept(1);
        w.Accept(1_000);
        Assert.False(w.CanAccept(1_000));
        Assert.True(w.CanAccept(999));
        Assert.False(w.CanAccept(1)); // too old
    }
}

public class TicketTests
{
    private static readonly byte[] ServerKey = RandomNumberGenerator.GetBytes(32);

    private static LoginTicket Sample() =>
        new(42, 7, "Ýrsa", RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());

    [Fact]
    public void Round_trips()
    {
        using var sealer = new TicketSealer(ServerKey);
        var t = Sample();
        var opened = sealer.TryOpen(sealer.Seal(t));
        Assert.NotNull(opened);
        Assert.Equal(t.TicketId, opened.TicketId);
        Assert.Equal(t.AccountId, opened.AccountId);
        Assert.Equal(t.CharacterName, opened.CharacterName);
        Assert.Equal(t.SessionKey, opened.SessionKey);
        Assert.Equal(t.ExpiresAt, opened.ExpiresAt);
    }

    [Fact]
    public void Same_ticket_seals_differently_each_time()
    {
        using var sealer = new TicketSealer(ServerKey);
        var t = Sample();
        Assert.NotEqual(sealer.Seal(t), sealer.Seal(t));
    }

    [Fact]
    public void Tampered_or_foreign_tickets_are_rejected()
    {
        using var sealer = new TicketSealer(ServerKey);
        using var other = new TicketSealer(RandomNumberGenerator.GetBytes(32));
        var sealedTicket = sealer.Seal(Sample());

        Assert.Null(other.TryOpen(sealedTicket));
        sealedTicket[20] ^= 1;
        Assert.Null(sealer.TryOpen(sealedTicket));
        Assert.Null(sealer.TryOpen([]));
        Assert.Null(sealer.TryOpen(new byte[100]));
    }

    [Fact]
    public void Each_ticket_can_be_used_once()
    {
        var used = new UsedTicketSet();
        var t = Sample();
        Assert.True(used.TryUse(t, 0));
        Assert.False(used.TryUse(t, 0));
        Assert.True(used.TryUse(t with { TicketId = 43 }, 0));
    }

    [Fact]
    public void Expired_ids_are_forgotten()
    {
        var used = new UsedTicketSet();
        var t = Sample() with { ExpiresAt = 100 };
        Assert.True(used.TryUse(t, 50));
        Assert.True(used.TryUse(t with { TicketId = 99, ExpiresAt = 1_000 }, 200)); // triggers sweep
        Assert.True(used.TryUse(t, 200)); // expired id was swept; the gateway also checks ExpiresAt itself
    }
}
