using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace IzzysFurniture;

internal sealed class InteriorFixtureCatalog
{
    private readonly InteriorFixtureCatalogItem[] items = ReadFixtureItems();

    public IReadOnlyList<InteriorFixtureCatalogItem> Items => items;

    public IEnumerable<InteriorFixtureCatalogItem> ItemsForPart(InteriorFixturePart part)
        => items.Where(item => item.Part == part);

    public InteriorFixtureCatalogItem? Find(uint fixtureId, InteriorFixturePart part)
        => Array.Find(items, item => item.FixtureId == fixtureId && item.Part == part);

    private static InteriorFixtureCatalogItem[] ReadFixtureItems()
    {
        var fixtures = new Dictionary<(uint Id, InteriorFixturePart Part), InteriorFixtureCatalogItem>();
        var interiorIds = Service.DataManager.GetExcelSheet<HousingInterior>()
            .Where(row => row.RowId != 0)
            .Select(row => row.RowId)
            .ToHashSet();

        var itemSheet = Service.DataManager.GetExcelSheet<Item>();
        foreach (var item in itemSheet)
        {
            if (item.RowId == 0 || item.AdditionalData.RowId == 0 || !interiorIds.Contains(item.AdditionalData.RowId))
                continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!TryGetPart(item.ItemUICategory.RowId, out var part))
                continue;

            fixtures.TryAdd((item.AdditionalData.RowId, part), new InteriorFixtureCatalogItem(
                item.AdditionalData.RowId,
                name,
                item.Icon,
                part));
        }

        var results = fixtures.Values.ToList();
        results.Sort((left, right) =>
        {
            var byPart = left.Part.CompareTo(right.Part);
            if (byPart != 0)
                return byPart;

            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return byName != 0 ? byName : left.FixtureId.CompareTo(right.FixtureId);
        });
        return results.ToArray();
    }

    private static bool TryGetPart(uint categoryId, out InteriorFixturePart part)
    {
        switch (categoryId)
        {
            case 73:
                part = InteriorFixturePart.Walls;
                return true;
            case 74:
                part = InteriorFixturePart.Floors;
                return true;
            case 75:
                part = InteriorFixturePart.CeilingLight;
                return true;
            default:
                part = default;
                return false;
        }
    }
}
