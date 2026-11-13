namespace Meridian.Wire.Delta;

/// <summary>
/// Packs entity states as changes against a baseline. Hand-written because the schema language
/// has no optional fields; it is fuzzed and round-trip tested like the generated codecs.
/// </summary>
/// <remarks>
/// <para>Stream: <c>varu32 count</c>, then per entity <c>varu32 id, u8 mask, fields...</c>.</para>
/// <para>Mask bits: 1 position, 2 yaw, 4 health, 8 flags, 0x80 absolute. An absolute entry
/// carries every field with positions as <c>i32</c>; it is used when the entity is not in the
/// baseline (or its offset would not fit). A relative entry carries only changed fields, with
/// positions as zig-zag varint offsets from the baseline (1–3 bytes for normal movement).</para>
/// </remarks>
public static class EntityDeltaCodec
{
    public const int MaxEntities = 64;

    /// <summary>Worst-case encoded size of one entity (absolute, 5-byte id).</summary>
    public const int MaxEntryBytes = 5 + 1 + 4 + 4 + 1 + 1 + 1;

    private const byte Position = 1, Yaw = 2, Health = 4, Flags = 8, Absolute = 0x80;
    private const byte AllFields = Position | Yaw | Health | Flags;

    /// <summary>
    /// Encodes the entries of <paramref name="current"/> that differ from <paramref name="baseline"/>
    /// (all of them when it is null) into <paramref name="destination"/>; returns bytes written.
    /// </summary>
    public static int Encode(ReadOnlySpan<EntityState> current, IReadOnlyDictionary<uint, EntityState>? baseline, Span<byte> destination)
    {
        if (current.Length > MaxEntities)
        {
            throw new WireFormatException($"{current.Length} entities exceeds the limit of {MaxEntities}.");
        }

        // Count first: only changed entities are written.
        var count = 0;
        foreach (var e in current)
        {
            if (baseline is null || !baseline.TryGetValue(e.EntityId, out var b) || !Same(e, b))
            {
                count++;
            }
        }

        var w = new WireWriter(destination);
        w.WriteVarU32((uint)count);
        foreach (var e in current)
        {
            if (baseline is not null && baseline.TryGetValue(e.EntityId, out var b))
            {
                if (!Same(e, b))
                {
                    WriteRelative(ref w, e, b);
                }
            }
            else
            {
                WriteAbsolute(ref w, e);
            }
        }
        return w.Position;
    }

    /// <summary>
    /// Applies an encoded stream to <paramref name="states"/>, which the caller has filled with
    /// the baseline (minus removed entities). Throws <see cref="WireFormatException"/> on any
    /// malformed input, including relative entries for entities the baseline lacks.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> data, Dictionary<uint, EntityState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var r = new WireReader(data);
        var count = r.ReadCount(MaxEntities);
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadVarU32();
            if (!seen.Add(id))
            {
                throw new WireFormatException($"Entity {id} appears twice in one snapshot.");
            }
            var mask = r.ReadU8();
            if ((mask & ~(AllFields | Absolute)) != 0)
            {
                throw new WireFormatException($"Unknown delta mask bits 0x{mask:X2}.");
            }

            EntityState e;
            if ((mask & Absolute) != 0)
            {
                if ((mask & AllFields) != AllFields)
                {
                    throw new WireFormatException("Absolute delta entry must carry every field.");
                }
                e = new EntityState { EntityId = id, X = r.ReadI32(), Z = r.ReadI32(), Yaw = r.ReadU8(), HealthPercent = r.ReadU8(), Flags = r.ReadU8() };
            }
            else
            {
                if (!states.TryGetValue(id, out e))
                {
                    throw new WireFormatException($"Relative delta for entity {id}, which is not in the baseline.");
                }
                if ((mask & Position) != 0)
                {
                    e.X = AddOffset(e.X, r.ReadVarI32());
                    e.Z = AddOffset(e.Z, r.ReadVarI32());
                }
                if ((mask & Yaw) != 0)
                {
                    e.Yaw = r.ReadU8();
                }
                if ((mask & Health) != 0)
                {
                    e.HealthPercent = r.ReadU8();
                }
                if ((mask & Flags) != 0)
                {
                    e.Flags = r.ReadU8();
                }
            }
            states[id] = e;
        }
        r.EnsureEnd();
    }

    private static int AddOffset(int value, int offset)
    {
        var sum = (long)value + offset;
        return sum is < int.MinValue or > int.MaxValue
            ? throw new WireFormatException("Position offset overflows.")
            : (int)sum;
    }

    private static bool Same(in EntityState a, in EntityState b) =>
        a.X == b.X && a.Z == b.Z && a.Yaw == b.Yaw && a.HealthPercent == b.HealthPercent && a.Flags == b.Flags;

    private static void WriteAbsolute(ref WireWriter w, in EntityState e)
    {
        w.WriteVarU32(e.EntityId);
        w.WriteU8(Absolute | AllFields);
        w.WriteI32(e.X);
        w.WriteI32(e.Z);
        w.WriteU8(e.Yaw);
        w.WriteU8(e.HealthPercent);
        w.WriteU8(e.Flags);
    }

    private static void WriteRelative(ref WireWriter w, in EntityState e, in EntityState b)
    {
        long dx = (long)e.X - b.X;
        long dz = (long)e.Z - b.Z;
        if (dx is < int.MinValue or > int.MaxValue || dz is < int.MinValue or > int.MaxValue)
        {
            WriteAbsolute(ref w, e); // a teleport across the whole int range: just send it all
            return;
        }

        byte mask = 0;
        if (dx != 0 || dz != 0)
        {
            mask |= Position;
        }
        if (e.Yaw != b.Yaw)
        {
            mask |= Yaw;
        }
        if (e.HealthPercent != b.HealthPercent)
        {
            mask |= Health;
        }
        if (e.Flags != b.Flags)
        {
            mask |= Flags;
        }

        w.WriteVarU32(e.EntityId);
        w.WriteU8(mask);
        if ((mask & Position) != 0)
        {
            w.WriteVarI32((int)dx);
            w.WriteVarI32((int)dz);
        }
        if ((mask & Yaw) != 0)
        {
            w.WriteU8(e.Yaw);
        }
        if ((mask & Health) != 0)
        {
            w.WriteU8(e.HealthPercent);
        }
        if ((mask & Flags) != 0)
        {
            w.WriteU8(e.Flags);
        }
    }
}
