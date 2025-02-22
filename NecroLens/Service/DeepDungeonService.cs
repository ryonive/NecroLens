﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NecroLens.Model;
using NecroLens.util;

namespace NecroLens.Service;

/**
 * Tracks the progress when inside a DeepDungeon.
 */
[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Local")]
public partial class DeepDungeonService : IDisposable
{
    private readonly Configuration conf;
    public readonly List<Pomander> floorEffects;
    private readonly Timer floorTimer;
    public readonly Dictionary<int, int> floorTimes;

    private readonly List<uint> InteractionList;
    public int currentContentId;

    public int currentFloor;
    public DeepDungeonContentInfo.DeepDungeonFloorSetInfo? floorSetInfo;
    private DateTime floorStartTime;
    private bool floorVerified;
    public bool nextFloorAffluence;
    public bool nextFloorAlteration;

    public bool nextFloorFlight;
    private bool nextFloorTransfer;

    private DateTime nextRespawn;
    public int passageProgress;

    public bool ready;

    // public int remainingKills;
    public DeepDungeonTrapStatus trapStatus;

    public DeepDungeonService()
    {
        PluginService.GameNetwork.NetworkMessage += NetworkMessage;
        floorTimes = new Dictionary<int, int>();
        floorEffects = new List<Pomander>();
        floorTimer = new Timer();
        floorTimer.Elapsed += OnTimerUpdate;
        floorTimer.Interval = 1000;
        nextFloorTransfer = false;
        ready = false;
        floorVerified = false;
        conf = PluginService.Configuration;
        InteractionList = new List<uint>();
    }

    public void Dispose()
    {
        PluginService.GameNetwork.NetworkMessage -= NetworkMessage;
    }

    [GeneratedRegex("\\d+")]
    private static partial Regex FloorNumber();

    private void EnterDeepDungeon(int contentId, DeepDungeonContentInfo.DeepDungeonFloorSetInfo info)
    {
        floorSetInfo = info;
        currentContentId = contentId;
        PluginService.PluginLog.Debug($"Entering ContentID {currentContentId} - StartFloor: {info.StartFloor}");

        currentFloor = info.StartFloor - 1; // NextFloor() adds 1
        floorTimes.Clear();

        PluginService.MobInfoService.TryReloadIfEmpty();

        for (var i = info.StartFloor; i < info.StartFloor + 10; i++)
            floorTimes[i] = 0;

        floorStartTime = DateTime.Now;
        nextRespawn = DateTime.Now.AddSeconds(info.RespawnTime);
        nextFloorTransfer = true;
        NextFloor();

        if (PluginService.Configuration.AutoOpenOnEnter)
            PluginService.Plugin.ShowMainWindow();

        InteractionList.Clear();

        floorTimer.Start();
        ready = true;
    }

    private void NextFloor()
    {
        if (nextFloorTransfer)
        {
            PluginService.PluginLog.Debug($"ContentID {currentContentId} - NextFloor: {currentFloor + 1}");

            // Reset
            floorEffects.Clear();
            InteractionList.Clear();

            // Apply effects
            if (nextFloorFlight)
                floorEffects.Add(Pomander.Flight);
            if (nextFloorAffluence)
                floorEffects.Add(Pomander.Affluence);
            if (nextFloorAlteration)
                floorEffects.Add(Pomander.Alteration);

            nextFloorFlight = false;
            nextFloorAffluence = false;
            nextFloorAlteration = false;

            floorTimes[currentFloor] = (int)(DateTime.Now - floorStartTime).TotalSeconds;

            currentFloor++;
            floorStartTime = DateTime.Now;
            trapStatus = DeepDungeonTrapStatus.Active;
            nextRespawn = DateTime.Now.AddSeconds(floorSetInfo!.RespawnTime);
            passageProgress = -1;
            nextFloorTransfer = false;
        }
    }

    [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalse")]
    private unsafe void VerifyFloorNumber()
    {
        var addon = (AtkUnitBase*)PluginService.GameGui.GetAddonByName("DeepDungeonMap");
        if (addon != null)
        {
            // Searching backwards - the first found TextNode
            for (var i = addon->UldManager.NodeListCount - 1; i >= 0; i--)
            {
                var atkResNode = addon->UldManager.NodeList[i];
                if (atkResNode == null) continue;

                var atkTextNode = atkResNode->GetAsAtkTextNode();
                if (atkTextNode == null) continue;

                var resultString = FloorNumber().Match(atkTextNode->NodeText.ToString()).Value;
                if (string.IsNullOrWhiteSpace(resultString)) continue;

                var floor = int.Parse(resultString);
                if (currentFloor != floor)
                {
                    PluginService.PluginLog.Information("Floor number mismatch - adjusting");
                    currentFloor = floor;
                }

                floorVerified = true;
            }
        }
    }

    private void ExitDeepDungeon()
    {
        PluginService.PluginLog.Debug($"ContentID {currentContentId} - Exiting");

        passageProgress = -1;
        floorTimer.Stop();
        floorSetInfo = null;
        nextFloorTransfer = false;
        ready = false;
        PluginService.Plugin.CloseMainWindow();
    }

    private void OnTimerUpdate(object? sender, ElapsedEventArgs e)
    {
        if (!InDeepDungeon())
        {
            PluginService.PluginLog.Debug("Failsafe exit");
            ExitDeepDungeon();
        }

        // If the plugin is loaded mid-dungeon then verify the floor
        if (!floorVerified)
            VerifyFloorNumber();

        var now = DateTime.Now;
        floorTimes[currentFloor] = (int)(now - floorStartTime).TotalSeconds;
        if (now > nextRespawn) nextRespawn = now.AddSeconds(floorSetInfo!.RespawnTime);
        UpdatePassageProgress();
    }

    public bool InDeepDungeon()
    {
        var mapId = PluginService.ClientState.TerritoryType;
        return DataIds.PalaceOfTheDeadMapIds.Contains(mapId) || DataIds.HeavenOnHighMapIds.Contains(mapId) ||
               DataIds.EurekaOrthosMapIds.Contains(mapId);
    }

    public void UpdatePassageProgress()
    {
        var part = GetPassagePart();
        passageProgress = part != null ? (int)(part * 10) : -1;
    }

    [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalse")]
    private unsafe ushort? GetPassagePart()
    {
        var addon = (AtkUnitBase*)PluginService.GameGui.GetAddonByName("DeepDungeonMap");
        if (addon != null)
        {
            // Searching backwards in nodelist. First is always return, second passage
            var skipReturn = false;
            for (var i = addon->UldManager.NodeListCount - 1; i >= 0; i--)
            {
                var atkResNode = addon->UldManager.NodeList[i];
                if (atkResNode == null) continue;

                var atkComponentNode = atkResNode->GetAsAtkComponentNode();
                if (atkComponentNode == null) continue;

                // Its a component with 2 child's, second is the image
                var atkImageNode = atkComponentNode->Component->UldManager.NodeListCount > 1
                                       ? atkComponentNode->Component->UldManager.NodeList[1]->GetAsAtkImageNode()
                                       : null;
                if (atkImageNode == null) continue;

                // Texture contains 11 states
                if (atkImageNode->PartsList->PartCount == 11)
                {
                    if (!skipReturn)
                    {
                        skipReturn = true;
                        continue;
                    }

                    return atkImageNode->PartId;
                }
            }
        }

        return null;
    }

    private void NetworkMessage(
        IntPtr dataPtr, ushort opCode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction)
    {
        if (direction == NetworkMessageDirection.ZoneDown)
        {
            switch (opCode)
            {
                case (int)ServerZoneIpcType.SystemLogMessage:
                    OnSystemLogMessage(dataPtr, ReadNumber(dataPtr, 4, 4));
                    break;
                case (int)ServerZoneIpcType.ActorControlSelf:
                    OnActorControlSelf(dataPtr);
                    break;
            }
        }
    }

    private void OnActorControlSelf(IntPtr dataPtr)
    {
        // OnDirectorUpdate
        if (Marshal.ReadByte(dataPtr) == DataIds.ActorControlSelfDirectorUpdate)
        {
            switch (Marshal.ReadByte(dataPtr, 8))
            {
                // OnDutyCommenced
                case DataIds.DirectorUpdateDutyCommenced:
                {
                    var contentId = ReadNumber(dataPtr, 4, 2);
                    if (!ready && DeepDungeonContentInfo.ContentInfo.TryGetValue(contentId, out var info))
                        EnterDeepDungeon(contentId, info);
                    break;
                }
                // OnDutyRecommenced
                case DataIds.DirectorUpdateDutyRecommenced:
                    if (ready && nextFloorTransfer)
                        NextFloor();
                    break;
            }
        }
    }

    private void OnSystemLogMessage(IntPtr dataPtr, int logId)
    {
        if (InDeepDungeon())
        {
            if (logId == DataIds.SystemLogPomanderUsed)
                OnPomanderUsed((Pomander)Marshal.ReadByte(dataPtr, 16));
            else if (logId == DataIds.SystemLogDutyEnded)
                ExitDeepDungeon();
            else if (logId == DataIds.SystemLogTransferenceInitiated)
                nextFloorTransfer = true;
        }
    }

    private void OnPomanderUsed(Pomander pomander)
    {
        PluginService.PluginLog.Debug($"Pomander ID: {pomander}");
        switch (pomander)
        {
            case Pomander.Safety:
            case Pomander.SafetyProtomander:
                floorEffects.Add(pomander);
                trapStatus = DeepDungeonTrapStatus.Inactive;
                break;

            case Pomander.Sight:
            case Pomander.SightProtomander:
                floorEffects.Add(pomander);
                if (trapStatus == DeepDungeonTrapStatus.Active)
                    trapStatus = DeepDungeonTrapStatus.Visible;
                break;

            case Pomander.Affluence:
            case Pomander.AffluenceProtomander:
                nextFloorAffluence = true;
                break;

            case Pomander.Flight:
            case Pomander.FlightProtomander:
                nextFloorFlight = true;
                break;

            case Pomander.Alteration:
            case Pomander.AlterationProtomander:
                nextFloorAlteration = true;
                break;

            case Pomander.Fortune:
            case Pomander.FortuneProtomander:
                floorEffects.Add(pomander);
                break;
        }
    }

    private static int ReadNumber(IntPtr dataPtr, int offset, int size)
    {
        var bytes = new byte[4];
        Marshal.Copy(dataPtr + offset, bytes, 0, size);
        return BitConverter.ToInt32(bytes);
    }

    public int TimeTillRespawn()
    {
        return (int)(DateTime.Now - nextRespawn).TotalSeconds;
    }

    public bool HasRespawn()
    {
        var mapId = PluginService.ClientState.TerritoryType;
        return !(currentFloor % 10 == 0 || (DataIds.EurekaOrthosMapIds.Contains(mapId) && currentFloor == 99));
    }

    private bool CheckChestOpenSafe(ESPObject.ESPType type)
    {
        var info = PluginService.DeepDungeonService.floorSetInfo;
        var unsafeChest = false;
        if (info != null)
        {
            unsafeChest = (info.MimicChests == DeepDungeonContentInfo.MimicChests.Silver &&
                           type == ESPObject.ESPType.SilverChest) ||
                          (info.MimicChests == DeepDungeonContentInfo.MimicChests.Gold &&
                           type == ESPObject.ESPType.GoldChest);
        }

        return !unsafeChest || (unsafeChest && conf.OpenUnsafeChests);
    }

    internal unsafe void TryInteract(ESPObject espObj)
    {
        var player = PluginService.ClientState.LocalPlayer!;
        if ((player.StatusFlags & StatusFlags.InCombat) == 0 && conf.OpenChests && espObj.IsChest())
        {
            var type = espObj.Type;

            if (!conf.OpenBronzeCoffers && type == ESPObject.ESPType.BronzeChest) return;
            if (!conf.OpenSilverCoffers && type == ESPObject.ESPType.SilverChest) return;
            if (!conf.OpenGoldCoffers && type == ESPObject.ESPType.GoldChest) return;
            if (!conf.OpenHoards && type == ESPObject.ESPType.AccursedHoardCoffer) return;

            // We dont want to kill the player
            if (type == ESPObject.ESPType.SilverChest && player.CurrentHp <= player.MaxHp * 0.77) return;

            if (CheckChestOpenSafe(type) && espObj.Distance() <= espObj.InteractionDistance()
                                         && !InteractionList.Contains(espObj.GameObject.ObjectId))
            {
                TargetSystem.Instance()->InteractWithObject((GameObject*)espObj.GameObject.Address);
                InteractionList.Add(espObj.GameObject.ObjectId);
            }
        }
    }

    public unsafe void TryNearestOpenChest()
    {
        // Checks every object to be a chest and try to open the  
        foreach (var obj in PluginService.ObjectTable)
            if (obj.IsValid())
            {
                var dataId = obj.DataId;
                if (DataIds.BronzeChestIDs.Contains(dataId) || DataIds.SilverChest == dataId ||
                    DataIds.GoldChest == dataId || DataIds.AccursedHoardCoffer == dataId)
                {
                    var espObj = new ESPObject(obj);
                    if (CheckChestOpenSafe(espObj.Type) && espObj.Distance() <= espObj.InteractionDistance())
                    {
                        TargetSystem.Instance()->InteractWithObject((GameObject*)espObj.GameObject.Address);
                        break;
                    }
                }
            }
    }
}
