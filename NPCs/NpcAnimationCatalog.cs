using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace IzzysFurniture;

internal sealed class NpcAnimationCatalog
{
    private readonly Lazy<IReadOnlyList<NpcAnimationCatalogItem>> items = new(BuildItems);

    public IReadOnlyList<NpcAnimationCatalogItem> Items => this.items.Value;

    public NpcAnimationCatalogItem? Find(ushort timelineId)
        => this.Items.FirstOrDefault(item => item.TimelineId == timelineId);

    private static IReadOnlyList<NpcAnimationCatalogItem> BuildItems()
    {
        var rows = new List<NpcAnimationCatalogItem>();
        var timelineLoops = new Dictionary<uint, bool>();

        AddCanonicalLocomotion(rows);

        foreach (var timeline in Service.DataManager.GetExcelSheet<ActionTimeline>())
        {
            if (timeline.RowId == 0)
                continue;

            var key = timeline.Key.ToString();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var isLoop = timeline.IsLoop;
            timelineLoops[timeline.RowId] = isLoop;

            if (LooksUsefulTimelineKey(key))
            {
                rows.Add(new NpcAnimationCatalogItem(
                    Name: key,
                    TimelineId: (ushort)timeline.RowId,
                    Kind: NpcAnimationKind.Timeline,
                    Category: CategorizeTimeline(key, isLoop),
                    IsLoop: isLoop));
            }
        }

        foreach (var emote in Service.DataManager.GetExcelSheet<Emote>())
        {
            for (var index = 0; index < emote.ActionTimeline.Count; index++)
            {
                var rowId = emote.ActionTimeline[index].RowId;
                if (rowId == 0 || !timelineLoops.TryGetValue(rowId, out var isLoop))
                    continue;

                rows.Add(new NpcAnimationCatalogItem(
                    Name: $"{emote.Name} {EmoteTimelineLabel(index)}",
                    TimelineId: (ushort)rowId,
                    Kind: NpcAnimationKind.Emote,
                    Category: EmoteCategory(emote.EmoteCategory.RowId),
                    IsLoop: isLoop));
            }
        }

        foreach (var action in Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            var rowId = action.AnimationEnd.RowId;
            if (rowId == 0 || !timelineLoops.TryGetValue(rowId, out var isLoop))
                continue;

            var name = action.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                name = $"Action {action.RowId}";

            rows.Add(new NpcAnimationCatalogItem(
                Name: name,
                TimelineId: (ushort)rowId,
                Kind: NpcAnimationKind.Action,
                Category: "Combat",
                IsLoop: isLoop));
        }

        return rows
            .GroupBy(item => item.TimelineId)
            .Select(group => group.OrderByDescending(item => item.Kind != NpcAnimationKind.Timeline).First())
            .OrderBy(item => CategoryOrder(item.Category))
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static int CategoryOrder(string category)
        => category switch
        {
            "Emotes" => 0,
            "Special Emotes" => 1,
            "Expressions" => 2,
            "Idle" => 3,
            "Locomotion" => 4,
            "Loops" => 5,
            "Timelines" => 6,
            "Combat" => 20,
            _ => 10,
        };

    private static string EmoteCategory(uint categoryId)
        => categoryId switch
        {
            2 => "Special Emotes",
            3 => "Expressions",
            _ => "Emotes",
        };

    private static void AddCanonicalLocomotion(List<NpcAnimationCatalogItem> rows)
    {
        rows.Add(new NpcAnimationCatalogItem("Walking Forward", 13, NpcAnimationKind.Timeline, "Locomotion", true));
        rows.Add(new NpcAnimationCatalogItem("Walking Left", 14, NpcAnimationKind.Timeline, "Locomotion", true));
        rows.Add(new NpcAnimationCatalogItem("Walking Right", 15, NpcAnimationKind.Timeline, "Locomotion", true));
        rows.Add(new NpcAnimationCatalogItem("Walking Backward", 16, NpcAnimationKind.Timeline, "Locomotion", true));
        rows.Add(new NpcAnimationCatalogItem("Running Forward", 22, NpcAnimationKind.Timeline, "Locomotion", true));
        rows.Add(new NpcAnimationCatalogItem("Running Left", 23, NpcAnimationKind.Timeline, "Locomotion", true));
        rows.Add(new NpcAnimationCatalogItem("Running Right", 24, NpcAnimationKind.Timeline, "Locomotion", true));
    }

    private static bool LooksUsefulTimelineKey(string key)
        => key.Contains("idle", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("wait", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("walk", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("run", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("move", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("loop", StringComparison.OrdinalIgnoreCase);

    private static string CategorizeTimeline(string key, bool isLoop)
    {
        if (key.Contains("walk", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("run", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("move", StringComparison.OrdinalIgnoreCase))
            return "Locomotion";

        if (key.Contains("idle", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("wait", StringComparison.OrdinalIgnoreCase))
            return "Idle";

        return isLoop ? "Loops" : "Timelines";
    }

    private static string EmoteTimelineLabel(int index)
        => index switch
        {
            0 => "Loop",
            1 => "Intro",
            2 => "Ground",
            3 => "Chair",
            4 => "Blend",
            _ => $"Timeline {index + 1}",
        };
}
