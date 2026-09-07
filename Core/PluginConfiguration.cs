using Dalamud.Configuration;

namespace IzzysFurniture;

internal sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int AutosaveIntervalSeconds { get; set; } = 60;
}
