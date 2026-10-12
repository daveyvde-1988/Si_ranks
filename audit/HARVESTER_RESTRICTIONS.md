# Native FPS restriction audit

Inspected the installed `Silica_Data/Managed/SilicaCore.dll` with the workspace ILSpy tool. Generated excerpts are development artifacts, not runtime dependencies.

- `Player.GetCanSwitchToUnit(Unit unit) : bool` is the server authorization gate for the network `PlayerSelectUnit` request. `NetworkLayer-fps.cs`, lines 269–309, checks it before relaying the packet or assigning `Player.ControlledUnit`. `NetworkLayer.SendPlayerSelectUnit(Player playerSelect, Unit unit) : void` also checks the same gate.
- A client can predict direct control locally. `NetworkLayer.SendPlayerUnitList(Player player) : void` sends the requesting client the authoritative controlled-unit list without changing server ownership. `NetworkGameClient.OnPacket_PlayerUnitList(GameByteStreamReader packetReader) : void` clears and restores client controlled units from that packet.
- `Player.ControlledUnit` is a public `Unit` property with a public setter (`set_ControlledUnit(Unit value) : void`). A final server-only prefix rejects assignment of any non-null native Harvester, including forced control from admin commands or other mods. Null and ordinary unit assignments proceed; existing control is not forcibly released on mod initialization. This guard runs before the setter removes ownership from the previously controlled unit. Purchased/spawned objects still exist as AI units.
- `UseAlienTakeOver.OnUse(Unit caller) : void` and protected `UnitCompartment.AddUnit(Unit unit) : void` assign `ControlledUnit` after calling `SendPlayerSelectUnit`, even if that send was denied. Admin Mod's existing `Event_Units.OnRequestEnterUnit` callback blocks these paths before that assignment. Restrict only `AsDriver` requests; passenger boarding remains unchanged.
- Private `FPSCommanding.InviteToGroupServer(Player player, Target target) : void` calls `player.Group.AddUnit(target.OwnerUnit)`. Patch this entry point and public `Silica.AI.AIGroup.AddUnit(Unit unit) : bool` for an absolute FPS recruitment veto, independent of Formation's configurable or admin limits.
- RTS AI groups are preserved: recruitment veto requires a player-controlled group leader and `player.Group == group`. Player/driver-controlled units, the player's own controlled unit, and existing membership are excluded from recruitment changes.
- Native `ObjectInfo.UnitType == UnitType.Harvester` (numeric 7) covers alien `ObjectInfo_Alien_Shrimp` and human `ObjectInfo_Cent_HeavyHarvester` and `ObjectInfo_Sol_HoverHarvester`, including the intro variant. No string matching or interception of purchases is needed.

`HarvesterRestrictions.cs` compiled against installed game/Admin Mod/Harmony assemblies using `HarvesterAudit.csproj`: zero warnings and errors. Runtime server testing remains necessary.

Sound inventory: requested `TK2.wav` was absent; `TK_2.wav` exists. `TK_1.wav` and all four `Promotion_*.wav` files exist.
