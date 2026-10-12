using System;
using System.Collections.Generic;
using HarmonyLib;
using Silica;
using Silica.AI;
using SilicaAdminMod;
using UnityEngine;

namespace SiPlayerRanks
{
    // Absolute FPS restrictions, independent of Formation caps and admin overrides.
    // Native Harvester includes both human harvesters and the alien Shrimp.
    internal static class HarvesterRestrictions
    {
        private static readonly Dictionary<ulong,float> nextNotice=new();
        private static readonly Dictionary<ulong,float> nextResync=new();
        private static bool Server=>Game.GetIsServer()&&!Game.GetIsClient();
        internal static bool IsForbidden(ObjectInfo info)=>info&&info.UnitType==UnitType.Harvester;
        private static bool IsForbidden(Unit unit)=>unit&&IsForbidden(unit.ObjectInfo);

        internal static void Bind()
        {
            Event_Units.OnRequestEnterUnit-=EnterUnit;
            Event_Units.OnRequestEnterUnit+=EnterUnit;
        }

        internal static void Unbind()
        {
            Event_Units.OnRequestEnterUnit-=EnterUnit;
            nextNotice.Clear();nextResync.Clear();
        }

        private static void Deny(Player player,bool resync=false)
        {
            if(!player)return;
            ulong id=player.PlayerID.SteamID.m_SteamID;
            if(id==0)return;
            float now=Time.realtimeSinceStartup;
            try
            {
                if(!nextNotice.TryGetValue(id,out float notice)||now>=notice)
                {
                    nextNotice[id]=now+2f;
                    HelperMethods.SendChatMessageToPlayer(player,"[Ranks] ","Harvesters and Shrimp cannot be controlled or recruited by FPS players.");
                }
                // The requesting client may have predicted direct control locally.
                // Restore its authoritative unit list without changing server ownership.
                if(resync&&(!nextResync.TryGetValue(id,out float sync)||now>=sync))
                {
                    nextResync[id]=now+.25f;
                    NetworkLayer.SendPlayerUnitList(player);
                }
            }
            catch(Exception e){RankLog.Error("Harvester restriction notification failed: "+e.Message);}
        }

        private static void EnterUnit(object? sender,OnRequestEnterUnitArgs args)
        {
            // Alien takeover and human driver's boarding assign ControlledUnit
            // directly even when SendPlayerSelectUnit is denied. Block before that.
            if(!Server||args==null||!args.AsDriver||!IsForbidden(args.Unit))return;
            args.Block=true;
            Deny(args.Player);
        }

        [HarmonyPatch(typeof(Player),nameof(Player.GetCanSwitchToUnit),new[]{typeof(Unit)})]
        private static class DirectControl
        {
            [HarmonyPrefix,HarmonyPriority(Priority.First)]
            private static bool Prefix(Player __instance,Unit __0,ref bool __result)
            {
                if(!Server||!__instance||!IsForbidden(__0))return true;
                __result=false;Deny(__instance,true);return false;
            }
        }

        // Final guard for admin commands or other mods that assign control
        // directly instead of using the network/boarding authorization paths.
        [HarmonyPatch(typeof(Player),nameof(Player.ControlledUnit),MethodType.Setter)]
        private static class AssignControl
        {
            [HarmonyPrefix,HarmonyPriority(Priority.First)]
            private static bool Prefix(Player __instance,Unit __0)
            {
                if(!Server||!__instance||!IsForbidden(__0))return true;
                Deny(__instance,true);return false;
            }
        }

        [HarmonyPatch(typeof(FPSCommanding),"InviteToGroupServer",new[]{typeof(Player),typeof(Target)})]
        private static class Invite
        {
            [HarmonyPrefix,HarmonyPriority(Priority.First)]
            private static bool Prefix(Player __0,Target __1)
            {
                if(!Server||!__0||!__1||!IsForbidden(__1.OwnerUnit))return true;
                Deny(__0);return false;
            }
        }

        [HarmonyPatch(typeof(AIGroup),nameof(AIGroup.AddUnit),new[]{typeof(Unit)})]
        private static class Recruit
        {
            [HarmonyPrefix,HarmonyPriority(Priority.First)]
            private static bool Prefix(AIGroup __instance,Unit __0,ref bool __result)
            {
                if(!Server||!IsForbidden(__0)||__0.PlayerControlled||
                    (__0.Driver&&__0.Driver.ControlledBy)||!__instance.Leader||
                    !__instance.Leader.ControlledBy||__instance.Units.Contains(__0))return true;
                Player player=__instance.Leader.ControlledBy;
                // RTS AI groups have no FPS player leader; preserve them and the
                // leader/driver's own group membership during control changes.
                if(player.Group!=__instance||player.ControlledUnit==__0)return true;
                __result=false;Deny(player);return false;
            }
        }
    }
}
