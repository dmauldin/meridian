using Meridian.Core.Protocols;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;
using Meridian.Wire;

namespace Meridian.Server.Zone;

/// <summary>Uploading, casting and expiring Protocols.</summary>
public sealed partial class ZoneSimulation
{
    public void OnUploadProtocol(ClientRef context, in UploadProtocol message)
    {
        var p = _byClient[context];
        if (p.Outfit.InventoryAt(message.Position) is not { } owned || !_content.Items.TryGet(owned.TemplateId, out var item))
        {
            SendNotice(p, NoticeRefused, "There is nothing there.");
            return;
        }
        var failures = new List<RequirementFailure>();
        var result = p.Lattice.Upload(item, failures);
        if (result == UploadResult.Learned)
        {
            p.Outfit.Discard(message.Position); // uploading is permanent and consumes the item
        }
        var name = item.ProtocolKey is { } key ? _content.Protocols[key].Name : item.Name;
        SendNotice(p, result == UploadResult.Learned ? NoticeInfo : NoticeRefused, result switch
        {
            UploadResult.Learned => $"Uploaded {name}.",
            UploadResult.AlreadyKnown => $"You already know {name}.",
            UploadResult.RequirementsNotMet => $"You cannot upload {name}: {Explain(failures)}.",
            _ => $"{item.Name} is not a Protocol.",
        });
    }

    public void OnCastProtocol(ClientRef context, in CastProtocol message)
    {
        var caster = _byClient[context];
        if (!_content.Protocols.TryGet((int)message.ProtocolId, out var protocol))
        {
            SendNotice(caster, NoticeRefused, "Unknown Protocol.");
            return;
        }
        var target = message.TargetId == 0 ? caster : Find(message.TargetId) as Player;
        if (target is null)
        {
            SendNotice(caster, NoticeRefused, $"{protocol.Name} can only be cast on players.");
            return;
        }
        if (caster.Dead)
        {
            SendNotice(caster, NoticeRefused, "You cannot cast while dead.");
            return;
        }

        var failures = new List<RequirementFailure>();
        var result = caster.Lattice.Cast(protocol, target.Lattice, !target.Dead, caster.Position.DistanceSquared(target.Position), Tick, failures);
        var on = ReferenceEquals(target, caster) ? "yourself" : target.Name;
        switch (result)
        {
            case CastResult.Applied or CastResult.Replaced or CastResult.Renewed:
                SendNotice(caster, NoticeInfo, $"{protocol.Name} {(result == CastResult.Renewed ? "renewed on" : "lands on")} {on}.");
                if (!ReferenceEquals(target, caster))
                {
                    SendNotice(target, NoticeInfo, $"{caster.Name} cast {protocol.Name} on you.");
                }
                SettleItems(target); // a buff can switch a twinked item on right away
                break;
            default:
                SendNotice(caster, NoticeRefused, result switch
                {
                    CastResult.NotKnown => $"You do not know {protocol.Name}.",
                    CastResult.SelfOnly => $"{protocol.Name} can only be cast on yourself.",
                    CastResult.TargetDead => $"{target.Name} is dead.",
                    CastResult.OutOfRange => $"{target.Name} is out of range ({protocol.RangeMm / 1000} m).",
                    CastResult.RequirementsNotMet => $"You cannot cast {protocol.Name}: {Explain(failures)}.",
                    CastResult.NotEnoughEnergy => $"Not enough energy ({caster.Lattice.Energy}/{protocol.EnergyCost}).",
                    CastResult.StrongerRunning => $"A stronger {LineName(protocol.LineId)} is already running on {on}.",
                    CastResult.LatticeFull => $"{(ReferenceEquals(target, caster) ? "Your" : target.Name + "'s")} lattice has no room for {protocol.Name}.",
                    _ => $"{protocol.Name} failed.",
                });
                break;
        }
    }

    public void OnCancelProtocol(ClientRef context, in CancelProtocol message)
    {
        var p = _byClient[context];
        if (p.Lattice.Cancel((int)message.ProtocolId))
        {
            SendNotice(p, NoticeInfo, $"{_content.Protocols[(int)message.ProtocolId].Name} ended.");
            SettleItems(p);
        }
    }

    /// <summary>Regenerates energy and ends expired Protocols, telling the player which.</summary>
    private void TickLattice(Player p)
    {
        foreach (var expired in p.Lattice.Tick(Tick))
        {
            SendNotice(p, NoticeInfo, $"{expired.Def.Name} has worn off.");
        }
    }

    private void SendProtocolsIfChanged(Player p)
    {
        if (p.LastSentLatticeRevision == p.Lattice.Revision)
        {
            return;
        }
        p.LastSentLatticeRevision = p.Lattice.Revision;
        Send(p, new ProtocolState
        {
            Known = [.. p.Lattice.Known.Select(id => (uint)id)],
            Running = [.. p.Lattice.RunningWithRemaining(Tick).Select(r => new RunningProtocol { ProtocolId = (uint)r.ProtocolId, RemainingMs = (uint)r.RemainingMs })],
            LatticeUsed = p.Lattice.Running.UsedCapacity,
            LatticeCapacity = p.Character.Get(Stat.LatticeCapacity),
        });
    }

    private string LineName(int lineId) =>
        _content.Lines.TryGet(lineId, out var line) ? line.Name : "Protocol";
}
