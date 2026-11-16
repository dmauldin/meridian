using Meridian.Content;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.World;
using Meridian.Persistence;
using Meridian.Server.Gateway;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

public class ZoneTravelTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ContentPack Pack = ContentPacks.Embedded;
    private static readonly ZoneExit ToRustfall = Pack.Zones["glasswind_flats"].Exits.Single();

    private static (ZoneSimulation Sim, RecordingOutbox Out) Glasswind()
    {
        var outbox = new RecordingOutbox();
        return (new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3 }, outbox), outbox);
    }

    private static CharacterRecord Stored(ulong id = 7) =>
        CharacterRecords.New(Pack, 1, "Rook", Profession.Operative, Origin.Baseline) with { Id = id, Version = 1 };

    private static void Steps(ZoneSimulation sim, int n)
    {
        for (var i = 0; i < n; i++)
        {
            sim.Step();
        }
    }

    [Fact]
    public void Stepping_into_an_exit_departs_with_the_character_placed_at_the_arrival_point()
    {
        var (sim, outbox) = Glasswind();
        var p = sim.Join(A, Stored())!;
        p.Lattice.Cast(Pack.Protocols["buff.rifle_enhancement_1"], p.Lattice, true, 0, sim.Tick);
        Steps(sim, ZoneSimulation.ExitGraceTicks);
        p.Position = ToRustfall.Area.Center;
        sim.Step();

        Assert.Equal(Pack.Zones["rustfall_basin"].Id, outbox.Last<ZoneChange>(A).ZoneId);
        Assert.Null(sim.FindCharacter(7));
        Assert.Equal(0, sim.PlayerCount);
        var departure = Assert.Single(sim.DrainDepartures());
        Assert.Equal((A, Pack.Zones["rustfall_basin"].Id), (departure.Client, departure.ToZoneId));
        Assert.Equal(new Placement("rustfall_basin", ToRustfall.Arrive, p.Health), departure.Record.Placement);
        Assert.Single(departure.Record.Buffs);                 // carried along
        Assert.Empty(sim.DrainCheckpoints());                  // the departure is the save, not a checkpoint
        Assert.Empty(sim.DrainDepartures());
    }

    [Fact]
    public void Exits_are_ignored_just_after_joining()
    {
        var (sim, _) = Glasswind();
        var p = sim.Join(A, Stored())!;
        p.Position = ToRustfall.Area.Center;
        Steps(sim, ZoneSimulation.ExitGraceTicks - 2);
        Assert.Empty(sim.DrainDepartures());
        Steps(sim, 3);
        Assert.Single(sim.DrainDepartures());
    }

    [Fact]
    public void Unstored_and_dead_characters_do_not_travel()
    {
        var (sim, _) = Glasswind();
        var temp = sim.Join(A, 1, "temp");
        var dead = sim.Join(new ClientRef(1, 2), Stored())!;
        Steps(sim, ZoneSimulation.ExitGraceTicks);
        temp.Position = dead.Position = ToRustfall.Area.Center;
        dead.Health = 0;
        dead.Dead = true;
        sim.Step();
        Assert.Empty(sim.DrainDepartures());
    }

    [Fact]
    public void Arriving_places_the_character_at_the_arrival_point()
    {
        var outbox = new RecordingOutbox();
        var rustfall = new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3, ZoneKey = "rustfall_basin" }, outbox);
        var arrived = rustfall.Join(A, Stored() with { Placement = new Placement("rustfall_basin", ToRustfall.Arrive, 50) })!;
        Assert.Equal(ToRustfall.Arrive, arrived.Position);
        Assert.Equal(50, arrived.Health);
        Assert.Equal(Pack.Zones["rustfall_basin"].Id, outbox.Last<Welcome>(A).ZoneId);
    }

    [Fact]
    public void Goto_is_a_development_command()
    {
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3, DevCommands = true }, outbox);
        var p = sim.Join(A, 1, "gm");
        sim.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = "/goto 12.5 -3" }));
        Assert.Equal(new Position(12_500, -3_000), p.Position);
        sim.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = "/goto 999999 0" }));
        Assert.Equal(Pack.Zones["glasswind_flats"].Bounds.MaxX, p.Position.X); // clamped to the zone
        sim.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = "/goto NaN 0" }));
        Assert.Equal("Usage: /goto <x metres> <z metres>", outbox.Last<Notice>(A).Text);
    }

    [Theory]
    [InlineData("1@127.0.0.1:7800", 1, "127.0.0.1", 7800)]
    [InlineData(" 2@zone-two.internal:7801 , 1@10.0.0.5:7800", 2, "zone-two.internal", 7801)]
    public void Gateway_zone_lists_parse(string text, uint id, string host, int port)
    {
        var first = ZoneEndpoint.ParseList(text)[0];
        Assert.Equal(new ZoneEndpoint(id, host, port), first);
    }

    [Theory]
    [InlineData("127.0.0.1:7800")]
    [InlineData("x@127.0.0.1:7800")]
    [InlineData("1@127.0.0.1")]
    [InlineData("1@:7800")]
    public void Bad_gateway_zone_lists_are_refused(string text) =>
        Assert.Throws<FormatException>(() => ZoneEndpoint.ParseList(text));
}
