using System;
using System.Collections.Generic;
using System.Linq;

namespace SiPlayerRanks
{
    [Flags]
    public enum UnitCategories { None = 0, Infantry = 1, Light = 2, Heavy = 4, Air = 8, Siege = 16, Other = 32, Any = 63 }
    public enum RewardKind { Kill, Structure, Logistics, Other }
    public enum Faction { Sol, Centauri, Alien }
    public sealed class RankDefinition
    {
        public string Name = "", CentauriName = "", AlienName = "";
        public int Score, Squad, BonusPercent, Seats;
        public UnitCategories Categories;
        public string[] ExcludedObjectInfoNames = new string[0];
        public RankDefinition() { }
        public RankDefinition(string name, int score, int squad, int bonus, UnitCategories categories, int seats = 0)
        { Name = name; Score = score; Squad = squad; BonusPercent = bonus; Categories = categories; Seats = seats; }
        public string Display(Faction faction) => faction == Faction.Alien && AlienName != "" ? AlienName : faction == Faction.Centauri && CentauriName != "" ? CentauriName : Name;
    }
    public sealed class RankConfig
    {
        public bool Debug = false;
        public double PollSeconds = 30, TeamkillCooldownSeconds = 10;
        public int StartingCommanderRank = 9, StartingCommanderScore = 3500;
        public int CommanderBonusPercent = 85, TraitorOtherBonusPercent = -25;
        // Declared rank mapping over verified native definitions (not editor labels).
        public Dictionary<string, string[]> NativeCategoryRules = DefaultNativeRules();
        public Dictionary<string, UnitCategories> NativeObjectCategoryOverrides = new Dictionary<string, UnitCategories> {
            { "ObjectInfo_Sol_Air_Dropship", UnitCategories.Air },
            { "ObjectInfo_Cent_Air_Shuttle", UnitCategories.Air }
        };
        public RankDefinition[] Ranks = Defaults();
        public static Dictionary<string, string[]> DefaultNativeRules()
        {
            return new Dictionary<string, string[]> {
                { "Infantry", new[] { "Infantry" } },
                { "Light", new[] { "Cavalry", "Special:Light" } },
                { "Heavy", new[] { "Tank", "Transport", "Harvester", "Special", "Repair" } },
                { "Air", new[] { "Aircraft" } }, { "Siege", new[] { "Siege" } }, { "Other", new[] { "None" } }
            };
        }
        public static RankDefinition[] Defaults()
        {
            string[] sol = { "Private", "Specialist 1", "Specialist 2", "Specialist 3", "Specialist 4", "Sergeant", "Technical Sergeant", "Master Sergeant", "Senior Master Sergeant", "Chief Master Sergeant", "Second Lieutenant", "First Lieutenant", "Captain", "Major", "Lieutenant Colonel", "Colonel", "Field Marshal" };
            string[] centauri = { "Recruit", "Private", "Private First Class", "Lance Corporal", "Corporal", "Sergeant", "Staff Sergeant", "Sergeant First Class", "Master Sergeant", "Sergeant Major", "Second Lieutenant", "First Lieutenant", "Captain", "Major", "Lieutenant Colonel", "Colonel", "Field Marshal" };
            string[] alien = { "Foetus", "Hatchling", "Stalker", "Hunter", "Ravager", "Predator", "Apex Predator", "Brood Warrior", "Brood Guardian", "Brood Warden", "Brood Champion", "Brood Leader", "Brood Master", "Brood Lord", "Hive Guardian", "Hive Lord", "Hive Mind" };
            int[] score = { 0, 300, 500, 700, 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5500, 7000, 9000, 12000, 15000 };
            int[] bonus = { 0, 1, 2, 4, 6, 9, 12, 16, 20, 24, 27, 30, 35, 40, 45, 50, 85 };
            var result = new RankDefinition[score.Length];
            for (int n = 0; n < 17; n++)
            {
                var categories = n == 0 ? UnitCategories.None : n < 3 ? UnitCategories.Infantry : n < 5 ? UnitCategories.Infantry | UnitCategories.Light : n < 7 ? UnitCategories.Infantry | UnitCategories.Light | UnitCategories.Heavy : n == 7 ? UnitCategories.Infantry | UnitCategories.Light | UnitCategories.Heavy | UnitCategories.Air : UnitCategories.Any;
                result[n] = new RankDefinition(sol[n], score[n], n == 16 ? 20 : n + 1, bonus[n], categories, n >= 10 ? 1 : 0) { CentauriName = centauri[n], AlienName = alien[n] };
                if (n == 8 || n == 9) result[n].ExcludedObjectInfoNames = new[] { "ObjectInfo_Sol_UltraHeavy_SiegeTank", "ObjectInfo_Cent_UltraHeavy_CrimsonTank" };
            }
            return result;
        }
        public void Validate()
        {
            if (!Finite(PollSeconds) || PollSeconds < 1 || PollSeconds > 3600 || !Finite(TeamkillCooldownSeconds) || TeamkillCooldownSeconds < 0 || TeamkillCooldownSeconds > 3600)
                throw new ArgumentException("Invalid polling or teamkill cooldown.");
            if (CommanderBonusPercent < 0 || CommanderBonusPercent > 1000 || TraitorOtherBonusPercent < -100 || TraitorOtherBonusPercent > 0) throw new ArgumentException("Invalid reward modifiers.");
            if (Ranks == null || Ranks.Length != 17) throw new ArgumentException("Exactly seventeen rank tiers are required.");
            if (NativeCategoryRules == null || new[] { "Infantry", "Light", "Heavy", "Air", "Siege", "Other" }.Any(k => !NativeCategoryRules.ContainsKey(k)) || NativeCategoryRules.Any(p => p.Value == null || p.Value.Any(r => !ValidNativeRule(r))))
                throw new ArgumentException("Invalid native category rules.");
            if (NativeObjectCategoryOverrides == null || NativeObjectCategoryOverrides.Any(p => string.IsNullOrWhiteSpace(p.Key) || (p.Value & ~UnitCategories.Any) != 0))
                throw new ArgumentException("Invalid native object category overrides.");
            for (int n = 0; n < 17; n++) { var r = Ranks[n]; if (r == null || string.IsNullOrWhiteSpace(r.Name) || r.Score < 0 || (n > 0 && r.Score <= Ranks[n - 1].Score) || r.Squad < 0 || r.Squad > 50 || r.BonusPercent < 0 || r.BonusPercent > 1000 || r.Seats < 0 || (r.Categories & ~UnitCategories.Any) != 0 || r.ExcludedObjectInfoNames == null || r.ExcludedObjectInfoNames.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid rank tier " + n); }
            if (Ranks[0].Score != 0 || Ranks[0].Seats != 0) throw new ArgumentException("Base tier must start at zero and have unlimited seats.");
            if (StartingCommanderRank < 0 || StartingCommanderRank >= Ranks.Length || Ranks[StartingCommanderRank].Seats != 0 || StartingCommanderScore < 0)
                throw new ArgumentException("Invalid starting commander tier or score award.");
        }
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool ValidNativeRule(string rule)
        {
            if (rule == "*") return true;
            if (string.IsNullOrWhiteSpace(rule)) return false;
            string[] parts = rule.Split(':');
            return parts.Length <= 2 && new[] { "None", "Infantry", "Cavalry", "Tank", "Siege", "Aircraft", "Transport", "Harvester", "Special", "Repair" }.Contains(parts[0]) &&
                (parts.Length == 1 || new[] { "Light", "Medium", "Heavy", "Any", "*" }.Contains(parts[1]));
        }
    }
    public sealed class RankState
    {
        public long Id;
        public string Name = "", TeamName = "", QueueNotice = "";
        public int Team = -1, Rank, Score, Strikes;
        public Faction Faction;
        public long ScoreOffset;
        public double? LastTeamkill;
        public bool Online, HasRecovery, StartingCommanderAwarded;
        public Dictionary<int, long> ThresholdOrder = new Dictionary<int, long>();
    }
    public sealed class RoundState
    {
        public string Token = Guid.NewGuid().ToString("N");
        public long Sequence;
        public Dictionary<long, RankState> Players = new Dictionary<long, RankState>();
        public Dictionary<int, long> Commanders = new Dictionary<int, long>();
    }
    public sealed class Privileges
    {
        public int Squad = 1, RankBonus, CommanderBonus, TraitorOtherBonus = -25;
        public UnitCategories Categories;
        public string[] ExcludedObjectInfoNames = new string[0];
        public bool Traitor, Commander;
        public decimal Multiplier(RewardKind kind) => Traitor ? (kind == RewardKind.Kill || kind == RewardKind.Structure ? 0m : 1m + TraitorOtherBonus / 100m) : 1m + (Commander ? CommanderBonus : RankBonus) / 100m;
    }
    public sealed class RankEngine
    {
        public readonly RankConfig Config;
        public RoundState Round { get; private set; } = new RoundState();
        public event Action<long, string>? Message;
        public event Action<long, Privileges>? PolicyChanged;
        public RankEngine(RankConfig config) { config.Validate(); Config = config; }
        public void Reset() { var ids = Round.Players.Keys.ToArray(); Round = new RoundState(); foreach (var id in ids) PolicyChanged?.Invoke(id, new Privileges { Squad = Config.Ranks[0].Squad, Categories = Config.Ranks[0].Categories }); }
        public void Restore(RoundState data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Token) || data.Players == null || data.Commanders == null || data.Sequence < 0) throw new ArgumentException("Invalid saved round.");
            foreach (var pair in data.Players) { var s = pair.Value; if (s == null || s.Id == 0 || s.Id != pair.Key || s.Rank < -1 || s.Rank >= Config.Ranks.Length || s.Strikes < 0 || s.Name == null || s.TeamName == null || s.QueueNotice == null || s.ThresholdOrder == null || s.ScoreOffset < -1000000000000L || s.ScoreOffset > 1000000000000L || !Enum.IsDefined(typeof(Faction), s.Faction) || (s.LastTeamkill.HasValue && (double.IsNaN(s.LastTeamkill.Value) || double.IsInfinity(s.LastTeamkill.Value))) || s.ThresholdOrder.Any(p => p.Key < 1 || p.Key >= Config.Ranks.Length || p.Value <= 0 || p.Value > data.Sequence)) throw new ArgumentException("Invalid saved player."); }
            if (data.Commanders.Any(p => !data.Players.ContainsKey(p.Value) || data.Players[p.Value].Team != p.Key)) throw new ArgumentException("Invalid saved commander.");
            foreach (var group in data.Players.Values.Where(s => s.Team >= 0).GroupBy(s => s.Team)) for (int rank = 1; rank < Config.Ranks.Length; rank++) if (Config.Ranks[rank].Seats > 0 && group.Count(s => s.Rank == rank) > Config.Ranks[rank].Seats) throw new ArgumentException("Saved seats exceed limits.");
            Round = data;
        }
        public RankState Observe(long id, string name, int team, string teamName, int score, bool online = true, bool promote = true, Faction? faction = null)
        {
            if (id == 0) throw new ArgumentException("Stable account identifier required.");
            bool fresh = !Round.Players.TryGetValue(id, out var s);
            if (fresh) { s = new RankState { Id = id, Team = team }; Round.Players.Add(id, s); }
            bool reconnect = !s.Online;
            s.Name = name ?? ""; s.TeamName = teamName ?? ""; s.Score = score; s.Online = online;
            s.Faction = faction ?? InferFaction(s.TeamName);
            if (s.Team != team)
            {
                bool penalty = s.Team >= 0 && team >= 0;
                RemoveAppointment(id); s.Team = team; s.ThresholdOrder.Clear(); s.QueueNotice = "";
                if (penalty && s.Rank >= 0) { int old = s.Rank; s.Rank = Math.Max(0, s.Rank - 5); FitDemotedSeat(s); Rebase(s); Message?.Invoke(id, "Team changed: demoted " + (old - s.Rank) + " positions to " + Title(s) + ". Earn additional score to recover."); }
                Notify(s);
            }

            if (fresh || reconnect) Notify(s);
            if (promote) Reconcile(); return s;
        }
        public static Faction InferFaction(string teamName) { string n = (teamName ?? "").ToLowerInvariant(); return n.Contains("alien") ? Faction.Alien : n.Contains("centauri") ? Faction.Centauri : Faction.Sol; }
        public void Disconnect(long id) { if (Round.Players.TryGetValue(id, out var s)) { s.Online = false; s.ThresholdOrder.Clear(); s.QueueNotice = ""; RemoveAppointment(id); Reconcile(); } }
        private void RemoveAppointment(long id) { foreach (int team in Round.Commanders.Where(p => p.Value == id).Select(p => p.Key).ToArray()) Round.Commanders.Remove(team); }
        // Game score stays untouched. Penalties rebase this separate offset at the
        // landed tier; every following threshold gap requires newly earned score.
        private long Effective(RankState s) => (long)s.Score - s.ScoreOffset;
        private bool Eligible(RankState s, int next) => s.Online && s.Team >= 0 && s.Rank >= 0 && Effective(s) >= Config.Ranks[next].Score;
        private bool SeatFree(int team, int rank, long except = 0) => Config.Ranks[rank].Seats == 0 || Round.Players.Values.Count(s => s.Id != except && s.Team == team && s.Rank == rank) < Config.Ranks[rank].Seats;
        public void Reconcile()
        {
            // All observations submitted before this call share a detection batch.
            // Stable account ID resolves ties; exact crossing times are unknown.
            long batch = ++Round.Sequence;
            foreach (var s in Round.Players.Values.Where(s => s.Online && s.Team >= 0 && s.Rank >= 0))
            {

                for (int r = s.Rank + 1; r < Config.Ranks.Length; r++)
                {
                    if (Effective(s) >= Config.Ranks[r].Score && !s.ThresholdOrder.ContainsKey(r))
                        s.ThresholdOrder[r] = batch;
                }
            }
            var changed = new Dictionary<long, RankState>(); bool progress;
            do
            {
                progress = false;

                for (int next = 1; next < Config.Ranks.Length; next++)
                {
                    var candidates = Round.Players.Values
                        .Where(s => s.Rank == next - 1 && Eligible(s, next))
                        .OrderBy(s => Order(s, next)).ThenBy(s => s.Id).ToArray();
                    foreach (var s in candidates)
                    {
                        if (!SeatFree(s.Team, next, s.Id)) continue;
                        s.Rank = next;
                        s.QueueNotice = "";
                        changed[s.Id] = s;
                        progress = true;
                    }
                }
            } while (progress);
            foreach (var s in changed.Values)
            {
                var r = Config.Ranks[s.Rank];
                Message?.Invoke(s.Id, "Promoted to " + Title(s) + "! Personal credits: +" + (Policy(s.Id).Commander ? Config.CommanderBonusPercent : r.BonusPercent) + "%. Squad size: " + Policy(s.Id).Squad + ".");
                Notify(s);
            }
            foreach (var s in Round.Players.Values)
            {
                string notice = ""; int next = s.Rank + 1;
                if (s.Online && s.Rank >= 0 && next < Config.Ranks.Length && Config.Ranks[next].Seats > 0 && Eligible(s, next))
                {
                    var waiting = Round.Players.Values.Where(p => p.Team == s.Team && p.Online && (p.Rank == next - 1 && Eligible(p, next))).OrderBy(p => Order(p, next)).ThenBy(p => p.Id).ToList();
                    notice = "Waiting for " + Config.Ranks[next].Display(s.Faction) + " seat: queue position " + (waiting.IndexOf(s) + 1) + ".";
                }
                if (s.QueueNotice != notice)
                {
                    if (notice != "") Message?.Invoke(s.Id, notice);
                    else if (s.QueueNotice != "") Message?.Invoke(s.Id, "No longer waiting for that rank seat.");
                    s.QueueNotice = notice;
                }
            }
        }
        private static long Order(RankState s, int next) => s.ThresholdOrder.TryGetValue(next, out var order) ? order : long.MaxValue;
        public bool Teamkill(long id, int scoreAfter, double nowSeconds)
        {
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) throw new ArgumentException("Finite monotonic time required.");
            if (!Round.Players.TryGetValue(id, out var s)) return false;
            s.Score = scoreAfter;
            if (s.LastTeamkill.HasValue && nowSeconds - s.LastTeamkill.Value < Config.TeamkillCooldownSeconds) return false;
            s.LastTeamkill = nowSeconds;
            s.Strikes = s.Strikes == int.MaxValue ? int.MaxValue : s.Strikes + 1;
            if (s.Rank >= 0) Demote(s, s.Strikes, "Teamkill #" + s.Strikes);
            return true;
        }
        // An occupied exact-rank seat cannot be stolen by a penalty. Fall back to
        // the closest lower free tier; this conflict policy is documented.
        private void FitDemotedSeat(RankState s) { while (s.Rank > 0 && !SeatFree(s.Team, s.Rank, s.Id)) s.Rank--; }
        private void Rebase(RankState s)
        {
            s.ScoreOffset = (long)s.Score - Config.Ranks[s.Rank].Score;
            s.HasRecovery = true;
            s.ThresholdOrder.Clear();
            s.QueueNotice = "";
        }
        // A replacement game Player may restart its score counter on reconnect.
        // Rebind only explicit penalty recovery: preserve the remaining debt, not
        // a separate ordinary score balance or cross-round promotion progress.
        public void RebindScore(long id, int freshScore)
        {
            if (Round.Players.TryGetValue(id, out var s) && s.HasRecovery && s.Rank >= 0)
            {
                s.ScoreOffset += (long)freshScore - s.Score;
                s.Score = freshScore;
            }
        }
        private void Demote(RankState s, int amount, string reason)
        {
            long target = (long)s.Rank - amount;
            if (target < 0)
            {
                s.Rank = -1;
                s.ScoreOffset = 0;
                s.HasRecovery = false;
                s.ThresholdOrder.Clear();
                Message?.Invoke(s.Id, reason + ": " + Title(s) + ". Promotions locked until next round.");
            }
            else
            {
                s.Rank = (int)target;
                FitDemotedSeat(s);
                Rebase(s);
                Message?.Invoke(s.Id, reason + ": demoted to " + Title(s) + ". Next promotion requires additional score.");
            }
            Notify(s);
            Reconcile();
        }
        public bool AdminPromote(long id, out string error)
        {
            error = ""; if (!Round.Players.TryGetValue(id, out var s)) { error = "Player not found."; return false; }
            if (s.Rank < 0) { error = "Traitor promotions are locked for this round."; return false; }
            if (s.Rank >= Config.Ranks.Length - 1 || s.Team < 0) { error = "Player cannot be promoted in the current state."; return false; }
            int next = s.Rank + 1; if (!SeatFree(s.Team, next, id)) { error = "The requested rank seat is occupied."; return false; }
            s.Rank = next; Rebase(s); Message?.Invoke(id, "Admin promotion: " + Title(s) + "."); Notify(s); Reconcile(); return true;
        }
        public bool AdminDemote(long id, int scoreAfter, out string error)
        {
            error = ""; if (!Round.Players.TryGetValue(id, out var s)) { error = "Player not found."; return false; }
            if (s.Rank < 0) { error = "Player is already a Traitor."; return false; }
            s.Score = scoreAfter; Demote(s, 1, "Admin demotion"); return true;
        }
        // Only a genuine round-start path grants this one-time scoreboard award.
        public bool StartCommander(long id, out int scoreAfter)
        {
            scoreAfter = 0;
            if (!Round.Players.TryGetValue(id, out var s) || !s.Online || s.Team < 0 || s.Rank < 0 || s.StartingCommanderAwarded || s.HasRecovery || !Round.Commanders.Values.Contains(id)) return false;
            s.StartingCommanderAwarded = true;
            s.Score = (int)Math.Min(int.MaxValue, (long)s.Score + Config.StartingCommanderScore);
            s.Rank = Math.Max(s.Rank, Config.StartingCommanderRank);
            s.ThresholdOrder.Clear();scoreAfter = s.Score;
            Message?.Invoke(id, "Starting commander rank: " + Title(s) + ". Round score: +" + Config.StartingCommanderScore + ". Personal credits: +" + Config.CommanderBonusPercent + "% while commanding.");
            Notify(s);return true;
        }
        public void SetCommanders(IDictionary<int, long> active, bool announceChanges = true)
        {
            var desired = active;
            foreach (var old in Round.Commanders.ToArray()) if (!desired.TryGetValue(old.Key, out var id) || id != old.Value || !Round.Players.TryGetValue(id, out var current) || !current.Online || current.Team != old.Key) { Round.Commanders.Remove(old.Key); if (announceChanges) Message?.Invoke(old.Value, "Commander role ended; normal rank rewards restored."); if (Round.Players.TryGetValue(old.Value, out var s)) Notify(s); }
            foreach (var pair in desired) if (Round.Players.TryGetValue(pair.Value, out var s) && s.Online && s.Team == pair.Key && (!Round.Commanders.TryGetValue(pair.Key, out var old) || old != pair.Value)) { Round.Commanders[pair.Key] = pair.Value; if (announceChanges) Message?.Invoke(pair.Value, "Commander role active; normal rank: " + Title(s) + ". Personal resources: +" + Config.CommanderBonusPercent + "%; no AI followers while commanding."); Notify(s); }
        }
        public Privileges Policy(long id)
        {
            var p = new Privileges { TraitorOtherBonus = Config.TraitorOtherBonusPercent }; if (!Round.Players.TryGetValue(id, out var s)) return p;
            p.Traitor = s.Rank < 0; p.Commander = Round.Commanders.Values.Contains(id); p.CommanderBonus = p.Commander ? Config.CommanderBonusPercent : 0;
            if (s.Rank >= 0) { var r = Config.Ranks[s.Rank]; p.RankBonus = r.BonusPercent; if (!p.Commander) { p.Squad = r.Squad; p.Categories = r.Categories; p.ExcludedObjectInfoNames = r.ExcludedObjectInfoNames; } }
            return p;
        }
        private string Title(RankState s) => s.Rank < 0 ? (s.Faction == Faction.Alien ? "Outcast" : s.Faction == Faction.Centauri ? "Deserter" : "Traitor") : Config.Ranks[s.Rank].Display(s.Faction);
        private void Notify(RankState s) => PolicyChanged?.Invoke(s.Id, Policy(s.Id));
        public string Describe(long id)
        {
            var s = Round.Players[id]; var p = Policy(id);
            string allowed = p.Categories.ToString();
            if (p.ExcludedObjectInfoNames.Length > 0) allowed += " except " + string.Join(" and ", p.ExcludedObjectInfoNames.Select(UnitLabel));
            return Plain(s.Name) + ": " + Title(s) + "; team: " + Plain(s.TeamName) + "; round score: " + s.Score + "; personal resources: " + (p.Traitor ? "combat 0%, other " + (p.Multiplier(RewardKind.Other) * 100) + "%" : (p.Multiplier(RewardKind.Other) * 100) + "% of base") + "; allowed units: " + allowed + "; FPS squad limit: " + p.Squad + (p.Commander ? "; role: RTS commander" : "") + ".";
        }
        private static string UnitLabel(string name) => name == "ObjectInfo_Sol_UltraHeavy_SiegeTank" ? "Siege Tank" : name == "ObjectInfo_Cent_UltraHeavy_CrimsonTank" ? "Crimson Tank" : Plain(name);
        public static string Plain(string text) => (text ?? "").Replace("<", "[").Replace(">", "]").Replace("\r", " ").Replace("\n", " ");
        public static long? Resolve(string query, IEnumerable<RankState> players, out string error)
        {
            query = (query ?? "").Trim(); if (query.Length >= 2 && query[0] == '\"' && query[query.Length - 1] == '\"') query = query.Substring(1, query.Length - 2);
            if (query == "") { error = "Specify a player name."; return null; }
            var list = players.Where(s => s.Online).ToArray(); var matches = list.Where(s => string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) matches = list.Where(s => s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            error = matches.Length > 1 ? "Name is ambiguous; use a more specific player name." : "Player not found."; if (matches.Length != 1) return null; error = ""; return matches[0].Id;
        }
    }
}


