namespace IzzysFurniture;

internal enum InteriorFixtureFloor : uint
{
    Ground = 0,
    Second = 1,
    Basement = 2,
}

internal enum InteriorFixturePart : uint
{
    Walls = 0,
    Floors = 3,
    CeilingLight = 4,
}

internal sealed record InteriorFixtureCatalogItem(
    uint FixtureId,
    string Name,
    uint IconId,
    InteriorFixturePart Part);
