using System;
using System.Collections.Generic;
using System.Linq;

namespace IzzysFurniture;

internal sealed class InteriorFixtureState
{
    private readonly Dictionary<InteriorFixtureSlot, uint> _fixtures = new();

    public event Action? Changed;

    public IReadOnlyCollection<InteriorFixtureSelection> Selections
        => _fixtures
            .Where(pair => pair.Value != 0)
            .Select(pair => new InteriorFixtureSelection(pair.Key.Floor, pair.Key.Part, pair.Value))
            .ToArray();

    public uint Get(InteriorFixtureFloor floor, InteriorFixturePart part)
        => _fixtures.TryGetValue(new InteriorFixtureSlot(floor, part), out var fixtureId) ? fixtureId : 0;

    public void Set(InteriorFixtureFloor floor, InteriorFixturePart part, uint fixtureId)
    {
        var slot = new InteriorFixtureSlot(floor, part);
        var current = Get(floor, part);
        if (current == fixtureId)
            return;

        if (fixtureId == 0)
            _fixtures.Remove(slot);
        else
            _fixtures[slot] = fixtureId;

        Changed?.Invoke();
    }

    public void Replace(IEnumerable<InteriorFixtureSelection> selections)
    {
        _fixtures.Clear();
        foreach (var selection in selections)
        {
            if (selection.FixtureId == 0)
                continue;

            _fixtures[new InteriorFixtureSlot(selection.Floor, selection.Part)] = selection.FixtureId;
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_fixtures.Count == 0)
            return;

        _fixtures.Clear();
        Changed?.Invoke();
    }
}

internal readonly record struct InteriorFixtureSelection(
    InteriorFixtureFloor Floor,
    InteriorFixturePart Part,
    uint FixtureId);

internal readonly record struct InteriorFixtureSlot(
    InteriorFixtureFloor Floor,
    InteriorFixturePart Part);
