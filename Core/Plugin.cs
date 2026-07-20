using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace IzzysFurniture;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string[] CommandNames =
    [
        "/izzysfurniture",
        "/izf",
        "/izzyfurniture",
        "/furniture",
    ];

    private readonly FurnitureCatalog catalog;
    private readonly MapAssetCatalog mapAssetCatalog;
    private readonly StainCatalog stainCatalog;
    private readonly InteriorFixtureCatalog interiorFixtureCatalog;
    private readonly SceneStore sceneStore;
    private readonly MyCatalogueStore myCatalogueStore;
    private readonly FurnitureSpawner spawner;
    private readonly NpcAppearanceInterop npcAppearanceInterop;
    private readonly NpcSpawner npcSpawner;
    private readonly InteriorFixtureApplier interiorFixtureApplier;
    private readonly PluginUi ui;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        this.catalog = new FurnitureCatalog();
        this.mapAssetCatalog = new MapAssetCatalog();
        this.stainCatalog = new StainCatalog();
        this.interiorFixtureCatalog = new InteriorFixtureCatalog();
        this.sceneStore = new SceneStore(this.catalog);
        this.myCatalogueStore = new MyCatalogueStore();
        this.spawner = new FurnitureSpawner();
        this.npcAppearanceInterop = new NpcAppearanceInterop();
        this.npcAppearanceInterop.RefreshPresets();
        this.npcSpawner = new NpcSpawner(this.npcAppearanceInterop);
        this.interiorFixtureApplier = new InteriorFixtureApplier();
        this.ui = new PluginUi(this.catalog, this.mapAssetCatalog, this.stainCatalog, this.interiorFixtureCatalog, this.sceneStore, this.myCatalogueStore, this.spawner, this.npcAppearanceInterop, this.npcSpawner);
        this.ui.InteriorFixtures.Changed += this.interiorFixtureApplier.MarkDirty;

        foreach (var commandName in CommandNames)
            Service.CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                ShowInHelp = false,
            });

        Service.PluginInterface.UiBuilder.DisableGposeUiHide = true;
        Service.PluginInterface.UiBuilder.Draw += this.ui.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi += this.ui.Open;
        Service.Framework.Update += this.OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Service.Framework.Update -= this.OnFrameworkUpdate;
        Service.PluginInterface.UiBuilder.OpenMainUi -= this.ui.Open;
        Service.PluginInterface.UiBuilder.Draw -= this.ui.Draw;
        foreach (var commandName in CommandNames)
            Service.CommandManager.RemoveHandler(commandName);

        this.ui.InteriorFixtures.Changed -= this.interiorFixtureApplier.MarkDirty;
        this.interiorFixtureApplier.Reset(this.ui.InteriorFixtures);
        this.spawner.Clear();
        this.npcSpawner.Dispose();
        this.npcAppearanceInterop.Dispose();
        this.ui.Dispose();
    }

    private void OnCommand(string command, string arguments)
        => this.ui.Open();

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.ui.UpdateSync();
        this.spawner.Apply(this.ui.SpawnedFurniture);
        this.npcSpawner.Apply(this.ui.SpawnedFurniture);
        this.interiorFixtureApplier.Apply(this.ui.InteriorFixtures);
    }
}
