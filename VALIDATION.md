# Validation and installation — 13 October 2026

## Current update: 1.3.0 rank rewards, unit bans, announcements and sounds

Release build passed with zero warnings/errors. **135 engine assertions and 31 offline reward compatibility assertions passed.** Added engine coverage for commander tier bonuses, all-tier harvester exclusions, native category changes, grouped sound selection, one cue per aggregate promotion, no repeated cues for unchanged/ignored state, Traitor precedence, top-tier global announcements, disabled cues and filename validation. The current Logistics DLL changed since the original integration; its PayDueRewards/TryGrant source and payout/chat IL were re-inspected and confirmed compatible before updating the reviewed hash.

All six actual WAV files passed offline decoder/playback-harness checks: PCM conversion/padding, invalid-file rejection, recipient-only queueing and reset cancellation. This verifies data handling, not audible game playback. Direct-control, alien takeover, boarding, setter assignment and FPS recruitment entry points were inspected against installed game/Admin Mod assemblies; no game methods or live server were executed for verification. Build and offline checks do not prove client prediction correction or sound delivery in game.

Old DLL/source/configuration backup: `Backups/FieldRanks-sounds-20261013-010652/`. Updated installed rank configuration to remove the commander-only reward bonus, move Transport/Repair to Light, remove Harvester from Heavy, and enable the six sound mappings. The actual Traitor sound filename is TK_2.wav. All mod-owned logging remains behind /debug. No other mod source, DLL or settings were changed and the server was not started/stopped.

User gameplay checks: commander tier9 pays +24%, then later rank percentages; tier15/16 global achievement messages; private cues in FPS and RTS; demotion/Traitor cue separation and splash cooldown; Light transport/repair recruitment with airborne transport exceptions preserved; Harvester/Shrimp control/recruitment denial including admin overrides, human boarding and Alien takeover; unchanged RTS AI harvesting/purchases; real Logistics balances/messages after its compatible DLL hash update.

## Current update: 1.2.1 initial commander lottery timing

Release build passed with zero warnings/errors; **105 engine and 29 offline reward compatibility assertions passed**. Current installed SilicaCore was decompiled locally: `MP_Strategy.OnMissionStateChanged(STARTED)` invokes `OnGameStarted`, but `UpdateGameLoop` resolves the initial commander lottery afterward. The old one-shot start-event snapshot could therefore contain no winners. The fix finalizes the pending genuine-round award from a postfix on `ResolveCommanderLottery`, once the selected commanders are assigned. Starting FPS messages are delayed to the same boundary. A confirmed-start flag distinguishes genuine start from a mid-round bootstrap when suppressing duplicate start callbacks; pending state is cleared on stop/unload. Native method existence/order were inspected; patch execution and scoreboard synchronization still require gameplay testing.

Old DLL/source/configuration backup: `Backups/FieldRanks-startscore-20261012-121238/`. No configuration changes or server start/stop were required. Relaunch the server normally and start a new round; initial human commanders should show tier 9 and +3,500 actual scoreboard points, with no second award after role changes.

## Current update: 1.2.0 earned final tier and starting commanders

Release build passed with zero warnings/errors. **105 engine assertions and 29 offline reward compatibility assertions passed.** Added coverage for the 15,000 threshold, 20 cap, 85% ordinary payout, per-team exact final seat and seat release, maximum admin rank, legitimate starting commander score/rank, refusal for non-commanders, repeated grants/reconnect/penalty bypass prevention, immediate commander bonus, retained earned rank on role exit, fresh-round reset and all faction starting names. The installed DLL hash matched its Release artifact. Configuration checks confirmed the new final tier and starting commander settings while preserving all previous sixteen rank entries.

Runtime writes the one-time score award through the actual server Player.Score setter only on a genuine new round; bootstrap, role transfers and reconnects cannot award it. Duplicate round-start notifications are guarded. Honorary rank titles and the twenty-second appointment delay were removed; commander role is separate from the displayed earned rank and gives the existing replacement 85% reward modifier. Logging stays behind /debug.

Old installed DLL, configuration and source are backed up in `Backups/FieldRanks-commander-20261012-111517/`. Installed Si_Ranks 1.2.0; no Formation/economy DLL or source was changed, and the server was not started or stopped. Native event timing, scoreboard synchronization, commander-start messages and earned final-rank gameplay remain for user testing after a normal server relaunch and a new round.

## Previous update: 1.1.2 logging gate


Built and installed Si_Ranks 1.1.2, zero warnings/errors. 86 engine assertions and 29 offline reward compatibility assertions passed. A production-source scan confirms all direct MelonLogger calls exist only in RankLog.cs and require the debug flag, including warning/error paths. The admin-only `/debug [on|off|status]` handler is available even outside an active round or following configuration failure. Logging is off by default; normal private player messages remain active. MelonLoader's own loading/patch infrastructure logs are not mod-owned and are not suppressed. Command delivery and permission behavior still require gameplay verification.

Previous source, installed DLL and configuration were backed up to `Backups/FieldRanks-debug-20261012-004748/`. Installed DLL hash matched its Release build and the configuration hash remained unchanged. No server was started or stopped.

## Previous update: 1.1.1 thresholds and announcements

Si_Ranks **1.1.1** built and installed with zero warnings/errors. **86 engine assertions passed**, with existing commander/seat/recovery cases updated for the new threshold gaps. The installed DLL hash matches the Release build. Configuration comparison against the backup confirms only the requested threshold changes; polling was already 30 seconds and the source default now matches it. Starting-rank messages target team-assigned non-commanders at actual round start, use faction names and are suppressed for a mid-round bootstrap. Announcement delivery remains for gameplay verification.

Before installation, source, installed DLL and configuration were backed up to `Backups/FieldRanks-20261012-004451/`. No Formation or economy code was changed for this update; no server was started or stopped. Relaunch normally to load the new DLL and configuration.

## Previous 1.1.0 integration validation — 11 October 2026

- **Si_Ranks 1.1.0 Release build passed: zero warnings/errors.**
- **Si_Formation 2.10.0 Release build passed: zero warnings/errors.** No Formation test suites were run, per its AGENTS.md; gameplay testing remains user-owned.
- Rank engine console suite: **86 assertions passed**, including corrected minimum cap 1, recovery/reconnects, rounds, seats/queues, strikes/cooldown, Traitor precedence, commander replacement, admin persistence, native mapping validation and verified airborne transport exception.
- Reward verification console suite: **29 assertions passed**. Checked rounding, saturation, Traitor zero/0.75×, commander 1.85×, and surgical transformations of raw method-body IL from the current installed KoH/Logistics DLLs. All five patched sites retain their original payment/grant, modify amounts once, and leave Logistics pending-ledger operands unchanged. Incompatible IL is rejected.
- Offline native ObjectInfo asset extraction verified 61 faction unit assets against installed SilicaCore field layout, without editor-category overrides. Evidence and source asset hash: `audit/native-units.json`.
- Tests use separate output/intermediate directories so their executable launchers cannot overwrite each other.
- **Installed:** `Mods/Si_Formation.dll` and `Mods/Si_Ranks.dll`; installed SHA256 hashes match their Release build artifacts.
- **Backup:** `Backups/FieldRanks-integration-20261011-215140/` contains both prior source folders and the previous installed Formation DLL. There was no previously installed Si_Ranks.dll.
- KoH/Logistics DLL hashes remain the inspected original values. Their source/DLL/config files were not edited. No other mod configuration was changed.
- The server was not started or stopped. No gameplay verification was performed.

## Reproducible checks

```powershell
dotnet build Silica-1283/si_ranks/Si_Ranks.csproj -c Release --no-restore
dotnet run --project Silica-1283/si_ranks/Tests/RankEngineTests.csproj --no-restore
dotnet run --project Silica-1283/si_ranks/Tests/RewardPatchTests.csproj --no-restore -- "D:\SteamLibrary\steamapps\common\Silica Dedicated Server"
```

Formation was built from its selected folder using its required command:

```powershell
dotnet build Si_Formation.csproj -c Release --ignore-failed-sources "-p:ServerRoot=D:\SteamLibrary\steamapps\common\Silica Dedicated Server"
```

## Installed file hashes

| File | SHA256 |
|---|---|
| Si_Ranks.dll | `3546F40B748900DBAD113224435A81181A87DCE9CE7D2EC723378656F7333CC2` |
| Si_Formation.dll | `2CC6CE6A14ED99144645F40AB0D027EB0560A8DBCC2DAC1AE46C3B2D3B23A95F` |
| Original Formation backup | `CF0DB344916C673102A599CF957987820DD434F53DA235CA6F3DD94814324BCD` |
| Unchanged Si_KingOfTheHill.dll | `5377B71A034E065DF76D5A3F595DE733DAF24D9A875DEF9D0C092BCCAAABF8BC` |
| Unchanged Si_Logistics.dll | `B6A363CF4CADCBED16C1F65B7F403D8BED65D0CE5B7B2679F82E35B5F32AAAF5` |

## Limits of verification

The installed Harmony build targets Mono, while offline verification runs under .NET 8. The reward harness therefore parses actual raw MethodBody IL directly; it does **not** install Harmony patches, execute game methods, or verify Unity/Mono dynamic-method generation. Live patch installation, caller-stack provenance, announcements and balance mutation still require server/gameplay testing. Runtime hash/signature/IL failures are reported and leave incompatible reward paths unchanged.

Launch the server normally with both installed DLLs and begin a new round. Check `/rank` for active reward sites and successful Formation policy status; verify kill/structure/Logistics balances and displayed amounts, commander 1.85× replacement, Traitor combat zero/other 0.75×, normal purchases/refunds/bounties unchanged, one-player minimum, faction unit categories including Dropship/Shuttle, exact tank exclusions, follower release after demotion, and admin override precedence. Also test round reset, reconnects, splash cooldown, team switches and limited-seat queues.
