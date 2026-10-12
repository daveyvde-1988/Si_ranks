using System;
using System.Collections.Generic;
using Silica;
using Silica.AI;
using Silica.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Interactions;

[RequireComponent(typeof(NetworkComponent))]
public class FPSCommanding : MonoBehaviour
{
	[Serializable]
	private class TaskIconPair
	{
		public EAITaskType TaskType;

		public Sprite Sprite;
	}

	public class AimingData
	{
		public Target target;

		public RaycastHit raycastHit;
	}

	private enum ERPCs
	{
		GROUP_INVITE,
		GROUP_KICK,
		MOVE,
		PROTECT,
		ATTACK,
		STOP_ALL,
		STOP_UNIT,
		FOLLOW_UNIT,
		FOLLOW_ALL
	}

	[SerializeField]
	private List<TaskIconPair> TaskIconPairs = new List<TaskIconPair>();

	public static Action OnHoldStarted;

	public static Action OnHoldCanceled;

	public static Action OnHoldPerformed;

	private const float TRACE_DISTANCE = 1000f;

	[Tooltip("Max count of units in a player group")]
	public int GroupUnitLimit = 8;

	private AimingData aimingData = new AimingData();

	private NetworkComponent networkComponent;

	private bool holdActionActive;

	public static FPSCommanding Instance;

	public static bool CanLocalFPSCommand
	{
		get
		{
			if ((bool)Instance && (bool)Unit.CurrentUnit && Unit.CurrentUnit.AIGroup != null && Unit.CurrentUnit.AIGroup.UnitCount > 1 && Unit.CurrentUnit.AIGroup.Leader == Unit.CurrentUnit && (bool)Player.CurrentPlayer)
			{
				return !Player.CurrentPlayer.IsCommander;
			}
			return false;
		}
	}

	private void Awake()
	{
		Instance = this;
	}

	private void Start()
	{
		InputAction action = GameInput.GetAction(GameInput.InputMap.Unit, "CommandingTap");
		action.started += TapActionStarted;
		action.performed += TapActionPerformed;
		InputAction action2 = GameInput.GetAction(GameInput.InputMap.Unit, "CommandingHold");
		action2.started += HoldActionStarted;
		action2.performed += HoldActionPerformed;
		action2.canceled += HoldActionCanceled;
		GameEvents.OnPlayerChangedUnit = (Action<Player, Unit, Unit>)Delegate.Combine(GameEvents.OnPlayerChangedUnit, new Action<Player, Unit, Unit>(OnPlayerChangedUnit));
		GameEvents.OnPlayerChangedTeam = (Action<Player, Team, Team>)Delegate.Combine(GameEvents.OnPlayerChangedTeam, new Action<Player, Team, Team>(OnPlayerChangedTeam));
		networkComponent = gameObject.GetComponent<NetworkComponent>();
	}

	public Sprite GetTaskIcon(EAITaskType taskType)
	{
		for (int i = 0; i < TaskIconPairs.Count; i++)
		{
			if (TaskIconPairs[i].TaskType == taskType)
			{
				return TaskIconPairs[i].Sprite;
			}
		}
		return null;
	}

	private void OnDestroy()
	{
		InputAction action = GameInput.GetAction(GameInput.InputMap.Unit, "CommandingTap");
		action.started -= TapActionStarted;
		action.performed -= TapActionPerformed;
		InputAction action2 = GameInput.GetAction(GameInput.InputMap.Unit, "CommandingHold");
		action2.started -= HoldActionStarted;
		action2.performed -= HoldActionPerformed;
		action2.canceled -= HoldActionCanceled;
		GameEvents.OnPlayerChangedUnit = (Action<Player, Unit, Unit>)Delegate.Remove(GameEvents.OnPlayerChangedUnit, new Action<Player, Unit, Unit>(OnPlayerChangedUnit));
		GameEvents.OnPlayerChangedTeam = (Action<Player, Team, Team>)Delegate.Remove(GameEvents.OnPlayerChangedTeam, new Action<Player, Team, Team>(OnPlayerChangedTeam));
	}

	private void OnPlayerChangedUnit(Player player, Unit oldUnit, Unit newUnit)
	{
		if (newUnit != null && !Game.GetIsClient() && !(player.Team == null))
		{
			if ((bool)oldUnit && oldUnit.AIGroup != null && oldUnit.Team == newUnit.Team)
			{
				AIGroup aIGroup = oldUnit.AIGroup;
				aIGroup.AddUnit(newUnit);
				aIGroup.RemoveFromIgnoreList(newUnit);
				aIGroup.RemoveUnit(oldUnit);
			}
			else
			{
				AIGroup.CreateNewGroup(player.Team, null).AddUnit(newUnit);
			}
		}
	}

	private void OnPlayerChangedTeam(Player player, Team oldTeam, Team newTeam)
	{
		if (!Game.GetIsClient() && newTeam != null && player.ControlledUnit != null)
		{
			AIGroup.CreateNewGroup(newTeam, null).AddUnit(player.ControlledUnit);
		}
	}

	private void CacheData()
	{
		aimingData.target = Crosshair.CurrentTarget;
		if ((!aimingData.target || aimingData.target.ObjectInfo.ObjectType != ObjectInfoType.Unit) && !RaycastIgnoringOwnUnit(out aimingData.raycastHit))
		{
			aimingData.raycastHit = default;
		}
	}

	private bool RaycastIgnoringOwnUnit(out RaycastHit hit)
	{
		hit = default;
		Transform transform = Camera.main.transform;
		int num = Physics.RaycastNonAlloc(transform.position, transform.forward, GameListCache.QueryRaycastHits, 1000f, GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC);
		float num2 = float.MaxValue;
		for (int i = 0; i < num; i++)
		{
			RaycastHit raycastHit = GameListCache.QueryRaycastHits[i];
			if (!(raycastHit.distance >= num2) && (bool)raycastHit.collider && !(raycastHit.collider.GetBaseGameObject() == Unit.CurrentUnit))
			{
				num2 = raycastHit.distance;
				hit = raycastHit;
			}
		}
		return num2 < float.MaxValue;
	}

	private void HoldActionStarted(InputAction.CallbackContext context)
	{
		CacheData();
		holdActionActive = true;
		if (OnHoldStarted != null)
		{
			OnHoldStarted();
		}
	}

	private void HoldActionCanceled(InputAction.CallbackContext context)
	{
		holdActionActive = false;
		if (OnHoldCanceled != null)
		{
			OnHoldCanceled();
		}
	}

	private void HoldActionPerformed(InputAction.CallbackContext context)
	{
		if (OnHoldPerformed != null)
		{
			OnHoldPerformed();
		}
		if ((!aimingData.target || aimingData.target.ObjectInfo.ObjectType != ObjectInfoType.Unit) && aimingData.raycastHit.point != default(Vector3))
		{
			MoveClient(aimingData.raycastHit.point);
		}
		else if ((bool)aimingData.target)
		{
			if ((bool)aimingData.target.OwnerUnit && aimingData.target.OwnerUnit.AIGroup == Player.CurrentPlayer.Group)
			{
				KickFromGroupClient(aimingData.target);
			}
			else if (aimingData.target.Team == Team.CurrentTeam)
			{
				ProtectClient(aimingData.target);
			}
		}
	}

	private void TapActionStarted(InputAction.CallbackContext context)
	{
		if (!(context.interaction is TapInteraction))
		{
			CacheData();
		}
	}

	private void TapActionPerformed(InputAction.CallbackContext context)
	{
		if (context.interaction is TapInteraction && (bool)aimingData.target)
		{
			if (aimingData.target.Team == Team.CurrentTeam)
			{
				AIGroup.EIgnoreTaskReason reason;
				if ((bool)aimingData.target.OwnerUnit && (aimingData.target.OwnerUnit.AIGroup == null || (aimingData.target.OwnerUnit.AIGroup != Player.CurrentPlayer.Group && !aimingData.target.OwnerUnit.AIGroup.PlayerControlled)))
				{
					InviteToGroupClient(aimingData.target);
				}
				else if ((bool)aimingData.target.OwnerUnit && aimingData.target.OwnerUnit.AIGroup.IgnoresTask(aimingData.target.OwnerUnit, out reason))
				{
					switch (reason)
					{
					case AIGroup.EIgnoreTaskReason.NONE:
						UnitFollowClient(aimingData.target);
						break;
					case AIGroup.EIgnoreTaskReason.FOLLOWING_LEADER:
						StopUnitClient(aimingData.target);
						break;
					}
				}
				else
				{
					StopUnitClient(aimingData.target);
				}
			}
			else
			{
				AttackClient(aimingData.target);
			}
		}
		else if (context.interaction is MultiTapInteraction && Player.CurrentPlayer.Group != null)
		{
			if (Player.CurrentPlayer.Group.AllStopped())
			{
				FollowAllClient();
			}
			else
			{
				StopAllClient();
			}
		}
	}

	public void OnNetRPC(byte rpcIndex, GameByteStreamReader packetReader)
	{
		switch ((ERPCs)rpcIndex)
		{
		case ERPCs.GROUP_INVITE:
			if (Game.GetIsServer())
			{
				Player player2 = Player.FindPlayer(packetReader.ReadNetworkID());
				NetworkComponent netObject = NetworkComponent.GetNetObject(packetReader.ReadUInt32());
				if (netObject != null)
				{
					InviteToGroupServer(player2, netObject.gameObject.GetTarget());
				}
			}
			break;
		case ERPCs.MOVE:
			if (Game.GetIsServer())
			{
				Player player6 = Player.FindPlayer(packetReader.ReadNetworkID());
				Vector3 position = packetReader.ReadVector3();
				MoveServer(player6, position);
			}
			break;
		case ERPCs.GROUP_KICK:
			if (Game.GetIsServer())
			{
				Player player8 = Player.FindPlayer(packetReader.ReadNetworkID());
				NetworkComponent netObject5 = NetworkComponent.GetNetObject(packetReader.ReadUInt32());
				KickFromGroupServer(player8, netObject5.gameObject.GetTarget());
			}
			break;
		case ERPCs.PROTECT:
			if (Game.GetIsServer())
			{
				Player player4 = Player.FindPlayer(packetReader.ReadNetworkID());
				NetworkComponent netObject3 = NetworkComponent.GetNetObject(packetReader.ReadUInt32());
				ProtectServer(player4, netObject3.gameObject.GetTarget());
			}
			break;
		case ERPCs.ATTACK:
			if (Game.GetIsServer())
			{
				Player player9 = Player.FindPlayer(packetReader.ReadNetworkID());
				NetworkComponent netObject6 = NetworkComponent.GetNetObject(packetReader.ReadUInt32());
				AttackServer(player9, netObject6.gameObject.GetTarget());
			}
			break;
		case ERPCs.STOP_ALL:
			if (Game.GetIsServer())
			{
				Player player7 = Player.FindPlayer(packetReader.ReadNetworkID());
				StopAllServer(player7);
			}
			break;
		case ERPCs.STOP_UNIT:
			if (Game.GetIsServer())
			{
				Player player5 = Player.FindPlayer(packetReader.ReadNetworkID());
				NetworkComponent netObject4 = NetworkComponent.GetNetObject(packetReader.ReadUInt32());
				StopUnitServer(player5, netObject4.gameObject.GetTarget());
			}
			break;
		case ERPCs.FOLLOW_UNIT:
			if (Game.GetIsServer())
			{
				Player player3 = Player.FindPlayer(packetReader.ReadNetworkID());
				NetworkComponent netObject2 = NetworkComponent.GetNetObject(packetReader.ReadUInt32());
				UnitFollowServer(player3, netObject2.gameObject.GetTarget());
			}
			break;
		case ERPCs.FOLLOW_ALL:
			if (Game.GetIsServer())
			{
				Player player = Player.FindPlayer(packetReader.ReadNetworkID());
				FollowAllServer(player);
			}
			break;
		}
	}

	private void KickFromGroupServer(Player player, Target target)
	{
		if ((bool)target && (bool)player && (bool)target.OwnerUnit && player.Group != null)
		{
			player.Group.RemoveUnit(target.OwnerUnit);
		}
	}

	private void KickFromGroupClient(Target target)
	{
		if (!target)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			KickFromGroupServer(Player.CurrentPlayer, target);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 1);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteUInt32(target.NetworkComponent.NetID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void UnitFollowServer(Player player, Target target)
	{
		if ((bool)player && (bool)target && (bool)target.OwnerUnit && target.OwnerUnit.AIGroup != null)
		{
			target.OwnerUnit.AIGroup.IgnoreTask(target.OwnerUnit, AIGroup.EIgnoreTaskReason.FOLLOWING_LEADER);
		}
	}

	private void UnitFollowClient(Target target)
	{
		if (!target.OwnerUnit)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			UnitFollowServer(Player.CurrentPlayer, target);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 7);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteUInt32(target.NetworkComponent.NetID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void StopUnitServer(Player player, Target target)
	{
		if ((bool)player && (bool)target && (bool)target.OwnerUnit && target.OwnerUnit.AIGroup != null)
		{
			target.OwnerUnit.AIGroup.IgnoreTask(target.OwnerUnit, AIGroup.EIgnoreTaskReason.NONE);
		}
	}

	private void StopUnitClient(Target target)
	{
		if (!target.OwnerUnit)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			StopUnitServer(Player.CurrentPlayer, target);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 6);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteUInt32(target.NetworkComponent.NetID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void FollowAllClient()
	{
		if (!Game.GetIsClient())
		{
			FollowAllServer(Player.CurrentPlayer);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 8);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void FollowAllServer(Player player)
	{
		if ((bool)player && player.Group != null)
		{
			List<Unit> units = player.Group.Units;
			for (int i = 0; i < units.Count; i++)
			{
				player.Group.IgnoreTask(units[i], AIGroup.EIgnoreTaskReason.FOLLOWING_LEADER);
			}
		}
	}

	private void StopAllServer(Player player)
	{
		if ((bool)player && player.Group != null)
		{
			List<Unit> units = player.Group.Units;
			for (int i = 0; i < units.Count; i++)
			{
				player.Group.IgnoreTask(units[i], AIGroup.EIgnoreTaskReason.NONE);
			}
			player.Group.Task = null;
			player.Group.OnStopOrder(AgentMoveSpeed.Normal, calledByAI: false);
		}
	}

	private void StopAllClient()
	{
		if (!Game.GetIsClient())
		{
			StopAllServer(Player.CurrentPlayer);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 5);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void InviteToGroupServer(Player player, Target target)
	{
		if ((bool)target && (bool)player && (bool)target.OwnerUnit && player.Group != null && player.Group.UnitCount < GroupUnitLimit)
		{
			player.Group.AddUnit(target.OwnerUnit);
			if (GameEvents.OnTutorialTaskEvent != null && player == Player.CurrentPlayer)
			{
				GameEvents.OnTutorialTaskEvent(ETutorialTaskEvent.GROUP_ADD, gameObject);
			}
		}
	}

	private void InviteToGroupClient(Target target)
	{
		if (!target || !target.OwnerUnit)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			InviteToGroupServer(Player.CurrentPlayer, target);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 0);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteUInt32(target.NetworkComponent.NetID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void AttackServer(Player player, Target target)
	{
		if ((bool)player && player.Group != null)
		{
			GameListCache.QueryTargets.Clear();
			GameListCache.QueryTargets.Add(target);
			AIGroup aIGroup = player.Group;
			Team team = player.Team;
			List<Target> queryTargets = GameListCache.QueryTargets;
			aIGroup.Task = AITaskBase.CreateTask(EAITaskType.Attack, team, 10, default, queryTargets);
			GameListCache.QueryTargets.Clear();
		}
	}

	private void AttackClient(Target target)
	{
		if (!target)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			AttackServer(Player.CurrentPlayer, target);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 4);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteUInt32(target.NetworkComponent.NetID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void MoveServer(Player player, Vector3 position)
	{
		if ((bool)player && player.Group != null)
		{
			player.Group.Task = AITaskBase.CreateTask(EAITaskType.Explore, player.Team, 10, position);
		}
	}

	private void MoveClient(Vector3 position)
	{
		if (Player.CurrentPlayer.Group.UnitCount <= 1)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			MoveServer(Player.CurrentPlayer, position);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 2);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteVector3(position);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}

	private void ProtectServer(Player player, Target target)
	{
		if ((bool)target && (bool)player && player.Group != null)
		{
			GameListCache.QueryTargets.Clear();
			GameListCache.QueryTargets.Add(target);
			AIGroup aIGroup = player.Group;
			Team team = player.Team;
			List<Target> queryTargets = GameListCache.QueryTargets;
			aIGroup.Task = AITaskBase.CreateTask(EAITaskType.Protect, team, 10, default, queryTargets);
			GameListCache.QueryTargets.Clear();
		}
	}

	private void ProtectClient(Target target)
	{
		if (Player.CurrentPlayer.Group.UnitCount <= 1)
		{
			return;
		}
		if (!Game.GetIsClient())
		{
			ProtectServer(Player.CurrentPlayer, target);
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = networkComponent.CreateRPCPacket(this, 3);
		if (gameByteStreamWriter != null)
		{
			gameByteStreamWriter.WriteNetworkID(Player.CurrentPlayer.PlayerID);
			gameByteStreamWriter.WriteUInt32(target.NetworkComponent.NetID);
			networkComponent.SendRPCPacket(gameByteStreamWriter);
		}
	}
}
