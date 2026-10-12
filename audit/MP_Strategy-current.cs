using System;
using System.Collections.Generic;
using System.Linq;
using Silica;
using Silica.AI;
using Silica.UI;
using Silica.Utilities;
using Silica.Voting;
using UnityEngine;

[DisallowMultipleComponent]
public class MP_Strategy : GameModeExt, IStrategyGameMode
{
	protected enum ECoopDifficulty
	{
		EASY,
		MEDIUM,
		HARD,
		IMPOSSIBLE
	}

	private enum ERPCs : byte
	{
		INIT_GAME,
		REQUEST_JOIN_TEAM,
		REQUEST_ROLE,
		CLEAR_REQUEST,
		SYNCH_UNITCAP,
		SYNCH_COMMANDER,
		MISSION_STATE_CHANGED,
		TIMER_UPDATE,
		VOTETIMER_UPDATE,
		TEAM_VERSUS,
		VOTE_DAYTIME,
		VOTE_TEAMVERSUS,
		TEAM_LOCK,
		REQUEST_COMMANDER_LOTTERY_ENTRY,
		REQUEST_COMMANDER_LOTTERY_REMOVE,
		SYNC_COMMANDER_LOTTERY_ENTRY,
		SYNC_COMMANDER_LOTTERY_REMOVE,
		SYNC_ALLOW_TEAM_SWITCH,
		SYNC_MAX_PLAYER_DIFFERENCE,
		SYNCH_ROLE,
		MAX
	}

	[Header("Cooperative")]
	[Tooltip("Whether this mode is cooperative, forcing everyone onto one team only")]
	public bool IsCooperative;

	[Tooltip("[COOPERATIVE] Damage applied to players is scaled by this amount (in %)")]
	[Range(0f, 10f)]
	public float CoopDamageToPlayersScale = 0.25f;

	[Tooltip("[COOPERATIVE] Damage dealt by players is scaled by the amount in the curve (X is input damage, Y is damage scale in %)")]
	public AnimationCurve CoopDamageByPlayersScale = AnimationCurve.EaseInOut(0f, 3f, 1000f, 1f);

	protected ECoopDifficulty CurrentCoopDifficulty = ECoopDifficulty.MEDIUM;

	[Header("Team setups")]
	[Tooltip("List of team setups")]
	public List<StrategyTeamSetup> TeamSetups;

	[Tooltip("Strategy Mode controller prefab to use for this game mode")]
	public StrategyMode StrategyModePrefab;

	[Header("Local Infrastructure")]
	[Tooltip("Handler for spawning the pre-existing infrastructure, such as old bunkers")]
	public PrefabsToPointsHandler LocalInfrastructureSpawner;

	[Tooltip("Team to assign spawned local infrastructure to")]
	public Team LocalInfrastructureTeam;

	[Tooltip("Spawn probability multiplier for spawning local infrastructure (multiplies the values in the handler)")]
	public float LocalInfrastructureProbabilityScale = 1f;

	[Header("Timers")]
	[Tooltip("How long to wait before the game begins (in s)")]
	public float StartDelay = 10f;

	[Tooltip("How long to wait after a match ends before the game restarts (in s)")]
	public float RestartDelay = 30f;

	[Tooltip("How long to wait before respawning a player after death (in s)")]
	public float RespawnTime = 10f;

	[Tooltip("How long the defeat screen is shown to a first-eliminated player before they are sent to team select (in s)")]
	public float DefeatChangeSideTime = 6f;

	[Header("Camera")]
	[Tooltip("Reference for positioning the camera when no team is selected")]
	public Transform NoTeamCameraRef;

	[Header("Tutorials")]
	[Tooltip("List of tutorials relevant to this game mode, processed from first to last")]
	public List<TutorialData> Tutorials = new List<TutorialData>();

	private Dictionary<Player, float> PlayerRespawnTracker = new Dictionary<Player, float>();

	private float RespawnMenuTimer;

	private float DefeatChangeSideTimer;

	private List<TutorialData> CompletedTutorials = new List<TutorialData>();

	private ETeamSelect ShowTeamSelectMenu = ETeamSelect.SELECT_TEAM;

	private bool m_OpenRoleSelectOnTeamConfirm;

	private ETeamRole m_LocalRole;

	private Dictionary<EDayTime, byte> DaytimeVoteResults = new Dictionary<EDayTime, byte>(4);

	private Dictionary<ETeamsVersus, byte> TeamsVersusVoteResults = new Dictionary<ETeamsVersus, byte>(4);

	private float NoCommanderOrderTime = 30f;

	private float MultStartingDist = 1f;

	private float MultStartingRes = 1f;

	private float MultLocationRes = 1f;

	private float MultLocationResHide;

	private Team LockedToTeam;

	private int MaxPlayerDifference;

	private bool AllowTeamSwitch;

	private bool QueueCoopSpawnBaseStructures;

	private const int NEXT_MAP_CHOICES = 3;

	private Dictionary<Team, List<Player>> CommanderLottery = new Dictionary<Team, List<Player>>(8);

	private float MutinyLockTime;

	public ETeamRole LocalRole
	{
		get
		{
			return m_LocalRole;
		}
		private set
		{
			if (m_LocalRole != value)
			{
				ETeamRole localRole = m_LocalRole;
				m_LocalRole = value;
				OnLocalRoleChanged?.Invoke(localRole, value);
			}
		}
	}

	public EMissionState MissionState { get; private set; }

	public override bool GameBegun => (int)MissionState >= 1;

	public override bool GameOver => (int)MissionState >= 2;

	public override bool LocalWon
	{
		get
		{
			if (MissionState == EMissionState.ENDED)
			{
				return GetWinningTeam() == Player.CurrentPlayer.Team;
			}
			return false;
		}
	}

	public override bool LocalLost
	{
		get
		{
			if (MissionState == EMissionState.ENDED)
			{
				return GetWinningTeam() != Player.CurrentPlayer.Team;
			}
			return false;
		}
	}

	public override bool ObjectivesGenerateAutomatic
	{
		get
		{
			if (LocalHasCommander)
			{
				return NoCommanderOrderTime > 30f;
			}
			return true;
		}
	}

	public override bool ObjectivesGenerateFromAIAgent => LocalHasCommander;

	public override bool AllowTeamSwitching
	{
		get
		{
			if (IsCooperative)
			{
				return false;
			}
			return AllowTeamSwitch;
		}
		set
		{
			if (AllowTeamSwitch != value)
			{
				AllowTeamSwitch = value;
				if (Game.GetIsServer())
				{
					RPC_AllowTeamSwitching();
				}
			}
		}
	}

	public override bool IsOneTeamOnly => IsCooperative;

	public bool MaximumPlayerDifferenceEnabled => MaxPlayerDifference > 0;

	public int MaximumPlayerDifference
	{
		get
		{
			return MaxPlayerDifference;
		}
		set
		{
			if (MaxPlayerDifference != value)
			{
				MaxPlayerDifference = value;
				if (Game.GetIsServer())
				{
					RPC_MaxPlayerDifference();
				}
			}
		}
	}

	public bool WasLocalCommanderMutiniedRecently => Time.time < MutinyLockTime;

	public float StartingDistanceMult => MultStartingDist;

	public event Action<ETeamRole, ETeamRole> OnLocalRoleChanged;

	public StrategyMode GetStrategyModePrefab()
	{
		return StrategyModePrefab;
	}

	public override bool RequestSpawnPlayerAtSpawnPoint(Player player, ObjectInfo unitInfo, Vector3 position)
	{
		ShowTeamSelectMenu = ETeamSelect.NONE;
		StrategyHandler.SwitchTo(StrategyScreenType.None);
		return base.RequestSpawnPlayerAtSpawnPoint(player, unitInfo, position);
	}

	public override Objective GetCurrentObjective()
	{
		if (Tutorials.Count == CompletedTutorials.Count)
		{
			return Objective.Empty();
		}
		Unit currentUnit = Unit.CurrentUnit;
		Team currentTeam = Team.CurrentTeam;
		if (!currentUnit || !currentTeam)
		{
			return Objective.Empty();
		}
		Objective aIAgentObjective = CurrentObjective.GetAIAgentObjective(currentUnit, currentTeam);
		if (ObjectivesGenerateFromAIAgent && !aIAgentObjective.IsEmpty())
		{
			NoCommanderOrderTime = 0f;
			aIAgentObjective.AssignedByCommander = true;
			return aIAgentObjective;
		}
		if (NoCommanderOrderTime <= 30f)
		{
			return Objective.Empty();
		}
		Objective result = Objective.Empty();
		int i = 0;
		for (int count = Tutorials.Count; i < count; i++)
		{
			TutorialData tutorialData = Tutorials[i];
			if (!tutorialData || CompletedTutorials.Contains(tutorialData))
			{
				continue;
			}
			result = tutorialData.CreateObjective(currentUnit, currentTeam, out var objectiveTutorialObject);
			if (result.IsEmpty())
			{
				continue;
			}
			if (result.IsSameAsCurrent())
			{
				switch (objectiveTutorialObject.EvaluateObjective(currentUnit))
				{
				case ObjectiveCompletion.Cancelled:
					CurrentObjective.ObjectiveCancelled();
					goto default;
				case ObjectiveCompletion.Failed:
					CurrentObjective.ObjectiveFailed();
					goto default;
				case ObjectiveCompletion.Succeeded:
					CurrentObjective.ObjectiveSucceeded();
					goto default;
				default:
					result = Objective.Empty();
					CompletedTutorials.Add(tutorialData);
					continue;
				case ObjectiveCompletion.Ongoing:
					break;
				}
			}
			break;
		}
		return result;
	}

	public override float GetDamageByPlayerScale(float damage)
	{
		if (IsCooperative)
		{
			return CoopDamageByPlayersScale.Evaluate(damage);
		}
		return 1f;
	}

	public override float GetDamageToPlayerScale()
	{
		if (IsCooperative)
		{
			return CoopDamageToPlayersScale;
		}
		return 1f;
	}

	public override bool GetPlayerIsCommander(Player player)
	{
		if (!player)
		{
			return false;
		}
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup) && teamSetup.Commander == player)
			{
				return true;
			}
		}
		return false;
	}

	public bool GetAnyPlayersOnAnyTeam()
	{
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup) && teamSetup.Team.GetNumPlayers() > 0)
			{
				return true;
			}
		}
		return false;
	}

	public StrategyTeamSetup GetStrategyCommanderTeamSetup(Player player)
	{
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup) && teamSetup.Commander == player)
			{
				return teamSetup;
			}
		}
		return null;
	}

	public BaseTeamSetup GetTeamSetup(Player player)
	{
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup) && teamSetup.Team == player.Team)
			{
				return teamSetup;
			}
		}
		return null;
	}

	public bool GetLocalTeamEliminated()
	{
		return GetTeamSetup(Team.CurrentTeam)?.GetHasLost() ?? false;
	}

	public bool GetCooperativeTeamEliminated()
	{
		if (!IsCooperative)
		{
			return false;
		}
		if (LockedToTeam == null)
		{
			return false;
		}
		return GetTeamSetup(LockedToTeam)?.GetHasLost() ?? false;
	}

	public bool GetShouldEndGame()
	{
		if (Game.GetIsSingleplayer() && GetLocalTeamEliminated())
		{
			return true;
		}
		if (GetCooperativeTeamEliminated())
		{
			return true;
		}
		int num = 0;
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (!GetTeamSetupActive(teamSetup))
			{
				continue;
			}
			if (teamSetup.GetHasLost())
			{
				if ((bool)GlobeMissionData.CurrentMission && GlobeCampaign.CurrentTeam == teamSetup.Team)
				{
					return true;
				}
			}
			else
			{
				num++;
			}
		}
		return num <= 1;
	}

	public Team GetWinningTeam()
	{
		Team result = null;
		int num = 0;
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup) && !teamSetup.GetHasLost())
			{
				num++;
				result = teamSetup.Team;
			}
		}
		if (num == 1)
		{
			return result;
		}
		return null;
	}

	public override void VoteTeamVersus(ETeamsVersus teamVersus)
	{
		if (TeamsVersusVotes.TryGetValue(Player.CurrentPlayer, out var value))
		{
			if (teamVersus == value)
			{
				TeamsVersusVotes.Remove(Player.CurrentPlayer);
				teamVersus = ETeamsVersus.NONE;
			}
			else
			{
				TeamsVersusVotes[Player.CurrentPlayer] = teamVersus;
			}
		}
		else
		{
			TeamsVersusVotes.Add(Player.CurrentPlayer, teamVersus);
		}
		RPC_VoteTeamVersus(Player.CurrentPlayer, teamVersus);
	}

	public float GetDayTimeVoteResult()
	{
		EDayTime eDayTime = EDayTime.NONE;
		byte b = 0;
		if (DaytimeVotes.Count > 0)
		{
			DaytimeVoteResults.Clear();
			foreach (KeyValuePair<Player, EDayTime> daytimeVote in DaytimeVotes)
			{
				if (DaytimeVoteResults.TryGetValue(daytimeVote.Value, out var value))
				{
					value++;
					DaytimeVoteResults[daytimeVote.Value] = value;
				}
				else
				{
					DaytimeVoteResults.Add(daytimeVote.Value, 1);
				}
			}
			foreach (KeyValuePair<EDayTime, byte> daytimeVoteResult in DaytimeVoteResults)
			{
				if (daytimeVoteResult.Value > b || (daytimeVoteResult.Value == b && UnityEngine.Random.Range(0f, 1f) > 0.5f))
				{
					eDayTime = daytimeVoteResult.Key;
					b = daytimeVoteResult.Value;
				}
			}
		}
		if (eDayTime == EDayTime.NONE)
		{
			eDayTime = (EDayTime)UnityEngine.Random.Range(1, 5);
		}
		float result = 11f;
		switch (eDayTime)
		{
		case EDayTime.DAWN:
			result = 7f;
			break;
		case EDayTime.DAY:
			result = 11f;
			break;
		case EDayTime.DUSK:
			result = 17f;
			break;
		case EDayTime.NIGHT:
			result = 23f;
			break;
		}
		return result;
	}

	public ETeamsVersus GetTeamsVersusVoteResult()
	{
		ETeamsVersus eTeamsVersus = ETeamsVersus.NONE;
		byte b = 0;
		if (TeamsVersusVotes.Count > 0)
		{
			TeamsVersusVoteResults.Clear();
			foreach (KeyValuePair<Player, ETeamsVersus> teamsVersusVote in TeamsVersusVotes)
			{
				if (TeamsVersusVoteResults.TryGetValue(teamsVersusVote.Value, out var value))
				{
					value++;
					TeamsVersusVoteResults[teamsVersusVote.Value] = value;
				}
				else
				{
					TeamsVersusVoteResults.Add(teamsVersusVote.Value, 1);
				}
			}
			foreach (KeyValuePair<ETeamsVersus, byte> teamsVersusVoteResult in TeamsVersusVoteResults)
			{
				if (teamsVersusVoteResult.Value > b || (teamsVersusVoteResult.Value == b && UnityEngine.Random.Range(0f, 1f) > 0.5f))
				{
					eTeamsVersus = teamsVersusVoteResult.Key;
					b = teamsVersusVoteResult.Value;
				}
			}
		}
		if (eTeamsVersus == ETeamsVersus.NONE)
		{
			eTeamsVersus = (ETeamsVersus)UnityEngine.Random.Range(1, 4);
		}
		return eTeamsVersus;
	}

	public override void ProcessNetRPC(GameByteStreamReader packetReader, byte rpcIndex)
	{
		if (rpcIndex > 20)
		{
			base.ProcessNetRPC(packetReader, rpcIndex);
			return;
		}
		switch ((ERPCs)rpcIndex)
		{
		case ERPCs.INIT_GAME:
			if (!Game.GetIsServer())
			{
				ReadInitData(packetReader);
			}
			break;
		case ERPCs.REQUEST_JOIN_TEAM:
		{
			if (!Game.GetIsServer())
			{
				break;
			}
			NetworkID networkID5 = packetReader.ReadNetworkID();
			int channel4 = packetReader.ReadByte();
			Player player7 = Player.FindPlayer(networkID5, channel4);
			if (!player7)
			{
				NetworkID networkID2 = networkID5;
				Debug.LogError("MP_Strategy::ProcessNetRPC - Player was NULL for REQUEST_JOIN_TEAM, ID: " + networkID2.ToString() + ", channel: " + channel4);
				break;
			}
			Team team4 = packetReader.ReadTeam();
			if (player7.Team == team4)
			{
				RPC_ClearRequest(player7);
				break;
			}
			player7.Team = team4;
			NetworkLayer.SendPlayerSelectTeam(player7, team4);
			break;
		}
		case ERPCs.REQUEST_ROLE:
		{
			if (!Game.GetIsServer())
			{
				break;
			}
			NetworkID networkID6 = packetReader.ReadNetworkID();
			int channel5 = packetReader.ReadByte();
			Player player9 = Player.FindPlayer(networkID6, channel5);
			if (!player9)
			{
				NetworkID networkID2 = networkID6;
				Debug.LogError("MP_Strategy::ProcessNetRPC - Player was NULL for REQUEST_ROLE, ID: " + networkID2.ToString() + ", channel: " + channel5);
				break;
			}
			switch ((ETeamRole)packetReader.ReadByte())
			{
			case ETeamRole.NONE:
			{
				DestroyAllUnitsForPlayer(player9);
				if (PlayerRespawnTracker.ContainsKey(player9))
				{
					PlayerRespawnTracker.Remove(player9);
				}
				BaseTeamSetup strategyCommanderTeamSetup2 = GetStrategyCommanderTeamSetup(player9);
				if (strategyCommanderTeamSetup2 != null)
				{
					SetCommander(strategyCommanderTeamSetup2.Team, null);
					RPC_SynchCommander(strategyCommanderTeamSetup2.Team);
				}
				else
				{
					RPC_ClearRequest(player9);
				}
				RPC_SynchRole(player9, ETeamRole.NONE);
				break;
			}
			case ETeamRole.COMMANDER:
			{
				BaseTeamSetup teamSetup = GetTeamSetup(player9.Team);
				if (teamSetup != null && !GetCommanderForTeam(player9.Team))
				{
					SetCommander(teamSetup.Team, player9);
					RPC_SynchCommander(teamSetup.Team);
					RPC_SynchRole(player9, ETeamRole.COMMANDER);
				}
				else
				{
					RPC_ClearRequest(player9);
				}
				break;
			}
			case ETeamRole.UNIT:
			{
				BaseTeamSetup strategyCommanderTeamSetup = GetStrategyCommanderTeamSetup(player9);
				if (strategyCommanderTeamSetup != null)
				{
					SetCommander(strategyCommanderTeamSetup.Team, null);
					RPC_SynchCommander(strategyCommanderTeamSetup.Team);
				}
				RPC_SynchRole(player9, ETeamRole.UNIT);
				break;
			}
			}
			break;
		}
		case ERPCs.CLEAR_REQUEST:
			if (!Game.GetIsServer())
			{
				NetworkID networkID3 = packetReader.ReadNetworkID();
				int channel2 = packetReader.ReadByte();
				Player player3 = Player.FindPlayer(networkID3, channel2);
				if (!player3)
				{
					NetworkID networkID2 = networkID3;
					Debug.LogError("MP_Strategy::ProcessNetRPC - Player was NULL for CLEAR_REQUEST, ID: " + networkID2.ToString() + ", channel: " + channel2);
				}
				else if (player3 == Player.CurrentPlayer)
				{
					WaitingForServer = false;
				}
			}
			break;
		case ERPCs.SYNCH_UNITCAP:
			if (!Game.GetIsServer())
			{
				m_UnitCapMultiplier = packetReader.ReadByte();
			}
			break;
		case ERPCs.SYNCH_COMMANDER:
			if (!Game.GetIsServer())
			{
				Player player5 = Player.FindPlayer(packetReader.ReadNetworkID(), packetReader.ReadByte());
				Team team3 = packetReader.ReadTeam();
				bool kicked = packetReader.ReadBool();
				Player prevCommander = GetTeamSetup(team3)?.Commander;
				SetCommander(team3, player5);
				ShowRoleSelectionIfOusted(prevCommander, kicked);
			}
			break;
		case ERPCs.MISSION_STATE_CHANGED:
			if (!Game.GetIsServer())
			{
				OnMissionStateChanged((EMissionState)packetReader.ReadByte());
			}
			break;
		case ERPCs.TIMER_UPDATE:
			if (!Game.GetIsServer())
			{
				Timer = packetReader.ReadFloat();
			}
			break;
		case ERPCs.VOTETIMER_UPDATE:
			if (!Game.GetIsServer())
			{
				VoteTimer = packetReader.ReadFloat();
			}
			break;
		case ERPCs.TEAM_VERSUS:
			if (!Game.GetIsServer())
			{
				SetTeamVersusMode((ETeamsVersus)packetReader.ReadByte());
			}
			break;
		case ERPCs.VOTE_DAYTIME:
		{
			NetworkID networkID7 = packetReader.ReadNetworkID();
			int channel6 = packetReader.ReadByte();
			Player player11 = Player.FindPlayer(networkID7, channel6);
			if (!player11)
			{
				NetworkID networkID2 = networkID7;
				Debug.LogError("MP_Strategy::ProcessNetRPC - Player was NULL for VOTE_DAYTIME, ID: " + networkID2.ToString() + ", channel: " + channel6);
				break;
			}
			EDayTime eDayTime = (EDayTime)packetReader.ReadByte();
			DaytimeVotes.Remove(player11);
			if (eDayTime != EDayTime.NONE)
			{
				DaytimeVotes.Add(player11, eDayTime);
			}
			if (Game.GetIsServer())
			{
				RPC_VoteDayTime(player11, eDayTime);
			}
			break;
		}
		case ERPCs.VOTE_TEAMVERSUS:
		{
			NetworkID networkID4 = packetReader.ReadNetworkID();
			int channel3 = packetReader.ReadByte();
			Player player6 = Player.FindPlayer(networkID4, channel3);
			if (!player6)
			{
				NetworkID networkID2 = networkID4;
				Debug.LogError("MP_Strategy::ProcessNetRPC - Player was NULL for VOTE_TEAMVERSUS, ID: " + networkID2.ToString() + ", channel: " + channel3);
				break;
			}
			ETeamsVersus eTeamsVersus = (ETeamsVersus)packetReader.ReadByte();
			TeamsVersusVotes.Remove(player6);
			if (eTeamsVersus != ETeamsVersus.NONE)
			{
				TeamsVersusVotes.Add(player6, eTeamsVersus);
			}
			if (Game.GetIsServer())
			{
				RPC_VoteTeamVersus(player6, eTeamsVersus);
			}
			break;
		}
		case ERPCs.TEAM_LOCK:
			if (!Game.GetIsServer())
			{
				LockedToTeam = packetReader.ReadTeam();
				RPC_RequestJoinTeam(LockedToTeam);
				SelectRole(ETeamRole.NONE);
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
			break;
		case ERPCs.REQUEST_COMMANDER_LOTTERY_ENTRY:
			if (Game.GetIsServer())
			{
				Player player4 = Player.FindPlayer(packetReader.ReadNetworkID());
				Team team2 = packetReader.ReadTeam();
				if ((bool)player4)
				{
					Sync_AddPlayerToCommanderLottery(team2, player4);
				}
			}
			break;
		case ERPCs.REQUEST_COMMANDER_LOTTERY_REMOVE:
			if (Game.GetIsServer())
			{
				Player player2 = Player.FindPlayer(packetReader.ReadNetworkID());
				Team team = packetReader.ReadTeam();
				if ((bool)player2)
				{
					Sync_RemovePlayerFromCommanderLottery(team, player2);
				}
			}
			break;
		case ERPCs.SYNC_COMMANDER_LOTTERY_ENTRY:
			if (!Game.GetIsServer())
			{
				Player player10 = Player.FindPlayer(packetReader.ReadNetworkID());
				Team team6 = packetReader.ReadTeam();
				if ((bool)player10)
				{
					Sync_AddPlayerToCommanderLottery(team6, player10);
				}
			}
			break;
		case ERPCs.SYNC_COMMANDER_LOTTERY_REMOVE:
			if (!Game.GetIsServer())
			{
				Player player8 = Player.FindPlayer(packetReader.ReadNetworkID());
				Team team5 = packetReader.ReadTeam();
				if ((bool)player8)
				{
					Sync_RemovePlayerFromCommanderLottery(team5, player8);
				}
			}
			break;
		case ERPCs.SYNC_MAX_PLAYER_DIFFERENCE:
			if (!Game.GetIsServer())
			{
				MaxPlayerDifference = packetReader.ReadInt32();
			}
			break;
		case ERPCs.SYNC_ALLOW_TEAM_SWITCH:
			if (!Game.GetIsServer())
			{
				AllowTeamSwitch = packetReader.ReadBool();
			}
			break;
		case ERPCs.SYNCH_ROLE:
		{
			if (Game.GetIsServer())
			{
				break;
			}
			NetworkID networkID = packetReader.ReadNetworkID();
			int channel = packetReader.ReadByte();
			Player player = Player.FindPlayer(networkID, channel);
			if (!player)
			{
				NetworkID networkID2 = networkID;
				Debug.LogError("MP_Strategy::ProcessNetRPC - Player was NULL for SYNCH_ROLE, ID: " + networkID2.ToString() + ", channel: " + channel);
				break;
			}
			ETeamRole eTeamRole = (ETeamRole)packetReader.ReadByte();
			if (player == Player.CurrentPlayer)
			{
				LocalRole = eTeamRole;
				if (eTeamRole == ETeamRole.UNIT && ShowTeamSelectMenu == ETeamSelect.SELECT_ROLE)
				{
					ShowTeamSelectMenu = ((!(Player.CurrentPlayer.ControlledUnit != null)) ? ETeamSelect.RESPAWN_MENU : ETeamSelect.NONE);
				}
			}
			break;
		}
		}
	}

	private void RPC_SynchTeamLock()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(12);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteTeam(LockedToTeam);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_SynchTeamVersus()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(9);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteByte((byte)TeamsVersus);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_RequestJoinTeam(Team team)
	{
		WaitingForServer = true;
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(1);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteByte((byte)Player.CurrentPlayer.PlayerChannel);
			gameByteStreamWriter.WriteTeam(team);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_RequestRole(ETeamRole role)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(2);
		if (gameByteStreamWriter != null)
		{
			WaitingForServer = true;
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteByte((byte)Player.CurrentPlayer.PlayerChannel);
			gameByteStreamWriter.WriteByte((byte)role);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_ClearRequest(Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(3);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(player.PlayerID);
			gameByteStreamWriter.WriteByte((byte)player.PlayerChannel);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_SynchRole(Player player, ETeamRole role)
	{
		if (!Game.GetIsClient())
		{
			GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(19);
			if (gameByteStreamWriter != null)
			{
				gameByteStreamWriter.WriteNetworkID(player.PlayerID);
				gameByteStreamWriter.WriteByte((byte)player.PlayerChannel);
				gameByteStreamWriter.WriteByte((byte)role);
				NetworkLayer.SendPacketToPlayer(gameByteStreamWriter, ENetworkPacketSend.Reliable, player);
			}
		}
	}

	protected override void RPC_SynchUnitCapMultiplier()
	{
		if (!Game.GetIsClient())
		{
			GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(4);
			if (gameByteStreamWriter != null)
			{
				gameByteStreamWriter.WriteByte((byte)UnitCapMultiplier);
				SendRPCPacket(gameByteStreamWriter);
			}
		}
	}

	private void RPC_SynchCommander(Team team, bool kicked = false)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(5);
		if (gameByteStreamWriter != null)
		{
			Player player = GetTeamSetup(team)?.Commander;
			if ((bool)player)
			{
				gameByteStreamWriter.WriteNetworkID(player.PlayerID);
				gameByteStreamWriter.WriteByte((byte)player.PlayerChannel);
			}
			else
			{
				gameByteStreamWriter.WriteNetworkID(default);
				gameByteStreamWriter.WriteByte(0);
			}
			gameByteStreamWriter.WriteTeam(team);
			gameByteStreamWriter.WriteBool(kicked);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	protected void RPC_VoteTeamVersus(Player player, ETeamsVersus teamVersus)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(11);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(player.PlayerID);
			gameByteStreamWriter.WriteByte((byte)player.PlayerChannel);
			gameByteStreamWriter.WriteByte((byte)teamVersus);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_InitGame()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(0);
		if (gameByteStreamWriter != null)
		{
			WriteInitData(gameByteStreamWriter);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_TimerUpdate()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(7);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteFloat(Timer);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_VoteTimerUpdate()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(8);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteFloat(VoteTimer);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_MissionStateChanged(EMissionState missionState)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(6);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteByte((byte)missionState);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_VoteDayTime(Player player, EDayTime dayTime)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(10);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(player.PlayerID);
			gameByteStreamWriter.WriteByte((byte)player.PlayerChannel);
			gameByteStreamWriter.WriteByte((byte)dayTime);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_AllowTeamSwitching()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(17);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteBool(AllowTeamSwitch);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_MaxPlayerDifference()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(18);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteInt32(MaxPlayerDifference);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void WriteInitData(GameByteStreamWriter packetWriter)
	{
		packetWriter.WriteByte((byte)MissionState);
		packetWriter.WriteByte((byte)TeamsVersus);
		packetWriter.WriteFloat(Timer);
		packetWriter.WriteByte((byte)m_UnitCapMultiplier);
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup))
			{
				packetWriter.WriteSByte((sbyte)teamSetup.BaseStructureStartPointIndex);
				if ((bool)teamSetup.Commander)
				{
					packetWriter.WriteNetworkID(teamSetup.Commander.PlayerID);
					packetWriter.WriteByte((byte)teamSetup.Commander.PlayerChannel);
				}
				else
				{
					packetWriter.WriteNetworkID(default);
					packetWriter.WriteByte(0);
				}
			}
		}
		packetWriter.WriteByte((byte)DaytimeVotes.Count);
		foreach (KeyValuePair<Player, EDayTime> daytimeVote in DaytimeVotes)
		{
			Player key = daytimeVote.Key;
			packetWriter.WriteNetworkID(key.PlayerID);
			packetWriter.WriteByte((byte)key.PlayerChannel);
			packetWriter.WriteByte((byte)daytimeVote.Value);
		}
		packetWriter.WriteByte((byte)TeamsVersusVotes.Count);
		foreach (KeyValuePair<Player, ETeamsVersus> teamsVersusVote in TeamsVersusVotes)
		{
			Player key2 = teamsVersusVote.Key;
			packetWriter.WriteNetworkID(key2.PlayerID);
			packetWriter.WriteByte((byte)key2.PlayerChannel);
			packetWriter.WriteByte((byte)teamsVersusVote.Value);
		}
		packetWriter.WriteBool(LockedToTeam != null);
		if (LockedToTeam != null)
		{
			packetWriter.WriteTeam(LockedToTeam);
		}
		packetWriter.WriteInt32(MaxPlayerDifference);
		packetWriter.WriteBool(AllowTeamSwitch);
	}

	private void ReadInitData(GameByteStreamReader packetReader)
	{
		MissionState = (EMissionState)packetReader.ReadByte();
		SetTeamVersusMode((ETeamsVersus)packetReader.ReadByte());
		Timer = packetReader.ReadFloat();
		m_UnitCapMultiplier = packetReader.ReadByte();
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup))
			{
				teamSetup.BaseStructureStartPointIndex = packetReader.ReadSByte();
				Player player = Player.FindPlayer(packetReader.ReadNetworkID(), packetReader.ReadByte());
				SetCommander(teamSetup.Team, player);
			}
		}
		DaytimeVotes.Clear();
		int i = 0;
		for (int num = packetReader.ReadByte(); i < num; i++)
		{
			Player key = Player.FindPlayer(packetReader.ReadNetworkID(), packetReader.ReadByte());
			EDayTime value = (EDayTime)packetReader.ReadByte();
			DaytimeVotes.Add(key, value);
		}
		TeamsVersusVotes.Clear();
		int j = 0;
		for (int num2 = packetReader.ReadByte(); j < num2; j++)
		{
			Player player2 = Player.FindPlayer(packetReader.ReadNetworkID(), packetReader.ReadByte());
			ETeamsVersus value2 = (ETeamsVersus)packetReader.ReadByte();
			if ((bool)player2)
			{
				TeamsVersusVotes.Add(player2, value2);
			}
		}
		if (packetReader.ReadBool())
		{
			LockedToTeam = packetReader.ReadTeam();
			RPC_RequestJoinTeam(LockedToTeam);
			SelectRole(ETeamRole.NONE);
			ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
		}
		MaxPlayerDifference = packetReader.ReadInt32();
		AllowTeamSwitch = packetReader.ReadBool();
	}

	public override void OnSendNetInit(GameByteStreamWriter packetWriter)
	{
		WriteInitData(packetWriter);
	}

	public override void ProcessNetInit(GameByteStreamReader packetReader)
	{
		ReadInitData(packetReader);
	}

	public override void SelectTeam(Team team)
	{
		if (Team.CurrentTeam != null && team == Team.CurrentTeam)
		{
			if (LocalRole == ETeamRole.NONE)
			{
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
			else if (LocalRole == ETeamRole.UNIT && Unit.CurrentUnit == null)
			{
				ShowTeamSelectMenu = ETeamSelect.RESPAWN_MENU;
			}
			else
			{
				ShowTeamSelectMenu = ETeamSelect.NONE;
			}
			return;
		}
		if (StrategyMode.InDirectControl)
		{
			StrategyMode.Instance.RequestDirectControl(null);
		}
		SelectRole(ETeamRole.NONE);
		if (Game.GetIsClient())
		{
			if (Player.CurrentPlayer != null && Player.CurrentPlayer.Team == team)
			{
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
			else
			{
				m_OpenRoleSelectOnTeamConfirm = true;
				RPC_RequestJoinTeam(team);
			}
		}
		else
		{
			Player.CurrentPlayer.Team = team;
			NetworkLayer.SendPlayerSelectTeam(Player.CurrentPlayer, team);
			ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
		}
		MoveCameraToNoTeamView();
	}

	public override void SelectRole(ETeamRole role)
	{
		StrategyHandler.SwitchTo(StrategyScreenType.None);
		if ((bool)Player.CurrentPlayer)
		{
			BaseTeamSetup teamSetup = GetTeamSetup(Player.CurrentPlayer.Team);
			switch (role)
			{
			case ETeamRole.NONE:
				SelectRole_None(teamSetup);
				break;
			case ETeamRole.UNIT:
				SelectRole_Unit(teamSetup);
				break;
			case ETeamRole.COMMANDER:
				SelectRole_Commander(teamSetup);
				break;
			}
		}
	}

	private void SelectRole_None(BaseTeamSetup teamSetup)
	{
		CinematicCamera.ForceStop();
		if (Game.GetIsClient())
		{
			RPC_RequestRole(ETeamRole.NONE);
		}
		else
		{
			Server_DropUnitsAndRespawn(Player.CurrentPlayer);
			LocalRole = ETeamRole.NONE;
		}
		if (teamSetup != null && Player.CurrentPlayer == teamSetup.Commander)
		{
			if (Game.GetIsClient())
			{
				RPC_RequestRole(ETeamRole.NONE);
			}
			else
			{
				Server_SetLocalCommanderAndSynch(null);
			}
		}
		MoveCameraToNoTeamView();
	}

	private void SelectRole_Unit(BaseTeamSetup teamSetup)
	{
		LeaveCommanderLotteryIfQueued();
		if (Player.CurrentPlayer.ControlledUnit != null && !Player.CurrentPlayer.IsCommander)
		{
			ShowTeamSelectMenu = ETeamSelect.NONE;
			StrategyHandler.SwitchTo(StrategyScreenType.None);
			return;
		}
		ShowTeamSelectMenu = ETeamSelect.RESPAWN_MENU;
		StrategyHandler.SwitchTo(StrategyScreenType.Respawn);
		if (Game.GetIsClient())
		{
			RPC_RequestRole(ETeamRole.UNIT);
		}
		else
		{
			if (teamSetup != null && Player.CurrentPlayer == teamSetup.Commander)
			{
				Server_SetLocalCommanderAndSynch(null);
			}
			LocalRole = ETeamRole.UNIT;
		}
		ApplySingleplayerFastStart();
	}

	private void SelectRole_Commander(BaseTeamSetup teamSetup)
	{
		if (Game.GetIsMultiplayer() && !GameBegun)
		{
			SelectRole(ETeamRole.UNIT);
			Request_EntryIntoCommanderLottery();
		}
		else
		{
			if (teamSetup == null)
			{
				return;
			}
			if (teamSetup.Commander == Player.CurrentPlayer)
			{
				ShowTeamSelectMenu = ETeamSelect.NONE;
				return;
			}
			if ((bool)teamSetup.Commander)
			{
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
				return;
			}
			ShowTeamSelectMenu = ETeamSelect.NONE;
			if (Game.GetIsClient())
			{
				RPC_RequestRole(ETeamRole.COMMANDER);
			}
			else
			{
				Server_SetLocalCommanderAndSynch(Player.CurrentPlayer);
			}
			ApplySingleplayerFastStart();
		}
	}

	private void Server_DropUnitsAndRespawn(Player player)
	{
		DestroyAllUnitsForPlayer(player);
		if (PlayerRespawnTracker.ContainsKey(player))
		{
			PlayerRespawnTracker.Remove(player);
		}
	}

	private void Server_SetLocalCommanderAndSynch(Player commander)
	{
		SetCommander(Player.CurrentPlayer.Team, commander);
		RPC_SynchCommander(Player.CurrentPlayer.Team);
	}

	private void MoveCameraToNoTeamView()
	{
		if ((bool)NoTeamCameraRef)
		{
			Game.CurrentCamera.transform.position = NoTeamCameraRef.position;
			Game.CurrentCamera.transform.rotation = NoTeamCameraRef.rotation;
			VisionModeManager.PlayTransition();
		}
	}

	private void ApplySingleplayerFastStart()
	{
		if (!GameBegun && Game.GetIsSingleplayer() && !(Timer <= 5f))
		{
			Timer = 5f;
			TimerBase = 5f;
		}
	}

	private void LeaveCommanderLotteryIfQueued()
	{
		if (Game.GetIsMultiplayer() && CommanderLottery.TryGetValue(Team.CurrentTeam, out var value) && value.Contains(Player.CurrentPlayer))
		{
			Request_RemoveFromCommanderLottery();
		}
	}

	private void Request_EntryIntoCommanderLottery()
	{
		if (Game.GetIsClient())
		{
			RPC_RequestEntryIntoCommanderLottery();
		}
		else if (Player.CurrentPlayer != null)
		{
			Sync_AddPlayerToCommanderLottery(Team.CurrentTeam, Player.CurrentPlayer);
		}
	}

	private void Request_RemoveFromCommanderLottery()
	{
		if (Game.GetIsClient())
		{
			RPC_RequestRemoveFromCommanderLottery();
		}
		else if (Player.CurrentPlayer != null)
		{
			Sync_RemovePlayerFromCommanderLottery(Team.CurrentTeam, Player.CurrentPlayer);
		}
	}

	private void RPC_RequestEntryIntoCommanderLottery()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(13);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteTeam(Team.CurrentTeam);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_RequestRemoveFromCommanderLottery()
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(14);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteTeam(Team.CurrentTeam);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_SyncAddToCommanderLottery(Team team, Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(15);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(player.PlayerID);
			gameByteStreamWriter.WriteTeam(team);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void RPC_SyncRemoveFromCommanderLottery(Team team, Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = CreateRPCPacket(16);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(player.PlayerID);
			gameByteStreamWriter.WriteTeam(team);
			SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void Sync_AddPlayerToCommanderLottery(Team team, Player player)
	{
		if (!CommanderLottery.TryGetValue(team, out var value))
		{
			value = new List<Player>();
			CommanderLottery.Add(team, value);
		}
		if (!value.Contains(player))
		{
			value.Add(player);
			if (Game.GetIsServer())
			{
				RPC_SyncAddToCommanderLottery(team, player);
			}
		}
	}

	private void Sync_RemovePlayerFromCommanderLottery(Team team, Player player)
	{
		if (!CommanderLottery.TryGetValue(team, out var value))
		{
			value = new List<Player>();
			CommanderLottery.Add(team, value);
		}
		if (value.Contains(player))
		{
			value.Remove(player);
			if (Game.GetIsServer())
			{
				RPC_SyncRemoveFromCommanderLottery(team, player);
			}
		}
	}

	private void ResolveCommanderLottery()
	{
		if (Game.GetIsClient() || CommanderLottery.Count == 0)
		{
			return;
		}
		foreach (KeyValuePair<Team, List<Player>> item in CommanderLottery)
		{
			Team key = item.Key;
			List<Player> value = item.Value;
			if (value.Count != 0)
			{
				int index = UnityEngine.Random.Range(0, value.Count);
				Player player = value[index];
				value.Clear();
				if (player.ControlledUnit != null)
				{
					UnityEngine.Object.Destroy(player.ControlledUnit.gameObject);
				}
				SetCommander(key, player);
				RPC_SynchCommander(key);
			}
		}
	}

	public bool HasLocalPlayerEnteredInCommanderLottery()
	{
		if (GameBegun)
		{
			return false;
		}
		if (!Team.CurrentTeam)
		{
			return false;
		}
		if (!CommanderLottery.TryGetValue(Team.CurrentTeam, out var value))
		{
			return false;
		}
		return value.Contains(Player.CurrentPlayer);
	}

	private void SetCommander(Team team, Player player)
	{
		if (!team)
		{
			return;
		}
		if ((bool)player && !StrategyNetworkManager.Instance)
		{
			StrategyNetworkManager.Initialize();
		}
		bool inStrategyMode = StrategyMode.InStrategyMode;
		BaseTeamSetup teamSetup = GetTeamSetup(team);
		if (teamSetup == null)
		{
			return;
		}
		Player commander = teamSetup.Commander;
		teamSetup.Commander = player;
		Unit unit = (player ? player.ControlledUnit : null);
		if ((bool)player && player == Player.CurrentPlayer)
		{
			player.ControlledUnit = null;
		}
		if ((bool)unit && !Game.GetIsClient())
		{
			if ((bool)unit.ObjectInfo && unit.ObjectInfo.UnitType == UnitType.Infantry)
			{
				UnityEngine.Object.Destroy(unit.gameObject);
			}
			else if (unit.ControlledBy == player)
			{
				player.ControlledUnit = null;
			}
		}
		AIBaseAgent.AllQueueSendLongRangePath(team);
		GameEvents.OnCommanderChanged?.Invoke(team, commander, player);
		if ((bool)Player.CurrentPlayer && Player.CurrentPlayer.Team == teamSetup.Team)
		{
			bool flag = teamSetup.Commander == Player.CurrentPlayer;
			StrategyMode.ToggleStrategyMode(flag);
			if (flag)
			{
				ShowTeamSelectMenu = ETeamSelect.NONE;
				LocalRole = ETeamRole.COMMANDER;
			}
		}
		if (inStrategyMode != StrategyMode.InStrategyMode)
		{
			WaitingForServer = false;
			VisionModeManager.PlayTransition();
			if (StrategyMode.InStrategyMode)
			{
				Vector3 zero = Vector3.zero;
				Structure firstStructureOfType = team.GetFirstStructureOfType(team.BaseStructure);
				if ((bool)firstStructureOfType)
				{
					zero = firstStructureOfType.transform.position;
				}
				else if (team.Structures.Count > 0)
				{
					zero = team.Structures[0].transform.position;
				}
				else
				{
					zero = ((team.Units.Count > 0) ? team.Units[0].transform.position : Vector3.zero);
				}
				zero.y += 100f;
				Vector3 vector = StrategyMode.Instance.transform.rotation * Vector3.forward;
				vector.y = 0f;
				vector.Normalize();
				zero -= vector * 50f;
				StrategyMode.Instance.transform.position = zero;
				StrategyMode.Instance.CameraHeightPct = 0.5f;
				StrategyMode.Instance.ClampCamera();
			}
		}
		if (!Game.GetIsClient() && !GameOver)
		{
			if ((bool)player)
			{
				Player.SendServerChatMessage("'" + player.PlayerName + "' is now the Commander for team " + (teamSetup.Team ? teamSetup.Team.TeamShortName : "None"));
				AIManager.EnableCommander(teamSetup.Team, enable: false);
			}
			else if ((bool)commander)
			{
				Player.SendServerChatMessage("'" + commander.PlayerName + "' left the Commander role for team " + (teamSetup.Team ? teamSetup.Team.TeamShortName : "None"));
			}
			if ((bool)player)
			{
				teamSetup.AICommanderBeginCommandDelay = 0f;
			}
			else
			{
				teamSetup.AICommanderBeginCommandDelay = teamSetup.AICommanderPlayerLeftWaitTime;
			}
		}
	}

	public void CommanderMutinyForTeam(Team team)
	{
		if (!(team == null))
		{
			Player prevCommander = GetTeamSetup(team)?.Commander;
			SetCommander(team, null);
			RPC_SynchCommander(team, kicked: true);
			ShowRoleSelectionIfOusted(prevCommander, kicked: true);
		}
	}

	private void ShowRoleSelectionIfOusted(Player prevCommander, bool kicked)
	{
		if (NetworkGameServer.GetServerDedicated() || !Player.CurrentPlayer || prevCommander != Player.CurrentPlayer || Player.CurrentPlayer.IsCommander)
		{
			return;
		}
		if (Player.CurrentPlayer.ControlledUnit != null)
		{
			LocalRole = ETeamRole.UNIT;
			return;
		}
		LocalRole = ETeamRole.NONE;
		ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
		if (kicked)
		{
			LocalizationUtility.TryGetTranslation("Voting", "MutinyVote_RemovedFromCommanderSeatPopup", null, out var translation);
			ConfirmationPopup.RequestConfirmation(translation, ConfirmationOption.Confirm());
			MutinyLockTime = Time.time + 30f;
		}
	}

	public override bool GetCanDeletePlayerControlledUnit(Player controllingPlayer, Unit unit)
	{
		StrategyTeamSetup strategyTeamSetup = (StrategyTeamSetup)GetTeamSetup(controllingPlayer);
		if (strategyTeamSetup != null && strategyTeamSetup.DeleteOnLeaveUnitTypes.Count > 0)
		{
			return strategyTeamSetup.DeleteOnLeaveUnitTypes.Contains(unit.ObjectInfo);
		}
		return true;
	}

	public override bool GetCanFreelyOpenStrategyMode()
	{
		Team currentTeam = Team.CurrentTeam;
		if (!currentTeam)
		{
			return false;
		}
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup) && teamSetup.Team == currentTeam)
			{
				return false;
			}
		}
		return true;
	}

	public override void OnRequestToggleStrategyMode(bool open)
	{
	}

	public override bool GetShowCursor()
	{
		return ShowTeamSelectMenu != ETeamSelect.NONE;
	}

	public override bool GetAmbientLifeEnabled()
	{
		return GameOngoing;
	}

	public override bool GetDamageEnabled()
	{
		if (GameBegun)
		{
			return !GameOver;
		}
		return false;
	}

	public override bool GetCanUnitMove(Unit unit)
	{
		if (GameBegun)
		{
			return !GameOver;
		}
		return false;
	}

	public override bool GetCanUnitAttack(Unit unit)
	{
		if (GameBegun)
		{
			return !GameOver;
		}
		return false;
	}

	public override void HandleUI()
	{
		if (((bool)UIManager.Instance && UIManager.Instance.UIHideLevel > 0) || !StrategyHandler.Instance || DefeatChangeSideTimer > 0f)
		{
			return;
		}
		if (MissionState == EMissionState.INIT && TeamsVersus == ETeamsVersus.NONE)
		{
			if (!Game.GetIsClient() || !NetworkGameClient.GetWaitingForServerToLoadLevel())
			{
				StrategyHandler.SwitchTo(StrategyScreenType.LayoutSelection);
			}
		}
		else if (ShowTeamSelectMenu != ETeamSelect.NONE)
		{
			if (!WaitingForServer)
			{
				if (ShowTeamSelectMenu == ETeamSelect.SELECT_TEAM)
				{
					StrategyHandler.SwitchTo(StrategyScreenType.Faction, this);
				}
				else if (ShowTeamSelectMenu == ETeamSelect.SELECT_ROLE)
				{
					StrategyHandler.SwitchTo(StrategyScreenType.Loadout);
				}
				else if (ShowTeamSelectMenu == ETeamSelect.RESPAWN_MENU)
				{
					StrategyHandler.SwitchTo(StrategyScreenType.Respawn);
				}
				else
				{
					StrategyHandler.SwitchTo(StrategyScreenType.None);
				}
			}
		}
		else if (GameOver)
		{
			if (MissionState == EMissionState.ENDED)
			{
				if (LocalLost)
				{
					StrategyHandler.Defeat();
				}
				else if (LocalWon)
				{
					StrategyHandler.Victory();
				}
			}
		}
		else if (GameBegun && Mathf.Ceil(Timer) <= 0f)
		{
			StrategyHandler.SwitchTo(StrategyScreenType.None);
		}
	}

	public override bool OnEscape()
	{
		if (ShowTeamSelectMenu == ETeamSelect.SELECT_TEAM && Team.CurrentTeam != null)
		{
			ShowTeamSelectMenu = ETeamSelect.NONE;
			return true;
		}
		if (ShowTeamSelectMenu == ETeamSelect.SELECT_ROLE && (Player.CurrentPlayer.ControlledUnit != null || Player.CurrentPlayer.IsCommander))
		{
			ShowTeamSelectMenu = ETeamSelect.NONE;
			return true;
		}
		return false;
	}

	public override void OnPlayerJoined(Player player)
	{
		Debug.Log("Player ID: " + player.PlayerID.ToString() + ", channel: " + player.PlayerChannel + " joined");
	}

	public override void OnPlayerLeft(Player player)
	{
		Debug.Log("Player ID: " + player.PlayerID.ToString() + ", channel: " + player.PlayerChannel + " '" + player.PlayerName + "' left");
		DaytimeVotes.Remove(player);
		TeamsVersusVotes.Remove(player);
		if (Game.GetIsClient())
		{
			return;
		}
		DestroyAllUnitsForPlayer(player);
		if (PlayerRespawnTracker.ContainsKey(player))
		{
			PlayerRespawnTracker.Remove(player);
		}
		if (player.Team != null && player.IsCommander)
		{
			foreach (StrategyTeamSetup teamSetup in TeamSetups)
			{
				if (teamSetup.Commander == player)
				{
					SetCommander(teamSetup.Team, null);
					RPC_SynchCommander(teamSetup.Team);
				}
			}
		}
		if (GameBegun)
		{
			return;
		}
		foreach (KeyValuePair<Team, List<Player>> item in CommanderLottery)
		{
			if (item.Value.Contains(player))
			{
				Sync_RemovePlayerFromCommanderLottery(item.Key, player);
				break;
			}
		}
	}

	public override void OnPlayerChangedTeam(Player player, Team oldTeam, Team newTeam)
	{
		if (player == Player.CurrentPlayer)
		{
			WaitingForServer = false;
			if (m_OpenRoleSelectOnTeamConfirm && newTeam != null)
			{
				m_OpenRoleSelectOnTeamConfirm = false;
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
		}
		if (!(oldTeam == newTeam) && !Game.GetIsClient())
		{
			HandleOneTeamLock(newTeam);
			if (QueueCoopSpawnBaseStructures)
			{
				QueueCoopSpawnBaseStructures = false;
				SpawnStructuresAndStartingResources();
			}
			DestroyAllUnitsForPlayer(player);
			if (PlayerRespawnTracker.ContainsKey(player))
			{
				PlayerRespawnTracker.Remove(player);
			}
			if (!GameBegun && oldTeam != null)
			{
				Sync_RemovePlayerFromCommanderLottery(oldTeam, player);
			}
		}
	}

	private void HandleOneTeamLock(Team newTeam)
	{
		if (!Game.GetIsClient() && IsOneTeamOnly && !(LockedToTeam != null) && !(newTeam == null))
		{
			LockedToTeam = newTeam;
			RPC_SynchTeamLock();
			if (!NetworkGameServer.GetServerDedicated() && (bool)Player.CurrentPlayer && !(Player.CurrentPlayer.Team != null))
			{
				Player.CurrentPlayer.Team = newTeam;
				NetworkLayer.SendPlayerSelectTeam(Player.CurrentPlayer, newTeam);
				SelectRole(ETeamRole.NONE);
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
		}
	}

	public override GameObject GetUnitPrefabForPlayer(Player player)
	{
		if (player.Team == null)
		{
			return null;
		}
		BaseTeamSetup teamSetup = GetTeamSetup(player.Team);
		if (teamSetup == null)
		{
			return null;
		}
		GameObject gameObject = null;
		if (teamSetup.PlayerSpawnExt != null)
		{
			for (int num = teamSetup.PlayerSpawnExt.Count - 1; num >= 0; num--)
			{
				ObjectInfo objectInfo = teamSetup.PlayerSpawnExt[num];
				if ((bool)objectInfo && (bool)objectInfo.Prefab && teamSetup.Team.GetHasPrerequisitesForConstruction(objectInfo.ConstructionData) && teamSetup.Team.GetHasStructureForConstruction(objectInfo.ConstructionData))
				{
					gameObject = objectInfo.Prefab;
					break;
				}
			}
		}
		if (!gameObject)
		{
			gameObject = (teamSetup.PlayerSpawn ? teamSetup.PlayerSpawn.Prefab : null);
		}
		return gameObject;
	}

	protected void CheckPlayersOnEliminatedTeam()
	{
		if (GameOver)
		{
			return;
		}
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (!teamSetup.GetHasLost())
			{
				continue;
			}
			if ((bool)teamSetup.Commander && !Game.GetIsClient())
			{
				SetCommander(teamSetup.Team, null);
				RPC_SynchCommander(teamSetup.Team);
			}
			foreach (Player player in Player.Players)
			{
				if (player.Team != teamSetup.Team)
				{
					continue;
				}
				if (Player.CurrentPlayer == player)
				{
					UIManager.RequestSwitchUIMode(UIMode.None);
					DeathCam.Play(Game.CurrentCamera.transform.position, Game.CurrentCamera.transform.rotation, 50f, StartDelay);
					MusicJukebox.OnMusicEvent(EMusicEvent.LOSE, force: true);
					MusicJukebox.SetLastMusicEvent(EMusicEvent.DEATH_TO_SUSPENSE, 10f, force: true, stopPrevious: false);
					if (DefeatChangeSideTimer <= 0f)
					{
						DefeatChangeSideTimer = DefeatChangeSideTime;
						StrategyHandler.SwitchTo(StrategyScreenType.End);
					}
				}
				if (!Game.GetIsClient())
				{
					NetworkLayer.SendPlayerSelectUnit(player, null);
					player.ControlledUnit = null;
					player.Team = null;
					NetworkLayer.SendPlayerSelectTeam(player, null);
				}
			}
		}
	}

	public override void OnUnitDestroyed(Unit unit, GameObject instigator)
	{
		if (GameOngoing)
		{
			if (GetShouldEndGame())
			{
				OnMissionStateChanged(EMissionState.ENDED);
			}
			else
			{
				CheckPlayersOnEliminatedTeam();
			}
			if (unit == Unit.CurrentUnit && (!unit || !unit.IsDirectControlled) && !GetLocalTeamEliminated() && !StrategyMode.InStrategyMode)
			{
				RespawnMenuTimer = RespawnTime;
			}
		}
	}

	public override void OnUnitDeleted(Unit unit)
	{
		if (GameOngoing && !Game.IsLoading)
		{
			if (GetShouldEndGame())
			{
				OnMissionStateChanged(EMissionState.ENDED);
			}
			else
			{
				CheckPlayersOnEliminatedTeam();
			}
		}
	}

	public override void OnStructureDestroyed(Structure structure, GameObject instigator)
	{
		if (GameOngoing)
		{
			if (GetShouldEndGame())
			{
				OnMissionStateChanged(EMissionState.ENDED);
			}
			else
			{
				CheckPlayersOnEliminatedTeam();
			}
		}
	}

	public override void OnStructureDeleted(Structure structure)
	{
		if (GameOngoing && !Game.IsLoading)
		{
			if (GetShouldEndGame())
			{
				OnMissionStateChanged(EMissionState.ENDED);
			}
			else
			{
				CheckPlayersOnEliminatedTeam();
			}
		}
	}

	public override void OnResourcesStored(Team team, Resource resource, int amount)
	{
		if (GameOngoing && !Game.IsLoading)
		{
			GlobeMissionData currentMission = GlobeMissionData.CurrentMission;
			if ((bool)currentMission && currentMission.GetHasResourcesObjective() && GetShouldEndGame())
			{
				OnMissionStateChanged(EMissionState.ENDED);
			}
		}
	}

	public override void OnPlayerSelectUnit(Player player, Unit oldUnit, Unit newUnit)
	{
		if (!(player != Player.CurrentPlayer))
		{
			CinematicCamera.ForceStop();
			WaitingForServer = false;
		}
	}

	public override void OnPlayerUnselectUnit(Player player, Unit unit)
	{
		_ = player != Player.CurrentPlayer;
	}

	public override List<Transform> GetStartingPointsForTeam(Team team)
	{
		BaseTeamSetup teamSetup = GetTeamSetup(team);
		if (teamSetup == null)
		{
			return null;
		}
		return ((StrategyTeamSetup)teamSetup).BaseStructureStartPoints;
	}

	public override int GetStartingPointsAll(List<Transform> startingPointsList)
	{
		startingPointsList.Clear();
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (!GetTeamSetupActive(teamSetup))
			{
				continue;
			}
			foreach (Transform baseStructureStartPoint in teamSetup.BaseStructureStartPoints)
			{
				startingPointsList.Add(baseStructureStartPoint);
			}
		}
		return startingPointsList.Count;
	}

	public override PrefabsToPointsHandler GetObjectsOfInterestSpawner()
	{
		return LocalInfrastructureSpawner;
	}

	protected void SpawnSideAITeams()
	{
		AIManager.Clear();
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup))
			{
				Debug.Log("Creating team AI for team '" + teamSetup.Team.TeamName + "'...");
				AIManager.AddCommander(teamSetup.Team, teamSetup.AICommanderSettings);
				AIManager.EnableCommander(teamSetup.Team, enable: false);
			}
		}
	}

	public override void EndRound()
	{
		if (!Game.GetIsClient())
		{
			OnMissionStateChanged(EMissionState.ENDED);
		}
	}

	public void SetMissionStateNoNotify(EMissionState missionState)
	{
		MissionState = missionState;
	}

	protected void OnMissionStateChanged(EMissionState missionState)
	{
		if (Game.GetIsServer())
		{
			RPC_MissionStateChanged(missionState);
		}
		MissionState = missionState;
		switch (MissionState)
		{
		case EMissionState.INIT:
			if (Game.GetIsMultiplayer())
			{
				if (IsCooperative)
				{
					Debug.Log("Strategy Cooperative game next map vote staring...");
				}
				else
				{
					Debug.Log("Strategy game next map vote staring...");
				}
				StartVoteForNextMap();
				StrategyHandler.SwitchTo(StrategyScreenType.NextMap);
				Timer = float.MaxValue;
				TimerBase = float.MaxValue;
			}
			else
			{
				if (IsCooperative)
				{
					Debug.Log("Strategy Cooperative game mode restarting...");
				}
				else
				{
					Debug.Log("Strategy game mode restarting...");
				}
				Restart();
				Timer = 10f;
				TimerBase = 10f;
			}
			break;
		case EMissionState.STARTED:
			if (IsCooperative)
			{
				Debug.Log("--- Strategy Cooperative game mode started ---");
			}
			else
			{
				Debug.Log("--- Strategy game mode started ---");
			}
			DialogueManager.StopAllDialogues();
			foreach (StrategyTeamSetup teamSetup in TeamSetups)
			{
				if (GetTeamSetupActive(teamSetup))
				{
					if ((bool)teamSetup.Commander)
					{
						Player.SendServerChatMessage("'" + teamSetup.Commander.PlayerName + "' is now the Commander for team " + (teamSetup.Team ? teamSetup.Team.TeamShortName : "None"));
					}
					else
					{
						Player.SendServerChatMessage("The Commander for " + (teamSetup.Team ? teamSetup.Team.TeamShortName : "None") + " is now AI controlled!");
					}
					AIManager.EnableCommander(teamSetup.Team, teamSetup.Commander == null);
				}
			}
			GameEvents.OnGameStarted?.Invoke(this);
			break;
		case EMissionState.ENDED:
		{
			DialogueManager.StopAllDialogues();
			Timer = RestartDelay;
			TimerBase = RestartDelay;
			VoteTimer = StartVoteTime;
			UIManager.RequestSwitchUIMode(UIMode.None);
			DeathCam.Play(Game.CurrentCamera.transform.position, Game.CurrentCamera.transform.rotation, 50f, RestartDelay, forcePlay: true);
			Team winningTeam = GetWinningTeam();
			GameEvents.OnGameEnded?.Invoke(this, winningTeam);
			GlobeCampaign.OnGameEnded(this, winningTeam, RestartDelay);
			if (winningTeam != null || (Game.GetIsSingleplayer() && GetLocalTeamEliminated()) || GetCooperativeTeamEliminated())
			{
				StrategyHandler.SwitchTo(StrategyScreenType.End);
			}
			if (IsCooperative)
			{
				Debug.Log("--- Strategy Cooperative game mode ended, winner is " + (winningTeam ? winningTeam.TeamName : "none") + " ---");
			}
			else
			{
				Debug.Log("--- Strategy game mode ended, winner is " + (winningTeam ? winningTeam.TeamName : "none") + " ---");
			}
			if (Game.IsLoading)
			{
				Debug.LogError("Game ended during loading!");
			}
			break;
		}
		}
	}

	private void StartVoteForNextMap()
	{
		if (Game.GetIsClient())
		{
			return;
		}
		List<LevelInfo> list = new List<LevelInfo>();
		foreach (LevelInfo allLevel in GameDatabase.Database.AllLevels)
		{
			if (allLevel.GameModes.Contains(GameModeInfo) && !(allLevel == LevelInfo.CurrentLevel) && allLevel.Enabled && !allLevel.IsDeveloperOnly)
			{
				list.Add(allLevel);
			}
		}
		list.Shuffle();
		list = list.Take(3).ToList();
		list.Insert(0, LevelInfo.CurrentLevel);
		List<VoteOption> list2 = new List<VoteOption>();
		for (int i = 0; i < list.Count; i++)
		{
			VoteOption item = new VoteOption();
			list2.Add(item);
		}
		NextMapVote nextMapVote = new NextMapVote();
		nextMapVote.Init(30f, lockInVotes: false, Vote.EVoteType.Free, allowKeybinds: true);
		nextMapVote.SetOptions(list2);
		nextMapVote.Levels = list;
		VoteSystem.Instance.InitVote(nextMapVote);
		VoteSystem.Instance.ForceCancelCurrentVote();
		VoteSystem.Instance.VoteStart_Synced(nextMapVote);
	}

	protected void OnTimer()
	{
		switch (MissionState)
		{
		case EMissionState.INIT:
			OnMissionStateChanged(EMissionState.STARTED);
			break;
		case EMissionState.ENDED:
			OnMissionStateChanged(EMissionState.INIT);
			break;
		}
	}

	protected override void OnParamChanged(string key, object oldValue, object newValue)
	{
		base.OnParamChanged(key, oldValue, newValue);
		if (Game.GetIsClient())
		{
			return;
		}
		if (key == "techmin")
		{
			foreach (Team team in Team.Teams)
			{
				team.TechnologyTierLimitMin = (int)newValue;
			}
		}
		if (!(key == "techmax"))
		{
			return;
		}
		foreach (Team team2 in Team.Teams)
		{
			team2.TechnologyTierLimitMax = (int)newValue;
		}
	}

	public void UpdateInitialStrategySettings()
	{
		UpdateUnitCapMultFromSetting();
		if (Game.GetIsClient())
		{
			return;
		}
		if (HasParam("techmin"))
		{
			int technologyTierLimitMin = (int)GetParam("techmin");
			foreach (Team team in Team.Teams)
			{
				if ((bool)team)
				{
					team.TechnologyTierLimitMin = technologyTierLimitMin;
				}
			}
		}
		if (HasParam("techmax"))
		{
			int technologyTierLimitMax = (int)GetParam("techmax");
			foreach (Team team2 in Team.Teams)
			{
				if ((bool)team2)
				{
					team2.TechnologyTierLimitMax = technologyTierLimitMax;
				}
			}
		}
		if (HasParam("maxplayerdif"))
		{
			int maximumPlayerDifference = (int)GetParam("maxplayerdif");
			MaximumPlayerDifference = maximumPlayerDifference;
		}
		if (HasParam("allowteamswitch"))
		{
			bool allowTeamSwitching = (bool)GetParam("allowteamswitch");
			AllowTeamSwitching = allowTeamSwitching;
		}
		if (HasParam("coopdifficulty") && IsCooperative)
		{
			CurrentCoopDifficulty = (ECoopDifficulty)GetParam("coopdifficulty");
		}
		switch ((!HasParam("diststart")) ? 1 : ((int)GetParam("diststart")))
		{
		case 0:
			MultStartingDist = 0.25f;
			break;
		case 1:
			MultStartingDist = 0.5f;
			break;
		case 2:
			MultStartingDist = 1f;
			break;
		default:
			MultStartingDist = 0.5f;
			break;
		}
		switch ((!HasParam("resstart")) ? 1 : ((int)GetParam("resstart")))
		{
		case 0:
			MultStartingRes = 0.5f;
			break;
		case 1:
			MultStartingRes = 1f;
			break;
		case 2:
			MultStartingRes = 2f;
			break;
		case 3:
			MultStartingRes = 4f;
			break;
		default:
			MultStartingRes = 1f;
			break;
		}
		switch ((!HasParam("resloc")) ? 1 : ((int)GetParam("resloc")))
		{
		case 0:
			MultLocationRes = 0.6f;
			MultLocationResHide = 0.75f;
			break;
		case 1:
			MultLocationRes = 0.8f;
			MultLocationResHide = 0.5f;
			break;
		case 2:
			MultLocationRes = 1f;
			MultLocationResHide = 0.25f;
			break;
		case 3:
			MultLocationRes = 1.5f;
			MultLocationResHide = 0f;
			break;
		case 4:
			MultLocationRes = 3f;
			MultLocationResHide = 0f;
			break;
		default:
			MultLocationRes = 1f;
			MultLocationResHide = 0.25f;
			break;
		}
	}

	public override void UpdateGameLoop(float dt)
	{
		if (Game.IsLoading || GlobeCampaign.IsWaitingToLoad)
		{
			return;
		}
		bool gameBegun = GameBegun;
		if (RespawnMenuTimer > 0f)
		{
			RespawnMenuTimer -= dt;
			if (RespawnMenuTimer <= 0f && !Player.CurrentPlayer.IsCommander && !GameOver && DefeatChangeSideTimer <= 0f && (bool)Player.CurrentPlayer.Team)
			{
				ShowTeamSelectMenu = ETeamSelect.RESPAWN_MENU;
				StrategyHandler.SwitchTo(StrategyScreenType.Respawn);
			}
		}
		if (DefeatChangeSideTimer > 0f)
		{
			DefeatChangeSideTimer -= dt;
			if (DefeatChangeSideTimer <= 0f)
			{
				DefeatChangeSideTimer = 0f;
				StrategyHandler.SwitchTo(StrategyScreenType.None);
			}
		}
		if (GlobeCampaign.IsCampaignRunning)
		{
			if (!Game.GetIsClient())
			{
				if (!GameBegun && TeamsVersus != ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS)
				{
					GlobeMissionData currentMission = GlobeCampaign.Instance.CurrentMission;
					SetTeamVersusMode(ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS, currentMission);
					if ((bool)currentMission)
					{
						currentMission.ApplyToWorld();
					}
				}
				Team currentTeam = GlobeCampaign.CurrentTeam;
				foreach (Player player in Player.Players)
				{
					if (!(player.Team == currentTeam))
					{
						player.Team = currentTeam;
						NetworkLayer.SendPlayerSelectTeam(player, currentTeam);
					}
				}
			}
			if (ShowTeamSelectMenu == ETeamSelect.SELECT_TEAM)
			{
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
			if (Game.GetIsSingleplayer() && ShowTeamSelectMenu == ETeamSelect.SELECT_ROLE)
			{
				ShowTeamSelectMenu = ETeamSelect.NONE;
				SelectRole(ETeamRole.COMMANDER);
			}
		}
		float timer = Timer;
		if (Timer > 0f)
		{
			if (!Game.GetIsClient() && GameInput.GetButtonDown("Skip Dialogue", GameInput.InputMap.Global) && !DialogueManager.GetCurrentDialogue() && !UIManager.TextInputActive)
			{
				Debug.Log("Skipped timer");
				if (TeamsVersus == ETeamsVersus.NONE)
				{
					VoteTimer = 0f;
				}
				else
				{
					Timer = 0f;
					StrategyHandler.SwitchTo(StrategyScreenType.None);
				}
			}
			if (MissionState == EMissionState.INIT && (TeamsVersus == ETeamsVersus.NONE || !GetAnyPlayersOnAnyTeam()))
			{
				Timer = StartDelay;
				TimerBase = StartDelay;
				if (TeamsVersus == ETeamsVersus.NONE)
				{
					WorldLight.SetDayTime(11f);
				}
				float voteTimer = VoteTimer;
				if (VoteTimer < StartVoteTime)
				{
					if (Game.GetIsSingleplayer())
					{
						VoteTimer = 0f;
					}
					else
					{
						VoteTimer -= dt;
					}
					if (VoteTimer <= 0f)
					{
						VoteTimer = 0f;
						if (TeamsVersus == ETeamsVersus.NONE && !Game.GetIsClient())
						{
							WorldLight.SetDayTime(GetDayTimeVoteResult());
							SetTeamVersusMode(GetTeamsVersusVoteResult());
							ResourceArea.DistributeAllResources(MultLocationRes, MultLocationResHide);
							return;
						}
					}
				}
				else if (TeamsVersusVotes.Count > 0)
				{
					VoteTimer = StartVoteTime - 0.1f;
				}
				else
				{
					VoteTimer = StartVoteTime;
				}
				if (Game.GetIsServer() && (float)(Mathf.FloorToInt(voteTimer) - Mathf.FloorToInt(VoteTimer)) >= 1f)
				{
					RPC_VoteTimerUpdate();
				}
			}
			else if (MissionState == EMissionState.INIT && GlobeCampaign.IsCampaignRunning && !GlobeCampaign.CurrentTeam.GetHasPlayerCommander() && !GlobeCampaign.CurrentTeam.GetHasAnyPlayerUnit())
			{
				Timer = StartDelay;
			}
			else
			{
				Timer -= dt;
			}
			if (Game.GetIsServer() && (float)(Mathf.FloorToInt(timer) - Mathf.FloorToInt(Timer)) >= 1f)
			{
				RPC_TimerUpdate();
			}
			if (Timer <= 0f)
			{
				Timer = 0f;
				if (!Game.GetIsClient())
				{
					OnTimer();
				}
			}
		}
		if (!WaitingForServer && GameOngoing && DefeatChangeSideTimer <= 0f && !Player.CurrentPlayer.Team)
		{
			ShowTeamSelectMenu = ETeamSelect.SELECT_TEAM;
		}
		if (GameOngoing)
		{
			NoCommanderOrderTime += dt;
		}
		if (Game.GetIsClient())
		{
			return;
		}
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (!GetTeamSetupActive(teamSetup) || teamSetup.AICommanderBeginCommandDelay <= 0f)
			{
				continue;
			}
			if ((bool)teamSetup.Commander)
			{
				if (teamSetup.AICommanderBeginCommandDelay > 0f)
				{
					teamSetup.AICommanderBeginCommandDelay = 0f;
				}
				continue;
			}
			float aICommanderBeginCommandDelay = teamSetup.AICommanderBeginCommandDelay;
			teamSetup.AICommanderBeginCommandDelay -= dt;
			if (teamSetup.AICommanderBeginCommandDelay < 0f)
			{
				teamSetup.AICommanderBeginCommandDelay = 0f;
			}
			if (teamSetup.AICommanderBeginCommandDelay > 0f)
			{
				if (Mathf.FloorToInt(aICommanderBeginCommandDelay / 10f) != Mathf.FloorToInt(teamSetup.AICommanderBeginCommandDelay / 10f))
				{
					int num = Mathf.CeilToInt(teamSetup.AICommanderBeginCommandDelay / 10f) * 10;
					Player.SendServerChatMessage("AI assuming " + (teamSetup.Team ? teamSetup.Team.TeamShortName : "None") + " Commander role in " + num + "s...");
				}
			}
			else
			{
				Player.SendServerChatMessage("The Commander for " + (teamSetup.Team ? teamSetup.Team.TeamShortName : "None") + " is now AI!");
				AIManager.EnableCommander(teamSetup.Team, enable: true);
			}
		}
		if (!GameOngoing || gameBegun)
		{
			return;
		}
		foreach (StrategyTeamSetup teamSetup2 in TeamSetups)
		{
			if (!GetTeamSetupActive(teamSetup2))
			{
				Debug.Log("Skipping initial unit spawn for inactive team " + teamSetup2.Team.TeamName);
				continue;
			}
			Debug.Log("Doing initial unit spawn for team " + teamSetup2.Team.TeamName);
			teamSetup2.SpawnAIUnits();
		}
		ResolveCommanderLottery();
		CommanderLottery.Clear();
	}

	public override bool ShowVoteTimer()
	{
		return TeamsVersusVotes.Count > 0;
	}

	public override void VoteDayTime(EDayTime dayTime)
	{
		if (DaytimeVotes.TryGetValue(Player.CurrentPlayer, out var value))
		{
			if (dayTime == value)
			{
				DaytimeVotes.Remove(Player.CurrentPlayer);
				dayTime = EDayTime.NONE;
			}
			else
			{
				DaytimeVotes[Player.CurrentPlayer] = dayTime;
			}
		}
		else
		{
			DaytimeVotes.Add(Player.CurrentPlayer, dayTime);
		}
		RPC_VoteDayTime(Player.CurrentPlayer, dayTime);
	}

	protected override void ClearGame()
	{
		base.ClearGame();
		WaitingForServer = false;
		NoCommanderOrderTime = 30f;
		DaytimeVotes.Clear();
		TeamsVersusVotes.Clear();
		LockedToTeam = null;
	}

	protected void SpawnStructuresAndStartingResources()
	{
		if (Game.GetIsClient())
		{
			return;
		}
		if (TeamsVersus != ETeamsVersus.NONE)
		{
			SpawnBaseStructures();
			int num = 1;
			int num2 = 1;
			if (IsCooperative)
			{
				switch (CurrentCoopDifficulty)
				{
				case ECoopDifficulty.EASY:
					num2 = 0;
					num = 2;
					break;
				case ECoopDifficulty.HARD:
					num2 *= 3;
					break;
				case ECoopDifficulty.IMPOSSIBLE:
					num2 *= 10;
					num /= 2;
					break;
				}
				if (!Game.GetIsClient())
				{
					Debug.Log("Difficulty is " + CurrentCoopDifficulty.ToString() + ", enemy teams have " + Mathf.RoundToInt(num2) + "x starting resources and players " + Mathf.RoundToInt(num) + "x (if 0, limited to minimum for 1 resource structure)");
				}
			}
			foreach (StrategyTeamSetup teamSetup in TeamSetups)
			{
				if (GetTeamSetupActive(teamSetup) && !Game.GetIsClient())
				{
					float num3 = MultStartingRes;
					if (IsCooperative)
					{
						num3 = ((!(teamSetup.Team == LockedToTeam)) ? (num3 * (float)num2) : (num3 * (float)num));
					}
					ConstructionData firstResourceDepositStructure = teamSetup.Team.GetFirstResourceDepositStructure();
					int startingResources = Mathf.Max(Mathf.CeilToInt((float)teamSetup.StartingResources * num3), firstResourceDepositStructure ? firstResourceDepositStructure.ResourceCost : 0);
					teamSetup.Team.StartingResources = startingResources;
				}
			}
		}
		SpawnSideAITeams();
	}

	public override void SetTeamVersusMode(ETeamsVersus teamsVersusMode, bool preventSpawning = false)
	{
		Debug.Log("Team versus mode set to " + teamsVersusMode);
		TeamsVersus = teamsVersusMode;
		QueueCoopSpawnBaseStructures = !preventSpawning;
		if (!Game.GetIsClient())
		{
			RPC_SynchTeamVersus();
		}
	}

	private void SpawnBaseStructures()
	{
		GameListCache.QueryVector.Clear();
		float num = 1000f * MultStartingDist;
		Vector3 vector = Vector3.zero;
		if ((bool)Game.MainTerrain)
		{
			Vector3 extents = Game.MainTerrain.terrainData.bounds.extents;
			vector = Game.MainTerrain.transform.position + Game.MainTerrain.terrainData.bounds.center;
			num = (extents.x + extents.z) * 0.5f * MultStartingDist;
		}
		float num2 = UnityEngine.Random.Range(0f, 360f);
		List<StrategyTeamSetup> list = new List<StrategyTeamSetup>(8);
		List<Transform> list2 = new List<Transform>(8);
		int num3 = 1;
		if (IsCooperative)
		{
			if (CurrentCoopDifficulty == ECoopDifficulty.IMPOSSIBLE)
			{
				num3 = ((TeamsVersus == ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS) ? 2 : 3);
			}
			else if (CurrentCoopDifficulty >= ECoopDifficulty.HARD)
			{
				num3 = ((TeamsVersus == ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS) ? 1 : 2);
			}
			Debug.Log("Difficulty is " + CurrentCoopDifficulty.ToString() + ", enemy teams will have " + num3 + " bases at start");
		}
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (GetTeamSetupActive(teamSetup))
			{
				teamSetup.BaseStructureStartPointIndex = -1;
				int num4 = num3;
				if (IsCooperative && teamSetup.Team == LockedToTeam)
				{
					num4 = 1;
				}
				for (int i = 0; i < num4; i++)
				{
					list.Add(teamSetup);
					list2.Add(null);
				}
			}
		}
		int count = list.Count;
		if (count > 1)
		{
			for (int j = 0; j < count; j++)
			{
				int num5 = UnityEngine.Random.Range(0, count);
				int num6 = num5 + UnityEngine.Random.Range(0, count - 1);
				if (num6 >= count)
				{
					num6 -= count;
				}
				StrategyTeamSetup value = list[num5];
				list[num5] = list[num6];
				list[num6] = value;
			}
		}
		float num7 = ((count > 0) ? (360f / (float)(count + 1)) : 0f);
		if (UnityEngine.Random.Range(0f, 1f) < 0.5f)
		{
			num7 *= -1f;
		}
		for (int k = 0; k < count; k++)
		{
			StrategyTeamSetup strategyTeamSetup = list[k];
			Vector3 vector2 = vector + GameMath.GetDirectionFromYaw(num2 + num7 * (float)k) * (num * UnityEngine.Random.Range(0.8f, 1.2f));
			Transform transform = null;
			float num8 = float.MaxValue;
			int baseStructureStartPointIndex = 0;
			bool flag = false;
			int l = 0;
			for (int count2 = strategyTeamSetup.BaseStructureStartPoints.Count; l < count2; l++)
			{
				Transform transform2 = strategyTeamSetup.BaseStructureStartPoints[l];
				float num9 = GameMath.Distance2D(vector2, transform2.position);
				if (list2.Contains(transform2))
				{
					continue;
				}
				bool flag2 = false;
				for (int m = 0; m < count; m++)
				{
					StrategyTeamSetup strategyTeamSetup2 = list[m];
					if (strategyTeamSetup != strategyTeamSetup2 && !(strategyTeamSetup.Team == strategyTeamSetup2.Team))
					{
						Transform transform3 = list2[m];
						if ((bool)transform3 && !(GameMath.DistanceSq2D(transform2.position, transform3.position) > 1000f))
						{
							flag2 = true;
							break;
						}
					}
				}
				if (!transform || (num9 < num8 && !flag2) || (flag && !flag2))
				{
					transform = transform2;
					num8 = num9;
					baseStructureStartPointIndex = l;
					flag = flag2;
				}
			}
			list2[k] = transform;
			if ((bool)transform)
			{
				if (strategyTeamSetup.BaseStructureStartPointIndex == -1)
				{
					strategyTeamSetup.BaseStructureStartPointIndex = baseStructureStartPointIndex;
				}
				Game.SpawnPrefab(strategyTeamSetup.Team.BaseStructure.Prefab, null, strategyTeamSetup.Team, transform.position, transform.rotation);
				continue;
			}
			Debug.LogError("SpawnBaseStructures: No valid point was found for team '" + strategyTeamSetup.Team.TeamName + "', using raycast in world");
			if (Physics.Linecast(vector2 + Vector3.up * 1000f, vector2 + Vector3.down * 1000f, out var hitInfo, GamePhysics.TRACELAYERMASK_STATIC, QueryTriggerInteraction.Ignore))
			{
				vector2 = hitInfo.point;
			}
			Game.SpawnPrefab(strategyTeamSetup.Team.BaseStructure.Prefab, null, strategyTeamSetup.Team, vector2, Quaternion.identity);
		}
		if (!IsCooperative || CurrentCoopDifficulty < ECoopDifficulty.HARD || !LockedToTeam)
		{
			return;
		}
		Debug.Log("Difficulty is " + CurrentCoopDifficulty.ToString() + ", revealing player team " + LockedToTeam.GetTeamShortName() + " base to enemy");
		foreach (StrategyTeamSetup teamSetup2 in TeamSetups)
		{
			if (!GetTeamSetupActive(teamSetup2) || teamSetup2.Team == LockedToTeam)
			{
				continue;
			}
			Debug.Log("Revealing to " + teamSetup2.Team.GetTeamShortName() + "...");
			foreach (Structure structure in LockedToTeam.Structures)
			{
				teamSetup2.Team.DetectTarget(structure.Target);
			}
		}
	}

	public override void Restart()
	{
		ClearGame();
		LoadParams();
		UpdateInitialStrategySettings();
		if (Player.CurrentPlayer != null)
		{
			SelectTeam(null);
		}
		GameEvents.OnGameRestart?.Invoke(this);
		ShowTeamSelectMenu = ETeamSelect.SELECT_TEAM;
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			teamSetup.OnRestart();
		}
		VoteTimer = StartVoteTime;
		Timer = StartDelay;
		TimerBase = StartDelay;
		if (!Game.GetIsClient())
		{
			SetTeamVersusMode(ETeamsVersus.NONE);
			if ((bool)LocalInfrastructureSpawner)
			{
				LocalInfrastructureSpawner.SpawnPrefabs(LocalInfrastructureTeam, LocalInfrastructureProbabilityScale);
			}
			if (GlobeCampaign.IsCampaignRunning && (bool)GlobeCampaign.Instance.CurrentMission)
			{
				WorldLight.SetDayTime(GlobeCampaign.Instance.CurrentMission.DayTime);
			}
			else
			{
				WorldLight.SetDayTime(11f);
			}
			MissionState = EMissionState.INIT;
			TeamsVersus = ETeamsVersus.NONE;
			if (Game.GetIsServer())
			{
				RPC_InitGame();
			}
			GameAI.ReloadAIData();
		}
	}

	public override void PlayersShuffled()
	{
		SelectRole(ETeamRole.NONE);
		ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
	}

	protected override void InitTeamSetups()
	{
		base.InitTeamSetups();
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			BaseTeamSetups.Add(teamSetup);
		}
	}

	public override void StartGameLoop()
	{
		if (IsCooperative)
		{
			Debug.Log("Strategy Cooperative game mode started...");
		}
		else
		{
			Debug.Log("Strategy game mode started...");
		}
		if ((bool)NoTeamCameraRef)
		{
			Game.CurrentCamera.transform.position = NoTeamCameraRef.position;
			Game.CurrentCamera.transform.rotation = NoTeamCameraRef.rotation;
			VisionModeManager.PlayTransition();
		}
		Restart();
		base.StartGameLoop();
	}

	public override void InitGameLoop()
	{
		base.InitGameLoop();
		CompletedTutorials.Clear();
		foreach (StrategyTeamSetup teamSetup in TeamSetups)
		{
			if (teamSetup != null && teamSetup.Enabled)
			{
				teamSetup.Init();
			}
		}
		if (IsCooperative)
		{
			Debug.Log("Strategy Cooperative game mode initializing...");
		}
		else
		{
			Debug.Log("Strategy game mode initializing...");
		}
		ClearGame();
	}

	public override void EndGameLoop()
	{
		ClearGame();
	}

	public override void Deactivate()
	{
		base.Deactivate();
		if ((bool)StrategyMode.Instance)
		{
			UnityEngine.Object.Destroy(StrategyMode.Instance.gameObject);
		}
	}

	public override bool GetPickSpawn()
	{
		return ShowTeamSelectMenu == ETeamSelect.RESPAWN_MENU;
	}

	public override void Redeploy()
	{
		ShowTeamSelectMenu = ETeamSelect.RESPAWN_MENU;
	}

	public override void TeamSelectActionPerformed()
	{
		if (!UIManager.TextInputActive && !GlobeCampaign.IsCampaignRunning)
		{
			if (LockedToTeam != null)
			{
				ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
			}
			else
			{
				ShowTeamSelectMenu = ETeamSelect.SELECT_TEAM;
			}
		}
	}

	public override void RoleSelectActionPerformed()
	{
		if (!UIManager.TextInputActive && !GlobeCampaign.IsCampaignRunning)
		{
			ShowTeamSelectMenu = ETeamSelect.SELECT_ROLE;
		}
	}
}
