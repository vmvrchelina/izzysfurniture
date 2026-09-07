using System;

namespace IzzysFurniture;

internal enum NpcAnimationKind
{
    Timeline,
    Emote,
    Action,
}

internal sealed record NpcAnimationCatalogItem(
    string Name,
    ushort TimelineId,
    NpcAnimationKind Kind,
    string Category,
    bool IsLoop)
{
    public bool Supports(NpcDisplayKind displayKind)
        => displayKind == NpcDisplayKind.Human || this.Kind == NpcAnimationKind.Timeline;

    public bool Matches(string search)
        => string.IsNullOrWhiteSpace(search) ||
            this.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            this.TimelineId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
            this.Category.Contains(search, StringComparison.OrdinalIgnoreCase);
}
