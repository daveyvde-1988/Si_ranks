# Silica Field Ranks

**Version 1.4.0 - server-side Silica player ranks, compiled as `Si_Ranks.dll`.**

Players progress through a shared rank ladder using their authoritative current-round game score. Faction-specific names, personal-credit bonuses and FPS squad permissions follow that rank. Progress, penalties and limited seats reset every round; there is no career score.

## Requirements

- Silica dedicated server with compatible MelonLoader and Admin Mod installed.
- **Si_Formation 2.10.0 or compatible owner-policy API** for FPS capacity and unit eligibility enforcement.
- The reviewed **Si_KingOfTheHill** personal-credit economy and **Si_Logistics** builds for the temporary reward patches.

The workspace's `Si_Resources` manages team resources; Logistics uses KoH for personal credits. Field Ranks uses that existing wallet, not a second balance. KoH and Logistics files are not modified by this integration: temporary patches run in memory.

## Ranks, scores and rewards

Defaults below are configurable. Each FPS cap **includes the player's controlled unit**: cap 1 means no recruited AI followers; cap 20 means the player plus up to 19 followers. Bonuses are the total increase over the original eligible base reward, not compounded promotion bonuses.

| Tier | Round score | SOL | Centauri | Alien | FPS cap | Reward bonus | Allowed recruited units | Seats per team |
|---:|---:|---|---|---|---:|---:|---|---|
| 0 | 0 | Private | Recruit | Foetus | 1 | 0% | None | Unlimited |
| 1 | 300 | Specialist 1 | Private | Hatchling | 2 | 1% | Infantry | Unlimited |
| 2 | 500 | Specialist 2 | Private First Class | Stalker | 3 | 2% | Infantry | Unlimited |
| 3 | 700 | Specialist 3 | Lance Corporal | Hunter | 4 | 4% | Infantry, light | Unlimited |
| 4 | 1,000 | Specialist 4 | Corporal | Ravager | 5 | 6% | Infantry, light | Unlimited |
| 5 | 1,500 | Sergeant | Sergeant | Predator | 6 | 9% | Up to heavy | Unlimited |
| 6 | 2,000 | Technical Sergeant | Staff Sergeant | Apex Predator | 7 | 12% | Up to heavy | Unlimited |
| 7 | 2,500 | Master Sergeant | Sergeant First Class | Brood Warrior | 8 | 16% | Up to air | Unlimited |
| 8 | 3,000 | Senior Master Sergeant | Master Sergeant | Brood Guardian | 9 | 20% | Any except Siege Tank / Crimson Tank | Unlimited |
| 9 | 3,500 | Chief Master Sergeant | Sergeant Major | Brood Warden | 10 | 24% | Any except Siege Tank / Crimson Tank | Unlimited |
| 10 | 4,000 | Second Lieutenant | Second Lieutenant | Brood Champion | 11 | 27% | Any | 1 |
| 11 | 4,500 | First Lieutenant | First Lieutenant | Brood Leader | 12 | 30% | Any | 1 |
| 12 | 5,500 | Captain | Captain | Brood Master | 13 | 35% | Any | 1 |
| 13 | 7,000 | Major | Major | Brood Lord | 14 | 40% | Any | 1 |
| 14 | 9,000 | Lieutenant Colonel | Lieutenant Colonel | Hive Guardian | 15 | 45% | Any | 1 |
| 15 | 12,000 | Colonel | Colonel | Hive Lord | 16 | 50% | Any | 1 |
| 16 | 16,000 | Field Marshal | Field Marshal | Hive Mind | 20 | 85% | Any | 1 |

Private has personal control of its own unit, but no permission to recruit AI followers. The two named tank exclusions refer to distinct objects:

- SOL: `ObjectInfo_Sol_UltraHeavy_SiegeTank`.
- Centauri: `ObjectInfo_Cent_UltraHeavy_CrimsonTank`.

They do not exclude Alien Goliath. From Second Lieutenant onward, all recruited unit types except Harvesters/Shrimp are permitted, subject to native game validation and Formation's existing restrictions.

## Commanders

At genuine round start, each active RTS commander receives:

- **Chief Master Sergeant** for SOL, **Sergeant Major** for Centauri, or **Brood Warden** for Alien: normal tier 9.
- **+3,500 actual scoreboard points**, once per account per round.
- **The same personal-credit bonus as the earned rank**, just like FPS players: tier 9 starts at +24%. No special commander multiplier is applied.

Version 1.2.1 fixes native start ordering: `OnGameStarted` fires before Strategy resolves its initial commander lottery. The rank reset arms a pending starting award; a server-side postfix on `MP_Strategy.ResolveCommanderLottery` awards the selected initial commanders immediately after the lottery returns. It consumes that pending award once. FPS starting-rank announcements also wait until commander selection is complete. Mid-round bootstrap, duplicate callbacks and subsequent commander replacements do not trigger this award.

There is no honorary title or twenty-second appointment delay. Commanders progress through the same score thresholds and exact-rank seat queues as FPS players, and receive private promotion and queue messages. Promotions display the earned rank and its normal bonus. Starting as commander does not claim the final Field Marshal/Hive Mind seat.

While in RTS, the FPS policy is player-only (cap 1). Leaving command retains the earned rank and reward bonus and restores that rank's FPS permissions. A commander already holding Field Marshal/Hive Mind continues to receive 85% after returning to FPS, because the final rank itself grants that bonus.

Late commander assignments and transfers keep their earned rank bonus and do not receive the starting-score grant or a forced starting tier. Reconnects, repeated notifications and penalties cannot repeat the score award. Traitor restrictions take precedence over normal rank rewards for every role.

## Score updates, rounds and reconnects

The source is server-authoritative `Player.Score` in `SilicaCore.dll`. There is no published score-change event; automatic eligibility is checked every configurable **30 seconds**. Promotions occur at the first check detecting eligibility, not necessarily at the exact threshold-crossing instant.

Roster and commander-role refreshes run approximately once per second without sampling scores for automatic progression. Commands and penalty/team-change handling read fresh score for display or baseline setup. `/rank` does not trigger promotions or establish queue priority.

`GameEvents.OnGameStarted` resets ranks, teamkill strikes/cooldowns, recovery offsets, seats/queues, Traitor status and commander roles. The native game clears its score before the next round. The sole direct rank-mod scoreboard award is the starting commander's +3,500; no career score is accumulated. Connected, team-assigned FPS players receive a private starting-rank message using Private / Recruit / Foetus.

State uses stable Steam account IDs, preserving current-round rank and penalties when the game replaces a Player instance on reconnect. Offline exact-rank holders retain their seats for the rest of this round. Offline contenders lose queue priority and enter a fresh observed eligibility batch on reconnect.

The native game may recreate the player's scoreboard at zero. Only an existing penalty/admin recovery offset is rebound to that counter reset, preserving the remaining recovery debt; ordinary score is not accumulated across instances.

No disk round snapshots are restored. Without a verified persistent game round identifier, a same-process mod reload suspends ranks until the next genuine round-start event. First attachment midway through a round cannot reconstruct earlier penalties or arrival order and does not grant starting commander score. Start a new round for complete history.

## Limited seats and team changes

Each team has one seat at each exact tier from Second Lieutenant through Field Marshal/Hive Mind. Promotion to a higher rank releases the previous exact seat. Players cannot skip an occupied intervening tier, even if their score qualifies for a higher one.

Eligibility order is first observed score-check batch, with ascending stable account ID breaking ties. Reconciliation continues while eligible seats can be awarded. One promotion message reports the final rank reached during an update; queue notifications are private and sent only when their state changes.

Changing teams releases old seats and queue entries, demotes five positions with a Private floor, preserves strikes/Traitor lockout and establishes a fresh recovery baseline. Initial team assignment and temporarily null teams during reconnect or cleanup are not switches.

If a demotion's destination has an occupied exact seat, the incumbent retains it and the demoted player falls to the nearest available lower tier. This can increase the normal demotion but never takes a team switch or admin demotion below Private. Existing Traitor lockout remains through a team switch.

## Teamkills, Traitor and recovery

Teamkill attribution surrounds the native server `Player.OnTargetDestroyed`: a responsible player-controlled instigator, a distinct friendly victim unit and an actual native `Kills--` delta are required. This includes friendly AI unit kills under the game's classification. Self-kills, environmental/unattributed deaths and structures alone are excluded.

The first qualifying kill counts immediately. Additional kills inside the configurable **10-second cooldown** do not add strikes and do not extend that cooldown. At expiry another strike counts. Strike 1 demotes one position and strike 2 demotes two additional positions, both with a Private floor. The third counted strike assigns Traitor regardless of current normal rank. Admin demotions do not add teamkill strikes and cannot go below Private.

Team switches and admin demotions never create Traitor status. Only the third counted teamkill strike assigns the hidden **Traitor / Deserter / Outcast** tier:

- Cap 1 and no recruited-unit permissions.
- No further promotions that round, including admin promotions, team changes and reconnects.
- Zero kill/structure personal rewards.
- 75% of other eligible rewards, including Logistics: `base * 0.75`.
- Existing credit balance is retained.

After a non-Traitor demotion, recovery stores `ScoreOffset = current score - landed tier threshold`. Eligibility compares `current score - ScoreOffset` against the ladder, requiring each next threshold gap in additional earned score. The offset remains until round reset. Repeated penalties rebase at the new landed tier and discard pre-penalty surplus. Surplus earned while queued is retained; seat availability never waives recovery.

Example: a 3,200-score Senior Master Sergeant demoted to Master Sergeant has offset 700. Recovering the 500-point gap requires a game score of 3,700; checking 3,200 again does not undo the penalty. Admin promotions waive the requested step and rebase at the awarded tier; later promotions require its next gap.

## Personal-credit integration

### Higher-ranked player kill reward

Killing an enemy player of a higher rank replaces the normal rank-percentage modifier for that single eligible KoH kill reward. The multiplier is `max(2, victimTier - killerTier)`: tier 1 killing tier 2 gets 2x, and tier 5 killing tier 10 gets 5x. For an original **KoH base kill payout** of 100 credits, the second example pays **500 total**, not an extra 500 plus the ordinary payout. Existing KoH cost/fraction rules still calculate the base; this feature does not multiply the unit purchase price separately.

The victim must actually control or drive the killed unit. Commander-owned AI units do not count as ranked-player victims. The killer follows KoH's authoritative credit attribution, so a credited commander-owned attack can qualify. Native enemy relationships are checked, including alliances. Equal/lower ranks, self-kills, friendly kills, missing player state and Traitor attackers use their ordinary reward rules instead. Bounties stay separate and unchanged.

Rank/name metadata is captured before native death handling can clear the victim's controller. The award is scoped to that death and original payment, validated against the current round/Traitor state, floored and capped to remaining wallet capacity. Only after verifying the actual positive balance increase does it announce: `<rank> <Playername> killed higher ranking <Rank> <playername> and got a <rewarded credits>cr reward!!!`. KoH's private message uses the same paid amount. Zero, failed or unmatched payments do not announce a special reward.

Temporary source-specific runtime transpilers adjust these verified earning sites before their existing payment executes once:

| Reward source | Ordinary FPS player | Active commander | Traitor |
|---|---|---|---|
| KoH unit kills | Rank multiplier | Rank multiplier | Zero |
| KoH structure destruction | Rank multiplier | Rank multiplier | Zero |
| KoH zone-presence reward | Rank multiplier | Rank multiplier | 0.75x |
| Logistics delivery/harvester batches | Rank multiplier | Rank multiplier | 0.75x |

Ordinary multiplier is `1 + rankBonus / 100`: Colonel receives 1.50x and Field Marshal/Hive Mind 1.85x. Commanders use this same rank multiplier; there is no separate commander modifier. Modified positive payouts are floored to whole credits and capped to remaining integer balance capacity. Fractional bonus credits are not carried between payments.

Purchases, refunds, starter/admin credits, transfers, bounties and unknown external grants are unchanged. The generic `AddCredits` primitive is not globally patched. KoH messages use the modified amount; Logistics announcements read the payment receipt while its base-credit pending ledger, consumption and retries remain unchanged. Logistics classification requires both its verified reason and the known credit bridge on the call stack.

The patches require reviewed DLL hashes and expected signatures/IL patterns. An incompatible KoH/Logistics update leaves the affected original reward paths intact and records diagnostic status. `/rank` shows per-source availability; `/debug on` enables log output. Review compatibility whenever either economy mod changes. See [INTEGRATION_REQUESTS.md](INTEGRATION_REQUESTS.md) for the future supported reward API replacement.

## Formation integration and native categories

Harvesters and Shrimp are permanently excluded at every tier, including tiers that otherwise permit any unit. Native Harvester type vetoes reject FPS recruitment, direct selection, alien takeover, driver boarding and forced Player.ControlledUnit assignments. This absolute ban also applies when Formation admin size overrides are active. RTS AI orders, purchases/spawns as AI and passenger boarding are preserved; already controlled units are not forcibly stripped on a live load. See [audit/HARVESTER_RESTRICTIONS.md](audit/HARVESTER_RESTRICTIONS.md) for inspected entry points.

Ranks calls the public owner-scoped `FormationApi.RequestPlayerPolicy` using owner `SilicaFieldRanks`. Formation remains the sole recruitment enforcer, checking existing server recruitment and AIGroup.AddUnit paths. Policy changes release disallowed eligible AI followers first, then excess followers, using existing RemoveUnit behavior. Controlled leaders, player-controlled units and driver-owned units are protected; no units are killed or transferred to another owner.

Ranks clears only its own policies. Formation clears policies on round/disconnect/unload cleanup; Ranks detects a missing policy and reapplies it after reconnect. Another owner receives `OwnerConflict` rather than being overwritten. Per-player and match-wide Formation admin size overrides bypass both rank capacity and eligibility, returning `AdminOverride` while keeping the rank policy stored. Restrictions depend on Formation being enabled; `/rank` reports disabled/native fallback status.

Native type/weight data for 61 faction unit assets is recorded in [audit/native-units.json](audit/native-units.json). There is no native LightVehicle enum, and weight alone is misleading. The defaults are an explicit configurable rank interpretation, without Formation editor-category overrides:

| Rank category | Native permission |
|---|---|
| Infantry | `Infantry`, all weights |
| Light | `Cavalry`, all weights; `Special:Light`; ground `Transport`; `Repair` |
| Heavy | `Tank`, `Special` |
| Air | `Aircraft`, plus exact Dropship/Shuttle overrides |
| Siege | `Siege` |
| Other | `None` |

SOL Dropship and Centauri Shuttle are native Transport:Heavy. Exact overrides (`ObjectInfo_Sol_Air_Dropship`, `ObjectInfo_Cent_Air_Shuttle`) require Air permission, even when generic Transport is allowed. Native asset membership does not establish recruitability; native validation remains in force.

Configure `NativeCategoryRules` and `NativeObjectCategoryOverrides` to refine this policy. Rules accept `Type`, exact `Type:Weight`, `Type:*`, or `*`. `Type:Any` means the native Any weight, not a wildcard. Exact overrides determine the object's rank category; denied names win. API signatures and result codes are documented in [INTEGRATION_REQUESTS.md](INTEGRATION_REQUESTS.md).

## Announcements and sound cues

Tier 9 now also announces to all chat in the same format as tiers 15 and 16: Chief Master Sergeant / Sergeant Major / Brood Warden. This includes actual admin promotions and starting commander tier grants. Repeated checks do not repeat an unchanged rank announcement.

Version 1.3.1 omits FPS squad counts from commander promotion announcements; FPS promotion messages retain their cap. Final-tier default and installed threshold are now 16,000. Strikes one and two cannot create Traitor, even if a team switch already reduced the player to Private; strike three does. Reconnects and switches preserve counted strikes, and ignored splash kills cannot advance this threshold.

Automatic and admin promotions to tier 15 (Colonel / Hive Lord) and tier 16 (Field Marshal / Hive Mind) announce to all chat: `<playername> just achieved <rank name> !!`. Each actual crossing is announced; unchanged score checks and occupied queues do not repeat it. If one update reaches both tiers, both achievements are announced while the private promotion message and sound report the final tier once.

Sound cues are private to the affected player, including commanders, through the existing Admin Mod recipient audio stream. Clips are decoded/cached once, queued per recipient, and stopped on round end/unload or disconnect. There is no broadcast audio or disk reading on every promotion.

| Event | File in `UserData/sounds` |
|---|---|
| Promotion to tier 1-4 | `Promotion_1.wav` |
| Promotion to tier 5-9 | `Promotion_2.wav` |
| Promotion to tier 10-14 | `Promotion_3.wav` |
| Promotion to tier 15-16 | `Promotion_4.wav` |
| Actual demotion, including admin/team-switch demotion | `TK_1.wav` |
| First transition to Traitor / Deserter / Outcast | `TK_2.wav` |

The supplied file is named `TK_2.wav`, not `TK2.wav`; configuration uses the existing filename. Reaching Traitor uses only its cue, not both demotion sounds. Ignored teamkills and rejected admin commands make no sound. Starting commander tier 9 uses `Promotion_2.wav`; starting FPS tier 0 is silent. Disable all cues with `Sounds.Enabled = false`, or use an empty filename to disable an individual cue. Missing/invalid files disable only their cue; diagnostics remain behind `/debug`.

Alien promotions use the same four tier groups with `Alien_Promotion_1.wav`, `Alien_Promotion_2.wav`, `Alien_Promotion_3.wav`, and `Alien_Promotion_4.wav`, including `Alien_Promotion_2.wav` for a starting Alien commander. The four filenames are configurable as `Sounds.AlienTier1To4`, `AlienTier5To9`, `AlienTier10To14`, and `AlienTier15To16`. Demotion/Traitor sounds remain shared between factions.

## Chat commands and logging

`/rank` and `/rank playername` now show only the player's active rank, reward bonus, squad quantity and allowed unit tier, for example: `Davey is Specialist 4, has +6% reward bonus, gets 5 squad units up to light units.` Player-only policies are stated directly; Traitor additionally states that combat rewards are zero. Team, score, role labels, raw object identifiers and integration diagnostics are omitted. Admins can inspect reward/Formation availability with `/debug status` instead.

| Command | Access | Behavior |
|---|---|---|
| `/rank` | Player | Show own rank, bonus, squad quantity and allowed types |
| `/rank playername` | Player | Show another connected player's information |
| `/promote playername` | Admin Generic power | Promote one step without its score threshold; seats and Traitor lock apply |
| `/demote playername` | Admin Generic power | Demote one step with Private floor; no teamkill strike |
| `/debug` | Admin Generic power | Toggle Ranks logging |
| `/debug on` / `/debug off` | Admin Generic power | Enable/disable Ranks logging |
| `/debug status` | Admin Generic power | Show current logging state |

Quoted names (`/rank "Player Name"`) and unquoted multiword names work. Exact matches take priority over partial matches; ambiguous names are rejected. Admin rank changes use the existing Admin Mod target hierarchy and remain effective at the next score check.

A scoped chat-dispatch patch handles only these slash commands. Commander Management's `!demote` retains its commander-removal meaning. The Admin Mod team-chat command preference is respected.

All Ranks-owned console/file logging, including startup, round messages, warnings and errors, is gated by debug mode and **off by default**. The toggle lasts until reload/restart. Optional `Debug: true` in the configuration enables startup diagnostics. `/debug` remains available outside a round or after a configuration-load failure; enabling it reports current reward patch status and the recorded initialization error. Normal private player messages remain active. Other mods retain their own debug settings; MelonLoader's own loading/patch infrastructure messages are outside this gate.

## Configuration

First load creates `UserData/Ranks_cfg/Si_Ranks_Config.json` under the server's established UserData directory. Options include:

- `Debug`, `PollSeconds` (30), and `TeamkillCooldownSeconds` (10).
- `StartingCommanderRank` (tier 9) and `StartingCommanderScore` (3,500).
- `TraitorOtherBonusPercent` (-25).
- All seventeen rank definitions: names, score, squad cap, total bonus, seats, categories and exact exclusions.
- `NativeCategoryRules` and `NativeObjectCategoryOverrides`.

Malformed configuration is preserved and disables the mod; defaults never overwrite a load error. Configuration changes require a reload and a new round. Existing installations must include the seventeenth rank entry for version 1.2.0; the installed workspace configuration has already been updated. The old CommanderDelaySeconds appointment option is no longer used.

## Build, installation and verification

From this project folder, build against an existing compatible dedicated-server installation:

```powershell
dotnet build Si_Ranks.csproj -c Release --ignore-failed-sources "-p:ServerRoot=D:\SteamLibrary\steamapps\common\Silica Dedicated Server"
```

Output: `bin/Release/netstandard2.1/Si_Ranks.dll`. Without an explicit ServerRoot, this project's default expects the layout `<server>/Silica-1283/si_ranks/`. The installed game, MelonLoader and Admin Mod assemblies supply the references; this repository is not a standalone game installation.

Back up the existing DLL and configuration before replacing `<server>/Mods/Si_Ranks.dll`. Use compatible Si_Formation as well, relaunch the server normally and begin a new round. Compilation does not install or start the server.

Offline verification commands, from the project folder:

```powershell
dotnet run --project Tests/RankEngineTests.csproj
dotnet run --project Tests/RewardPatchTests.csproj "-p:ServerRoot=D:\SteamLibrary\steamapps\common\Silica Dedicated Server" -- "D:\SteamLibrary\steamapps\common\Silica Dedicated Server"
```

Version 1.4.0 built with zero warnings/errors. **160 engine assertions and 39 offline reward compatibility assertions passed.** The reward harness inspects raw IL from installed DLLs; it does not execute game methods or validate Unity/Mono patch installation. Recorded checks and limitations are in [VALIDATION.md](VALIDATION.md).

Gameplay verification remains user-owned: check startup patch/policy status with `/rank`, commander starting score and names, promotion messages and queues, final-seat exclusivity, rewards/announcements, Traitor reductions, recruitment restrictions and admin overrides, round resets, team switches and reconnect recovery.

The pre-1.2.0 source, installed DLL and configuration were backed up to `<server>/Backups/FieldRanks-commander-20261012-111517/`. Earlier integration backups are documented in VALIDATION.md.
