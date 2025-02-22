﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ImGuiNET;
using NecroLens.Model;
using NecroLens.util;

namespace NecroLens.Service;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class ESPService : IDisposable
{
    private const ushort Tick = 250;
    private readonly Configuration conf;

    private readonly List<ESPObject> mapObjects;
    private readonly Task mapScanner;
    private bool active;

    public ESPService()
    {
        PluginService.PluginLog.Debug("ESP Service loading...");

        mapObjects = new List<ESPObject>();
        conf = PluginService.Configuration;

        active = true;

        PluginService.PluginInterface.UiBuilder.Draw += OnUpdate;
        PluginService.ClientState.TerritoryChanged += OnCleanup;

        // Enable Scanner
        mapScanner = Task.Run(MapScanner);
    }


    public void Dispose()
    {
        PluginService.PluginInterface.UiBuilder.Draw -= OnUpdate;
        PluginService.ClientState.TerritoryChanged -= OnCleanup;
        active = false;
        while (!mapScanner.IsCompleted) PluginService.PluginLog.Debug("wait till scanner is stopped...");
        mapObjects.Clear();
        PluginService.PluginLog.Information("ESP Service unloaded");
    }


    /**
     * Clears the drawable GameObjects on MapChange.
     */
    private void OnCleanup(ushort e)
    {
        Monitor.Enter(mapObjects);
        mapObjects.Clear();
        Monitor.Exit(mapObjects);
    }

    /**
     * Main-Drawing method.
     */
    private void OnUpdate()
    {
        if (ShouldDraw())
        {
            if (!Monitor.TryEnter(mapObjects)) return;

            var drawList = ImGui.GetBackgroundDrawList();
            foreach (var gameObject in mapObjects) DrawEspObject(drawList, gameObject);

            Monitor.Exit(mapObjects);
        }
    }

    private bool DoDrawName(ESPObject espObject)
    {
        return espObject.Type switch
        {
            ESPObject.ESPType.Player => false,
            ESPObject.ESPType.Enemy => !espObject.InCombat(),
            ESPObject.ESPType.Mimic => !espObject.InCombat(),
            ESPObject.ESPType.FriendlyEnemy => !espObject.InCombat(),
            ESPObject.ESPType.BronzeChest => conf.ShowBronzeCoffers,
            ESPObject.ESPType.SilverChest => conf.ShowSilverCoffers,
            ESPObject.ESPType.GoldChest => conf.ShowGoldCoffers,
            ESPObject.ESPType.AccursedHoard => conf.ShowHoards,
            ESPObject.ESPType.MimicChest => conf.ShowMimicCoffer,
            ESPObject.ESPType.Trap => conf.ShowTraps,
            ESPObject.ESPType.Return => conf.ShowReturn,
            ESPObject.ESPType.Passage => conf.ShowPassage,
            _ => false
        };
    }

    /**
     * Draws every Object for the ESP-Overlay.
     */
    private void DrawEspObject(ImDrawListPtr drawList, ESPObject espObject)
    {
        var type = espObject.Type;
        var onScreen = PluginService.GameGui.WorldToScreen(espObject.GameObject.Position, out var position2D);
        if (onScreen)
        {
            var distance = espObject.Distance();
            
            if (conf.ShowPlayerDot && type == ESPObject.ESPType.Player)
                ESPUtils.DrawPlayerDot(drawList, position2D);

            if (DoDrawName(espObject))
                ESPUtils.DrawName(drawList, espObject, position2D);

            if (espObject.Type == ESPObject.ESPType.AccursedHoard && conf.ShowHoards)
            {
                var chestRadius = type == ESPObject.ESPType.AccursedHoard ? 2.0f : 1f; // Make Hoards bigger

                if (distance <= 35 && conf.HighlightCoffers)
                    ESPUtils.DrawCircleFilled(drawList, espObject, chestRadius, espObject.RenderColor(), 1f);
            }
            
            if (espObject.IsChest())
            {
                if (!conf.ShowBronzeCoffers && type == ESPObject.ESPType.BronzeChest) return;
                if (!conf.ShowSilverCoffers && type == ESPObject.ESPType.SilverChest) return;
                if (!conf.ShowGoldCoffers && type == ESPObject.ESPType.GoldChest) return;
                if (!conf.ShowHoards && type == ESPObject.ESPType.AccursedHoardCoffer) return;
                
                if (distance <= 35 && conf.HighlightCoffers)
                    ESPUtils.DrawCircleFilled(drawList, espObject, 1f, espObject.RenderColor(), 1f);
                if (distance <= 10 && conf.ShowCofferInteractionRange)
                    ESPUtils.DrawInteractionCircle(drawList, espObject, espObject.InteractionDistance());
            }

            if (conf.ShowTraps && type == ESPObject.ESPType.Trap)
                ESPUtils.DrawCircleFilled(drawList, espObject, 1.7f, espObject.RenderColor());

            if (conf.ShowMimicCoffer && type == ESPObject.ESPType.MimicChest)
                ESPUtils.DrawCircleFilled(drawList, espObject, 1f, espObject.RenderColor());

            if (conf.HighlightPassage && type == ESPObject.ESPType.Passage)
                ESPUtils.DrawCircleFilled(drawList, espObject, 2f, espObject.RenderColor());
        }

        if (PluginService.Configuration.ShowMobViews && (type == ESPObject.ESPType.Enemy || type == ESPObject.ESPType.Mimic) &&
            BattleNpcSubKind.Enemy.Equals((BattleNpcSubKind)espObject.GameObject.SubKind) &&
            !espObject.InCombat())
        {
            if (conf.ShowPatrolArrow && espObject.IsPatrol())
                ESPUtils.DrawFacingDirectionArrow(drawList, espObject, Color.Red.ToUint(), 0.6f);

            if (espObject.Distance() <= 50)
            {
                switch (espObject.AggroType())
                {
                    case ESPObject.ESPAggroType.Proximity:
                        ESPUtils.DrawCircle(drawList, espObject, espObject.AggroDistance(),
                                            conf.NormalAggroColor, ESPUtils.DefaultFilledOpacity);
                        break;
                    case ESPObject.ESPAggroType.Sound:
                        ESPUtils.DrawCircle(drawList, espObject, espObject.AggroDistance(),
                                            conf.SoundAggroColor, ESPUtils.DefaultFilledOpacity);
                        ESPUtils.DrawCircleFilled(drawList, espObject, espObject.GameObject.HitboxRadius,
                                                  conf.SoundAggroColor, ESPUtils.DefaultFilledOpacity);
                        break;
                    case ESPObject.ESPAggroType.Sight:
                        ESPUtils.DrawConeFromCenterPoint(drawList, espObject, espObject.SightRadian,
                                                         espObject.AggroDistance(), conf.NormalAggroColor);
                        break;
                    default:
                        PluginService.PluginLog.Error(
                            $"Unable to process AggroType {espObject.AggroType().ToString()}");
                        break;
                }
            }
        }
    }

    /**
     * Method returns true if the ESP is Enabled, In valid state and in DeepDungeon
     */
    private bool ShouldDraw()
    {
        return PluginService.Configuration.EnableESP &&
               !(PluginService.Condition[ConditionFlag.LoggingOut] ||
                 PluginService.Condition[ConditionFlag.BetweenAreas] ||
                 PluginService.Condition[ConditionFlag.BetweenAreas51]) &&
               PluginService.DeepDungeonService.InDeepDungeon() && PluginService.ClientState.LocalPlayer != null &&
               PluginService.ClientState.LocalContentId > 0 && PluginService.ObjectTable.Length > 0;
    }

    /**
     * Not-Drawing Scanner method updating mapObjects every Tick.
     */
    private void MapScanner()
    {
        PluginService.PluginLog.Debug("ESP Background scan started");
        // Keep scanner alive till Dispose()
        while (active)
        {
            try
            {
                if (ShouldDraw())
                {
                    var entityList = new List<ESPObject>();
                    foreach (var obj in PluginService.ObjectTable)
                    {
                        // Ignore every player object
                        if (obj.IsValid() && !ESPUtils.IsIgnoredObject(obj))
                        {
                            MobInfo mobInfo = null!;
                            if (obj is BattleNpc npcObj)
                                PluginService.MobInfoService.MobInfoDictionary.TryGetValue(npcObj.NameId, out mobInfo!);

                            var espObj = new ESPObject(obj, mobInfo);

                            PluginService.DeepDungeonService.TryInteract(espObj);

                            entityList.Add(espObj);
                        }

                        if (PluginService.ClientState.LocalPlayer != null &&
                            PluginService.ClientState.LocalPlayer.ObjectId == obj.ObjectId)
                            entityList.Add(new ESPObject(obj));
                    }

                    Monitor.Enter(mapObjects);
                    mapObjects.Clear();
                    mapObjects.AddRange(entityList);
                    Monitor.Exit(mapObjects);
                }
            }
            catch (Exception e)
            {
                PluginService.PluginLog.Error(e.ToString());
            }

            Thread.Sleep(Tick);
        }
    }
}
