using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;

namespace IzzysFurniture;

internal sealed class NpcCatalog
{
    private readonly Lazy<IReadOnlyList<NpcCatalogItem>> items = new(BuildItems);

    public IReadOnlyList<NpcCatalogItem> Items => this.items.Value;

    public NpcCatalogItem? Find(NpcSourceKind sourceKind, uint rowId)
        => sourceKind == NpcSourceKind.DirectModelChara
            ? this.FindModelChara(rowId)
            : this.Items.FirstOrDefault(item => item.SourceKind == sourceKind && item.RowId == rowId);

    public NpcCatalogItem? FindModelChara(uint modelCharaId)
        => this.Items
            .Where(item => item.ModelCharaId == modelCharaId)
            .OrderBy(item => item.SourceKind == NpcSourceKind.DirectModelChara ? 1 : 0)
            .ThenByDescending(item => item.HasResolvedName)
            .ThenBy(item => item.SourceKind)
            .ThenBy(item => item.RowId)
            .FirstOrDefault();

    private static IReadOnlyList<NpcCatalogItem> BuildItems()
    {
        var usagesByModelChara = BuildModelCharaUsageMap();
        var rows = new List<NpcCatalogItem>();

        foreach (var model in Service.DataManager.GetExcelSheet<ModelChara>())
        {
            if (model.RowId == 0)
                continue;

            usagesByModelChara.TryGetValue(model.RowId, out var usages);
            usages ??= [];

            // named npc rows are added separately and should not appear as direct models too
            if (usages.Any(usage => usage.Source is "ENpc" or "BNpc"))
                continue;

            var bestUsage = usages.FirstOrDefault(usage => usage.HasResolvedName) ?? usages.FirstOrDefault();
            var name = bestUsage is null
                ? $"ModelChara {model.RowId}"
                : bestUsage.Name;

            rows.Add(new NpcCatalogItem(
                Name: name,
                SourceKind: NpcSourceKind.DirectModelChara,
                RowId: model.RowId,
                ModelCharaId: model.RowId,
                DisplayKind: ModelDisplayKind(model),
                HasResolvedName: bestUsage?.HasResolvedName ?? false));
        }

        rows.AddRange(BuildSourceNpcRows());

        var unique = new Dictionary<string, NpcCatalogItem>(StringComparer.Ordinal);
        foreach (var row in rows)
            unique.TryAdd(row.StableKey, row);

        var result = unique.Values.ToArray();
        Array.Sort(result, (left, right) =>
        {
            var byKind = left.DisplayKind.CompareTo(right.DisplayKind);
            if (byKind != 0)
                return byKind;

            var byModel = left.ModelCharaId.CompareTo(right.ModelCharaId);
            return byModel != 0
                ? byModel
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });
        return result;
    }

    private static Dictionary<uint, List<ModelCharaUsage>> BuildModelCharaUsageMap()
    {
        var usages = new Dictionary<uint, List<ModelCharaUsage>>();
        var eventNpcNames = BuildNameMap<ENpcResident>(row => row.Singular.ExtractText());

        foreach (var npc in Service.DataManager.GetExcelSheet<ENpcBase>())
        {
            if (npc.RowId == 0 || npc.ModelChara.RowId == 0)
                continue;

            var name = ResolveName(eventNpcNames, npc.RowId, "ENpc");
            AddUsage(usages, npc.ModelChara.RowId, new ModelCharaUsage(
                "ENpc",
                npc.RowId,
                name.Name,
                name.Resolved));
        }

        foreach (var npc in Service.DataManager.GetExcelSheet<BNpcBase>())
        {
            if (npc.RowId == 0 || npc.ModelChara.RowId == 0)
                continue;

            AddUsage(usages, npc.ModelChara.RowId, new ModelCharaUsage(
                "BNpc",
                npc.RowId,
                $"BNpc {npc.RowId}",
                false));
        }

        foreach (var row in Service.DataManager.GetExcelSheet<Mount>())
            AddUsageRow(usages, "Mount", row.RowId, row.ModelChara.RowId, row.Singular.ExtractText());
        foreach (var row in Service.DataManager.GetExcelSheet<Companion>())
            AddUsageRow(usages, "Companion", row.RowId, row.Model.RowId, row.Singular.ExtractText());
        foreach (var row in Service.DataManager.GetExcelSheet<Ornament>())
            AddUsageRow(usages, "Ornament", row.RowId, row.Model, row.Singular.ExtractText());

        return usages;
    }

    private static IReadOnlyList<NpcCatalogItem> BuildSourceNpcRows()
    {
        var rows = new List<NpcCatalogItem>();
        var eventNpcNames = BuildNameMap<ENpcResident>(row => row.Singular.ExtractText());

        foreach (var npc in Service.DataManager.GetExcelSheet<ENpcBase>())
        {
            if (npc.RowId == 0 || npc.ModelChara.RowId == 0)
                continue;

            var name = ResolveName(eventNpcNames, npc.RowId, "ENpc");
            rows.Add(new NpcCatalogItem(
                name.Name,
                NpcSourceKind.EventNpc,
                npc.RowId,
                npc.ModelChara.RowId,
                ModelDisplayKind(npc.ModelChara.Value),
                name.Resolved));
        }

        foreach (var npc in Service.DataManager.GetExcelSheet<BNpcBase>())
        {
            if (npc.RowId == 0 || npc.ModelChara.RowId == 0)
                continue;

            rows.Add(new NpcCatalogItem(
                $"BNpc {npc.RowId}",
                NpcSourceKind.BattleNpc,
                npc.RowId,
                npc.ModelChara.RowId,
                ModelDisplayKind(npc.ModelChara.Value)));
        }

        return rows;
    }

    private static void AddUsageRow(Dictionary<uint, List<ModelCharaUsage>> usages, string source, uint rowId, uint modelCharaId, string name)
    {
        if (rowId == 0 || modelCharaId == 0)
            return;

        var hasName = !string.IsNullOrWhiteSpace(name);
        AddUsage(usages, modelCharaId, new ModelCharaUsage(
            source, rowId, hasName ? name : $"{source} {rowId}", hasName));
    }

    private static void AddUsage(Dictionary<uint, List<ModelCharaUsage>> usages, uint modelCharaId, ModelCharaUsage usage)
    {
        if (!usages.TryGetValue(modelCharaId, out var list))
        {
            list = [];
            usages[modelCharaId] = list;
        }

        if (!list.Any(existing => existing.Source == usage.Source && existing.RowId == usage.RowId))
            list.Add(usage);
    }

    private static NpcDisplayKind ModelDisplayKind(ModelChara model)
        => model.Type == (byte)CharacterBase.ModelType.Human
            ? NpcDisplayKind.Human
            : NpcDisplayKind.NonHuman;

    private static Dictionary<uint, string> BuildNameMap<T>(Func<T, string> extractName)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        var names = new Dictionary<uint, string>();
        foreach (var row in Service.DataManager.GetExcelSheet<T>())
        {
            var text = extractName(row);
            if (!string.IsNullOrWhiteSpace(text))
                names[row.RowId] = text;
        }

        return names;
    }

    private static (string Name, bool Resolved) ResolveName(IReadOnlyDictionary<uint, string> names, uint rowId, string rowType)
    {
        if (names.TryGetValue(rowId, out var name) && !string.IsNullOrWhiteSpace(name))
            return (name, true);

        return ($"{rowType} {rowId}", false);
    }

    private sealed record ModelCharaUsage(
        string Source,
        uint RowId,
        string Name,
        bool HasResolvedName);
}
