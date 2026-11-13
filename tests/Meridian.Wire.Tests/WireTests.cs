using Meridian.Wire.Gen;

namespace Meridian.Wire.Tests;

public class GeneratedCodeTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }

    [Fact]
    public void Generated_file_matches_the_schema()
    {
        var schema = File.ReadAllText(RepoFile("src", "Meridian.Wire", "Schema", "messages.wire"));
        var committed = File.ReadAllText(RepoFile("src", "Meridian.Wire", "Generated", "Messages.g.cs"));
        Assert.True(
            Generator.Generate(schema) == committed,
            "Messages.g.cs is stale. Run: dotnet run --project src/Meridian.Wire.Gen");
    }

    [Fact]
    public void Comments_and_whitespace_do_not_change_the_schema_hash()
    {
        const string a = "message 5 Foo server_bound reliable_ordered\n  u32 bar\nend\n";
        const string b = "# hello\n\nmessage   5 Foo server_bound reliable_ordered  # trailing\n\tu32   bar\nend";
        Assert.Equal(HashLine(Generator.Generate(a)), HashLine(Generator.Generate(b)));
    }

    [Fact]
    public void Changing_a_field_changes_the_schema_hash()
    {
        const string a = "message 5 Foo server_bound reliable_ordered\n  u32 bar\nend\n";
        const string b = "message 5 Foo server_bound reliable_ordered\n  u16 bar\nend\n";
        Assert.NotEqual(HashLine(Generator.Generate(a)), HashLine(Generator.Generate(b)));
    }

    [Theory]
    [InlineData("message 5 Foo server_bound reliable_ordered\n  u32 bar\n", "missing 'end'")]
    [InlineData("message 5 Foo sideways reliable_ordered\nend", "unknown direction")]
    [InlineData("message 5 Foo server_bound reliable_ordered\nend\nmessage 5 Bar server_bound reliable_ordered\nend", "used twice")]
    [InlineData("message 5 Foo server_bound reliable_ordered\n  float bar\nend", "unknown type")]
    [InlineData("message 5 Foo server_bound reliable_ordered\n  Missing bar\nend", "not a struct")]
    [InlineData("message 5 Foo server_bound reliable_ordered\n  u8 a\n  u8 a\nend", "duplicate field")]
    [InlineData("message 5 Foo server_bound reliable_ordered\n  string(0) a\nend", "positive")]
    [InlineData("message 5 Foo server_bound reliable_ordered\n  list<Later>(4) a\nend\nstruct Later\nend", "defined before")]
    [InlineData("message 5 foo server_bound reliable_ordered\nend", "PascalCase")]
    public void Invalid_schemas_fail_with_a_useful_message(string schema, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Generator.Generate(schema));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("line ", ex.Message, StringComparison.Ordinal);
    }

    private static string HashLine(string code) =>
        code.Split('\n').Single(l => l.Contains("const ulong Hash", StringComparison.Ordinal));
}

public class RoundTripTests
{
    [Fact]
    public void DeltaSnapshot_round_trips_bytes_and_lists()
    {
        var original = new DeltaSnapshot
        {
            Tick = 123_456,
            BaselineTick = 123_450,
            AckedInputSequence = 99,
            Entities = [1, 2, 3, 250],
            Removed = [5, 300_000],
        };

        var bytes = Envelope.ToArray(original);
        var copy = Envelope.Decode<DeltaSnapshot>(bytes);

        Assert.Equal(original.Tick, copy.Tick);
        Assert.Equal(original.BaselineTick, copy.BaselineTick);
        Assert.Equal(original.Entities, copy.Entities);
        Assert.Equal(original.Removed, copy.Removed);
        Assert.Equal(bytes, Envelope.ToArray(copy));
    }

    [Fact]
    public void Null_lists_and_strings_encode_as_empty()
    {
        var copy = Envelope.Decode<DeltaSnapshot>(Envelope.ToArray(new DeltaSnapshot { Tick = 1 }));
        Assert.Empty(copy.Entities!);
        Assert.Empty(copy.Removed!);
        var chat = Envelope.Decode<ChatMessage>(Envelope.ToArray(new ChatMessage()));
        Assert.Equal(string.Empty, chat.Text);
    }

    [Fact]
    public void Unicode_text_survives()
    {
        var copy = Envelope.Decode<ChatMessage>(Envelope.ToArray(new ChatMessage { FromName = "Ýrsa", Text = "twilight ☀︎/☾ band" }));
        Assert.Equal("Ýrsa", copy.FromName);
        Assert.Equal("twilight ☀︎/☾ band", copy.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(uint.MaxValue)]
    public void Varints_round_trip(uint value)
    {
        Span<byte> buf = stackalloc byte[8];
        var w = new WireWriter(buf);
        w.WriteVarU32(value);
        var r = new WireReader(w.Written);
        Assert.Equal(value, r.ReadVarU32());
        r.EnsureEnd();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Signed_varints_round_trip(int value)
    {
        Span<byte> buf = stackalloc byte[8];
        var w = new WireWriter(buf);
        w.WriteVarI32(value);
        var r = new WireReader(w.Written);
        Assert.Equal(value, r.ReadVarI32());
    }
}

public class HostileInputTests
{
    private sealed class Recorder : IServerBoundHandler<int>
    {
        public List<string> Calls { get; } = [];

        public void OnMoveIntent(int context, in MoveIntent message) => Calls.Add($"move {message.Sequence}");

        public void OnChatSay(int context, in ChatSay message) => Calls.Add($"say {message.Text}");

        public void OnPing(int context, in Ping message) => Calls.Add("ping");

        public void OnSetTarget(int context, in SetTarget message) => Calls.Add($"target {message.EntityId}");

        public void OnSetAttack(int context, in SetAttack message) => Calls.Add($"attack {message.Enabled}");

        public void OnSnapshotAck(int context, in SnapshotAck message) => Calls.Add($"ack {message.Tick}");
    }

    [Fact]
    public void Dispatch_routes_server_bound_messages()
    {
        var h = new Recorder();
        WireDispatch.DispatchServerBound(Envelope.ToArray(new MoveIntent { Sequence = 7 }), h, 0);
        WireDispatch.DispatchServerBound(Envelope.ToArray(new ChatSay { Text = "hi" }), h, 0);
        Assert.Equal(["move 7", "say hi"], h.Calls);
    }

    [Fact]
    public void Client_cannot_send_internal_or_client_bound_messages()
    {
        var h = new Recorder();
        Assert.Throws<WireFormatException>(() =>
            WireDispatch.DispatchServerBound(Envelope.ToArray(new FromClient { ConnectionId = 1 }), h, 0));
        Assert.Throws<WireFormatException>(() =>
            WireDispatch.DispatchServerBound(Envelope.ToArray(new Welcome()), h, 0));
        Assert.Throws<WireFormatException>(() =>
            WireDispatch.DispatchServerBound(Envelope.ToArray(new ConnectRequest()), h, 0));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public void Trailing_bytes_are_rejected()
    {
        var bytes = Envelope.ToArray(new Ping { ClientTimeMs = 1 }).Append((byte)0).ToArray();
        Assert.Throws<WireFormatException>(() => WireDispatch.DispatchServerBound(bytes, new Recorder(), 0));
    }

    [Fact]
    public void Truncated_packets_are_rejected()
    {
        var bytes = Envelope.ToArray(new MoveIntent { Sequence = 1 });
        for (var len = 0; len < bytes.Length; len++)
        {
            var cut = bytes[..len];
            Assert.Throws<WireFormatException>(() => WireDispatch.DispatchServerBound(cut, new Recorder(), 0));
        }
    }

    [Fact]
    public void Oversized_string_is_rejected_by_writer_and_reader()
    {
        var tooLong = new string('x', 257);
        Assert.Throws<WireFormatException>(() => Envelope.ToArray(new ChatSay { Text = tooLong }));

        // Hand-craft a packet that claims a 300-byte string.
        var bytes = new byte[2 + 2 + 300];
        bytes[0] = (byte)(ChatSay.Id & 0xFF);
        bytes[1] = (byte)(ChatSay.Id >> 8);
        bytes[2] = 0xAC; // varint 300
        bytes[3] = 0x02;
        Assert.Throws<WireFormatException>(() => WireDispatch.DispatchServerBound(bytes, new Recorder(), 0));
    }

    [Fact]
    public void Huge_list_count_is_rejected_before_allocating()
    {
        // DeltaSnapshot: tick, baseline, ack, empty entity bytes, then a removed count of uint.MaxValue.
        byte[] bytes = [(byte)(DeltaSnapshot.Id & 0xFF), (byte)(DeltaSnapshot.Id >> 8), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F];
        var ex = Assert.Throws<WireFormatException>(() => Envelope.Decode<DeltaSnapshot>(bytes));
        Assert.Contains("limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        byte[] bytes = [(byte)(ChatSay.Id & 0xFF), (byte)(ChatSay.Id >> 8), 2, 0xC3, 0x28];
        Assert.Throws<WireFormatException>(() => WireDispatch.DispatchServerBound(bytes, new Recorder(), 0));
    }

    [Fact]
    public void Overlong_varint_is_rejected()
    {
        Assert.Throws<WireFormatException>(() =>
        {
            var r = new WireReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F });
            r.ReadVarU32();
        });
        Assert.Throws<WireFormatException>(() =>
        {
            var r = new WireReader(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 });
            r.ReadVarU32();
        });
    }

    [Fact]
    public void Random_garbage_only_ever_produces_WireFormatException()
    {
        var rng = new Random(1234);
        var h = new Recorder();
        for (var i = 0; i < 50_000; i++)
        {
            var bytes = new byte[rng.Next(0, 40)];
            rng.NextBytes(bytes);
            if (bytes.Length >= 2 && rng.Next(2) == 0)
            {
                // Bias toward valid ids so bodies get exercised too.
                ushort[] ids = [MoveIntent.Id, ChatSay.Id, Ping.Id];
                var id = ids[rng.Next(ids.Length)];
                bytes[0] = (byte)id;
                bytes[1] = (byte)(id >> 8);
            }
            try
            {
                WireDispatch.DispatchServerBound(bytes, h, 0);
            }
            catch (WireFormatException)
            {
            }
        }
    }
}
