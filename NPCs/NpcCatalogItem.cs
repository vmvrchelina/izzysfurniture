using System;

namespace IzzysFurniture;

internal enum NpcSourceKind
{
    DirectModelChara,
    EventNpc,
    BattleNpc,
}

internal enum NpcDisplayKind
{
    Human,
    NonHuman,
}

internal sealed record NpcCatalogItem(
    string Name,
    NpcSourceKind SourceKind,
    uint RowId,
    uint ModelCharaId,
    NpcDisplayKind DisplayKind,
    bool HasResolvedName = false)
{
    public string StableKey => $"{this.SourceKind}:{this.RowId}";

    public bool Matches(string search)
        => string.IsNullOrWhiteSpace(search) ||
            this.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            this.SourceKind.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
            this.RowId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
            this.ModelCharaId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
}
