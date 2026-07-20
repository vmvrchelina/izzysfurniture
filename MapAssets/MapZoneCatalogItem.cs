namespace IzzysFurniture;

internal sealed class MapZoneCatalogItem
{
    public MapZoneCatalogItem(uint territoryId, string name, string bgPath)
    {
        TerritoryId = territoryId;
        Name = name;
        BgPath = bgPath;
    }

    public uint TerritoryId { get; }
    public string Name { get; }
    public string BgPath { get; }
}
