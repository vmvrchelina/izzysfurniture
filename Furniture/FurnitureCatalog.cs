using System;
using System.Collections.Generic;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace IzzysFurniture;

internal sealed class FurnitureCatalog
{
    private readonly FurnitureCatalogItem[] items = ReadHousingItems();

    public IReadOnlyList<FurnitureCatalogItem> Items => items;

    public FurnitureCatalogItem? Find(uint itemId, FurnitureSourceKind sourceKind)
        => Array.Find(items, item => item.ItemId == itemId && item.SourceKind == sourceKind);

    private static FurnitureCatalogItem[] ReadHousingItems()
    {
        var results = new List<FurnitureCatalogItem>(2048);

        var furnitureSheet = Service.DataManager.GetExcelSheet<HousingFurniture>();
        foreach (var row in furnitureSheet)
        {
            if (row.RowId == 0 || row.ModelKey == 0 || row.Item.RowId == 0)
                continue;

            var item = row.Item.Value;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new FurnitureCatalogItem(
                item.RowId,
                name,
                item.Icon,
                row.ModelKey,
                row.HousingItemCategory,
                item.ItemUICategory.Value.Name.ExtractText(),
                item.DyeCount,
                FurnitureSourceKind.Indoor));
        }

        var yardSheet = Service.DataManager.GetExcelSheet<HousingYardObject>();
        foreach (var row in yardSheet)
        {
            if (row.RowId == 0 || row.ModelKey == 0 || row.Item.RowId == 0)
                continue;

            var item = row.Item.Value;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new FurnitureCatalogItem(
                item.RowId,
                name,
                item.Icon,
                row.ModelKey,
                row.HousingItemCategory,
                item.ItemUICategory.Value.Name.ExtractText(),
                item.DyeCount,
                FurnitureSourceKind.Outdoor));
        }

        results.Sort((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return byName != 0 ? byName : left.ItemId.CompareTo(right.ItemId);
        });

        return results.ToArray();
    }
}
