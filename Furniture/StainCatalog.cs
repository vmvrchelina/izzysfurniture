using System;
using System.Collections.Generic;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Lumina.Excel.Sheets;

namespace IzzysFurniture;

internal sealed class StainCatalog
{
    public IReadOnlyList<StainCatalogItem> Items { get; } = LoadHousingStains();

    public StainCatalogItem? Find(byte stainId)
    {
        foreach (var stain in Items)
        {
            if (stain.StainId == stainId)
                return stain;
        }

        return null;
    }

    private static StainCatalogItem[] LoadHousingStains()
    {
        var results = new List<StainCatalogItem>(SharedGroupLayoutInstance.ObjectStainCount);
        var stainSheet = Service.DataManager.GetExcelSheet<Stain>();
        foreach (var row in stainSheet)
        {
            if (row.RowId == 0 || row.RowId >= SharedGroupLayoutInstance.ObjectStainCount || !row.IsHousingApplicable)
                continue;

            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new StainCatalogItem((byte)row.RowId, name, row.Color));
        }

        results.Sort((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return byName != 0 ? byName : left.StainId.CompareTo(right.StainId);
        });
        return results.ToArray();
    }
}
