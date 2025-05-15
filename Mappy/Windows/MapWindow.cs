﻿using System.Drawing;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ImGuiNET;
using KamiLib.Classes;
using KamiLib.CommandManager;
using KamiLib.Extensions;
using KamiLib.Window;
using Lumina.Excel.Sheets;
using Mappy.Classes.MapWindowComponents;
using Mappy.Controllers;
using Mappy.Data;
using Map = Lumina.Excel.Sheets.Map;

namespace Mappy.Windows;

public class MapWindow : Window {
    public Vector2 MapDrawOffset { get; private set; }
    public bool IsMapHovered { get; private set; }
    public bool ProcessingCommand { get; set; }

    private bool isMapItemHovered;
    private bool isDragStarted;
    private Vector2 lastWindowSize;
    private uint lastMapId;
    private uint lastAreaPlaceNameId;
    private uint lastSubAreaPlaceNameId;

    private readonly MapToolbar mapToolbar = new();
    private readonly MapCoordinateBar mapCoordinateBar = new();
    private readonly MapContextMenu mapContextMenu = new();

    public MapWindow() : base("###MappyMapWindow", new Vector2(100.0f, 100.0f)) {
        UpdateTitle();

        DisableWindowSounds = true;
        RegisterCommands();

        // Mirroring behavior doesn't let the close button work, so, remove it.
        ShowCloseButton = false;
    }

    public override bool DrawConditions()
        => IntegrationsController.ShouldShowMap();

    public override unsafe void PreOpenCheck() {
        IsOpen = AgentMap.Instance()->IsAgentActive() || System.SystemConfig.KeepOpen;

        if (Service.ClientState is { IsLoggedIn: false } or { IsPvP: true }) IsOpen = false;
    }
    
    public override unsafe void OnOpen() {
        if (!AgentMap.Instance()->IsAgentActive()) {
            AgentMap.Instance()->Show();
        }
        
        YeetVanillaMap();

        if (ProcessingCommand) {
            ProcessingCommand = false;
            System.SystemConfig.FollowPlayer = false;
            return;
        }
        
        if (System.SystemConfig.FollowOnOpen) {
            System.IntegrationsController.OpenOccupiedMap();
            System.SystemConfig.FollowPlayer = true;
        }

        switch (System.SystemConfig.CenterOnOpen) {
            case CenterTarget.Player when Service.ClientState.LocalPlayer is {} localPlayer:
                System.MapRenderer.CenterOnGameObject(localPlayer);
                break;

            case CenterTarget.Map:
                System.SystemConfig.FollowPlayer = false;
                System.MapRenderer.DrawOffset = Vector2.Zero;
                break;

            case CenterTarget.Disabled:
            default:
                break;
        }
    }

    protected override void DrawContents() {
        UpdateTitle();
        UpdateStyle();
        UpdateSizePosition();
        IsMapHovered = WindowBounds.IsBoundedBy(ImGui.GetMousePos(), ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + ImGui.GetContentRegionMax());
        isMapItemHovered = false;
        
        MapDrawOffset = ImGui.GetCursorScreenPos();
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, System.SystemConfig.FadePercent,  ShouldFade());
        using (var renderChild = ImRaii.Child("render_child", ImGui.GetContentRegionAvail(), false, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar)) {
            if (!renderChild) return;
            if (!System.SystemConfig.AcceptedSpoilerWarning) {
                DrawSpoilerWarning();
                return;
            }
            
            System.MapRenderer.Draw();
            ImGui.SetCursorPos(Vector2.Zero);

            if (ShouldShowToolbar()) {
                mapToolbar.Draw();
            }
            
            isMapItemHovered |= ImGui.IsItemHovered();
            
            if (!System.SystemConfig.ShowCoordinateBar) {
                mapCoordinateBar.Draw(isMapItemHovered, MapDrawOffset);
            }
            isMapItemHovered |= ImGui.IsItemHovered();
        }
        isMapItemHovered |= ImGui.IsItemHovered();
        
        // Process Inputs
        ProcessInputs();
    }

    private bool ShouldShowToolbar() {
        if (isDragStarted) return false;
        if (System.SystemConfig.ShowToolbarOnHover && IsMapHovered) return true;
        if (System.SystemConfig.AlwaysShowToolbar) return true;

        return false;
    }
    
    private unsafe void UpdateTitle() {
        var mapChanged = lastMapId != AgentMap.Instance()->SelectedMapId;
        var areaChanged = lastAreaPlaceNameId != TerritoryInfo.Instance()->AreaPlaceNameId;
        var subAreaChanged = lastSubAreaPlaceNameId != TerritoryInfo.Instance()->SubAreaPlaceNameId;
        var locationChanged = mapChanged || areaChanged || subAreaChanged;

        if (!locationChanged) return;
        var subLocationString = string.Empty;
        var mapData = Service.DataManager.GetExcelSheet<Map>().GetRow(AgentMap.Instance()->SelectedMapId);

        if (System.SystemConfig.ShowRegionLabel) {
            var mapRegionName = mapData.PlaceNameRegion.Value.Name.ExtractText();
            subLocationString += $" - {mapRegionName}";
        }

        if (System.SystemConfig.ShowMapLabel) {
            var mapName = mapData.PlaceName.Value.Name.ExtractText();
            subLocationString += $" - {mapName}";
        }

        // Don't show specific locations if we aren't there.
        if (AgentMap.Instance()->SelectedMapId == AgentMap.Instance()->CurrentMapId) {
            if (TerritoryInfo.Instance()->AreaPlaceNameId is not 0 && System.SystemConfig.ShowAreaLabel) {
                var areaLabel = Service.DataManager.GetExcelSheet<PlaceName>().GetRow(TerritoryInfo.Instance()->AreaPlaceNameId);
                subLocationString += $" - {areaLabel.Name}";
            }

            if (TerritoryInfo.Instance()->SubAreaPlaceNameId is not 0 && System.SystemConfig.ShowSubAreaLabel) {
                var subAreaLabel = Service.DataManager.GetExcelSheet<PlaceName>().GetRow(TerritoryInfo.Instance()->SubAreaPlaceNameId);
                subLocationString += $" - {subAreaLabel.Name}";
            }
        }

        WindowName = $"Mappy Map Window{subLocationString}###MappyMapWindow";
        
        lastMapId = AgentMap.Instance()->SelectedMapId;
        lastAreaPlaceNameId = TerritoryInfo.Instance()->AreaPlaceNameId;
        lastSubAreaPlaceNameId = TerritoryInfo.Instance()->SubAreaPlaceNameId;
    }

    public void RefreshTitle() {
        lastMapId = 0;
        lastAreaPlaceNameId = 0;
        lastSubAreaPlaceNameId = 0;
    }

    private void ProcessInputs() {
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
            ImGui.OpenPopup("Mappy_Context_Menu");
        }
        else {
            if (isMapItemHovered) {
                if (System.SystemConfig.EnableShiftDragMove && ImGui.GetIO().KeyShift) {
                    Flags &= ~ImGuiWindowFlags.NoMove;
                }
                else {
                    ProcessMouseScroll();
                    ProcessMapDragStart();
                    Flags |= ImGuiWindowFlags.NoMove;
                }
            }
            
            ProcessMapDragDragging();
            ProcessMapDragEnd();
        }

        // Draw Context Menu
        mapContextMenu.Draw(MapDrawOffset);
    }
    
    private unsafe void UpdateStyle() {
        if (System.SystemConfig.HideWindowFrame) {
            Flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground;
        }
        else {
            Flags &= ~(ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground);
        }

        if (System.SystemConfig.LockWindow) {
            Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        }
        else {
            Flags &= ~(ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
        }

        if (System.SystemConfig.NoFocusOnAppear) {
            Flags |= ImGuiWindowFlags.NoFocusOnAppearing;
        }
        else {
            Flags &= ~ImGuiWindowFlags.NoFocusOnAppearing;
        }

        if (Service.KeyState[VirtualKey.ESCAPE] && IsFocused && !IsMapLocked()) {
            AgentMap.Instance()->Hide();
        }
        
        YeetVanillaMap();
        
        if (System.SystemConfig.FollowPlayer && Service.ClientState is { LocalPlayer: {} localPlayer}) {
            System.MapRenderer.CenterOnGameObject(localPlayer);
        }
        
        if (System.SystemConfig.LockCenterOnMap) {
            System.SystemConfig.FollowPlayer = false;
            System.MapRenderer.DrawOffset = Vector2.Zero;
        }
    }

    private void UpdateSizePosition() {
        var systemConfig = System.SystemConfig;
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();

        if (!IsFocused) {
            if (windowPosition != systemConfig.WindowPosition) {
                ImGui.SetWindowPos(systemConfig.WindowPosition);
            }

            if (windowSize != systemConfig.WindowSize) {
                ImGui.SetWindowSize(systemConfig.WindowSize);
            }
        }
        else { // If focused
            if (systemConfig.WindowPosition != windowPosition) {
                systemConfig.WindowPosition = windowPosition;
                SystemConfig.Save();
            }

            if (systemConfig.WindowSize != windowSize) {
                systemConfig.WindowSize = windowSize;
                SystemConfig.Save();
            }
        }
    }
    
    private static void DrawSpoilerWarning() {
        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Orange.Vector())) {
            const string warningLine1 = "Warning, Mappy does not protect you from spoilers and will show everything.";
            const string warningLine2 = "Do not use Mappy if you are not comfortable with this.";

            ImGui.SetCursorPos(ImGui.GetContentRegionAvail() / 2.0f - (ImGui.CalcTextSize(warningLine1) * 2.0f) with { X = 0.0f });
            ImGuiHelpers.CenteredText(warningLine1);
            ImGuiHelpers.CenteredText(warningLine2);
        }
            
        ImGuiHelpers.ScaledDummy(30.0f);
        ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X / 3.0f);
        using (ImRaii.Disabled(!(ImGui.GetIO().KeyShift && ImGui.GetIO().KeyCtrl))) {
            if (ImGui.Button("I understand", new Vector2(ImGui.GetContentRegionAvail().X / 2.0f, 23.0f * ImGuiHelpers.GlobalScale))) {
                System.SystemConfig.AcceptedSpoilerWarning = true;
                SystemConfig.Save();
            }
                
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1.0f)) {
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                    ImGui.SetTooltip("Hold Shift + Control while clicking activate button");
                }
            }
        }
    }
    
    public override void OnClose() {
        UnYeetVanillaMap();
        
        SystemConfig.Save();
    }

    private static void ProcessMouseScroll() {
        if (System.SystemConfig.ZoomLocked) return;
        if (ImGui.GetIO().MouseWheel is 0) return;
        
        if (System.SystemConfig.UseLinearZoom) {
            MapRenderer.MapRenderer.Scale += System.SystemConfig.ZoomSpeed * ImGui.GetIO().MouseWheel;
        }
        else {
            MapRenderer.MapRenderer.Scale *= 1.0f + System.SystemConfig.ZoomSpeed * ImGui.GetIO().MouseWheel;
        }
    }
    
    private void ProcessMapDragDragging() {
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && isDragStarted) {
            System.MapRenderer.DrawOffset += ImGui.GetMouseDragDelta() / MapRenderer.MapRenderer.Scale;
            ImGui.ResetMouseDragDelta();
        }
    }
    
    private void ProcessMapDragEnd() {
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
            isDragStarted = false;
        }
    }
    
    private void ProcessMapDragStart() {
        // Don't allow a drag to start if the window size is changing
        if (ImGui.GetWindowSize() == lastWindowSize) {
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !isDragStarted) {
                isDragStarted = true;
                System.SystemConfig.FollowPlayer = false;
            }
        } else {
            lastWindowSize = ImGui.GetWindowSize();
            isDragStarted = false;
        }
    }
    
    private unsafe bool ShouldFade() 
        => System.SystemConfig.FadeMode.HasFlag(FadeMode.Always) ||
           System.SystemConfig.FadeMode.HasFlag(FadeMode.WhenFocused) && IsFocused ||
           System.SystemConfig.FadeMode.HasFlag(FadeMode.WhenMoving) && AgentMap.Instance()->IsPlayerMoving ||
           System.SystemConfig.FadeMode.HasFlag(FadeMode.WhenUnFocused) && !IsFocused;

    private unsafe void YeetVanillaMap() {
        var addon = Service.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon is null || addon->RootNode is null) return;
        
        addon->RootNode->SetPositionFloat(-9001.0f, -9001.0f);
        addon->RootNode->ToggleVisibility(false);
    }
    
    private unsafe void UnYeetVanillaMap() {
        var addon = Service.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon is null || addon->RootNode is null) return;
        
        AgentMap.Instance()->Hide();
        addon->RootNode->SetPositionFloat(addon->X, addon->Y);
        addon->RootNode->ToggleVisibility(false);
        Service.Framework.RunOnTick(() => addon->RootNode->ToggleVisibility(true), delayTicks: 10);
    }
    
    private void RegisterCommands() {
        System.CommandManager.RegisterCommand(new ToggleCommandHandler {
            UseShowHideText = true,
            BaseActivationPath = "/map",
            EnableDelegate = _ => System.MapWindow.UnCollapseOrShow(),
            DisableDelegate = _ => System.MapWindow.Close(),
            ToggleDelegate = _ => System.MapWindow.UnCollapseOrToggle(),
        });
        
        System.CommandManager.RegisterCommand(new CommandHandler {
            ActivationPath = "/map/follow",
            Delegate = _ => {
                System.SystemConfig.FollowPlayer = true;
                SystemConfig.Save();
            },
        });
        
        System.CommandManager.RegisterCommand(new CommandHandler {
            ActivationPath = "/map/unfollow",
            Delegate = _ => {
                System.SystemConfig.FollowPlayer = false;
                SystemConfig.Save();
            },
        });
        
        System.CommandManager.RegisterCommand(new ToggleCommandHandler {
            BaseActivationPath = "/autofollow",
            EnableDelegate = _ => {
                System.SystemConfig.FollowOnOpen = true;
                SystemConfig.Save();
            },
            DisableDelegate = _ => {
                System.SystemConfig.FollowOnOpen = false;
                SystemConfig.Save();
            },
            ToggleDelegate = _ => {
                System.SystemConfig.FollowOnOpen = !System.SystemConfig.FollowOnOpen;
                SystemConfig.Save();
            },
        });
        
        System.CommandManager.RegisterCommand(new ToggleCommandHandler {
            BaseActivationPath = "/keepopen",
            EnableDelegate = _ => {
                System.SystemConfig.KeepOpen = true;
                SystemConfig.Save();
            },
            DisableDelegate = _ => {
                System.SystemConfig.KeepOpen = false;
                SystemConfig.Save();
            },
            ToggleDelegate = _ => {
                System.SystemConfig.KeepOpen = !System.SystemConfig.KeepOpen;
                SystemConfig.Save();
            },
        });
        
        System.CommandManager.RegisterCommand(new CommandHandler {
            ActivationPath = "/center/player",
            Delegate = _ => {
                if (Service.ClientState.LocalPlayer is { } localPlayer) {
                    System.MapRenderer.CenterOnGameObject(localPlayer);
                }
            },
        });
        
        System.CommandManager.RegisterCommand(new CommandHandler {
            ActivationPath = "/center/map",
            Delegate = _ => {
                System.SystemConfig.FollowPlayer = false;
                System.MapRenderer.DrawOffset = Vector2.Zero;
            },
        });
    }

    private unsafe bool IsMapLocked() {
        var addon = Service.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon is null || addon->RootNode is null) return false;

        return (addon->Param & 0x8_0000) > 0;
    }
}
