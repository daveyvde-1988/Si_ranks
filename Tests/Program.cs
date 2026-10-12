using System;
using System.Collections.Generic;
using SiPlayerRanks;

static class Program
{
    static int checks;
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
    static RankEngine Engine() => new RankEngine(new RankConfig());
    static void Reject(Action action, string description)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, description);
    }
    static void Main()
    {
        var e = Engine();
        for (int r = 0; r < 17; r++) { Check(e.Config.Ranks[r].Squad == (r == 16 ? 20 : r + 1), "squad tier " + r); Check(e.Config.Ranks[r].Seats == (r >= 10 ? 1 : 0), "seat tier " + r); }
        e.Observe(2, "Second", 0, "SOL", 4000, true, false); e.Observe(1, "First", 0, "SOL", 4000, true, false); e.Reconcile();
        Check(e.Round.Players[1].Rank == 10 && e.Round.Players[2].Rank == 9, "batched eligibility ties use account ID");
        e.Observe(1, "First", 0, "SOL", 4500); Check(e.Round.Players[1].Rank == 11 && e.Round.Players[2].Rank == 10, "exact previous seat released");
        e.Disconnect(1); e.Observe(2, "Second", 0, "SOL", 4500); Check(e.Round.Players[2].Rank == 10, "offline exact seat retained");
        e.Observe(1, "First", 0, "SOL", 4500); Check(e.Round.Players[1].Rank == 11 && e.Round.Players[1].Strikes == 0, "reconnect no penalty");
        e = Engine(); e.Observe(1, "P", 0, "SOL", 3000); Check(e.Teamkill(1, 3000, 100), "first teamkill"); Check(e.Round.Players[1].Rank == 7, "first strike demotes one");
        Check(!e.Teamkill(1, 3000, 109), "ignored cooldown"); Check(e.Teamkill(1, 3000, 110), "expiry does not move on ignored kill"); Check(e.Round.Players[1].Rank == 5 && e.Round.Players[1].Strikes == 2, "second demotes two more");
        e.Observe(1, "P", 0, "SOL", 3000); Check(e.Round.Players[1].Rank == 5, "no recovery at same score");
        e.Observe(1, "P", 0, "SOL", 3500); Check(e.Round.Players[1].Rank == 6, "earn threshold gap");
        e.Observe(1, "P", 0, "SOL", 4500); Check(e.Round.Players[1].Rank == 8, "retains all earned score");
        e.Observe(1, "P", 1, "Centauri", 4500); Check(e.Round.Players[1].Rank == 3 && e.Round.Players[1].Strikes == 2, "switch five and preserve strikes");
        e.Reconcile(); Check(e.Round.Players[1].Rank == 3, "switch baseline prevents instant recovery");
        Check(e.Describe(1).Contains("Lance Corporal"), "faction names");
        e = Engine(); e.Observe(1, "P", 0, "SOL", 300); Check(e.Teamkill(1, 300, 0) && e.Round.Players[1].Rank == 0, "landing private not traitor");
        Check(e.Teamkill(1, 300, 10) && e.Round.Players[1].Rank == -1, "below private traitor");
        Check(e.Policy(1).Squad == 1 && e.Policy(1).Multiplier(RewardKind.Kill) == 0 && e.Policy(1).Multiplier(RewardKind.Structure) == 0 && e.Policy(1).Multiplier(RewardKind.Logistics) == 0.75m, "traitor restrictions");
        e.Observe(1, "P", 1, "Alien", 10000); Check(e.Round.Players[1].Rank == -1, "traitor switch lock"); Check(!e.AdminPromote(1, out _), "admin cannot unlock");
        e.Disconnect(1); e.Observe(1, "P", 1, "Alien", 10000); Check(e.Round.Players[1].Rank == -1, "traitor reconnect lock");
        e.SetCommanders(new Dictionary<int, long> { { 1, 1 } }); Check(e.Policy(1).Multiplier(RewardKind.Logistics) == 0.75m, "traitor overrides commander");
        e.Reset(); e.Observe(1, "P", 0, "SOL", 0); Check(e.Round.Players[1].Rank == 0 && e.Round.Players[1].Strikes == 0, "round clears penalties");
        e.Observe(1, "P", 0, "SOL", 12000); Check(e.Policy(1).Multiplier(RewardKind.Other) == 1.50m, "normal Colonel bonus before commander role");
        e.SetCommanders(new Dictionary<int, long> { { 0, 1 } }); Check(e.Policy(1).Multiplier(RewardKind.Other) == 1.85m && e.Policy(1).Squad == 1 && e.Round.Players[1].Rank == 15, "commander replaces bonus and preserves rank");
        e.SetCommanders(new Dictionary<int, long>()); Check(e.Policy(1).Multiplier(RewardKind.Other) == 1.50m, "commander restores rank bonus");
        e.AdminDemote(1, 12000, out _); Check(e.Round.Players[1].Rank == 14 && e.Round.Players[1].Strikes == 0, "admin demote no strike"); e.Reconcile(); Check(e.Round.Players[1].Rank == 14, "admin demote survives poll");
        e = Engine(); e.Observe(1, "P", 0, "SOL", 0); e.AdminPromote(1, out _); e.Reconcile(); Check(e.Round.Players[1].Rank == 1, "admin promo survives poll");
        e.Observe(1, "P", 0, "SOL", 299); Check(e.Round.Players[1].Rank == 2, "admin promotes rebase to next gap");
        // Recovery accumulated while waiting for a seat must not be consumed by acquiring it.
        e = Engine(); e.Observe(1, "Holder", 0, "SOL", 4000); e.Observe(2, "Recover", 0, "SOL", 3500); e.AdminDemote(2, 3500, out _); e.Observe(2, "Recover", 0, "SOL", 5800);
        Check(e.Round.Players[2].Rank == 9, "seat prevents skipping occupied lieutenant");
        e.Observe(1, "Holder", 0, "SOL", 4500); Check(e.Round.Players[2].Rank == 10, "queued recovery reaches first opened seat");
        e.Observe(1, "Holder", 0, "SOL", 5500); Check(e.Round.Players[2].Rank == 11, "queued excess earned preserved for next rank");
        e.Observe(1, "Holder", 0, "SOL", 7000); Check(e.Round.Players[2].Rank == 11, "recovery score still gates captain");
        e = Engine(); e.Observe(1, "Unassigned", -1, "", 0); e.Observe(1, "Unassigned", 0, "SOL", 1000); Check(e.Round.Players[1].Rank == 4, "initial assignment has no penalty");
        e.Observe(1, "Unassigned", 0, "SOL", 0); Check(e.Round.Players[1].Rank == 4, "ordinary score decrease does not demote");
        e = Engine(); e.Observe(2, "Higher", 0, "SOL", 4500); e.Observe(1, "Seat", 0, "SOL", 4000); e.AdminDemote(2, 4500, out _); Check(e.Round.Players[2].Rank == 9 && e.Round.Players[1].Rank == 10, "occupied demotion seat protects existing holder");
        string error; Check(RankEngine.Resolve("\"Higher\"", e.Round.Players.Values, out error) == 2, "quoted name resolution");
        e.Observe(3, "Higher duplicate", 1, "SOL", 0); Check(RankEngine.Resolve("High", e.Round.Players.Values, out error) == null && error.Contains("ambiguous"), "ambiguous partial names");
        Check(e.Config.Ranks[8].Categories == UnitCategories.Any && e.Config.Ranks[8].ExcludedObjectInfoNames.Length == 2, "named exclusion permits other siege units");
        Reject(() => new RankEngine(new RankConfig { PollSeconds = double.NaN }), "nonfinite polling rejected");
        Reject(() => new RankEngine(new RankConfig { TeamkillCooldownSeconds = double.PositiveInfinity }), "nonfinite cooldown rejected");
        Reject(() => new RankEngine(new RankConfig { StartingCommanderScore = -1 }), "negative commander score award rejected");
        Reject(() => new RankEngine(new RankConfig { TraitorOtherBonusPercent = -101 }), "invalid traitor multiplier rejected");
        var invalid = new RankConfig(); invalid.Ranks[3].Score = invalid.Ranks[2].Score; Reject(() => new RankEngine(invalid), "unordered thresholds rejected");
        invalid = new RankConfig(); invalid.Ranks[8].ExcludedObjectInfoNames = null; Reject(() => new RankEngine(invalid), "null exclusion list rejected");
        Reject(() => e.Restore(new RoundState { Token = "" }), "malformed saved round rejected");
        invalid = new RankConfig(); invalid.NativeCategoryRules["Light"] = new[] { "MadeUpVehicle" }; Reject(() => new RankEngine(invalid), "invalid native category rejected");
        invalid = new RankConfig(); invalid.NativeObjectCategoryOverrides = null; Reject(() => new RankEngine(invalid), "null exact native override rejected");
        Check(new RankConfig().NativeObjectCategoryOverrides["ObjectInfo_Sol_Air_Dropship"] == UnitCategories.Air, "native Transport dropship classified as Air by verified exact exception");
        e=Engine();e.Observe(1,"Recovery",0,"SOL",3000);e.Teamkill(1,3000,0);e.Observe(1,"Recovery",0,"SOL",3200);
        e.RebindScore(1,0);e.Observe(1,"Recovery",0,"SOL",0);Check(e.Round.Players[1].Rank==7,"score counter replacement retains recovery debt");
        e.Observe(1,"Recovery",0,"SOL",299);Check(e.Round.Players[1].Rank==7,"reconnect still requires remaining gap");
        e.Observe(1,"Recovery",0,"SOL",300);Check(e.Round.Players[1].Rank==8,"reconnect recovery preserves earned partial gap");
        e=Engine();e.Observe(1,"Ordinary",0,"SOL",3000);e.RebindScore(1,0);e.Observe(1,"Ordinary",0,"SOL",500);Check(e.Round.Players[1].ScoreOffset==0&&e.Round.Players[1].Rank==8,"ordinary score is never accumulated across reconnect");
        e=Engine();e.Observe(1,"Marshal",0,"SOL",14999);Check(e.Round.Players[1].Rank==15,"final rank unavailable below 15000");
        e.Observe(1,"Marshal",0,"SOL",15000);Check(e.Round.Players[1].Rank==16&&e.Policy(1).Squad==20&&e.Policy(1).Multiplier(RewardKind.Kill)==1.85m,"earned Field Marshal grants 20 cap and 85 percent");
        e.Observe(2,"Queue",0,"Centauri",15000);Check(e.Round.Players[2].Rank==15,"one exact final seat per team");
        e.Observe(3,"Other",1,"Alien",15000);Check(e.Round.Players[3].Rank==16&&e.Describe(3).Contains("Hive Mind"),"independent faction final seat and display");
        Check(!e.AdminPromote(1,out _),"admin cannot exceed new final rank");
        e.AdminDemote(1,15000,out _);Check(e.Round.Players[2].Rank==16&&e.Round.Players[1].Rank==14,"final demotion frees seat and protects occupied Colonel");
        e=Engine();e.Observe(1,"Starter",0,"SOL",0);Check(!e.StartCommander(1,out _),"noncommander cannot claim starting award");
        e.SetCommanders(new Dictionary<int,long>{{0,1}},false);Check(e.StartCommander(1,out int award)&&award==3500&&e.Round.Players[1].Rank==9,"commander starts at tier9 and gains real score");
        Check(e.Describe(1).Contains("Chief Master Sergeant")&&!e.Describe(1).Contains("Field Marshal")&&e.Policy(1).Multiplier(RewardKind.Other)==1.85m,"commander uses actual starting rank and active role bonus");
        Check(!e.StartCommander(1,out _)&&e.Round.Players[1].Score==3500,"starting score award occurs once");
        e.Observe(1,"Starter",0,"SOL",4000);Check(e.Round.Players[1].Rank==10&&e.Policy(1).Multiplier(RewardKind.Other)==1.85m,"commander progresses normally without losing role bonus");
        e.Disconnect(1);e.Observe(1,"Starter",0,"SOL",0);e.SetCommanders(new Dictionary<int,long>{{0,1}});Check(!e.StartCommander(1,out _),"reconnect cannot claim commander award twice");
        e.SetCommanders(new Dictionary<int,long>());Check(e.Policy(1).Squad==11&&e.Policy(1).Multiplier(RewardKind.Other)==1.27m,"leaving command restores earned rank privileges");
        e.Reset();e.Observe(1,"Starter",0,"Alien",0);e.SetCommanders(new Dictionary<int,long>{{0,1}});Check(e.StartCommander(1,out award)&&e.Describe(1).Contains("Brood Warden"),"new round permits fresh starting award and Alien title");
        e=Engine();e.Observe(1,"Starter",0,"Centauri",0);e.SetCommanders(new Dictionary<int,long>{{0,1}});e.StartCommander(1,out _);Check(e.Describe(1).Contains("Sergeant Major"),"Centauri commander starting title");
        e.Teamkill(1,3500,0);Check(!e.StartCommander(1,out _),"penalty cannot be undone by repeating commander start");
        Reject(()=>new RankEngine(new RankConfig{StartingCommanderRank=16}),"limited starting commander rank rejected");
        Console.WriteLine("Passed " + checks + " engine assertions.");
    }
}


