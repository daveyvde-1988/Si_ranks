using System.Collections.Generic;
using Silica.AI;
using UnityEngine;

public class UnitCompartment : MonoBehaviour
{
	private enum ERPCs : byte
	{
		ADD,
		REMOVE,
		REQUEST_ADD,
		REQUEST_REMOVE
	}

	[Tooltip("Whether this component is a driver component, which allows a unit to board as a driver instead of a passenger")]
	public bool IsDriver;

	[Tooltip("Whether this component is a construction logistics compartment, one of which must be filled (if present) by a driver to allow construction for the given team")]
	public bool IsConstructionLogistics;

	[Tooltip("Whether this compartment allows an eject action to eject all units")]
	public bool AllowEjectAction = true;

	[Tooltip("Whether this compartment destroys all boarded units when it is destroyed")]
	public bool DestroyBoarded = true;

	[Tooltip("Maximum number of units this compartment may hold (if this is a driver compartment, automatically reset to 1)")]
	[Range(1f, 200f)]
	public int MaxUnitCount = 6;

	[Tooltip("List of transforms to use for the exit position/rotation (leave empty to use this object's transform only; limit to 255!)")]
	public List<Transform> ExitTransforms = new List<Transform>();

	[Tooltip("Custom compartment animation data for all seats (if null, units will be disabled)")]
	public UnitCompartmentAnimationData AnimationData;

	[Tooltip("Whether the exiting unit is offset in the forward direction by its size")]
	public bool OffsetExitBySize = true;

	[Tooltip("Unique Identifier for this compartment")]
	public string ID;

	private const float EJECT_STAGGER_INTERVAL = 0.25f;

	private const float EJECT_TOTAL_BUDGET = 1.5f;

	private float m_EjectStep = 0.25f;

	private bool m_Ejecting;

	private bool m_EjectPlayers;

	private float m_EjectInterval;

	public BaseGameObject Owner { get; protected set; }

	public NetworkComponent NetworkComponent { get; private set; }

	public List<Unit> BoardedUnits { get; private set; } = new List<Unit>();

	public Unit DriverUnit
	{
		get
		{
			if (!IsDriver || BoardedUnits.Count <= 0)
			{
				return null;
			}
			return BoardedUnits[0];
		}
	}

	public int NumBoarded => BoardedUnits.Count;

	public Unit OwnerUnit => Owner as Unit;

	public Structure OwnerStructure => Owner as Structure;

	public Team Team
	{
		get
		{
			if (!Owner)
			{
				return null;
			}
			return Owner.Team;
		}
	}

	public void OnSendNetInit(GameByteStreamWriter packetWriter)
	{
		packetWriter.WriteByte((byte)BoardedUnits.Count);
		foreach (Unit boardedUnit in BoardedUnits)
		{
			packetWriter.WritePackedUInt32(boardedUnit.NetworkComponent.NetID);
		}
	}

	public void OnNetInit(GameByteStreamReader packetReader)
	{
		BoardedUnits.Clear();
		byte b = packetReader.ReadByte();
		for (byte b2 = 0; b2 < b; b2++)
		{
			uint num = packetReader.ReadPackedUInt32();
			Unit unit = NetworkComponent.NetworkComponentsIndexed[num].Owner as Unit;
			AddUnit(unit);
		}
	}

	public bool GetOverrideSendRPC(byte rpcIndex)
	{
		return rpcIndex switch
		{
			2 => true, 
			3 => true, 
			_ => false, 
		};
	}

	public void OnNetRPC(byte rpcIndex, GameByteStreamReader packetReader)
	{
		uint num = packetReader.ReadPackedUInt32();
		if (num >= NetworkComponent.NetworkComponentsIndexed.Length)
		{
			Debug.LogError("Error! Unit Compartment received id outside the NetworkComponentsIndexed array!");
			return;
		}
		NetworkComponent networkComponent = NetworkComponent.NetworkComponentsIndexed[num];
		if (!networkComponent)
		{
			Debug.LogError("Error! Unit Compartment references non-existent Network Component!");
			return;
		}
		switch ((ERPCs)rpcIndex)
		{
		case ERPCs.ADD:
			if (!Game.GetIsServer())
			{
				Unit unit3 = networkComponent.Owner as Unit;
				if ((bool)unit3)
				{
					AddUnit(unit3);
				}
			}
			break;
		case ERPCs.REMOVE:
			if (!Game.GetIsServer())
			{
				int exitIndex = packetReader.ReadByte();
				Unit unit2 = networkComponent.Owner as Unit;
				if ((bool)unit2)
				{
					RemoveUnit(unit2, exitIndex);
				}
			}
			break;
		case ERPCs.REQUEST_ADD:
			if (Game.GetIsServer())
			{
				Unit unit4 = networkComponent.Owner as Unit;
				if ((bool)unit4)
				{
					RequestAddUnit(unit4);
				}
			}
			break;
		case ERPCs.REQUEST_REMOVE:
			if (Game.GetIsServer())
			{
				Unit unit = networkComponent.Owner as Unit;
				if ((bool)unit)
				{
					RequestRemoveUnit(unit);
				}
			}
			break;
		}
	}

	private void RPC_AddUnit(Unit unit)
	{
		if (Game.GetIsServer())
		{
			GameByteStreamWriter gameByteStreamWriter = NetworkComponent.CreateRPCPacket(this, 0);
			if (gameByteStreamWriter != null)
			{
				gameByteStreamWriter.WritePackedUInt32(unit.NetworkComponent.NetID);
				NetworkComponent.SendRPCPacket(gameByteStreamWriter);
			}
		}
	}

	private void RPC_RemoveUnit(Unit unit, int exitIndex)
	{
		if (Game.GetIsServer())
		{
			GameByteStreamWriter gameByteStreamWriter = NetworkComponent.CreateRPCPacket(this, 1);
			if (gameByteStreamWriter != null)
			{
				gameByteStreamWriter.WritePackedUInt32(unit.NetworkComponent.NetID);
				gameByteStreamWriter.WriteByte((byte)exitIndex);
				NetworkComponent.SendRPCPacket(gameByteStreamWriter);
			}
		}
	}

	private void RPC_RequestAddUnit(Unit unit)
	{
		if (Game.GetIsClient())
		{
			GameByteStreamWriter gameByteStreamWriter = NetworkComponent.CreateRPCPacket(this, 2);
			if (gameByteStreamWriter != null)
			{
				gameByteStreamWriter.WritePackedUInt32(unit.NetworkComponent.NetID);
				NetworkComponent.SendRPCPacket(gameByteStreamWriter);
			}
		}
	}

	private void RPC_RequestRemoveUnit(Unit unit)
	{
		if (Game.GetIsClient())
		{
			GameByteStreamWriter gameByteStreamWriter = NetworkComponent.CreateRPCPacket(this, 3);
			if (gameByteStreamWriter != null)
			{
				gameByteStreamWriter.WritePackedUInt32(unit.NetworkComponent.NetID);
				NetworkComponent.SendRPCPacket(gameByteStreamWriter);
			}
		}
	}

	public void TeleportUnitToExit(Unit unit, int exitIndex)
	{
		Transform transform = ((ExitTransforms.Count > 0) ? ExitTransforms[exitIndex] : base.transform);
		Vector3 position = transform.position;
		if (OffsetExitBySize)
		{
			position += transform.forward * (0f - unit.PhysicalBounds.max.z);
		}
		position += (transform.forward * Random.Range(0f - unit.PhysicalBounds.max.z, 0f) + transform.right * Random.Range(unit.PhysicalBounds.min.x, unit.PhysicalBounds.max.x)) * Random.Range(1f, 2f);
		Quaternion rotation = transform.rotation;
		unit.QueueTeleport(position, rotation);
		if ((bool)unit.RigidBody)
		{
			if ((bool)Owner.RigidBody)
			{
				unit.RigidBody.linearVelocity = Owner.RigidBody.linearVelocity;
				unit.RigidBody.angularVelocity = Owner.RigidBody.angularVelocity;
			}
			else
			{
				unit.RigidBody.linearVelocity = Vector3.zero;
				unit.RigidBody.angularVelocity = Vector3.zero;
			}
		}
		if (!Game.GetIsClient() && unit.AIControlled)
		{
			unit.OrderAgent?.IssueResolvedOrder(transform.position + transform.forward * (0f - unit.PhysicalRadius - 10f), null, OrderIssueParams.Ai(AgentMoveSpeed.Fast));
		}
	}

	public int GetExitClearForUnit(Unit unit)
	{
		return 0;
	}

	protected virtual void OnAddUnit(Unit unit)
	{
	}

	protected virtual void OnRemoveUnit(Unit unit, int exitIndex)
	{
	}

	public bool GetCanEjectBoardedUnits(bool ejectPlayers)
	{
		if (!AllowEjectAction)
		{
			return false;
		}
		for (int num = BoardedUnits.Count - 1; num >= 0; num--)
		{
			Unit unit = BoardedUnits[num];
			if (ejectPlayers || !unit.PlayerControlled)
			{
				return true;
			}
		}
		return false;
	}

	public void EjectBoardedUnits(bool ejectPlayers)
	{
		if (Game.GetIsClient())
		{
			return;
		}
		CancelStaggeredEject();
		for (int num = BoardedUnits.Count - 1; num >= 0; num--)
		{
			Unit unit = BoardedUnits[num];
			if (ejectPlayers || !unit.PlayerControlled)
			{
				unit.ExitCompartment();
			}
		}
	}

	public void EjectBoardedUnitsStaggered(bool ejectPlayers)
	{
		if (!Game.GetIsClient() && BoardedUnits.Count > 0 && !m_Ejecting)
		{
			m_EjectPlayers = ejectPlayers;
			m_Ejecting = true;
			m_EjectInterval = 0f;
			m_EjectStep = Mathf.Min(0.25f, 1.5f / (float)Mathf.Max(1, BoardedUnits.Count));
			OrderedUpdateManager.RegisterUpdate(OrderedUpdate, Owner ? Owner.CallPriority_Update : 0);
		}
	}

	public void CancelStaggeredEject()
	{
		if (m_Ejecting)
		{
			m_Ejecting = false;
			OrderedUpdateManager.UnregisterUpdate(OrderedUpdate);
		}
	}

	private void OrderedUpdate(float dt)
	{
		if (!m_Ejecting)
		{
			return;
		}
		m_EjectInterval -= dt;
		if (m_EjectInterval > 0f)
		{
			return;
		}
		for (int num = BoardedUnits.Count - 1; num >= 0; num--)
		{
			Unit unit = BoardedUnits[num];
			if ((bool)unit && (m_EjectPlayers || !unit.PlayerControlled))
			{
				unit.ExitCompartment();
				m_EjectInterval = m_EjectStep;
				return;
			}
		}
		CancelStaggeredEject();
	}

	public void DestroyBoardedUnits()
	{
		if (Game.GetIsClient())
		{
			return;
		}
		for (int num = BoardedUnits.Count - 1; num >= 0; num--)
		{
			Unit unit = BoardedUnits[num];
			if ((bool)unit.DamageManager)
			{
				if ((bool)AnimationData)
				{
					unit.ExitCompartment();
				}
				unit.DamageManager.SetHealth(0f, null, force: true);
			}
		}
	}

	protected void AddUnit(Unit unit)
	{
		if (!unit || (!Game.GetIsClient() && NumBoarded >= MaxUnitCount))
		{
			return;
		}
		unit.SetUnitCompartment(this);
		if (Game.GetIsServer())
		{
			RPC_AddUnit(unit);
		}
		if (IsDriver && !Game.GetIsClient() && (bool)unit.ControlledBy && (bool)OwnerUnit)
		{
			Player controlledBy = unit.ControlledBy;
			if (Game.GetIsMultiplayer())
			{
				NetworkLayer.SendPlayerSelectUnit(controlledBy, OwnerUnit);
			}
			controlledBy.ControlledUnit = OwnerUnit;
		}
		OnAddUnit(unit);
	}

	protected void RemoveUnit(Unit unit, int exitIndex)
	{
		if (Game.IsLoading || Game.IsQuitting || !unit || exitIndex == -1)
		{
			return;
		}
		if (Game.GetIsServer())
		{
			RPC_RemoveUnit(unit, exitIndex);
		}
		if (IsDriver && !Game.GetIsClient() && unit == DriverUnit && (bool)OwnerUnit && (bool)OwnerUnit.ControlledBy)
		{
			Player controlledBy = OwnerUnit.ControlledBy;
			if (Game.GetIsMultiplayer())
			{
				NetworkLayer.SendPlayerSelectUnit(controlledBy, unit);
			}
			controlledBy.ControlledUnit = unit;
		}
		unit.SetUnitCompartment(null);
		OnRemoveUnit(unit, exitIndex);
	}

	public void OnBoardedUnitDeleted(Unit unit)
	{
		if (BoardedUnits.Contains(unit))
		{
			BoardedUnits.Remove(unit);
			OnRemoveUnit(unit, 0);
		}
	}

	public void RequestAddUnit(Unit unit)
	{
		if (NumBoarded < MaxUnitCount)
		{
			if (Game.GetIsClient())
			{
				RPC_RequestAddUnit(unit);
			}
			else
			{
				AddUnit(unit);
			}
		}
	}

	public void RequestRemoveUnit(Unit unit)
	{
		int exitClearForUnit = GetExitClearForUnit(unit);
		if (exitClearForUnit != -1)
		{
			if (Game.GetIsClient())
			{
				RPC_RequestRemoveUnit(unit);
			}
			else
			{
				RemoveUnit(unit, exitClearForUnit);
			}
		}
	}

	public virtual bool CanBoard(Unit unit)
	{
		if (NumBoarded >= MaxUnitCount)
		{
			return false;
		}
		if (IsDriver && (bool)OwnerUnit && (bool)OwnerUnit.ControlledBy)
		{
			return false;
		}
		return true;
	}

	private void OnEnable()
	{
		Owner = transform.GetBaseGameObject();
		NetworkComponent = gameObject.GetNetworkComponent();
	}

	private void OnDisable()
	{
		CancelStaggeredEject();
	}
}
