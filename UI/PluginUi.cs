using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace IzzysFurniture;

internal sealed class PluginUi : IDisposable
{
    private enum DuplicatePlacement
    {
        Left,
        Right,
        OnTop,
    }

    private readonly FurnitureCatalog catalog;
    private readonly MapAssetCatalog mapAssetCatalog;
    private readonly FxCatalog fxCatalog = new();
    private readonly NpcCatalog npcCatalog = new();
    private readonly NpcAnimationCatalog npcAnimationCatalog = new();
    private readonly StainCatalog stainCatalog;
    private readonly InteriorFixtureCatalog interiorFixtureCatalog;
    private readonly SceneStore sceneStore;
    private readonly MyCatalogueStore myCatalogueStore;
    private readonly FurnitureSpawner spawner;
    private readonly NpcAppearanceInterop npcAppearanceInterop;
    private readonly NpcSpawner npcSpawner;
    private readonly SyncRelayClient syncClient = new();
    private readonly List<SpawnedFurniture> spawned = [];
    private readonly List<FurnitureFolder> folders = [];
    private readonly HashSet<Guid> draggedFurnitureIds = [];
    private readonly InteriorFixtureState interiorFixtures = new();
    private readonly FileDialogManager fileDialogManager = new();

    private bool isOpen;
    private bool openAddPicker;
    private bool openClearConfirmation;
    private string sceneStatus = string.Empty;
    private string pendingOverwritePath = string.Empty;
    private bool openOverwriteConfirmation;
    private string syncRelayUrl = string.Empty;
    private string syncRoom = string.Empty;
    private string syncSecret = string.Empty;
    private bool syncAsHost = true;
    private long lastBroadcastMilliseconds;
    private string pickerSearch = string.Empty;
    private string furnitureCategory = "All";
    private string mapAssetSearch = string.Empty;
    private string mapAssetCategory = "All";
    private string fxSearch = string.Empty;
    private string fxCategory = "All";
    private string fxRawPath = string.Empty;
    private string npcSearch = string.Empty;
    private string npcModelCharaInput = string.Empty;
    private string npcDisplayFilter = "All";
    private string npcAnimationSearch = string.Empty;
    private string npcAnimationCategory = "Emotes";
    private string myCatalogueSearch = string.Empty;
    private string newCatalogueName = string.Empty;
    private string saveCatalogueEntryName = string.Empty;
    private Guid? saveCatalogueEntrySourceId;
    private Guid selectedMyCatalogueId;
    private string mapZoneSearch = string.Empty;
    private uint selectedMapTerritoryId;
    private Guid? selectedId;
    private readonly HashSet<Guid> selectedIds = [];
    private ImGuizmoOperation gizmoOperation = ImGuizmoOperation.Universal;
    private ImGuizmoMode gizmoMode = ImGuizmoMode.World;

    public PluginUi(FurnitureCatalog catalog, MapAssetCatalog mapAssetCatalog, StainCatalog stainCatalog, InteriorFixtureCatalog interiorFixtureCatalog, SceneStore sceneStore, MyCatalogueStore myCatalogueStore, FurnitureSpawner spawner, NpcAppearanceInterop npcAppearanceInterop, NpcSpawner npcSpawner)
    {
        this.catalog = catalog;
        this.mapAssetCatalog = mapAssetCatalog;
        this.stainCatalog = stainCatalog;
        this.interiorFixtureCatalog = interiorFixtureCatalog;
        this.sceneStore = sceneStore;
        this.myCatalogueStore = myCatalogueStore;
        this.selectedMyCatalogueId = myCatalogueStore.EnsureDefault().Id;
        this.spawner = spawner;
        this.npcAppearanceInterop = npcAppearanceInterop;
        this.npcSpawner = npcSpawner;
    }

    public SpawnedFurniture? SelectedFurniture
        => this.spawned.FirstOrDefault(item => item.Id == this.selectedId);

    private IReadOnlyList<SpawnedFurniture> SelectedFurnitureItems
        => this.spawned.Where(item => this.selectedIds.Contains(item.Id)).ToArray();

    public IReadOnlyList<SpawnedFurniture> SpawnedFurniture => this.spawned;

    public InteriorFixtureState InteriorFixtures => this.interiorFixtures;

    public void Open()
        => isOpen = true;

    public void Dispose()
    {
        syncClient.Dispose();
    }

    public void Draw()
    {
        if (!this.isOpen)
            return;

        // TODO: gizmo for multi-select, needs a synthetic group pivot
        var selectedForGizmo = this.selectedIds.Count == 1 ? this.SelectedFurniture : null;

        ImGui.SetNextWindowSize(new Vector2(860, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Izzy's Furniture", ref this.isOpen))
        {
            ImGui.End();
            return;
        }

        this.DrawToolbar();
        ImGui.Separator();

        if (ImGui.BeginTabBar("main-tabs"))
        {
            if (ImGui.BeginTabItem("Furniture"))
            {
                this.DrawFurnitureMainTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Interior"))
            {
                this.DrawInteriorTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Sync"))
            {
                this.DrawSyncTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (this.openAddPicker)
        {
            this.openAddPicker = false;
            ImGui.OpenPopup("Add");
        }

        this.DrawPickerPopup();
        this.DrawOverwriteConfirmation();
        this.DrawClearConfirmation();
        this.fileDialogManager.Draw();

        ImGui.End();

        this.DrawGizmoOverlay(selectedForGizmo);
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("Save...", new Vector2(72, 24)))
            this.OpenSaveDialog();

        ImGui.SameLine();
        if (ImGui.Button("Load...", new Vector2(72, 24)))
            this.OpenLoadDialog();

        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(64, 24)))
            this.openClearConfirmation = true;

        if (!string.IsNullOrWhiteSpace(this.sceneStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(this.sceneStatus);
        }
    }

    private void DrawClearConfirmation()
    {
        const string popupName = "Clear Scene?";
        if (this.openClearConfirmation)
        {
            this.openClearConfirmation = false;
            ImGui.OpenPopup(popupName);
        }

        if (!ImGui.BeginPopupModal(popupName, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextUnformatted("Remove every object, folder, and interior fixture?");
        ImGui.Spacing();

        if (ImGui.Button("Clear scene", new Vector2(112, 28)))
        {
            this.spawned.Clear();
            this.folders.Clear();
            this.selectedId = null;
            this.selectedIds.Clear();
            this.interiorFixtures.Clear();
            this.spawner.Clear();
            this.npcSpawner.Clear();
            this.sceneStatus = "";
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(88, 28)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void OpenSaveDialog()
    {
        this.fileDialogManager.SaveFileDialog(
            "Save Izzy's Furniture Scene",
            ".json",
            "izzys-furniture-scene",
            "json",
            (success, path) =>
            {
                if (success && !string.IsNullOrWhiteSpace(path))
                    this.RequestSceneSave(path);
            },
            this.sceneStore.DefaultDirectory,
            true);
    }

    private void RequestSceneSave(string path)
    {
        path = SceneStore.NormalizeSavePath(path);
        if (!File.Exists(path))
        {
            this.SaveScene(path);
            return;
        }

        this.pendingOverwritePath = path;
        this.openOverwriteConfirmation = true;
    }

    private void DrawOverwriteConfirmation()
    {
        const string popupName = "Overwrite Scene?";
        if (this.openOverwriteConfirmation)
        {
            this.openOverwriteConfirmation = false;
            ImGui.OpenPopup(popupName);
        }

        if (!ImGui.BeginPopupModal(popupName, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped($"{Path.GetFileName(this.pendingOverwritePath)} already exists.");
        ImGui.TextWrapped("Replace it?");
        ImGui.Spacing();

        if (ImGui.Button("Replace", new Vector2(104, 28)))
        {
            var path = this.pendingOverwritePath;
            this.pendingOverwritePath = string.Empty;
            ImGui.CloseCurrentPopup();
            this.SaveScene(path);
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(104, 28)))
        {
            this.pendingOverwritePath = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void OpenLoadDialog()
    {
        this.fileDialogManager.OpenFileDialog(
            "Load Izzy's Furniture Scene",
            ".json",
            (success, paths) =>
            {
                var path = paths.FirstOrDefault();
                if (success && !string.IsNullOrWhiteSpace(path))
                    this.LoadScene(path);
            },
            1,
            this.sceneStore.DefaultDirectory,
            true);
    }

    private void SaveScene(string path)
    {
        try
        {
            this.sceneStore.Save(path, this.spawned, this.folders, this.interiorFixtures);
            this.sceneStatus = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Service.Log.Error(error, "failed to save scene");
            this.sceneStatus = "failed to save scene";
        }
    }

    private void LoadScene(string path)
    {
        try
        {
            var result = this.sceneStore.Load(path);
            this.ApplyLoadedScene(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Service.Log.Error(error, "failed to load scene");
            this.sceneStatus = "failed to load scene";
        }
    }

    private void ApplyLoadedScene(LoadSceneResult result)
    {
        if (result.Props.Count == 0 && result.Folders.Count == 0 && result.InteriorFixtures.Count == 0 && result.Skipped == 0)
        {
            this.sceneStatus = result.Error;
            return;
        }

        this.spawned.Clear();
        this.spawned.AddRange(result.Props);
        this.folders.Clear();
        this.folders.AddRange(result.Folders);
        this.selectedId = this.spawned.FirstOrDefault()?.Id;
        this.selectedIds.Clear();
        if (this.selectedId is { } selected)
            this.selectedIds.Add(selected);
        this.spawner.Clear();
        this.npcSpawner.Clear();
        this.interiorFixtures.Replace(result.InteriorFixtures);
        this.sceneStatus = result.Error.Length != 0
            ? result.Error
            : result.Skipped == 0 ? "" : $"skipped {result.Skipped} saved objects";
    }

    private void DrawFurnitureMainTab()
    {
        var region = ImGui.GetContentRegionAvail();
        var listWidth = MathF.Min(300, region.X * 0.40f);
        var workspaceHeight = MathF.Max(1, region.Y - 34);

        if (ImGui.BeginChild("spawned-list", new Vector2(listWidth, workspaceHeight), true))
            this.DrawSpawnedList();
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("details", new Vector2(0, workspaceHeight), true))
            this.DrawDetails();
        ImGui.EndChild();

        if (ImGui.Button("Add", new Vector2(72, 24)))
            this.openAddPicker = true;

        ImGui.SameLine();
        using (Service.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (ImGui.Button($"{FontAwesomeIcon.FolderPlus.ToIconString()}##new-folder", new Vector2(32, 24)))
                this.CreateFolder();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("New folder");
    }

    private void DrawInteriorTab()
    {
        if (ImGui.Button("Reset", new Vector2(72, 24)))
            this.interiorFixtures.Clear();

        ImGui.Separator();

        if (ImGui.BeginChild("interior-fixtures", new Vector2(0, 0), true))
        {
            this.DrawInteriorFloor(InteriorFixtureFloor.Ground);
            this.DrawInteriorFloor(InteriorFixtureFloor.Second);
            this.DrawInteriorFloor(InteriorFixtureFloor.Basement);
        }

        ImGui.EndChild();
    }

    private void DrawSyncTab()
    {
        ImGui.TextWrapped(syncClient.Status);
        ImGui.Separator();

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("Relay URL", ref syncRelayUrl, 256);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Room", ref syncRoom, 96);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Secret", ref syncSecret, 128, ImGuiInputTextFlags.Password);

        if (syncClient.IsConnected)
            ImGui.BeginDisabled();
        ImGui.Checkbox("Host mode", ref syncAsHost);
        if (syncClient.IsConnected)
            ImGui.EndDisabled();

        if (!syncClient.IsConnected)
        {
            if (ImGui.Button("Connect", new Vector2(104, 24)))
            {
                lastBroadcastMilliseconds = 0;
                RunSyncTask(() => syncClient.ConnectAsync(syncRelayUrl, syncRoom, syncSecret, syncAsHost));
            }
        }
        else
        {
            if (ImGui.Button("Disconnect", new Vector2(104, 24)))
                syncClient.Disconnect();
        }
    }

    private void DrawInteriorFloor(InteriorFixtureFloor floor)
    {
        ImGui.TextUnformatted(FloorName(floor));
        this.DrawInteriorFixtureCombo(floor, InteriorFixturePart.Walls);
        this.DrawInteriorFixtureCombo(floor, InteriorFixturePart.Floors);
        this.DrawInteriorFixtureCombo(floor, InteriorFixturePart.CeilingLight);
        ImGui.Separator();
    }

    private void DrawInteriorFixtureCombo(InteriorFixtureFloor floor, InteriorFixturePart part)
    {
        var selectedId = this.interiorFixtures.Get(floor, part);
        var selected = selectedId == 0 ? null : this.interiorFixtureCatalog.Find(selectedId, part);
        var preview = selectedId == 0 ? "Default" : selected?.Name ?? $"Fixture {selectedId}";
        var label = $"{PartName(part)}##{floor}-{part}";

        ImGui.SetNextItemWidth(360);
        if (!ImGui.BeginCombo(label, preview))
            return;

        if (ImGui.Selectable("Default", selectedId == 0))
            this.interiorFixtures.Set(floor, part, 0);

        foreach (var item in this.interiorFixtureCatalog.ItemsForPart(part))
        {
            ImGui.PushID($"{floor}-{part}-{item.FixtureId}");
            if (this.DrawIcon(item.IconId, 22))
                ImGui.SameLine();

            if (ImGui.Selectable(item.Name, selectedId == item.FixtureId, ImGuiSelectableFlags.None, new Vector2(0, 26)))
                this.interiorFixtures.Set(floor, part, item.FixtureId);

            ImGui.PopID();
        }

        ImGui.EndCombo();
    }

    public void UpdateSync()
    {
        ApplyPendingSyncScene();

        if (syncClient.IsConnected && syncAsHost)
            BroadcastSceneSnapshot();
    }

    private void ApplyPendingSyncScene()
    {
        if (!syncClient.TryTakePendingScene(out var sceneJson))
            return;

        try
        {
            var result = sceneStore.LoadJson(sceneJson);
            ApplyLoadedScene(result with { Error = result.Skipped == 0 ? "" : "some synced objects were skipped" });
        }
        catch (JsonException)
        {
            Service.Log.Error("sync sent a scene that would not parse");
            sceneStatus = "sync sent a broken scene";
        }
    }

    private void BroadcastSceneSnapshot()
    {
        var now = Environment.TickCount64;
        if (now - lastBroadcastMilliseconds < 1000)
            return;

        var sceneJson = sceneStore.Serialize(spawned, folders, interiorFixtures);
        lastBroadcastMilliseconds = now;
        RunSyncTask(() => syncClient.SendSceneSnapshotAsync(sceneJson));
    }

    private void RunSyncTask(Func<Task> action)
        => _ = RunSyncTaskAsync(action);

    private async Task RunSyncTaskAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (WebSocketException error)
        {
            Service.Log.Error(error, "sync connection failed");
            syncClient.SetError("sync connection failed");
        }
        catch (OperationCanceledException)
        {
        }
        catch (FormatException)
        {
            syncClient.SetError("relay url is invalid");
        }
    }

    private void DrawSpawnedList()
    {
        if (this.spawned.Count == 0 && this.folders.Count == 0)
            return;

        foreach (var folder in this.folders.ToArray())
        {
            if (!this.DrawFolderRow(folder))
                continue;

            ImGui.Indent(18.0f);
            foreach (var furniture in this.spawned.Where(item => item.FolderId == folder.Id).ToArray())
                this.DrawSpawnedListItem(furniture);
            ImGui.Unindent(18.0f);
        }

        var looseFurniture = this.spawned.Where(item => item.FolderId is null || !this.folders.Any(folder => folder.Id == item.FolderId)).ToArray();
        if (this.folders.Count > 0 && looseFurniture.Length > 0)
            ImGui.TextDisabled("Unfiled");

        foreach (var furniture in looseFurniture)
            this.DrawSpawnedListItem(furniture);

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            this.draggedFurnitureIds.Clear();
    }

    private bool DrawFolderRow(FurnitureFolder folder)
    {
        ImGui.PushID(folder.Id.ToString());

        ImGui.SetNextItemOpen(folder.IsOpen, ImGuiCond.Always);
        var isOpen = ImGui.CollapsingHeader($"{folder.Name}##folder", ImGuiTreeNodeFlags.None);
        folder.IsOpen = isOpen;

        var headerMin = ImGui.GetItemRectMin();
        var headerMax = ImGui.GetItemRectMax();
        var mousePosition = ImGui.GetMousePos();
        var mouseOverHeader = mousePosition.X >= headerMin.X
            && mousePosition.X <= headerMax.X
            && mousePosition.Y >= headerMin.Y
            && mousePosition.Y <= headerMax.Y;

        if (this.draggedFurnitureIds.Count > 0 && mouseOverHeader)
        {
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRect(
                headerMin,
                headerMax,
                ImGui.GetColorU32(ImGuiCol.DragDropTarget),
                0.0f,
                ImDrawFlags.None,
                2.0f);

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                foreach (var item in this.spawned.Where(item => this.draggedFurnitureIds.Contains(item.Id)))
                    item.FolderId = folder.Id;

                this.draggedFurnitureIds.Clear();
            }
        }

        var deleted = false;
        if (ImGui.BeginPopupContextItem("folder-actions"))
        {
            ImGui.SetNextItemWidth(200);
            var name = folder.Name;
            if (ImGui.InputText("Name", ref name, 64) && !string.IsNullOrWhiteSpace(name))
                folder.Name = name.Trim();

            ImGui.Separator();
            if (ImGui.MenuItem("Delete folder"))
            {
                foreach (var item in this.spawned.Where(item => item.FolderId == folder.Id))
                    item.FolderId = null;

                this.folders.Remove(folder);
                deleted = true;
            }

            ImGui.EndPopup();
        }

        ImGui.PopID();
        return isOpen && !deleted;
    }

    private void DrawSpawnedListItem(SpawnedFurniture furniture)
    {
        ImGui.PushID(furniture.Id.ToString());

        var enabled = furniture.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
            furniture.Enabled = enabled;

        ImGui.SameLine();
        if (furniture.CatalogItem is not null && this.DrawIcon(furniture.CatalogItem.IconId, 24))
            ImGui.SameLine();

        var selected = this.selectedIds.Contains(furniture.Id);
        if (ImGui.Selectable(this.DisplayNameWithDuplicateIndex(furniture), selected, ImGuiSelectableFlags.None, new Vector2(0, 28)))
        {
            if (ImGui.GetIO().KeyCtrl)
                this.ToggleSelected(furniture);
            else
                this.SelectOnly(furniture);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            if (this.draggedFurnitureIds.Count == 0)
            {
                if (selected)
                    this.draggedFurnitureIds.UnionWith(this.selectedIds);
                else
                    this.draggedFurnitureIds.Add(furniture.Id);
            }

            ImGui.BeginTooltip();
            ImGui.TextUnformatted(this.draggedFurnitureIds.Count == 1
                ? this.DisplayNameWithDuplicateIndex(furniture)
                : $"{this.draggedFurnitureIds.Count:N0} objects");
            ImGui.EndTooltip();
        }

        ImGui.PopID();
    }

    private void DrawDetails()
    {
        var selectedItems = this.SelectedFurnitureItems;
        if (selectedItems.Count == 0)
            return;

        if (selectedItems.Count > 1)
        {
            this.DrawMultiDetails(selectedItems);
            return;
        }

        var selected = selectedItems[0];

        ImGui.TextUnformatted(this.DisplayNameWithDuplicateIndex(selected));
        ImGui.Separator();

        this.DrawSaveToMyCatalogue(selected);

        var position = selected.Position;
        if (ImGui.DragFloat3("Position", ref position, 0.05f))
            selected.Position = position;

        if (ImGui.Button("Move to player", new Vector2(124, 24)))
            selected.Position = this.DefaultSpawnPosition();

        ImGui.SameLine();
        if (ImGui.Button("Zero", new Vector2(64, 24)))
            selected.Position = Vector3.Zero;

        var rotation = selected.RotationDegrees;
        if (ImGui.DragFloat3("Rotation", ref rotation, 0.25f))
            selected.RotationDegrees = rotation;

        var scale = selected.Scale3;
        if (ImGui.DragFloat3("Scale", ref scale, 0.01f, 0.05f, 10.0f))
            selected.SetScale(scale);

        if (this.spawner.TryGetWorldSize(selected.Id, out var worldSize))
        {
            if (ImGui.Button("Duplicate left", new Vector2(112, 24)))
                this.DuplicateSelected(selected, DuplicatePlacement.Left, worldSize);

            ImGui.SameLine();
            if (ImGui.Button("Duplicate right", new Vector2(116, 24)))
                this.DuplicateSelected(selected, DuplicatePlacement.Right, worldSize);

            ImGui.SameLine();
            if (ImGui.Button("Duplicate on top", new Vector2(136, 24)))
                this.DuplicateSelected(selected, DuplicatePlacement.OnTop, worldSize);
        }
        else if (ImGui.Button("Duplicate", new Vector2(92, 24)))
        {
            this.AddSpawned(selected.DuplicateAt(selected.Position));
        }

        if (selected.IsFxEmitter)
            DrawFxControls(selected);
        else if (selected.IsNpc)
            this.DrawNpcControls(selected);
        else
            this.DrawDyeSelector(selected);
        this.DrawGizmoControls();

        if (ImGui.Button("Remove", new Vector2(88, 24)))
        {
            this.RemoveSpawnedItem(selected);
        }
    }

    private void DrawMultiDetails(IReadOnlyList<SpawnedFurniture> selectedItems)
    {
        ImGui.TextUnformatted($"{selectedItems.Count:N0} selected");
        ImGui.Separator();

        var moveDelta = Vector3.Zero;
        if (ImGui.DragFloat3("Move Delta", ref moveDelta, 0.05f))
        {
            foreach (var item in selectedItems)
                item.Position += moveDelta;
        }

        var rotationDelta = Vector3.Zero;
        if (ImGui.DragFloat3("Rotation Delta", ref rotationDelta, 0.25f))
        {
            foreach (var item in selectedItems)
                item.RotationDegrees += rotationDelta;
        }

        var scaleDelta = Vector3.Zero;
        if (ImGui.DragFloat3("Scale Delta", ref scaleDelta, 0.01f))
        {
            foreach (var item in selectedItems)
                item.AddScale(scaleDelta);
        }

        if (ImGui.Button("Move group to player", new Vector2(156, 24)))
        {
            var center = selectedItems.Aggregate(Vector3.Zero, (sum, item) => sum + item.Position) / selectedItems.Count;
            var delta = this.DefaultSpawnPosition() - center;
            foreach (var item in selectedItems)
                item.Position += delta;
        }

        ImGui.SameLine();
        if (ImGui.Button("Enable", new Vector2(72, 24)))
        {
            foreach (var item in selectedItems)
                item.Enabled = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Disable", new Vector2(76, 24)))
        {
            foreach (var item in selectedItems)
                item.Enabled = false;
        }

        if (ImGui.Button("Remove selected", new Vector2(132, 24)))
        {
            var removed = selectedItems.Select(item => item.Id).ToHashSet();
            this.spawned.RemoveAll(item => removed.Contains(item.Id));
            this.selectedIds.ExceptWith(removed);
            this.selectedId = this.selectedIds.LastOrDefault();
            if (this.selectedId == Guid.Empty)
                this.selectedId = null;
            if (this.spawned.Count == 0)
                this.spawner.Clear();
        }
    }

    private void RemoveSpawnedItem(SpawnedFurniture selected)
    {
        this.spawned.Remove(selected);
        this.selectedIds.Remove(selected.Id);
        this.selectedId = this.spawned.LastOrDefault()?.Id;
        this.selectedIds.Clear();
        if (this.selectedId is { } replacement)
            this.selectedIds.Add(replacement);
        if (this.selectedId is null)
            this.spawner.Clear();
    }

    private void DrawPickerPopup()
    {
        if (!ImGui.BeginPopupModal("Add", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (ImGui.BeginTabBar("addition-tabs"))
        {
            if (ImGui.BeginTabItem("Furniture"))
            {
                this.DrawFurniturePickerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Map Assets"))
            {
                this.DrawMapAssetPickerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Placed Map Assets"))
            {
                this.DrawPlacedMapAssetPickerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("FX Emitters"))
            {
                this.DrawFxPickerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("NPCs"))
            {
                this.DrawNpcPickerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("My Catalogue"))
            {
                this.DrawMyCataloguePickerTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (ImGui.Button("Close", new Vector2(96, 24)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawFurniturePickerTab()
    {
        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##furniture-search", "Search furniture", ref this.pickerSearch, 128);

        this.DrawFurnitureCategorySelector();

        var rows = this.FilteredCatalog().ToArray();
        if (ImGui.BeginChild("furniture-picker-list", new Vector2(560, 420), true))
        {
            foreach (var item in rows)
            {
                ImGui.PushID($"{item.SourceKind}-{item.ItemId}");

                if (this.DrawIcon(item.IconId, 28))
                    ImGui.SameLine();

                var label = $"[{item.CategoryLabel}] {item.Name}##{item.SourceKind}-{item.ItemId}";
                if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(0, 32)))
                {
                    this.AddSpawned(new SpawnedFurniture(item, this.DefaultSpawnPosition()));
                    ImGui.CloseCurrentPopup();
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
    }

    private void DrawMapAssetPickerTab()
    {
        this.EnsureMapTerritorySelected();
        this.DrawMapZoneSelector();

        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##map-asset-search", "Search map assets", ref this.mapAssetSearch, 128);

        var sourceRows = this.mapAssetCatalog.ItemsForTerritory(this.selectedMapTerritoryId);
        this.DrawMapAssetCategorySelector(sourceRows);

        var rows = this.FilteredMapAssets(sourceRows).ToArray();
        if (ImGui.BeginChild("map-asset-picker-list", new Vector2(560, 420), true))
        {
            foreach (var item in rows)
            {
                ImGui.PushID(item.ModelPath);

                var suffix = item.IsSharedGroup ? $" ({item.ChildItems.Count:N0})" : string.Empty;
                if (ImGui.Selectable($"[{item.Category}] {item.Name}{suffix}##{item.ModelPath}", false, ImGuiSelectableFlags.None, new Vector2(0, 28)))
                {
                    this.AddMapAsset(item with
                    {
                        OriginalInstanceId = 0,
                    }, this.DefaultSpawnPosition());
                    ImGui.CloseCurrentPopup();
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
    }

    private void DrawPlacedMapAssetPickerTab()
    {
        this.EnsureMapTerritorySelected();
        this.DrawMapZoneSelector();

        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##placed-map-asset-search", "Search placed map assets", ref this.mapAssetSearch, 128);

        var sourceRows = this.mapAssetCatalog.PlacedItemsForTerritory(this.selectedMapTerritoryId);
        this.DrawMapAssetCategorySelector(sourceRows);

        var rows = this.FilteredMapAssets(sourceRows).ToArray();
        if (ImGui.BeginChild("placed-map-asset-picker-list", new Vector2(560, 420), true))
        {
            for (var index = 0; index < rows.Length; index++)
            {
                var item = rows[index];
                ImGui.PushID($"{item.OriginalInstanceId}-{index}-{item.ModelPath}");

                var suffix = item.IsSharedGroup
                    ? $" ({item.ChildItems.Count:N0})"
                    : string.Empty;
                if (ImGui.Selectable($"[{item.Category}] {item.Name}{suffix}##{index}", false, ImGuiSelectableFlags.None, new Vector2(0, 28)))
                {
                    this.AddMapAsset(item, item.Position!.Value);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
    }

    private void DrawFxPickerTab()
    {
        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##fx-search", "Search FX emitters", ref this.fxSearch, 128);

        this.DrawFxCategorySelector();

        var rows = this.FilteredFxItems().ToArray();
        if (ImGui.BeginChild("fx-picker-list", new Vector2(560, 330), true))
        {
            foreach (var item in rows)
            {
                ImGui.PushID(item.Path);

                if (ImGui.Selectable($"[{item.Category}] {item.Name}##{item.Path}", false, ImGuiSelectableFlags.None, new Vector2(0, 28)))
                {
                    this.AddSpawned(new SpawnedFurniture(item, this.DefaultSpawnPosition()));
                    ImGui.CloseCurrentPopup();
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();

        ImGui.Separator();
        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##fx-raw-path", "Paste .avfx path", ref this.fxRawPath, 256);
        ImGui.SameLine();
        if (ImGui.Button("Spawn FX", new Vector2(96, 24)))
        {
            var path = this.fxRawPath.Trim();
            if (path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) &&
                Service.DataManager.FileExists(path))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                this.AddSpawned(new SpawnedFurniture(new FxCatalogItem(name, path, "Custom"), this.DefaultSpawnPosition()));
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.sceneStatus = "not a valid .avfx path";
            }
        }
    }

    private void DrawNpcPickerTab()
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##npc-model-chara-id", "NPC ModelChara #", ref this.npcModelCharaInput, 16);
        ImGui.SameLine();
        if (ImGui.Button("Spawn NPC #", new Vector2(112, 24)))
        {
            if (uint.TryParse(this.npcModelCharaInput.Trim(), out var modelCharaId) &&
                this.npcCatalog.FindModelChara(modelCharaId) is { } exactModel)
            {
                this.AddSpawned(new SpawnedFurniture(exactModel, this.DefaultSpawnPosition()));
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.sceneStatus = "no npc matches that modelchara id";
            }
        }

        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##npc-search", "Search NPCs", ref this.npcSearch, 128);

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Type", this.npcDisplayFilter))
        {
            foreach (var label in new[] { "All", "Human", "Non-human" })
            {
                if (ImGui.Selectable(label, this.npcDisplayFilter == label))
                    this.npcDisplayFilter = label;
            }

            ImGui.EndCombo();
        }

        var rows = this.FilteredNpcItems().ToArray();
        if (ImGui.BeginChild("npc-picker-list", new Vector2(560, 420), true))
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(rows.Length, 28.0f);
            while (clipper.Step())
            {
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var item = rows[index];
                    ImGui.PushID(item.StableKey);
                    if (ImGui.Selectable($"{NpcPickerLabel(item)}##{item.StableKey}", false, ImGuiSelectableFlags.None, new Vector2(0, 28)))
                    {
                        this.AddSpawned(new SpawnedFurniture(item, this.DefaultSpawnPosition()));
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.PopID();
                }
            }

            clipper.End();
        }

        ImGui.EndChild();
    }

    private void DrawMyCataloguePickerTab()
    {
        var catalogue = this.EnsureSelectedMyCatalogue();

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Catalogue", catalogue.Name))
        {
            foreach (var item in this.myCatalogueStore.Catalogues)
            {
                if (ImGui.Selectable(item.Name, item.Id == catalogue.Id))
                    this.selectedMyCatalogueId = item.Id;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##new-catalogue-name", "New catalogue", ref this.newCatalogueName, 96);
        ImGui.SameLine();
        var canAddCatalogue = !string.IsNullOrWhiteSpace(this.newCatalogueName);
        if (!canAddCatalogue)
            ImGui.BeginDisabled();
        if (ImGui.Button("+ Catalogue", new Vector2(104, 24)))
        {
            var created = this.myCatalogueStore.AddCatalogue(this.newCatalogueName);
            this.selectedMyCatalogueId = created.Id;
            this.newCatalogueName = string.Empty;
        }
        if (!canAddCatalogue)
            ImGui.EndDisabled();

        if (this.myCatalogueStore.Catalogues.Count > 1 && ImGui.Button("Delete catalogue", new Vector2(128, 24)))
        {
            this.myCatalogueStore.RemoveCatalogue(catalogue.Id);
            this.selectedMyCatalogueId = this.myCatalogueStore.EnsureDefault().Id;
            return;
        }

        ImGui.SetNextItemWidth(420);
        ImGui.InputTextWithHint("##my-catalogue-search", "Search My Catalogue", ref this.myCatalogueSearch, 128);

        var search = this.myCatalogueSearch.Trim();
        var rows = catalogue.Items
            .Where(item => search.Length == 0 ||
                item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.ModelPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (ImGui.BeginChild("my-catalogue-picker-list", new Vector2(560, 420), true))
        {
            foreach (var item in rows)
            {
                ImGui.PushID(item.Id.ToString());

                if (ImGui.Button("Spawn", new Vector2(72, 24)))
                {
                    if (item.IsFxEmitter)
                        this.AddSpawned(new SpawnedFurniture(this.myCatalogueStore.ToFxEmitter(item), this.DefaultSpawnPosition()));
                    else
                        this.AddMapAsset(this.myCatalogueStore.ToMapAsset(item), this.DefaultSpawnPosition());
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("Remove", new Vector2(76, 24)))
                {
                    this.myCatalogueStore.RemoveItem(catalogue.Id, item.Id);
                    ImGui.PopID();
                    continue;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(item.Name);

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
    }

    private IEnumerable<FurnitureCatalogItem> FilteredCatalog()
    {
        var search = this.pickerSearch.Trim();
        var rows = this.catalog.Items.AsEnumerable();

        if (this.furnitureCategory != "All")
            rows = rows.Where(item => item.CategoryKey.Equals(this.furnitureCategory, StringComparison.OrdinalIgnoreCase));

        if (search.Length != 0)
            rows = rows.Where(item =>
                item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.CategoryLabel.Contains(search, StringComparison.OrdinalIgnoreCase));

        return rows;
    }

    private void DrawFurnitureCategorySelector()
    {
        var categories = this.catalog.Items
            .GroupBy(item => item.CategoryKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.SourceKind)
            .ThenBy(item => item.HousingItemCategory)
            .ToArray();

        if (this.furnitureCategory != "All" && !categories.Any(item => item.CategoryKey.Equals(this.furnitureCategory, StringComparison.OrdinalIgnoreCase)))
            this.furnitureCategory = "All";

        var currentLabel = this.furnitureCategory == "All"
            ? "All"
            : categories.FirstOrDefault(item => item.CategoryKey.Equals(this.furnitureCategory, StringComparison.OrdinalIgnoreCase))?.CategoryLabel ?? "All";

        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo("Category", currentLabel))
            return;

        if (ImGui.Selectable("All", this.furnitureCategory == "All"))
            this.furnitureCategory = "All";

        foreach (var item in categories)
        {
            if (ImGui.Selectable(item.CategoryLabel, item.CategoryKey.Equals(this.furnitureCategory, StringComparison.OrdinalIgnoreCase)))
                this.furnitureCategory = item.CategoryKey;
        }

        ImGui.EndCombo();
    }

    private void DrawMapAssetCategorySelector(IReadOnlyList<MapAssetCatalogItem> items)
    {
        var categories = items
            .Select(item => item.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (this.mapAssetCategory != "All" && !categories.Contains(this.mapAssetCategory, StringComparer.OrdinalIgnoreCase))
            this.mapAssetCategory = "All";

        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo("Category", this.mapAssetCategory))
            return;

        if (ImGui.Selectable("All", this.mapAssetCategory == "All"))
            this.mapAssetCategory = "All";

        foreach (var category in categories)
        {
            if (ImGui.Selectable(category, category.Equals(this.mapAssetCategory, StringComparison.OrdinalIgnoreCase)))
                this.mapAssetCategory = category;
        }

        ImGui.EndCombo();
    }

    private void AddSpawned(SpawnedFurniture spawned)
    {
        this.spawned.Add(spawned);
        this.SelectOnly(spawned);
        this.sceneStatus = "";
    }

    private void AddMapAsset(MapAssetCatalogItem item, Vector3 position)
    {
        if (!item.IsSharedGroup || item.ChildItems.Count == 0)
        {
            this.AddSpawned(new SpawnedFurniture(item, position));
            return;
        }

        SpawnedFurniture? first = null;
        foreach (var child in item.ChildItems)
        {
            var childItem = new MapAssetCatalogItem(
                $"{item.Name} / {child.Name}",
                child.ModelPath,
                item.ModelPath,
                item.Category,
                Kind: MapAssetKind.SharedGroupChild);
            var spawned = new SpawnedFurniture(childItem, position + child.Offset)
            {
                RotationDegrees = child.RotationDegrees,
            };
            spawned.SetScale(child.Scale3);

            this.spawned.Add(spawned);
            first ??= spawned;
        }

        if (first is not null)
            this.SelectOnly(first);

        this.sceneStatus = "";
    }

    private void DrawSaveToMyCatalogue(SpawnedFurniture selected)
    {
        if (selected.MapAssetItem is null && !selected.IsFxEmitter)
            return;

        var catalogue = this.EnsureSelectedMyCatalogue();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Save to", catalogue.Name))
        {
            foreach (var item in this.myCatalogueStore.Catalogues)
            {
                if (ImGui.Selectable(item.Name, item.Id == catalogue.Id))
                    this.selectedMyCatalogueId = item.Id;
            }

            ImGui.EndCombo();
        }

        if (this.saveCatalogueEntrySourceId != selected.Id)
        {
            this.saveCatalogueEntryName = selected.Name;
            this.saveCatalogueEntrySourceId = selected.Id;
        }

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##save-catalogue-entry-name", "Saved asset name", ref this.saveCatalogueEntryName, 96);
        ImGui.SameLine();
        var canSaveCatalogueEntry = !string.IsNullOrWhiteSpace(this.saveCatalogueEntryName);
        if (!canSaveCatalogueEntry)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add to My Catalogue", new Vector2(156, 24)))
        {
            if (selected.IsFxEmitter)
                this.myCatalogueStore.AddFxEmitter(catalogue.Id, selected, this.saveCatalogueEntryName);
            else
                this.myCatalogueStore.AddMapAsset(catalogue.Id, selected, this.saveCatalogueEntryName);
            this.sceneStatus = "";
            this.saveCatalogueEntryName = string.Empty;
        }
        if (!canSaveCatalogueEntry)
            ImGui.EndDisabled();
    }

    private MyCatalogue EnsureSelectedMyCatalogue()
        => this.myCatalogueStore.Catalogues.Single(item => item.Id == this.selectedMyCatalogueId);

    private void CreateFolder()
    {
        var folder = new FurnitureFolder(this.NextFolderName());
        this.folders.Add(folder);

        foreach (var item in this.SelectedFurnitureItems)
            item.FolderId = folder.Id;
    }

    private string NextFolderName()
    {
        for (var index = 1; ; index++)
        {
            var name = $"Folder {index}";
            if (this.folders.All(folder => !folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private void SelectOnly(SpawnedFurniture furniture)
    {
        this.selectedIds.Clear();
        this.selectedIds.Add(furniture.Id);
        this.selectedId = furniture.Id;
    }

    private void ToggleSelected(SpawnedFurniture furniture)
    {
        if (!this.selectedIds.Remove(furniture.Id))
        {
            this.selectedIds.Add(furniture.Id);
            this.selectedId = furniture.Id;
            return;
        }

        if (this.selectedId == furniture.Id)
            this.selectedId = this.selectedIds.LastOrDefault();

        if (this.selectedId == Guid.Empty)
            this.selectedId = null;
    }

    private void DuplicateSelected(SpawnedFurniture selected, DuplicatePlacement placement, Vector3 size)
    {
        var duplicate = selected.DuplicateAt(selected.Position + GetDuplicateOffset(selected, placement, size));
        this.AddSpawned(duplicate);
    }

    private static Vector3 GetDuplicateOffset(SpawnedFurniture selected, DuplicatePlacement placement, Vector3 size)
    {
        if (placement == DuplicatePlacement.OnTop)
            return Vector3.UnitY * MathF.Max(size.Y, 0.05f);

        var yaw = selected.YawRadians;
        var right = new Vector3(MathF.Cos(yaw), 0.0f, -MathF.Sin(yaw));
        var distance = MathF.Abs(right.X) * size.X + MathF.Abs(right.Z) * size.Z;
        var direction = placement == DuplicatePlacement.Left ? -right : right;
        return direction * MathF.Max(distance, 0.05f);
    }

    private string DisplayNameWithDuplicateIndex(SpawnedFurniture furniture)
    {
        var path = furniture.ModelPath.Trim();
        var matching = this.spawned
            .Where(item => item.ModelPath.Trim().Equals(path, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matching.Length <= 1)
            return furniture.Name;

        var index = Array.FindIndex(matching, item => item.Id == furniture.Id);
        return $"{furniture.Name} #{index + 1}";
    }

    private IEnumerable<MapAssetCatalogItem> FilteredMapAssets(IReadOnlyList<MapAssetCatalogItem> items)
    {
        var search = this.mapAssetSearch.Trim();
        var category = this.mapAssetCategory;
        if (category != "All")
        {
            items = items
                .Where(item => item.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (search.Length == 0)
            return items;

        return items.Where(item =>
            item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.ModelPath.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<FxCatalogItem> FilteredFxItems()
    {
        var search = this.fxSearch.Trim();
        var rows = this.fxCatalog.Items.AsEnumerable();

        if (this.fxCategory != "All")
            rows = rows.Where(item => item.Category.Equals(this.fxCategory, StringComparison.OrdinalIgnoreCase));

        if (search.Length == 0)
            return rows;

        return rows.Where(item =>
            item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<NpcCatalogItem> FilteredNpcItems()
    {
        var search = this.npcSearch.Trim();
        var rows = this.npcCatalog.Items.Where(item =>
            (this.npcDisplayFilter == "All" ||
                (this.npcDisplayFilter == "Human" && item.DisplayKind == NpcDisplayKind.Human) ||
                (this.npcDisplayFilter == "Non-human" && item.DisplayKind == NpcDisplayKind.NonHuman)) &&
            item.Matches(search));

        return uint.TryParse(search, out var exactModelCharaId)
            ? rows.OrderByDescending(item => item.ModelCharaId == exactModelCharaId)
                .ThenBy(item => item.ModelCharaId)
            : rows;
    }

    private IEnumerable<NpcAnimationCatalogItem> CompatibleNpcAnimations(SpawnedFurniture selected)
    {
        var displayKind = selected.NpcItem?.DisplayKind ?? NpcDisplayKind.NonHuman;
        return this.npcAnimationCatalog.Items.Where(item => item.Supports(displayKind));
    }

    private IEnumerable<NpcAnimationCatalogItem> FilteredNpcAnimations(SpawnedFurniture selected)
    {
        var search = this.npcAnimationSearch.Trim();
        return this.CompatibleNpcAnimations(selected).Where(item =>
            (this.npcAnimationCategory == "All" || item.Category.Equals(this.npcAnimationCategory, StringComparison.OrdinalIgnoreCase)) &&
            item.Matches(search));
    }

    private void DrawFxCategorySelector()
    {
        var categories = this.fxCatalog.Items
            .Select(item => item.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (this.fxCategory != "All" && !categories.Contains(this.fxCategory, StringComparer.OrdinalIgnoreCase))
            this.fxCategory = "All";

        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo("Category", this.fxCategory))
            return;

        if (ImGui.Selectable("All", this.fxCategory == "All"))
            this.fxCategory = "All";

        foreach (var category in categories)
        {
            if (ImGui.Selectable(category, category.Equals(this.fxCategory, StringComparison.OrdinalIgnoreCase)))
                this.fxCategory = category;
        }

        ImGui.EndCombo();
    }

    private void DrawNpcAnimationCategorySelector(SpawnedFurniture selected)
    {
        var categories = this.CompatibleNpcAnimations(selected)
            .Select(item => item.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NpcAnimationCatalog.CategoryOrder)
            .ThenBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (this.npcAnimationCategory != "All" && !categories.Contains(this.npcAnimationCategory, StringComparer.OrdinalIgnoreCase))
            this.npcAnimationCategory = "All";

        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo("Animation Category", this.npcAnimationCategory))
            return;

        if (ImGui.Selectable("All", this.npcAnimationCategory == "All"))
            this.npcAnimationCategory = "All";

        foreach (var category in categories)
        {
            if (ImGui.Selectable(category, category.Equals(this.npcAnimationCategory, StringComparison.OrdinalIgnoreCase)))
                this.npcAnimationCategory = category;
        }

        ImGui.EndCombo();
    }

    private void EnsureMapTerritorySelected()
    {
        if (this.selectedMapTerritoryId != 0)
            return;

        var currentTerritory = Service.ClientState.TerritoryType;
        if (this.mapAssetCatalog.Zones.Any(zone => zone.TerritoryId == currentTerritory))
            this.selectedMapTerritoryId = currentTerritory;
        else
            this.selectedMapTerritoryId = this.mapAssetCatalog.Zones.FirstOrDefault()?.TerritoryId ?? 0;
    }

    private void DrawMapZoneSelector()
    {
        var selectedZone = this.mapAssetCatalog.Zones.FirstOrDefault(zone => zone.TerritoryId == this.selectedMapTerritoryId);
        var preview = selectedZone?.Name ?? "Select zone";

        ImGui.SetNextItemWidth(420);
        if (!ImGui.BeginCombo("Zone", preview))
            return;

        ImGui.SetNextItemWidth(390);
        ImGui.InputTextWithHint("##map-zone-search", "Search zones", ref this.mapZoneSearch, 128);
        ImGui.Separator();

        var search = this.mapZoneSearch.Trim();
        foreach (var zone in this.mapAssetCatalog.Zones)
        {
            if (search.Length != 0 &&
                !zone.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !zone.BgPath.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ImGui.Selectable(zone.Name, zone.TerritoryId == this.selectedMapTerritoryId))
            {
                this.selectedMapTerritoryId = zone.TerritoryId;
                this.mapAssetSearch = string.Empty;
                this.mapAssetCategory = "All";
            }
        }

        ImGui.EndCombo();
    }

    private Vector3 DefaultSpawnPosition()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return Vector3.Zero;

        const float distance = 2.0f;
        var rotation = player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));
        return player.Position + forward * distance;
    }

    private static string FloorName(InteriorFixtureFloor floor)
        => floor switch
        {
            InteriorFixtureFloor.Ground => "Ground Floor",
            InteriorFixtureFloor.Second => "Second Floor",
            InteriorFixtureFloor.Basement => "Basement",
            _ => floor.ToString(),
        };

    private static string PartName(InteriorFixturePart part)
        => part switch
        {
            InteriorFixturePart.Walls => "Wallpaper",
            InteriorFixturePart.Floors => "Flooring",
            InteriorFixturePart.CeilingLight => "Ceiling Light",
            _ => part.ToString(),
        };

    private static string NpcDisplayKindLabel(NpcDisplayKind kind)
        => kind == NpcDisplayKind.Human ? "Human" : "Non-human";

    private static string NpcPickerLabel(NpcCatalogItem item)
    {
        var prefix = item.HasResolvedName ? item.Name : $"Unnamed {item.SourceKind} {item.RowId}";
        return $"[{NpcDisplayKindLabel(item.DisplayKind)}] ModelChara {item.ModelCharaId}  {prefix}  {item.SourceKind} {item.RowId}";
    }

    private bool DrawIcon(uint iconId, float size)
    {
        if (iconId == 0)
            return false;

        var lookup = new GameIconLookup(iconId);
        var texture = Service.TextureProvider.GetFromGameIcon(lookup).GetWrapOrEmpty();
        if (texture == null)
            return false;

        ImGui.Image(texture.Handle, new Vector2(size, size));
        return true;
    }

    private void DrawDyeSelector(SpawnedFurniture selected)
    {
        ImGui.Spacing();

        for (var channel = 0; channel < selected.DyeChannelCount; channel++)
            this.DrawDyeChannelSelector(selected, channel);

        var forcedDyesEnabled = selected.ForcedDyesEnabled;
        if (ImGui.Checkbox("Forced Dyes", ref forcedDyesEnabled))
        {
            selected.ForcedDyesEnabled = forcedDyesEnabled;
            this.spawner.InvalidateForcedDyes(selected.Id);
        }

        if (selected.ForcedDyesEnabled)
            this.DrawForcedDyeControls(selected);
    }

    private static void DrawFxControls(SpawnedFurniture selected)
    {
        ImGui.Spacing();
        var color = selected.FxColor;
        ImGui.SetNextItemWidth(260);
        if (ImGui.ColorEdit4("Color", ref color, ImGuiColorEditFlags.PickerHueWheel))
            selected.FxColor = ColorMath.Clamp01(color);
    }

    private void DrawNpcControls(SpawnedFurniture selected)
    {
        ImGui.Spacing();
        this.DrawNpcAppearanceControls(selected);
        ImGui.Separator();
        ImGui.TextUnformatted("Animation");

        var animationPreview = selected.NpcAnimation is null
            ? "None"
            : $"{selected.NpcAnimation.Name} ({selected.NpcAnimation.TimelineId})";
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("Timeline", animationPreview))
        {
            ImGui.SetNextItemWidth(330);
            ImGui.InputTextWithHint("##npc-animation-search", "Search animations", ref this.npcAnimationSearch, 128);
            this.DrawNpcAnimationCategorySelector(selected);
            ImGui.Separator();

            if (ImGui.Selectable("None", selected.NpcAnimation is null))
                selected.NpcAnimation = null;

            var animations = this.FilteredNpcAnimations(selected).ToArray();
            var clipper = new ImGuiListClipper();
            clipper.Begin(animations.Length);
            while (clipper.Step())
            {
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var animation = animations[index];
                    var label = $"[{animation.Category}] {animation.Name}  {animation.TimelineId}";
                    if (ImGui.Selectable(label, selected.NpcAnimation?.TimelineId == animation.TimelineId))
                    {
                        selected.NpcAnimation = animation;
                        selected.NpcLoopAnimation = animation.IsLoop;
                    }
                }
            }
            clipper.End();

            ImGui.EndCombo();
        }

        if (selected.NpcAnimation?.IsLoop == true)
        {
            var loopAnimation = selected.NpcLoopAnimation;
            if (ImGui.Checkbox("Loop animation", ref loopAnimation))
                selected.NpcLoopAnimation = loopAnimation;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Patrol");
        var patrolEnabled = selected.NpcPatrolEnabled;
        if (ImGui.Checkbox("Enable patrol", ref patrolEnabled))
            selected.NpcPatrolEnabled = patrolEnabled;

        ImGui.SameLine();
        var patrolLoop = selected.NpcPatrolLoop;
        if (ImGui.Checkbox("Loop path", ref patrolLoop))
            selected.NpcPatrolLoop = patrolLoop;

        ImGui.SameLine();
        var snapToTerrain = selected.NpcPatrolSnapToTerrain;
        if (ImGui.Checkbox("Snap to terrain", ref snapToTerrain))
            selected.NpcPatrolSnapToTerrain = snapToTerrain;

        var speed = selected.NpcPatrolSpeed;
        ImGui.SetNextItemWidth(180);
        if (ImGui.DragFloat("Speed", ref speed, 0.05f, 0.05f, 20.0f))
            selected.NpcPatrolSpeed = Math.Clamp(speed, 0.05f, 20.0f);

        if (ImGui.Button("+ Point here", new Vector2(104, 24)))
            selected.NpcPatrolPoints.Add(new NpcPatrolPoint(selected.Position));

        ImGui.SameLine();
        if (ImGui.Button("Clear path", new Vector2(92, 24)))
            selected.NpcPatrolPoints.Clear();

        for (var index = 0; index < selected.NpcPatrolPoints.Count; index++)
        {
            var point = selected.NpcPatrolPoints[index];
            ImGui.PushID($"patrol-{index}");
            var pointPosition = point.Position;
            if (ImGui.DragFloat3($"Point {index + 1}", ref pointPosition, 0.05f))
                point.Position = pointPosition;
            ImGui.SameLine();
            if (ImGui.Button("Use current", new Vector2(92, 24)))
                point.Position = selected.Position;
            ImGui.SameLine();
            if (ImGui.Button("X", new Vector2(28, 24)))
            {
                selected.NpcPatrolPoints.RemoveAt(index);
                ImGui.PopID();
                index--;
                continue;
            }

            ImGui.PopID();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Speech Bubble");
        var speechEnabled = selected.NpcSpeechEnabled;
        if (ImGui.Checkbox("Enable speech", ref speechEnabled))
            selected.NpcSpeechEnabled = speechEnabled;

        ImGui.SetNextItemWidth(360);
        var speechText = selected.NpcSpeechText;
        if (ImGui.InputText("Text", ref speechText, 512))
            selected.NpcSpeechText = speechText;

        var requireNear = selected.NpcSpeechRequirePlayerNear;
        if (ImGui.Checkbox("Trigger near player", ref requireNear))
            selected.NpcSpeechRequirePlayerNear = requireNear;

        var interval = selected.NpcSpeechIntervalSeconds;
        ImGui.SetNextItemWidth(180);
        if (ImGui.DragFloat("Interval seconds", ref interval, 0.25f, 0.25f, 3600.0f))
            selected.NpcSpeechIntervalSeconds = Math.Clamp(interval, 0.25f, 3600.0f);

        var duration = selected.NpcSpeechDurationSeconds;
        ImGui.SetNextItemWidth(180);
        if (ImGui.DragFloat("Visible seconds", ref duration, 0.1f, 0.1f, 120.0f))
            selected.NpcSpeechDurationSeconds = Math.Clamp(duration, 0.1f, 120.0f);

        var distance = selected.NpcSpeechTriggerDistance;
        ImGui.SetNextItemWidth(180);
        if (ImGui.DragFloat("Trigger distance", ref distance, 0.1f, 0.1f, 100.0f))
            selected.NpcSpeechTriggerDistance = Math.Clamp(distance, 0.1f, 100.0f);
    }

    private void DrawNpcAppearanceControls(SpawnedFurniture selected)
    {
        ImGui.TextUnformatted("Appearance");
        if (ImGui.Button("Refresh presets", new Vector2(112, 24)))
            this.npcAppearanceInterop.RefreshPresets();

        ImGui.SameLine();
        if (ImGui.Button("Reapply appearance", new Vector2(144, 24)))
            this.npcSpawner.ReapplyAppearance(selected.Id);

        var isHuman = selected.NpcItem?.DisplayKind == NpcDisplayKind.Human;
        if (!isHuman)
            ImGui.BeginDisabled();

        if (ImGui.Button("Edit live in Glamourer", new Vector2(168, 24)))
        {
            if (!this.npcSpawner.TryGetObjectIndex(selected.Id, out var objectIndex) ||
                !this.npcAppearanceInterop.OpenGlamourerEditor(objectIndex))
            {
                this.sceneStatus = "could not open the glamourer editor";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Capture appearance", new Vector2(148, 24)) &&
            !this.npcSpawner.CaptureGlamourerAppearance(selected.Id, selected))
            this.sceneStatus = "could not capture the appearance";

        var collectionPreview = selected.NpcPenumbraCollectionId == Guid.Empty
            ? "Default"
            : string.IsNullOrWhiteSpace(selected.NpcPenumbraCollectionName) ? selected.NpcPenumbraCollectionId.ToString() : selected.NpcPenumbraCollectionName;
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("Penumbra collection", collectionPreview))
        {
            if (ImGui.Selectable("Default", selected.NpcPenumbraCollectionId == Guid.Empty))
            {
                selected.NpcPenumbraCollectionId = Guid.Empty;
                selected.NpcPenumbraCollectionName = string.Empty;
            }

            foreach (var preset in this.npcAppearanceInterop.PenumbraCollections)
            {
                if (ImGui.Selectable(preset.Name, selected.NpcPenumbraCollectionId == preset.Id))
                {
                    selected.NpcPenumbraCollectionId = preset.Id;
                    selected.NpcPenumbraCollectionName = preset.Name;
                }
            }

            ImGui.EndCombo();
        }

        var glamourerPreview = selected.NpcGlamourerDesignId == Guid.Empty
            ? "None"
            : string.IsNullOrWhiteSpace(selected.NpcGlamourerDesignName) ? selected.NpcGlamourerDesignId.ToString() : selected.NpcGlamourerDesignName;
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("Glamourer design", glamourerPreview))
        {
            if (ImGui.Selectable("None", selected.NpcGlamourerDesignId == Guid.Empty))
            {
                selected.NpcGlamourerDesignId = Guid.Empty;
                selected.NpcGlamourerDesignName = string.Empty;
                selected.NpcGlamourerStateBase64 = string.Empty;
            }

            foreach (var preset in this.npcAppearanceInterop.GlamourerDesigns)
            {
                if (ImGui.Selectable(preset.Name, selected.NpcGlamourerDesignId == preset.Id))
                {
                    selected.NpcGlamourerDesignId = preset.Id;
                    selected.NpcGlamourerDesignName = preset.Name;
                    selected.NpcGlamourerStateBase64 = string.Empty;
                }
            }

            ImGui.EndCombo();
        }

        var cPlusPreview = selected.NpcCustomizePlusProfileId == Guid.Empty
            ? "None"
            : string.IsNullOrWhiteSpace(selected.NpcCustomizePlusProfileName) ? selected.NpcCustomizePlusProfileId.ToString() : selected.NpcCustomizePlusProfileName;
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("Customize+ profile", cPlusPreview))
        {
            if (ImGui.Selectable("None", selected.NpcCustomizePlusProfileId == Guid.Empty))
            {
                selected.NpcCustomizePlusProfileId = Guid.Empty;
                selected.NpcCustomizePlusProfileName = string.Empty;
                selected.NpcCustomizePlusProfileJson = string.Empty;
            }

            foreach (var preset in this.npcAppearanceInterop.CustomizePlusProfiles)
            {
                if (ImGui.Selectable(preset.Name, selected.NpcCustomizePlusProfileId == preset.Id) &&
                    this.npcAppearanceInterop.TryGetCustomizePlusProfile(preset.Id, out var profileJson))
                {
                    selected.NpcCustomizePlusProfileId = preset.Id;
                    selected.NpcCustomizePlusProfileName = preset.Name;
                    selected.NpcCustomizePlusProfileJson = profileJson;
                }
            }

            ImGui.EndCombo();
        }

        if (!isHuman)
            ImGui.EndDisabled();
    }

    private void DrawDyeChannelSelector(SpawnedFurniture selected, int channel)
    {
        var stainId = selected.GetStainId(channel);
        var preview = stainId == 0 ? "Default" : this.stainCatalog.Find(stainId)?.Name ?? stainId.ToString();
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo($"Dye {channel + 1}", preview))
        {
            if (ImGui.Selectable("Default", stainId == 0))
                selected.SetStainId(channel, 0);
            foreach (var stain in this.stainCatalog.Items)
            {
                if (ImGui.Selectable($"{stain.Name}##dye-{channel}-{stain.StainId}", stain.StainId == stainId))
                    selected.SetStainId(channel, stain.StainId);
            }
            ImGui.EndCombo();
        }

        var enabled = selected.IsDyeColorEnabled(channel);
        if (ImGui.Checkbox($"Custom##dye-custom-{channel}", ref enabled))
            selected.SetDyeColorEnabled(channel, enabled);

        if (enabled)
        {
            ImGui.SameLine();
            var color = selected.GetDyeColor(channel);
            ImGui.SetNextItemWidth(220);
            if (ImGui.ColorEdit4($"##dye-color-{channel}", ref color, ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.NoAlpha))
                selected.SetDyeColor(channel, color);
        }
    }

    private void DrawForcedDyeControls(SpawnedFurniture selected)
    {
        ImGui.Indent();

        var materialCount = this.spawner.GetMaterialSlotCount(selected.Id);
        if (materialCount <= 0)
        {
            ImGui.Unindent();
            return;
        }

        for (var materialIndex = 0; materialIndex < materialCount; materialIndex++)
            this.DrawForcedMaterialDyeSelector(selected, materialIndex);

        ImGui.Unindent();
    }

    private void DrawForcedMaterialDyeSelector(SpawnedFurniture selected, int materialIndex)
    {
        var materialInfo = this.spawner.GetMaterialSlotInfo(selected.Id, materialIndex);
        var stainId = selected.GetForcedMaterialStainId(materialIndex);
        var preview = stainId == 0 ? "Default" : this.stainCatalog.Find(stainId)?.Name ?? stainId.ToString();
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo($"Material {materialIndex + 1}", preview))
        {
            if (ImGui.Selectable("Default", stainId == 0))
                selected.SetForcedMaterialStainId(materialIndex, 0);
            foreach (var stain in this.stainCatalog.Items)
            {
                if (ImGui.Selectable($"{stain.Name}##material-{materialIndex}-{stain.StainId}", stain.StainId == stainId))
                    selected.SetForcedMaterialStainId(materialIndex, stain.StainId);
            }
            ImGui.EndCombo();
        }

        var enabled = selected.IsForcedMaterialDyeColorEnabled(materialIndex);
        if (ImGui.Checkbox($"Custom##material-custom-{materialIndex}", ref enabled))
        {
            if (enabled && materialInfo.DiffuseColor is { } currentColor)
                selected.SetForcedMaterialDyeColor(materialIndex, currentColor);
            selected.SetForcedMaterialDyeColorEnabled(materialIndex, enabled);
            this.spawner.InvalidateForcedDyes(selected.Id);
        }

        if (enabled)
        {
            ImGui.SameLine();
            var color = selected.GetForcedMaterialDyeColor(materialIndex);
            ImGui.SetNextItemWidth(220);
            if (ImGui.ColorEdit4($"##forced-dye-color-{materialIndex}", ref color, ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.NoAlpha))
            {
                selected.SetForcedMaterialDyeColor(materialIndex, color);
                this.spawner.InvalidateForcedDyes(selected.Id);
            }
        }

        ImGui.SameLine();
        this.DrawMaterialCurrentColor(materialInfo);
    }

    private void DrawMaterialCurrentColor(MaterialSlotInfo info)
    {
        if (!info.IsLoaded || !info.HasDiffuseControl || info.DiffuseColor is null)
            return;

        ImGui.ColorButton($"##current-material-{info.Slot}", info.DiffuseColor.Value, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(18, 18));
    }

    private void DrawGizmoControls()
    {
        ImGui.Spacing();

        if (this.GizmoButton("Move", ImGuizmoOperation.Translate))
            this.gizmoOperation = ImGuizmoOperation.Translate;
        ImGui.SameLine();
        if (this.GizmoButton("Rotate", ImGuizmoOperation.Rotate))
            this.gizmoOperation = ImGuizmoOperation.Rotate;
        ImGui.SameLine();
        if (this.GizmoButton("Scale", ImGuizmoOperation.Scale))
            this.gizmoOperation = ImGuizmoOperation.Scale;
        ImGui.SameLine();
        if (this.GizmoButton("All", ImGuizmoOperation.Universal))
            this.gizmoOperation = ImGuizmoOperation.Universal;

        ImGui.SameLine();
        ImGui.TextUnformatted("Mode");
        ImGui.SameLine();
        this.DrawModeToggle();
    }

    private bool GizmoButton(string label, ImGuizmoOperation operation)
    {
        var active = this.gizmoOperation == operation;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        var clicked = ImGui.Button(label, new Vector2(64, 24));
        if (active)
            ImGui.PopStyleColor();
        return clicked;
    }

    private void DrawModeToggle()
    {
        var worldActive = this.gizmoMode == ImGuizmoMode.World;
        if (worldActive)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        if (ImGui.Button("World##gizmo-mode-world", new Vector2(64, 24)))
            this.gizmoMode = ImGuizmoMode.World;
        if (worldActive)
            ImGui.PopStyleColor();

        ImGui.SameLine(0, 4);

        var localActive = this.gizmoMode == ImGuizmoMode.Local;
        if (localActive)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        if (ImGui.Button("Local##gizmo-mode-local", new Vector2(64, 24)))
            this.gizmoMode = ImGuizmoMode.Local;
        if (localActive)
            ImGui.PopStyleColor();
    }

    private unsafe void DrawGizmoOverlay(SpawnedFurniture? selected)
    {
        if (selected is null || !selected.Enabled || string.IsNullOrWhiteSpace(selected.ModelPath))
            return;

        var cameraManager = SceneCameraManager.Instance();
        if (cameraManager is null)
            return;

        var camera = cameraManager->CurrentCamera;
        if (camera is null || camera->RenderCamera is null)
            return;

        var io = ImGui.GetIO();
        if (io.DisplaySize.X <= 0 || io.DisplaySize.Y <= 0)
            return;

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(io.DisplaySize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (ImGui.Begin("Izzy's Furniture Gizmo Overlay", flags))
        {
            ImGui.SetWindowSize(io.DisplaySize);
            ImGuizmo.SetID(selected.Id.GetHashCode());
            ImGuizmo.BeginFrame();
            ImGuizmo.SetDrawlist();
            ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
            ImGuizmo.SetOrthographic(false);
            ImGuizmo.AllowAxisFlip(true);
            ImGuizmo.Enable(true);

            Matrix4x4 view = camera->ViewMatrix;
            view.M44 = 1;

            var renderCamera = camera->RenderCamera;
            Matrix4x4 projection = renderCamera->ProjectionMatrix;
            projection.M33 = -((renderCamera->FarPlane + renderCamera->NearPlane) / (renderCamera->FarPlane - renderCamera->NearPlane));
            projection.M43 = -((2.0f * renderCamera->FarPlane * renderCamera->NearPlane) / (renderCamera->FarPlane - renderCamera->NearPlane));

            this.DrawNpcPatrolPathOverlay(selected, view, projection, io.DisplaySize);

            var position = selected.Position;
            var rotation = selected.RotationDegrees;
            var scale = selected.Scale3;
            Matrix4x4 matrix = default;
            ImGuizmo.RecomposeMatrixFromComponents(ref position.X, ref rotation.X, ref scale.X, ref matrix.M11);

            if (ImGuizmo.Manipulate(
                ref view.M11,
                ref projection.M11,
                this.gizmoOperation,
                this.gizmoMode,
                ref matrix.M11))
            {
                ImGuizmo.DecomposeMatrixToComponents(ref matrix.M11, ref position.X, ref rotation.X, ref scale.X);
                selected.Position = position;
                selected.RotationDegrees = rotation;
                selected.SetScale(new Vector3(MathF.Abs(scale.X), MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
            }

            ImGuizmo.SetID(0);
        }

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawNpcPatrolPathOverlay(SpawnedFurniture selected, Matrix4x4 view, Matrix4x4 projection, Vector2 displaySize)
    {
        if (!selected.IsNpc || selected.NpcPatrolPoints.Count == 0)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var lineColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
        var pointColor = ImGui.GetColorU32(ImGuiCol.ButtonActive);
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var disabledColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        var screenPoints = selected.NpcPatrolPoints
            .Select(point => WorldToScreen(point.Position, view, projection, displaySize, out var screen)
                ? screen
                : new Vector2(float.NaN, float.NaN))
            .ToArray();

        for (var index = 0; index < screenPoints.Length - 1; index++)
            DrawPathLine(drawList, screenPoints[index], screenPoints[index + 1], lineColor);

        if (selected.NpcPatrolLoop && screenPoints.Length > 2)
            DrawPathLine(drawList, screenPoints[^1], screenPoints[0], lineColor);

        if (WorldToScreen(selected.Position, view, projection, displaySize, out var currentScreen))
        {
            drawList.AddCircleFilled(currentScreen, 6.0f, pointColor, 16);
            drawList.AddCircle(currentScreen, 8.0f, textColor, 16, 1.5f);
            drawList.AddText(currentScreen + new Vector2(10.0f, -10.0f), textColor, "NPC");
        }

        for (var index = 0; index < screenPoints.Length; index++)
        {
            var screen = screenPoints[index];
            if (float.IsNaN(screen.X) || float.IsNaN(screen.Y))
                continue;

            drawList.AddCircleFilled(screen, 5.0f, pointColor, 16);
            drawList.AddCircle(screen, 7.0f, textColor, 16, 1.25f);
            drawList.AddText(screen + new Vector2(9.0f, -9.0f), textColor, (index + 1).ToString());
        }
    }

    private static void DrawPathLine(ImDrawListPtr drawList, Vector2 a, Vector2 b, uint color)
    {
        if (float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(b.X) || float.IsNaN(b.Y))
            return;

        drawList.AddLine(a, b, color, 2.0f);
    }

    private static bool WorldToScreen(Vector3 world, Matrix4x4 view, Matrix4x4 projection, Vector2 displaySize, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1.0f), view * projection);
        if (clip.W <= 0.0001f)
        {
            screen = Vector2.Zero;
            return false;
        }

        clip /= clip.W;
        if (clip.X < -1.0f || clip.X > 1.0f || clip.Y < -1.0f || clip.Y > 1.0f || clip.Z < 0.0f || clip.Z > 1.0f)
        {
            screen = Vector2.Zero;
            return false;
        }

        screen = new Vector2(
            (clip.X + 1.0f) * displaySize.X * 0.5f,
            (1.0f - clip.Y) * displaySize.Y * 0.5f);
        return true;
    }

}
