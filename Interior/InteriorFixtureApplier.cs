using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace IzzysFurniture;

internal sealed class InteriorFixtureApplier
{
    private readonly Dictionary<InteriorFixtureSlot, uint> applied = [];
    private bool dirty = true;

    public void MarkDirty()
        => this.dirty = true;

    public void Apply(InteriorFixtureState state)
    {
        // several ui changes can collapse into one native fixture update
        if (!this.dirty)
            return;

        var wanted = new Dictionary<InteriorFixtureSlot, uint>();
        foreach (var selection in state.Selections)
            wanted[new InteriorFixtureSlot(selection.Floor, selection.Part)] = selection.FixtureId;

        var resetSlots = new List<InteriorFixtureSlot>();
        foreach (var pair in this.applied)
        {
            if (wanted.ContainsKey(pair.Key))
                continue;

            var result = ApplySlot(pair.Key, 0);
            if (result != 0)
            {
                Service.Log.Warning("could not reset an interior fixture");
                continue;
            }

            resetSlots.Add(pair.Key);
        }

        foreach (var pair in wanted)
        {
            if (this.applied.TryGetValue(pair.Key, out var current) && current == pair.Value)
                continue;

            var result = ApplySlot(pair.Key, pair.Value);
            if (result != 0)
            {
                Service.Log.Warning("could not apply an interior fixture");
                continue;
            }

            this.applied[pair.Key] = pair.Value;
        }

        foreach (var slot in resetSlots)
            this.applied.Remove(slot);

        this.dirty = false;
    }

    public void Reset(InteriorFixtureState state)
    {
        foreach (var selection in state.Selections)
            ApplySlot(new InteriorFixtureSlot(selection.Floor, selection.Part), 0);

        this.applied.Clear();
        this.dirty = true;
    }

    private static int ApplySlot(InteriorFixtureSlot slot, uint fixtureId)
        // -1 applies the fixture to every room
        => LayoutWorld.SetInteriorFixture((uint)slot.Floor, (uint)slot.Part, -1, (int)fixtureId);
}
