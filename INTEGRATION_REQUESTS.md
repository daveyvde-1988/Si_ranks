# Integration status — Field Ranks 1.3.0

The previously blocked Formation integration is now implemented and installed as **Si_Formation 2.10.0**. Temporary source-specific reward patches are implemented inside **Si_Ranks 1.3.0**; KoH/Logistics source and DLL files remain unchanged. The user's corrected squad convention is **1 = controlled player alone, zero AI followers**.

## Implemented Formation API

Public static `Si_Formation.FormationApi` methods, main server thread and readiness checked:

```csharp
ChangeResult RequestPlayerPolicy(ulong steamId, string owner, int limit,
    string[] allowedCategories, string[] allowedObjectNames, string[] deniedObjectNames);
ChangeResult TryGetPlayerPolicy(ulong steamId, out string owner, out int limit,
    out string[] allowedCategories, out string[] allowedObjectNames, out string[] deniedObjectNames);
ChangeResult ClearPlayerPolicy(ulong steamId, string owner);
ChangeResult ClearOwnerPolicies(string owner);
ChangeResult TryGetUnitDefinitions(out string[] objectNames, out string[] unitTypes, out string[] unitWeights);
string[] GetNativeUnitCategories();
```

Limits are 1–50 including the leader. Categories use native `UnitType`, exact `UnitType:UnitWeight`, `Type:*`, or `*`; native `Type:Any` is exact Any, not a wildcard. Null category rules are unrestricted; empty rules permit only explicitly allowed names; denied names win. Arrays are cloned. Different owners receive `OwnerConflict`; identical requests are idempotent. The owning mod can clear its policies even when admin overrides shadow them. Admin capacity overrides bypass category restrictions too, returning `AdminOverride` while retaining the stored rank policy. Disabling Formation restores native behavior; Ranks reports this status.

Enforcement stays inside Formation's existing recruitment, AIGroup.AddUnit and follower-release behavior. Player-controlled leaders/followers and driver-owned units remain protected. Reset/disconnect/unload cleanup is built in, and Ranks reapplies missing own policies after reconnect. Source: `../si_formation/PlayerPolicies.cs` and `FormationSettings.cs`; additional details in its README.

Native data for 61 faction unit assets is recorded in `audit/native-units.json`. Defaults intentionally map Cavalry/Special:Light/ground Transport/Repair to light privilege, Tank/Special to heavy privilege, and Aircraft plus exact airborne Transport exceptions to Air. This is configurable rank interpretation, not a native LightVehicle enum. Dropship/Shuttle require explicit Air exceptions. Both 3,000/3,500 tiers exclude the distinct SOL Siege Tank and Centauri Crimson Tank, not Alien Goliath.

## Temporary personal-credit integration

The installed Si_Resources manages team resources. Logistics uses the KoH personal wallet. Source-specific runtime transpilers adjust verified kill, structure, zone-presence and Logistics rewards before their original payment runs once; the generic AddCredits method is not globally patched. Purchases, refunds, bounties, starter/admin grants, transfers and unknown external grants are untouched.

The patch validates reviewed DLL hashes, signatures and IL structure. Updates to KoH or Logistics require review; incompatible sites retain base behavior with diagnostics. Logistics source classification also verifies its actual bridge on the caller stack. Whole-credit modified payouts are floored and capped at remaining balance capacity. Original reward messages are adjusted without changing Logistics's pending base-credit ledger or retries.

## Later replacement: supported reward API prompt

> Locate the source of the deployed Si_KingOfTheHill personal economy (Si_Resources is a separate team-resource mod). Add an owner-scoped typed reward modifier registration API receiving stable account ID, original integer base payout and an explicit reward source. Invoke it once before unit kill, structure destruction, zone-presence and typed Logistics delivery/harvester rewards reach the existing wallet. Preserve source distinctions: exclude purchases, refunds, starter/admin grants, transfers, bounties and unknown external credits. Define modifier ownership, integer rounding, capacity limits, cleanup and inactive behavior. Give Logistics a typed source-aware grant entry point while preserving the existing public methods. Its pending ledger must consume base rewards exactly once, with correct failure restoration, and chat must show actual paid credits. Support normal rank total bonuses, commanders using their earned rank bonus identically to FPS players, and Traitor combat zero/other eligible 0.75× without debiting balances. Compile without deploying unless authorized. Report exact signatures and all covered sources. Then replace Si_Ranks's temporary hash-gated transpilers with the confirmed API and remove the temporary patches so modifiers cannot apply twice.

Gameplay verification remains with the user. Builds and offline instruction checks do not verify live recruitment, balance mutations, call-stack provenance or message delivery.
