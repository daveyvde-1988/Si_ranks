using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using SilicaAdminMod;
using UnityEngine;

[assembly: MelonInfo(typeof(SiPlayerRanks.RankMod), "Silica Field Ranks", "1.2.1", "Local")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod", "Si_Formation")]

namespace SiPlayerRanks
{
    public sealed class RankMod : MelonMod
    {
        private const string LifetimeKey="SilicaFieldRanks.ProcessLifetime.v1";
        private static RankEngine? engine;
        private static RankConfig config=null!;
        private static readonly FormationAdapter formation=new();
        private static readonly Dictionary<long,Player> playerInstances=new();
        private static bool active,awaitNewRound;
        private static bool confirmedRoundStart,startingCommandersPending;
        private static int modeId;
        private static float startTime,lastGameTime,nextPoll,nextRoster;
        private static string initializationError="";

        public override void OnInitializeMelon()
        {
            RankLog.Enabled=false;initializationError="";
            try
            {
                string directory=Path.Combine(MelonEnvironment.UserDataDirectory,"Ranks_cfg");
                string path=Path.Combine(directory,"Si_Ranks_Config.json");
                if(File.Exists(path))
                    config=JsonConvert.DeserializeObject<RankConfig>(File.ReadAllText(path))??throw new InvalidDataException("Empty configuration.");
                else
                {
                    config=new RankConfig();config.Validate();Directory.CreateDirectory(directory);
                    using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    using var writer=new StreamWriter(stream);
                    writer.Write(JsonConvert.SerializeObject(config,Formatting.Indented));
                }
                RankLog.Enabled=config.Debug;
                config.Validate();formation.Configure(config);engine=new RankEngine(config);
                engine.Message+=(id,message)=>Tell(id,message);
                engine.PolicyChanged+=formation.Update;
                // No verified persistent game round GUID exists. Never restore unprovable reload state.
                awaitNewRound=AppDomain.CurrentDomain.GetData(LifetimeKey)!=null;
                AppDomain.CurrentDomain.SetData(LifetimeKey,true);
                if(awaitNewRound)RankLog.Warning("Reload detected: ranks suspended until the next genuine round-start event; old round state will not leak.");
                RankLog.Info("Personal rewards use temporary source-specific KoH patches. FPS limits include the player: 1 means no AI followers.");
            }
            catch(Exception e)
            {engine=null;initializationError="Ranks disabled: configuration load failed. Existing file preserved. "+e.Message;RankLog.Error(initializationError);}
        }
        public override void OnLateInitializeMelon(){formation.Bind();if(engine!=null)RewardPatches.Bind(HarmonyInstance);Bind();}
        internal static decimal RewardPolicy(long id,RewardKind kind)=>Ready?engine!.Policy(id).Multiplier(kind):1m;
        public override void OnSceneWasLoaded(int index,string name)=>Bind();
        public override void OnSceneWasInitialized(int index,string name)=>Bind();
        private static void Bind()
        {
            if(engine==null)return;
            GameEvents.OnGameStarted-=Started;GameEvents.OnGameStarted+=Started;
            GameEvents.OnGameEnded-=Ended;GameEvents.OnGameEnded+=Ended;
            GameEvents.OnGameRestart-=Restarted;GameEvents.OnGameRestart+=Restarted;
            GameEvents.OnCommanderChanged-=CommanderChanged;GameEvents.OnCommanderChanged+=CommanderChanged;
            GameEvents.OnPlayerChangedTeam-=TeamChanged;GameEvents.OnPlayerChangedTeam+=TeamChanged;
            GameEvents.OnPlayerLeft-=Left;GameEvents.OnPlayerLeft+=Left;
            GameEvents.OnPlayerDeleted-=Left;GameEvents.OnPlayerDeleted+=Left;
        }
        private static bool Server=>Game.GetIsServer()&&!Game.GetIsClient();
        private static bool Ready=>engine!=null&&active&&!awaitNewRound&&Server;
        private static long Id(Player p)=>unchecked((long)p.PlayerID.SteamID.m_SteamID);
        private static void Tell(long id,string message)
        {
            var p=Player.Players.FirstOrDefault(p=>p&&Id(p)==id);
            if(p)HelperMethods.SendChatMessageToPlayer(p,"[Ranks] ",message);
        }
        private static string TeamLabel(Team t)
        {
            switch(t.name){case "Team_Alien":return "Alien";case "Team_Human_Centauri":return "Centauri";case "Team_Human_Sol":return "Sol";default:return t.TeamShortName;}
        }
        private static void Observe(Player p,bool promote=false,bool readScore=false)
        {
            if(!Ready||!p||Id(p)==0)return;
            long id=Id(p);engine!.Round.Players.TryGetValue(id,out var prior);
            if(prior!=null&&playerInstances.TryGetValue(id,out var previousInstance)&&!ReferenceEquals(previousInstance,p))
            {
                engine.RebindScore(id,p.Score);readScore=true;
            }
            playerInstances[id]=p;
            // Team can be temporarily null during reconnect or a leave/join transition.
            int team=p.Team?p.Team.Index:prior?.Team??-1;
            string label=p.Team?TeamLabel(p.Team):prior?.TeamName??"No team";
            engine.Observe(id,p.PlayerName,team,label,readScore?p.Score:prior?.Score??0,true,promote);
        }
        private static void Started(GameMode mode)
        {
            // Repeated notifications for the same running round must not award twice.
            if(confirmedRoundStart&&Ready&&mode.GetInstanceID()==modeId&&mode.GameTime+0.1f>=lastGameTime)return;
            StartRound(mode,true);
        }
        private static void StartRound(GameMode mode,bool announceStartingRank)
        {
            if(engine==null||!Server)return;
            formation.ClearAll();engine.Reset();formation.ForgetApplications();playerInstances.Clear();
            awaitNewRound=false;active=true;modeId=mode.GetInstanceID();
            confirmedRoundStart=announceStartingRank;startingCommandersPending=announceStartingRank;
            startTime=Time.realtimeSinceStartup;lastGameTime=mode.GameTime;
            nextPoll=startTime+(float)config.PollSeconds;nextRoster=0;
            foreach(var p in Player.Players.Where(p=>p).OrderBy(Id))
            {
                Observe(p);
            }
            RefreshCommanders(!announceStartingRank);
            // Strategy's round-start event runs BEFORE its initial commander lottery.
            // The native lottery postfix finalizes only this pending genuine start.
            if(announceStartingRank&&!(mode is MP_Strategy))FinishStartingCommanders(mode);
            RankLog.Info("Round started: ranks, strikes, cooldowns, recovery, queues, seats and commander roles reset.");
        }
        private static void FinishStartingCommanders(GameMode mode)
        {
            if(!Ready||!startingCommandersPending||mode.GetInstanceID()!=modeId)return;
            // Consume first: duplicate callbacks/reentrancy cannot grant a second award.
            startingCommandersPending=false;
            RefreshRoster(false,false);RefreshCommanders(false);
            foreach(long id in engine!.Round.Commanders.Values.Distinct().ToArray())
            {
                var p=Player.Players.FirstOrDefault(p=>p&&Id(p)==id);
                if(!p)continue;
                Observe(p,false,true);
                if(engine.StartCommander(id,out int awardedScore))p.Score=awardedScore;
            }
            foreach(var p in Player.Players.Where(p=>p&&p.Team&&!p.IsCommander).OrderBy(Id))
            {
                if(!engine.Round.Players.TryGetValue(Id(p),out var state))continue;
                var rank=config.Ranks[0];
                Tell(Id(p),$"Starting rank: {rank.Display(state.Faction)}. Personal credits: +{rank.BonusPercent}%. Squad size: {rank.Squad} (including you).");
            }
        }
        private static void Ended(GameMode mode,Team winner)=>StopRound();
        private static void Restarted(GameMode mode)=>StopRound();
        private static void StopRound()
        {
            if(!Server)return;
            active=false;confirmedRoundStart=false;startingCommandersPending=false;engine?.Reset();formation.ClearAll();playerInstances.Clear();
        }
        private static void Left(Player p)
        {if(Ready&&p&&Id(p)!=0){engine!.Disconnect(Id(p));RefreshCommanders();}}
        private static void TeamChanged(Player p,Team oldTeam,Team newTeam)
        {
            if(!Ready||!p||!newTeam)return;
            // Capture actual penalty baseline; do not promote from this event's score sample.
            Observe(p,false,true);engine!.Reconcile();RefreshCommanders();
        }
        private static void CommanderChanged(Team team,Player oldPlayer,Player newPlayer)
        {if(Ready){RefreshRoster(false,false);RefreshCommanders(!startingCommandersPending);}}
        private static void RefreshRoster(bool readScores,bool reconcile)
        {
            if(!Ready)return;
            var online=new HashSet<long>();
            foreach(var p in Player.Players.Where(p=>p).OrderBy(Id))
            {if(Id(p)==0)continue;online.Add(Id(p));Observe(p,false,readScores);}
            foreach(long id in engine!.Round.Players.Keys.ToArray())if(!online.Contains(id))engine.Disconnect(id);
            if(reconcile)engine.Reconcile();
            foreach(long id in online)formation.Update(id,engine.Policy(id));
        }
        private static void RefreshCommanders(bool announceChanges=true)
        {
            if(!Ready)return;
            var commanders=new Dictionary<int,long>();
            if(GameMode.CurrentGameMode is GameModeExt mode)
                foreach(var team in Player.Players.Where(p=>p&&p.Team).Select(p=>p.Team).Distinct())
                {
                    var p=mode.GetCommanderForTeam(team);
                    if(p&&Id(p)!=0&&engine!.Round.Players.ContainsKey(Id(p)))commanders[team.Index]=Id(p);
                }
            engine!.SetCommanders(commanders,announceChanges);
        }
        public override void OnUpdate()
        {
            if(engine==null||!Server||awaitNewRound)return;
            var mode=GameMode.CurrentGameMode;
            if(!mode||!mode.GameBegun||mode.GameOver){if(active)StopRound();return;}
            if(!active)
            {
                StartRound(mode,false);startTime=Time.realtimeSinceStartup-mode.GameTime;
                RankLog.Warning("Attached during an existing round: earlier penalties and eligibility order cannot be reconstructed. Begin a new round for complete history.");
                RefreshRoster(true,true);RefreshCommanders();return;
            }
            if(mode.GetInstanceID()!=modeId||mode.GameTime+0.1f<lastGameTime){Started(mode);return;}
            lastGameTime=mode.GameTime;float now=Time.realtimeSinceStartup;
            if(now>=nextRoster){nextRoster=now+1;RefreshRoster(false,false);RefreshCommanders();}
            if(now>=nextPoll){nextPoll=now+(float)config.PollSeconds;RefreshRoster(true,true);}
        }
        private static bool TryQuery(string text,out string command,out string query,out string error)
        {
            text=text.Trim();int split=text.IndexOfAny(new[]{' ','\t'});
            command=(split<0?text:text.Substring(0,split)).ToLowerInvariant();
            query=split<0?"":text.Substring(split+1).Trim();error="";
            if(query.StartsWith("\"",StringComparison.Ordinal))
            {
                try{query=JsonConvert.DeserializeObject<string>(query,new JsonSerializerSettings{CheckAdditionalContent=true})??"";}
                catch{error="Invalid quoted player name; use /rank \"Player Name\".";return false;}
            }
            else if(query.Contains('"')){error="Quote the entire player name.";return false;}
            if(query.Any(char.IsControl)){error="Invalid player name.";return false;}
            return true;
        }
        private static void Command(Player caller,string text)
        {
            if(!TryQuery(text,out string command,out string query,out string error)){Tell(Id(caller),error);return;}
            if(command=="/debug")
            {
                if(!caller.CanAdminExecute(Power.Generic)){Tell(Id(caller),"Permission denied: admin Generic power required.");return;}
                switch(query.ToLowerInvariant())
                {
                    case "":RankLog.Enabled=!RankLog.Enabled;break;
                    case "on":RankLog.Enabled=true;break;
                    case "off":RankLog.Enabled=false;break;
                    case "status":break;
                    default:Tell(Id(caller),"Usage: /debug [on|off|status] (Ranks logging).");return;
                }
                Tell(Id(caller),"Ranks debug logging: "+(RankLog.Enabled?"ON":"OFF")+".");
                if(RankLog.Enabled&&query.ToLowerInvariant()!="status")
                {
                    RankLog.Info("Ranks debug logging enabled. "+RewardPatches.Status);
                    if(initializationError!="")RankLog.Error(initializationError);
                }
                return;
            }
            if(!Ready){Tell(Id(caller),"Ranks inactive; wait for a valid new round.");return;}
            RefreshRoster(false,false);
            bool admin=command!="/rank";
            if(admin&&!caller.CanAdminExecute(Power.Generic)){Tell(Id(caller),"Permission denied: admin Generic power required.");return;}
            if(admin&&query.Length==0){Tell(Id(caller),"Usage: "+command+" \"Player Name\".");return;}
            long? target=query.Length==0?Id(caller):RankEngine.Resolve(query,engine!.Round.Players.Values,out error);
            if(!target.HasValue){Tell(Id(caller),error);return;}
            var player=Player.Players.FirstOrDefault(p=>p&&Id(p)==target.Value);
            if(!player){Tell(Id(caller),"Player not found.");return;}
            if(admin&&!caller.CanAdminExecute(Power.Generic,player)){Tell(Id(caller),"Permission denied for this target.");return;}
            if(command=="/rank")
            {
                // A display query cannot trigger promotions or establish queue priority.
                var state=engine!.Round.Players[target.Value];int score=state.Score;
                try
                {
                    state.Score=player.Score;
                    Tell(Id(caller),engine.Describe(target.Value)+" "+RewardPatches.Status+" "+formation.Describe(target.Value));
                }
                finally{state.Score=score;}
                return;
            }
            Observe(player,false,true);
            bool changed=command=="/promote"?engine!.AdminPromote(target.Value,out error):engine!.AdminDemote(target.Value,player.Score,out error);
            Tell(Id(caller),changed?"Rank updated for "+RankEngine.Plain(player.PlayerName)+".":error);
        }
        // Existing Commander Management owns !demote. Route ONLY these exact slash
        // commands before AdminMod's dispatcher, using its existing permission checks.
        [HarmonyPatch(typeof(ClientChatHandler),nameof(ClientChatHandler.OnRequestPlayerChat))]
        private static class ChatPatch
        {
            [HarmonyPriority(HarmonyLib.Priority.First)]
            private static bool Prefix(OnRequestPlayerChatArgs args)
            {
                if(!Server||args==null||!args.Player||string.IsNullOrWhiteSpace(args.Text))return true;
                string token=args.Text.Trim().Split(new[]{' ','\t'},2)[0];
                if(!new[]{"/rank","/promote","/demote","/debug"}.Contains(token,StringComparer.OrdinalIgnoreCase))return true;
                if(engine==null&&!token.Equals("/debug",StringComparison.OrdinalIgnoreCase))return true;
                args.Block=true;
                if(args.TeamOnly&&SiAdminMod.Pref_Admin_AcceptTeamChatCommands!=null&&!SiAdminMod.Pref_Admin_AcceptTeamChatCommands.Value)return false;
                try{Command(args.Player,args.Text);}catch(Exception e){Tell(Id(args.Player),"Rank command failed; see server log.");RankLog.Error(e.ToString());}
                return false;
            }
        }
        private sealed class KillState{public Player? Attacker;public short Kills;public bool FriendlyUnit;}
        [HarmonyPatch(typeof(MP_Strategy),"ResolveCommanderLottery")]
        private static class StartingCommanderLotteryPatch
        {
            private static void Postfix(MP_Strategy __instance)=>FinishStartingCommanders(__instance);
        }
        [HarmonyPatch(typeof(Player),nameof(Player.OnTargetDestroyed))]
        private static class TeamkillPatch
        {
            private static void Prefix(Target __0,GameObject __1,out KillState? __state)
            {
                __state=null;if(!Ready||!__0||!__1||!__0.ObjectInfo)return;
                var unit=GameFuncs.GetBaseGameObject(__1,true) as Unit;
                if(!unit||!unit.ControlledBy)return;
                __state=new KillState{Attacker=unit.ControlledBy,Kills=unit.ControlledBy.Kills,
                    FriendlyUnit=__0.OwnerUnit&&__0.OwnerUnit!=unit&&__0.Team==unit.ControlledBy.Team};
            }
            private static void Postfix(KillState? __state)
            {
                if(!Ready||__state==null||!__state.Attacker||!__state.FriendlyUnit)return;
                var p=__state.Attacker;
                if(p.Kills!=unchecked((short)(__state.Kills-1)))return;
                Observe(p,false,true);engine!.Teamkill(Id(p),p.Score,Time.realtimeSinceStartup);
            }
        }
        public override void OnDeinitializeMelon()
        {
            active=false;confirmedRoundStart=false;startingCommandersPending=false;formation.ClearAll();RewardPatches.Unbind(HarmonyInstance);
            GameEvents.OnGameStarted-=Started;GameEvents.OnGameEnded-=Ended;GameEvents.OnGameRestart-=Restarted;
            GameEvents.OnCommanderChanged-=CommanderChanged;GameEvents.OnPlayerChangedTeam-=TeamChanged;
            GameEvents.OnPlayerLeft-=Left;GameEvents.OnPlayerDeleted-=Left;
        }
    }
}
