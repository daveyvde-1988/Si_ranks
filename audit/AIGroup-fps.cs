using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Silica.AI;

public class AIGroup
{
	public enum EIgnoreTaskReason
	{
		NONE,
		FOLLOWING_LEADER
	}

	public enum EAIGroupPacketType
	{
		Init,
		Update
	}

	public enum EAIGroupChange : byte
	{
		UnitAdded,
		UnitRemoved,
		LeaderChanged
	}

	public const float UPDATE_INTERVAL = 1f;

	public static readonly Dictionary<uint, AIGroup> ActiveGroups = new Dictionary<uint, AIGroup>(128);

	public static readonly Dictionary<uint, AIGroup> PooledGroups = new Dictionary<uint, AIGroup>(128);

	private static List<AIGroup> QueueReturnToPoolGroups = new List<AIGroup>(4);

	private static readonly List<Unit> s_FarStragglers = new List<Unit>(4);

	public const uint NULL_ID = uint.MaxValue;

	private Dictionary<ObjectInfo, int> UnitTypeCounts = new Dictionary<ObjectInfo, int>();

	public List<EAITaskType> SuitedToTaskTypes = new List<EAITaskType>();

	public List<Unit> Units = new List<Unit>(8);

	private Team m_Team;

	private AITaskBase m_Task;

	public int TotalDefenseRating;

	public int TotalOffenseRating;

	public int MaxDefenseRating;

	public int MinDefenseRating;

	public int AvgDefenseRating;

	public int MaxOffenseRating;

	public int MinOffenseRating;

	public int AvgOffenseRating;

	public int MaxSpeedRating;

	public int MinSpeedRating;

	public int AvgSpeedRating;

	public int MaxRangeRating;

	public int MinRangeRating;

	public int AvgRangeRating;

	private float CurrentThinkDelay;

	private Dictionary<Unit, EIgnoreTaskReason> IgnoreTaskUnitsList;

	public Action OnUnitTaskChange;

	private static readonly List<Unit> _unitCache = new List<Unit>();

	private static readonly List<(Unit unit, EIgnoreTaskReason reason)> _ignoreTaskCache = new List<(Unit, EIgnoreTaskReason)>();

	public uint ID { get; private set; } = uint.MaxValue;

	public Unit Leader { get; private set; }

	public bool PlayerControlled
	{
		get
		{
			if (!Leader)
			{
				return false;
			}
			return Leader.PlayerControlled;
		}
	}

	public Vector3 Position
	{
		get
		{
			if (!Leader)
			{
				return Vector3.zero;
			}
			return Leader.transform.position;
		}
	}

	public Team Team
	{
		get
		{
			return m_Team;
		}
		private set
		{
			if (!(m_Team == value))
			{
				if ((bool)m_Team)
				{
					m_Team.Groups.Remove(this);
				}
				m_Team = value;
				if ((bool)m_Team)
				{
					m_Team.Groups.Add(this);
				}
			}
		}
	}

	public AIGroupPreset Preset { get; private set; }

	public Dictionary<byte, int> PresetNeeds { get; private set; } = new Dictionary<byte, int>();

	public int PresetNeedsNum { get; private set; }

	public EAIGroupPresetType Type
	{
		get
		{
			if (Preset == null)
			{
				return EAIGroupPresetType.Combat;
			}
			return Preset.Type;
		}
	}

	public AITaskBase Task
	{
		get
		{
			return m_Task;
		}
		set
		{
			if (m_Task != value)
			{
				CheckIgnoreList(value);
				if (m_Task != null)
				{
					m_Task.RemoveGroup(this);
				}
				m_Task = value;
				if (m_Task != null)
				{
					m_Task.AddGroup(this);
				}
				if (OnUnitTaskChange != null)
				{
					OnUnitTaskChange();
				}
			}
		}
	}

	public int UnitCount => Units.Count;

	public int UnitsLostToDestruction { get; private set; }

	public int UnitsTransferredAway { get; private set; }

	public bool IsForHarvest { get; private set; }

	public bool IsForExplore { get; private set; }

	public bool IsForOffense { get; private set; }

	public bool IsForDefense { get; private set; }

	private static List<AIGroup> CreatedGroups { get; } = new List<AIGroup>();

	private static List<(AIGroup group, EAIGroupChange changeType, uint unitID)> GroupChanges { get; } = new List<(AIGroup, EAIGroupChange, uint)>();

	private static List<(AIGroup group, uint unitID, EIgnoreTaskReason reason)> IgnoreTaskChanges { get; } = new List<(AIGroup, uint, EIgnoreTaskReason)>();

	internal static List<uint> RemovedGroupIDs { get; } = new List<uint>();

	public void SetPreset(AIGroupPreset preset)
	{
		if (Preset != preset)
		{
			Preset = preset;
			UpdatePresetNeeds();
		}
	}

	public int GetAIUnitsCount()
	{
		int num = 0;
		for (int i = 0; i < Units.Count; i++)
		{
			if (Units[i].PlayerControlled)
			{
				num++;
			}
		}
		return Units.Count - num;
	}

	public int GetPlayerUnitsCount()
	{
		int num = 0;
		for (int i = 0; i < Units.Count; i++)
		{
			if (Units[i].PlayerControlled)
			{
				num++;
			}
		}
		return num;
	}

	public bool GetAnyPlayerUnits()
	{
		for (int i = 0; i < Units.Count; i++)
		{
			if (Units[i].PlayerControlled)
			{
				return true;
			}
		}
		return false;
	}

	public bool AllStopped()
	{
		if (IgnoreTaskUnitsList == null || IgnoreTaskUnitsList.Count != GetAIUnitsCount())
		{
			return false;
		}
		for (int i = 0; i < Units.Count; i++)
		{
			if (!(Units[i] == null) && !Units[i].IsDestroyed && IgnoreTaskUnitsList.TryGetValue(Units[i], out var value) && !Units[i].PlayerControlled && value != EIgnoreTaskReason.NONE)
			{
				return false;
			}
		}
		return true;
	}

	public void RemoveFromIgnoreList(Unit unit)
	{
		if (!(unit == null) && IgnoreTaskUnitsList != null)
		{
			IgnoreTaskUnitsList.Remove(unit);
		}
	}

	private void CheckIgnoreList(AITaskBase newTask)
	{
		if (IgnoreTaskUnitsList == null || newTask == null)
		{
			return;
		}
		if (!AllStopped())
		{
			for (int num = IgnoreTaskUnitsList.Count - 1; num >= 0; num--)
			{
				if (IgnoreTaskUnitsList.ElementAt(num).Value == EIgnoreTaskReason.FOLLOWING_LEADER)
				{
					IgnoreTaskUnitsList.Remove(IgnoreTaskUnitsList.ElementAt(num).Key);
				}
			}
		}
		else
		{
			IgnoreTaskUnitsList.Clear();
		}
	}

	private bool IgnoresTask(Unit unit)
	{
		if (IgnoreTaskUnitsList != null)
		{
			return IgnoreTaskUnitsList.ContainsKey(unit);
		}
		return false;
	}

	public bool IgnoresTask(Unit unit, out EIgnoreTaskReason reason)
	{
		reason = EIgnoreTaskReason.NONE;
		if (!unit || IgnoreTaskUnitsList == null)
		{
			return false;
		}
		if (IgnoreTaskUnitsList.ContainsKey(unit))
		{
			reason = IgnoreTaskUnitsList[unit];
			return true;
		}
		return false;
	}

	public void IgnoreTask(Unit unit, EIgnoreTaskReason reason)
	{
		if (!unit || unit.PlayerControlled)
		{
			return;
		}
		if (IgnoreTaskUnitsList == null)
		{
			IgnoreTaskUnitsList = new Dictionary<Unit, EIgnoreTaskReason>();
		}
		if (IgnoreTaskUnitsList.ContainsKey(unit))
		{
			if (IgnoreTaskUnitsList[unit] == reason)
			{
				return;
			}
			IgnoreTaskUnitsList[unit] = reason;
			if (Game.GetIsServer() && HasPlayer())
			{
				IgnoreTaskChanges.Add((this, unit.NetworkComponent.NetID, reason));
			}
		}
		else
		{
			IgnoreTaskUnitsList.Add(unit, reason);
			if (Game.GetIsServer() && HasPlayer())
			{
				IgnoreTaskChanges.Add((this, unit.NetworkComponent.NetID, reason));
			}
		}
		if (!Game.GetIsClient())
		{
			switch (IgnoreTaskUnitsList[unit])
			{
			case EIgnoreTaskReason.NONE:
				unit.OrderAgent?.IssueOrder(OrderDefinitionRegistry.Stop, in OrderTarget.None, OrderIssueParams.Commanded(unit.AIAgent.MoveSpeed));
				break;
			case EIgnoreTaskReason.FOLLOWING_LEADER:
				unit.OrderAgent?.IssueResolvedOrder(Leader.transform.position, null, OrderIssueParams.Commanded(unit.AIAgent.MoveSpeed, queueOrder: false, 1f, broadcastEvent: false));
				if (GameEvents.OnTutorialTaskEvent != null && Leader.ControlledBy == Player.CurrentPlayer)
				{
					GameEvents.OnTutorialTaskEvent(ETutorialTaskEvent.GROUP_FOLLOW, null);
				}
				break;
			}
		}
		if (OnUnitTaskChange != null)
		{
			OnUnitTaskChange();
		}
	}

	public void StopIgnoringTask(Unit unit)
	{
		if ((bool)unit && IgnoreTaskUnitsList != null && IgnoreTaskUnitsList.Remove(unit) && OnUnitTaskChange != null)
		{
			OnUnitTaskChange();
		}
	}

	public void OnAttackOrder(Target target, AgentMoveSpeed moveSpeed)
	{
		if (target == null)
		{
			OnStopOrder(moveSpeed);
			return;
		}
		foreach (Unit unit in Units)
		{
			if (!IgnoresTask(unit))
			{
				unit.OrderAgent?.IssueOrder(OrderDefinitionRegistry.Attack, new OrderTarget(target.transform.position, target), OrderIssueParams.Ai(moveSpeed));
			}
		}
	}

	public void OnMoveOrder(Vector3 position, AgentMoveSpeed moveSpeed)
	{
		if (position == Vector3.zero)
		{
			OnStopOrder(moveSpeed);
			return;
		}
		foreach (Unit unit in Units)
		{
			if (!IgnoresTask(unit))
			{
				unit.OrderAgent?.IssueResolvedOrder(position, null, OrderIssueParams.Ai(moveSpeed));
			}
		}
	}

	public void OnStopOrder(AgentMoveSpeed moveSpeed, bool calledByAI = true)
	{
		foreach (Unit unit in Units)
		{
			if (!IgnoresTask(unit))
			{
				unit.OrderAgent?.IssueOrder(OrderDefinitionRegistry.Stop, in OrderTarget.None, OrderIssueParams.For(moveSpeed, calledByAI));
			}
		}
	}

	public void EvaluateCommands()
	{
		if (Task != null)
		{
			EvaluateMovingTask();
		}
		EvaluateLeaderFollow();
	}

	private void EvaluateLeaderFollow()
	{
		if (IgnoreTaskUnitsList == null)
		{
			return;
		}
		Vector3 position = Leader.transform.position;
		for (int i = 0; i < Units.Count; i++)
		{
			AIBaseAgent aIAgent = Units[i].AIAgent;
			if (!aIAgent)
			{
				continue;
			}
			AIPathfinding aIPathfinding = (aIAgent ? aIAgent.AgentPathfinding : null);
			if (!aIPathfinding || aIPathfinding.IsCalculatingPath)
			{
				continue;
			}
			float finalWaypointThreshold = aIPathfinding.FinalWaypointThreshold;
			finalWaypointThreshold = MathF.Max(Units[i].PhysicalBounds.size.x, Units[i].PhysicalBounds.size.z);
			bool hasPath = aIPathfinding.HasPath;
			if (!IgnoreTaskUnitsList.ContainsKey(Units[i]) || IgnoreTaskUnitsList[Units[i]] != EIgnoreTaskReason.FOLLOWING_LEADER)
			{
				continue;
			}
			if (hasPath)
			{
				float num = GameMath.Distance2D(aIPathfinding.AgentFinalInstructedDestination, position);
				float num2 = GameMath.Distance2D(aIAgent.transform.position, position);
				if (num > finalWaypointThreshold && num2 < num)
				{
					Units[i].OrderAgent?.IssueResolvedOrder(position, null, OrderIssueParams.Ai(aIAgent.MoveSpeed, broadcastEvent: false));
				}
			}
			else if (!hasPath && GameMath.Distance2D(aIAgent.transform.position, position) > finalWaypointThreshold)
			{
				Units[i].OrderAgent?.IssueResolvedOrder(position, null, OrderIssueParams.Ai(aIAgent.MoveSpeed, broadcastEvent: false));
			}
		}
	}

	private void EvaluateMovingTask()
	{
		Vector3 vector = Position;
		float num = 0f;
		if (Task.State == EAITaskState.Ongoing)
		{
			vector = Task.TaskPosition;
			if (Task.TaskIsMoving)
			{
				num = Mathf.Min(10f, GameMath.Distance(Position, vector) * 0.1f);
			}
		}
		foreach (Unit unit in Units)
		{
			AIBaseAgent aIAgent = unit.AIAgent;
			if (!aIAgent)
			{
				continue;
			}
			AIPathfinding aIPathfinding = (aIAgent ? aIAgent.AgentPathfinding : null);
			if (!aIPathfinding || aIPathfinding.IsCalculatingPath)
			{
				continue;
			}
			float num2 = aIPathfinding.FinalWaypointThreshold + num;
			if (unit.AIHoldingStation)
			{
				num2 = Mathf.Max(num2, unit.AIHoldStationRadius * 1.75f);
			}
			bool hasPath = aIPathfinding.HasPath;
			if (IgnoreTaskUnitsList != null && IgnoreTaskUnitsList.ContainsKey(unit))
			{
				if (IgnoreTaskUnitsList[unit] == EIgnoreTaskReason.NONE && aIAgent.IsFollowingPath)
				{
					unit.OrderAgent?.IssueOrder(OrderDefinitionRegistry.Stop, in OrderTarget.None, OrderIssueParams.Commanded(aIAgent.MoveSpeed));
				}
			}
			else if (hasPath && vector != Vector3.zero)
			{
				float num3 = GameMath.Distance2D(aIPathfinding.AgentFinalInstructedDestination, vector);
				float num4 = GameMath.Distance2D(aIAgent.transform.position, vector);
				if (num3 > num2 && num4 < num3)
				{
					if (AIManager.ShowDebug && FreeFlyCameraController.GetReferenceUnit() == unit)
					{
						Debug.LogError("DEBUG: Redirect group unit " + unit.ObjectInfo.DisplayName + " that freefly cam is stuck to");
					}
					if (!IgnoresTask(unit))
					{
						unit.OrderAgent?.IssueResolvedOrder(vector, null, OrderIssueParams.Ai(aIAgent.MoveSpeed));
					}
				}
			}
			else if (!hasPath && GameMath.Distance2D(aIAgent.transform.position, vector) > num2 && !IgnoresTask(unit))
			{
				unit.OrderAgent?.IssueResolvedOrder(vector, null, OrderIssueParams.Ai(aIAgent.MoveSpeed));
			}
		}
	}

	public void Think()
	{
		if (!Leader)
		{
			if (Units.Count == 0)
			{
				Debug.LogError("Group " + ID + " has no units! Removing...");
				if (!Game.GetIsClient() && !QueueReturnToPoolGroups.Contains(this))
				{
					QueueReturnToPoolGroups.Add(this);
				}
			}
			else
			{
				Debug.LogWarning("Group " + ID + " had no leader!");
				UpdateLeader();
			}
		}
		else if (Leader.PlayerControlled)
		{
			EvaluateCommands();
		}
		else if ((bool)Team && Team.GetHasPlayerCommander())
		{
			if (!Game.GetIsClient() && !QueueReturnToPoolGroups.Contains(this))
			{
				QueueReturnToPoolGroups.Add(this);
			}
		}
		else
		{
			if (IsForHarvest)
			{
				return;
			}
			Vector3 vector = Position;
			if ((bool)Leader && (bool)Leader.AIAgent && (bool)Leader.AIAgent.AgentPathfinding)
			{
				vector = Leader.AIAgent.AgentPathfinding.AgentFinalInstructedDestination;
			}
			float num = 0f;
			if (Task != null && Task.State == EAITaskState.Ongoing)
			{
				vector = Task.GetMoveToPositionForGroup(this);
				if (Task.TaskIsMoving)
				{
					num = Mathf.Min(10f, GameMath.Distance(Position, vector) * 0.1f);
				}
			}
			bool flag = false;
			Vector3 position = Position;
			s_FarStragglers.Clear();
			if (UnitCount > 1)
			{
				GameListCache.QueryUnits.Clear();
				foreach (Unit unit in Units)
				{
					if (unit == Leader || !unit.AIAgent || AIRepairSupport.GetIsRepairUnit(unit))
					{
						continue;
					}
					float num2 = GameMath.Distance(position, unit.transform.position);
					if (num2 < unit.PhysicalRadius * 10f)
					{
						continue;
					}
					if (num2 > 300f)
					{
						if (!unit.AIAgent.HasMoveOrder)
						{
							unit.OrderAgent?.IssueResolvedOrder(position, null, OrderIssueParams.Ai(unit.AIAgent.MoveSpeed));
						}
						s_FarStragglers.Add(unit);
					}
					else
					{
						GameListCache.QueryUnits.Add(unit);
					}
				}
				flag = GameListCache.QueryUnits.Count > UnitCount / 2;
			}
			bool flag2 = AIRepairSupport.EvaluateGroup(this);
			foreach (Unit unit2 in Units)
			{
				if ((flag2 && AIRepairSupport.GetIsRepairUnit(unit2)) || s_FarStragglers.Contains(unit2))
				{
					continue;
				}
				AIBaseAgent aIAgent = unit2.AIAgent;
				if (!aIAgent)
				{
					continue;
				}
				AIPathfinding aIPathfinding = (aIAgent ? aIAgent.AgentPathfinding : null);
				if (!aIPathfinding || aIPathfinding.IsCalculatingPath)
				{
					continue;
				}
				float num3 = aIPathfinding.FinalWaypointThreshold + num;
				bool hasPath = aIPathfinding.HasPath;
				if (flag)
				{
					if (GameListCache.QueryUnits.Contains(unit2))
					{
						if (!hasPath || GameMath.Distance2D(aIPathfinding.AgentFinalDestination, position) >= num3)
						{
							unit2.OrderAgent?.IssueResolvedOrder(position, null, OrderIssueParams.Ai(aIAgent.MoveSpeed));
						}
					}
					else if (hasPath)
					{
						unit2.OrderAgent?.IssueOrder(OrderDefinitionRegistry.Stop, in OrderTarget.None, OrderIssueParams.Ai(aIAgent.MoveSpeed));
					}
				}
				else
				{
					if (unit2.AIUsingOwnMovePoint)
					{
						continue;
					}
					if (hasPath)
					{
						float num4 = GameMath.Distance2D(aIPathfinding.AgentFinalInstructedDestination, vector);
						float num5 = GameMath.Distance2D(aIAgent.transform.position, vector);
						if (num4 > num3 && num5 < num4)
						{
							if (AIManager.ShowDebug && FreeFlyCameraController.GetReferenceUnit() == unit2)
							{
								Debug.LogError("DEBUG: Redirect group unit " + unit2.ObjectInfo.DisplayName + " that freefly cam is stuck to");
							}
							unit2.OrderAgent?.IssueResolvedOrder(vector, null, OrderIssueParams.Ai(aIAgent.MoveSpeed));
						}
					}
					else if (!hasPath && vector != Vector3.zero && GameMath.Distance2D(aIAgent.transform.position, vector) > num3)
					{
						unit2.OrderAgent?.IssueResolvedOrder(vector, null, OrderIssueParams.Ai(aIAgent.MoveSpeed));
					}
				}
			}
		}
	}

	private void Tick(float dt)
	{
		CurrentThinkDelay -= dt;
		if (!(CurrentThinkDelay > 0f))
		{
			CurrentThinkDelay++;
			Think();
		}
	}

	public static void TickAll(float dt)
	{
		if (Game.GetIsClient())
		{
			return;
		}
		foreach (KeyValuePair<uint, AIGroup> activeGroup in ActiveGroups)
		{
			activeGroup.Value.Tick(dt);
		}
		for (int num = QueueReturnToPoolGroups.Count - 1; num >= 0; num--)
		{
			QueueReturnToPoolGroups[num].ReturnToPool();
		}
		QueueReturnToPoolGroups.Clear();
	}

	public int GetNumUnitsOfType(ObjectInfo unitInfo)
	{
		if (UnitTypeCounts.TryGetValue(unitInfo, out var value))
		{
			return value;
		}
		return 0;
	}

	public bool GetSuitedForTaskType(EAITaskType taskType)
	{
		return SuitedToTaskTypes.Contains(taskType);
	}

	public int GetRatingForRequest(AIRequest unitRequest)
	{
		if (unitRequest.Type != EAIRequestType.Unit)
		{
			return 0;
		}
		if (!GetSuitedForTaskType(unitRequest.UnitTask))
		{
			return 0;
		}
		return Mathf.Clamp(((Task != null && Task.State == EAITaskState.Ongoing) ? (Task.Priority - unitRequest.Priority) : 100) - Mathf.RoundToInt(GameMath.Distance(unitRequest.GetRequestPosition(), Position) * 0.1f), 0, 100);
	}

	public bool GetUnitNeededForPreset(Unit unit)
	{
		if (Preset == null)
		{
			return false;
		}
		if (PresetNeedsNum == 0)
		{
			return false;
		}
		if (!unit.ObjectInfo)
		{
			return false;
		}
		if (PresetNeeds.TryGetValue(unit.ObjectInfo.CompressedUnitTypeAndWeight, out var value))
		{
			return true;
		}
		if (PresetNeeds.TryGetValue(unit.ObjectInfo.CompressedUnitTypeAndAnyWeight, out value))
		{
			return true;
		}
		return false;
	}

	public void OnPlayerSelectedUnit(Player player, Unit unit)
	{
		UpdateLeader();
	}

	public void OnPlayerUnselectedUnit(Player player, Unit unit)
	{
		UpdateLeader();
	}

	public bool AddUnit(Unit unit)
	{
		if (Units.Contains(unit))
		{
			return false;
		}
		if (unit == null || unit.IsDestroyed)
		{
			return false;
		}
		AIGroup aIGroup = unit.AIGroup;
		Units.Add(unit);
		if (Game.GetIsServer())
		{
			GroupChanges.Add((this, EAIGroupChange.UnitAdded, unit.NetworkComponent.NetID));
		}
		if ((bool)unit.ObjectInfo)
		{
			int value = 1;
			if (UnitTypeCounts.TryGetValue(unit.ObjectInfo, out value))
			{
				value++;
				UnitTypeCounts[unit.ObjectInfo] = value;
			}
			else
			{
				UnitTypeCounts.Add(unit.ObjectInfo, 1);
			}
		}
		UpdateLeader();
		UpdateRatingsAndSuitedForTaskTypes();
		UpdatePresetNeeds();
		if (unit.AIGroup != this)
		{
			unit.AIGroup = this;
		}
		bool flag = (bool)Unit.CurrentUnit && Unit.CurrentUnit.AIGroup == this;
		if ((GameEvents.OnLocalPlayerGroupChanged != null) & flag)
		{
			GameEvents.OnLocalPlayerGroupChanged(unit, aIGroup, this);
		}
		if ((GameEvents.OnLocalPlayerGroupUnitAdded != null) & flag)
		{
			GameEvents.OnLocalPlayerGroupUnitAdded(this, unit);
		}
		if ((bool)Leader && Leader.PlayerControlled)
		{
			IgnoreTask(unit, EIgnoreTaskReason.FOLLOWING_LEADER);
		}
		return true;
	}

	public bool RemoveUnit(Unit unit)
	{
		if (!Units.Remove(unit))
		{
			return false;
		}
		if (!unit || unit.IsDestroyed)
		{
			UnitsLostToDestruction++;
		}
		else
		{
			UnitsTransferredAway++;
		}
		if (Game.GetIsServer())
		{
			GroupChanges.Add((this, EAIGroupChange.UnitRemoved, unit.NetworkComponent.NetID));
		}
		AIGroup aIGroup = unit.AIGroup;
		if (IgnoreTaskUnitsList != null)
		{
			IgnoreTaskUnitsList.Remove(unit);
		}
		if ((bool)unit.ObjectInfo && UnitTypeCounts.TryGetValue(unit.ObjectInfo, out var value))
		{
			value--;
			if (value <= 0)
			{
				UnitTypeCounts.Remove(unit.ObjectInfo);
			}
			else
			{
				UnitTypeCounts[unit.ObjectInfo] = value;
			}
		}
		bool flag = (bool)Unit.CurrentUnit && Unit.CurrentUnit.AIGroup == this;
		if (unit.AIGroup == this)
		{
			unit.AIGroup = null;
		}
		if ((GameEvents.OnLocalPlayerGroupChanged != null) & flag)
		{
			GameEvents.OnLocalPlayerGroupChanged(unit, aIGroup, null);
		}
		if ((GameEvents.OnLocalPlayerGroupUnitRemoved != null) & flag)
		{
			GameEvents.OnLocalPlayerGroupUnitRemoved(this, unit);
		}
		if (UnitCount == 0)
		{
			if (!Game.GetIsClient())
			{
				ReturnToPool();
			}
		}
		else
		{
			UpdateLeader();
			UpdateRatingsAndSuitedForTaskTypes();
			UpdatePresetNeeds();
		}
		return true;
	}

	private void UpdateRatingsAndSuitedForTaskTypes()
	{
		SuitedToTaskTypes.Clear();
		SuitedToTaskTypes.Add(EAITaskType.Protect);
		SuitedToTaskTypes.Add(EAITaskType.Guard);
		SuitedToTaskTypes.Add(EAITaskType.SeekAndDestroy);
		SuitedToTaskTypes.Add(EAITaskType.Attack);
		SuitedToTaskTypes.Add(EAITaskType.Explore);
		SuitedToTaskTypes.Add(EAITaskType.Prospect);
		SuitedToTaskTypes.Add(EAITaskType.Harvest);
		int num = 0;
		TotalDefenseRating = 0;
		TotalOffenseRating = 0;
		MaxDefenseRating = 0;
		MinDefenseRating = 0;
		AvgDefenseRating = 0;
		MaxOffenseRating = 0;
		MinOffenseRating = 0;
		AvgOffenseRating = 0;
		MaxSpeedRating = 0;
		MinSpeedRating = 0;
		AvgSpeedRating = 0;
		MaxRangeRating = 0;
		MinRangeRating = 0;
		AvgRangeRating = 0;
		foreach (Unit unit in Units)
		{
			if (!unit.ObjectInfo)
			{
				continue;
			}
			TotalDefenseRating += unit.ObjectInfo.DefenseRating;
			TotalOffenseRating += unit.ObjectInfo.OffenseRating;
			MaxDefenseRating = Mathf.Max(unit.ObjectInfo.DefenseRating, MaxDefenseRating);
			MinDefenseRating = ((MinDefenseRating == 0) ? unit.ObjectInfo.DefenseRating : Mathf.Min(unit.ObjectInfo.DefenseRating, MinDefenseRating));
			AvgDefenseRating += unit.ObjectInfo.DefenseRating;
			MaxOffenseRating = Mathf.Max(unit.ObjectInfo.OffenseRating, MaxOffenseRating);
			MinOffenseRating = ((MinOffenseRating == 0) ? unit.ObjectInfo.OffenseRating : Mathf.Min(unit.ObjectInfo.OffenseRating, MinOffenseRating));
			AvgOffenseRating += unit.ObjectInfo.OffenseRating;
			MaxSpeedRating = Mathf.Max(unit.ObjectInfo.SpeedRating, MaxSpeedRating);
			MinSpeedRating = ((MinSpeedRating == 0) ? unit.ObjectInfo.SpeedRating : Mathf.Min(unit.ObjectInfo.SpeedRating, MinSpeedRating));
			AvgSpeedRating += unit.ObjectInfo.SpeedRating;
			MaxRangeRating = Mathf.Max(unit.ObjectInfo.RangeRating, MaxRangeRating);
			MinRangeRating = ((MinRangeRating == 0) ? unit.ObjectInfo.RangeRating : Mathf.Min(unit.ObjectInfo.RangeRating, MinRangeRating));
			AvgRangeRating += unit.ObjectInfo.RangeRating;
			if (AIRepairSupport.GetIsRepairUnit(unit) || unit.ObjectInfo.IsTransport)
			{
				continue;
			}
			num++;
			for (int num2 = SuitedToTaskTypes.Count - 1; num2 >= 0; num2--)
			{
				if (!unit.ObjectInfo.SuitedToTaskTypes.Contains(SuitedToTaskTypes[num2]))
				{
					SuitedToTaskTypes.RemoveAt(num2);
				}
			}
		}
		if (num <= 0)
		{
			foreach (Unit unit2 in Units)
			{
				if (!unit2 || !unit2.ObjectInfo)
				{
					continue;
				}
				for (int num3 = SuitedToTaskTypes.Count - 1; num3 >= 0; num3--)
				{
					if (!unit2.ObjectInfo.SuitedToTaskTypes.Contains(SuitedToTaskTypes[num3]))
					{
						SuitedToTaskTypes.RemoveAt(num3);
					}
				}
			}
		}
		IsForExplore = GetSuitedForTaskType(EAITaskType.Explore) || GetSuitedForTaskType(EAITaskType.Prospect);
		IsForDefense = GetSuitedForTaskType(EAITaskType.Protect) || GetSuitedForTaskType(EAITaskType.Guard);
		IsForOffense = GetSuitedForTaskType(EAITaskType.Attack) || GetSuitedForTaskType(EAITaskType.SeekAndDestroy);
		IsForHarvest = GetSuitedForTaskType(EAITaskType.Harvest);
		if (!IsForHarvest)
		{
			SuitedToTaskTypes.Add(EAITaskType.Sentry);
		}
		if (Units.Count > 1)
		{
			float num4 = 1f / (float)Units.Count;
			AvgDefenseRating = Mathf.RoundToInt((float)AvgDefenseRating * num4);
			AvgOffenseRating = Mathf.RoundToInt((float)AvgOffenseRating * num4);
			AvgSpeedRating = Mathf.RoundToInt((float)AvgSpeedRating * num4);
			AvgRangeRating = Mathf.RoundToInt((float)AvgRangeRating * num4);
		}
	}

	private void UpdateLeader()
	{
		if (Game.GetIsClient() || ((bool)Leader && Leader.PlayerControlled && !Leader.IsDestroyed && Units.Contains(Leader)))
		{
			return;
		}
		Unit leader = Leader;
		Leader = null;
		foreach (Unit unit in Units)
		{
			if (unit.PlayerControlled)
			{
				Leader = unit;
				break;
			}
			if (!Leader || unit.NormalSpeed < Leader.NormalSpeed)
			{
				Leader = unit;
			}
		}
		if (IgnoreTaskUnitsList != null && (!Leader || !Leader.PlayerControlled))
		{
			IgnoreTaskUnitsList.Clear();
		}
		if (Game.GetIsServer() && leader != Leader)
		{
			GroupChanges.Add((this, EAIGroupChange.LeaderChanged, (Leader != null) ? Leader.NetworkComponent.NetID : 0u));
		}
	}

	public void SetLeaderFromNetwork(Unit leader)
	{
		if (Game.GetIsClient())
		{
			Leader = leader;
			if (IgnoreTaskUnitsList != null && (!Leader || !Leader.PlayerControlled))
			{
				IgnoreTaskUnitsList.Clear();
			}
		}
	}

	private void UpdatePresetNeeds()
	{
		PresetNeeds.Clear();
		PresetNeedsNum = 0;
		if (Preset == null)
		{
			return;
		}
		PresetNeedsNum = Preset.CopyPresetNeeds(PresetNeeds);
		foreach (Unit unit in Units)
		{
			if (!unit.ObjectInfo)
			{
				continue;
			}
			if (PresetNeeds.TryGetValue(unit.ObjectInfo.CompressedUnitTypeAndWeight, out var value))
			{
				PresetNeedsNum--;
				value--;
				if (value <= 0)
				{
					PresetNeeds.Remove(unit.ObjectInfo.CompressedUnitTypeAndWeight);
				}
				else
				{
					PresetNeeds[unit.ObjectInfo.CompressedUnitTypeAndWeight] = value;
				}
			}
			else if (PresetNeeds.TryGetValue(unit.ObjectInfo.CompressedUnitTypeAndAnyWeight, out value))
			{
				PresetNeedsNum--;
				value--;
				if (value <= 0)
				{
					PresetNeeds.Remove(unit.ObjectInfo.CompressedUnitTypeAndAnyWeight);
				}
				else
				{
					PresetNeeds[unit.ObjectInfo.CompressedUnitTypeAndAnyWeight] = value;
				}
			}
		}
	}

	public bool HasPlayer()
	{
		foreach (Unit unit in Units)
		{
			if ((bool)unit.ControlledBy)
			{
				return true;
			}
		}
		return false;
	}

	public static AIGroup TryGetGroupWithID(uint id)
	{
		return ActiveGroups.GetValueOrDefault(id);
	}

	public static AIGroup CreateNewGroup(Team team, AIGroupPreset preset)
	{
		uint num;
		for (num = 0u; ActiveGroups.ContainsKey(num) && num < uint.MaxValue; num++)
		{
		}
		if (num == uint.MaxValue)
		{
			Debug.LogError("Maximum number of AI groups reached!");
			return null;
		}
		AIGroup orCreateGroupWithID = GetOrCreateGroupWithID(num, team, preset);
		if (Game.GetIsServer())
		{
			RemovedGroupIDs.Remove(num);
			CreatedGroups.Add(orCreateGroupWithID);
		}
		return orCreateGroupWithID;
	}

	public static AIGroup GetOrCreateGroupWithID(uint id, Team team, AIGroupPreset preset)
	{
		if (id == uint.MaxValue)
		{
			Debug.LogError($"Invalid AI Group ID {uint.MaxValue} requested!");
			return null;
		}
		if (ActiveGroups.TryGetValue(id, out var value))
		{
			return value;
		}
		AIGroup aIGroup = ((!PooledGroups.Remove(id, out var value2)) ? new AIGroup() : value2);
		if (aIGroup == null)
		{
			Debug.LogError($"Invalid result for ID {id}");
			return null;
		}
		if (ActiveGroups.Count == 0)
		{
			RegisterEvents();
		}
		ActiveGroups[id] = aIGroup;
		aIGroup.CurrentThinkDelay = UnityEngine.Random.Range(0f, 1f);
		aIGroup.ID = id;
		aIGroup.Team = team;
		aIGroup.Preset = preset;
		return aIGroup;
	}

	private static void OnUnitChangedTeam(Unit unit, Team prevTeam, Team newTeam)
	{
		if (unit.AIGroup != null && unit.AIGroup.Team != newTeam)
		{
			unit.AIGroup.RemoveUnit(unit);
		}
	}

	private static void OnUnitDestroyed(Unit unit, GameObject instigator)
	{
		if (unit.AIGroup != null)
		{
			unit.AIGroup.RemoveUnit(unit);
		}
	}

	private static void OnUnitDeleted(Unit unit)
	{
		if (unit.AIGroup != null)
		{
			unit.AIGroup.RemoveUnit(unit);
		}
	}

	public static void RegisterEvents()
	{
		GameEvents.OnUnitChangedTeam = (Action<Unit, Team, Team>)Delegate.Combine(GameEvents.OnUnitChangedTeam, new Action<Unit, Team, Team>(OnUnitChangedTeam));
		GameEvents.OnUnitDestroyed = (Action<Unit, GameObject>)Delegate.Combine(GameEvents.OnUnitDestroyed, new Action<Unit, GameObject>(OnUnitDestroyed));
		GameEvents.OnUnitDeleted = (Action<Unit>)Delegate.Combine(GameEvents.OnUnitDeleted, new Action<Unit>(OnUnitDeleted));
	}

	public static void UnregisterEvents()
	{
		GameEvents.OnUnitChangedTeam = (Action<Unit, Team, Team>)Delegate.Remove(GameEvents.OnUnitChangedTeam, new Action<Unit, Team, Team>(OnUnitChangedTeam));
		GameEvents.OnUnitDestroyed = (Action<Unit, GameObject>)Delegate.Remove(GameEvents.OnUnitDestroyed, new Action<Unit, GameObject>(OnUnitDestroyed));
		GameEvents.OnUnitDeleted = (Action<Unit>)Delegate.Remove(GameEvents.OnUnitDeleted, new Action<Unit>(OnUnitDeleted));
	}

	public static void ClearAll()
	{
		while (ActiveGroups.Count > 0)
		{
			AIGroup value = ActiveGroups.First().Value;
			ActiveGroups.Remove(value.ID);
			value.ReturnToPool();
		}
		PooledGroups.Clear();
	}

	public static void ReturnToPool(Team team)
	{
		foreach (AIGroup item in ActiveGroups.Values.ToList())
		{
			if (!(item.Team != team) && (item.Task == null || item.Task.Type != EAITaskType.Sentry))
			{
				item.ReturnToPool();
			}
		}
	}

	private void Clear()
	{
		UnitTypeCounts.Clear();
		for (int num = Units.Count - 1; num >= 0; num--)
		{
			Unit unit = Units[num];
			Units.RemoveAt(num);
			if (unit.AIGroup == this)
			{
				unit.AIGroup = null;
			}
		}
		Leader = null;
		Team = null;
		Task = null;
		UnitsLostToDestruction = 0;
		UnitsTransferredAway = 0;
		Preset = null;
		PresetNeedsNum = 0;
		PresetNeeds.Clear();
		Units.Clear();
		IgnoreTaskUnitsList?.Clear();
		OnUnitTaskChange = null;
		SuitedToTaskTypes.Clear();
		IsForExplore = false;
		IsForDefense = false;
		IsForOffense = false;
		IsForHarvest = false;
	}

	private void ReturnToPool()
	{
		Clear();
		if (Game.GetIsServer() && !CreatedGroups.Remove(this))
		{
			RemovedGroupIDs.Add(ID);
		}
		ActiveGroups.Remove(ID);
		PooledGroups[ID] = this;
		if (ActiveGroups.Count == 0)
		{
			UnregisterEvents();
		}
	}

	internal AIGroup()
	{
	}

	public static GameByteStreamWriter CreatePacket()
	{
		if (!Game.GetIsServer())
		{
			return null;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "AIGroup::SendNetInit");
		if (gameByteStreamWriter == null)
		{
			return null;
		}
		gameByteStreamWriter.WriteByte(37);
		return gameByteStreamWriter;
	}

	public static bool OnSendPacket(GameByteStreamWriter packetWriter, Player player)
	{
		if (!Game.GetIsServer())
		{
			return false;
		}
		if (player == null)
		{
			return NetworkLayer.SendPacket(packetWriter, ENetworkPacketSend.Reliable);
		}
		return NetworkLayer.SendPacketToPlayer(packetWriter, ENetworkPacketSend.Reliable, player);
	}

	public static void OnReceivePacket(GameByteStreamReader packetReader)
	{
		if (Game.GetIsClient())
		{
			switch ((EAIGroupPacketType)packetReader.ReadByte())
			{
			case EAIGroupPacketType.Init:
				ReadNetInitAll(packetReader);
				break;
			case EAIGroupPacketType.Update:
				ReadNetUpdateAll(packetReader);
				break;
			}
		}
	}

	public static bool SendNetInitAll(Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = CreatePacket();
		if (gameByteStreamWriter == null)
		{
			return false;
		}
		gameByteStreamWriter.WriteByte(0);
		gameByteStreamWriter.WritePackedUInt32((uint)ActiveGroups.Count);
		foreach (AIGroup value in ActiveGroups.Values)
		{
			value.WriteData(gameByteStreamWriter);
		}
		return OnSendPacket(gameByteStreamWriter, player);
	}

	public static void ReadNetInitAll(GameByteStreamReader packetReader)
	{
		ClearAll();
		int num = (int)packetReader.ReadPackedUInt32();
		for (int i = 0; i < num; i++)
		{
			AIGroup aIGroup = ReadData(packetReader);
			if (aIGroup != null)
			{
				ActiveGroups[aIGroup.ID] = aIGroup;
			}
		}
	}

	public static void SendNetUpdateAll(Player player)
	{
		if (!Game.GetIsServer() || (CreatedGroups.Count == 0 && GroupChanges.Count == 0 && RemovedGroupIDs.Count == 0 && IgnoreTaskChanges.Count == 0))
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = CreatePacket();
		if (gameByteStreamWriter == null)
		{
			return;
		}
		gameByteStreamWriter.WriteByte(1);
		gameByteStreamWriter.WritePackedUInt32((uint)CreatedGroups.Count);
		foreach (AIGroup createdGroup in CreatedGroups)
		{
			createdGroup.WriteData(gameByteStreamWriter);
		}
		gameByteStreamWriter.WritePackedUInt32((uint)GroupChanges.Count);
		foreach (var groupChange in GroupChanges)
		{
			gameByteStreamWriter.WritePackedUInt32(groupChange.group.ID);
			gameByteStreamWriter.WriteByte((byte)groupChange.changeType);
			gameByteStreamWriter.WritePackedUInt32(groupChange.unitID);
		}
		gameByteStreamWriter.WritePackedUInt32((uint)IgnoreTaskChanges.Count);
		foreach (var ignoreTaskChange in IgnoreTaskChanges)
		{
			gameByteStreamWriter.WritePackedUInt32(ignoreTaskChange.group.ID);
			gameByteStreamWriter.WritePackedUInt32(ignoreTaskChange.unitID);
			gameByteStreamWriter.WriteByte((byte)ignoreTaskChange.reason);
		}
		gameByteStreamWriter.WritePackedUInt32((uint)RemovedGroupIDs.Count);
		foreach (uint removedGroupID in RemovedGroupIDs)
		{
			gameByteStreamWriter.WritePackedUInt32(removedGroupID);
		}
		CreatedGroups.Clear();
		GroupChanges.Clear();
		IgnoreTaskChanges.Clear();
		RemovedGroupIDs.Clear();
		OnSendPacket(gameByteStreamWriter, player);
	}

	public static void ReadNetUpdateAll(GameByteStreamReader packetReader)
	{
		int num = (int)packetReader.ReadPackedUInt32();
		for (int i = 0; i < num; i++)
		{
			AIGroup aIGroup = ReadData(packetReader);
			if (aIGroup != null)
			{
				ActiveGroups[aIGroup.ID] = aIGroup;
			}
		}
		int num2 = (int)packetReader.ReadPackedUInt32();
		for (int j = 0; j < num2; j++)
		{
			uint num3 = packetReader.ReadPackedUInt32();
			EAIGroupChange eAIGroupChange = (EAIGroupChange)packetReader.ReadByte();
			uint num4 = packetReader.ReadPackedUInt32();
			Unit unit = NetworkComponent.GetNetObject(num4)?.Owner as Unit;
			AIGroup aIGroup2 = TryGetGroupWithID(num3);
			if (aIGroup2 == null)
			{
				Debug.LogError($"AIGroup::ReadNetUpdateAll: Failed to find group with ID {num3} for change!");
				continue;
			}
			switch (eAIGroupChange)
			{
			case EAIGroupChange.UnitAdded:
				aIGroup2.AddUnit(unit);
				break;
			case EAIGroupChange.UnitRemoved:
				if (!(unit == null))
				{
					aIGroup2.RemoveUnit(unit);
				}
				break;
			case EAIGroupChange.LeaderChanged:
				if (num4 == 0)
				{
					aIGroup2.SetLeaderFromNetwork(null);
				}
				else if (unit != null)
				{
					aIGroup2.SetLeaderFromNetwork(unit);
				}
				else
				{
					Debug.LogError($"AIGroup::ReadNetUpdateAll: leader {num4} for group {num3} has not replicated yet, keeping the current leader!");
				}
				break;
			default:
				Debug.LogError($"AIGroup::ReadNetUpdateAll: Unknown change type {(byte)eAIGroupChange} for group {num3}!");
				break;
			}
		}
		int num5 = (int)packetReader.ReadPackedUInt32();
		for (int k = 0; k < num5; k++)
		{
			uint num6 = packetReader.ReadPackedUInt32();
			uint num7 = packetReader.ReadPackedUInt32();
			EIgnoreTaskReason reason = (EIgnoreTaskReason)packetReader.ReadByte();
			AIGroup aIGroup3 = TryGetGroupWithID(num6);
			if (aIGroup3 == null)
			{
				Debug.LogError($"AIGroup::ReadNetUpdateAll: Failed to find group with ID {num6} for ignore task change!");
				continue;
			}
			Unit unit2 = NetworkComponent.GetNetObject(num7)?.Owner as Unit;
			if (unit2 == null)
			{
				Debug.LogError($"AIGroup::ReadNetUpdateAll: Failed to find unit with ID {num7} for ignore task change!");
			}
			else
			{
				aIGroup3.IgnoreTask(unit2, reason);
			}
		}
		int num8 = (int)packetReader.ReadPackedUInt32();
		for (int l = 0; l < num8; l++)
		{
			uint num9 = packetReader.ReadPackedUInt32();
			AIGroup aIGroup4 = TryGetGroupWithID(num9);
			if (aIGroup4 == null)
			{
				Debug.LogError($"AIGroup::ReadNetUpdateAll: Failed to find group with ID {num9} for removal!");
			}
			else
			{
				aIGroup4.ReturnToPool();
			}
		}
	}

	private void WriteData(GameByteStreamWriter packetWriter)
	{
		packetWriter.WritePackedUInt32(ID);
		packetWriter.WriteTeam(m_Team);
		packetWriter.WriteBaseGameObject(Leader);
		int num = 0;
		for (int i = 0; i < Units.Count; i++)
		{
			if (Units[i] != Leader)
			{
				num++;
			}
		}
		packetWriter.WritePackedUInt32((uint)num);
		for (int j = 0; j < Units.Count; j++)
		{
			Unit unit = Units[j];
			if (!(unit == Leader))
			{
				packetWriter.WriteBaseGameObject(unit);
			}
		}
		packetWriter.WritePackedUInt32((uint)(IgnoreTaskUnitsList?.Count ?? 0));
		if (IgnoreTaskUnitsList == null)
		{
			return;
		}
		foreach (KeyValuePair<Unit, EIgnoreTaskReason> ignoreTaskUnits in IgnoreTaskUnitsList)
		{
			packetWriter.WriteBaseGameObject(ignoreTaskUnits.Key);
			packetWriter.WriteByte((byte)ignoreTaskUnits.Value);
		}
	}

	private static AIGroup ReadData(GameByteStreamReader packetReader)
	{
		_unitCache.Clear();
		_ignoreTaskCache.Clear();
		uint num = packetReader.ReadPackedUInt32();
		Team team = packetReader.ReadTeam();
		Unit unit = packetReader.ReadBaseGameObject() as Unit;
		int num2 = (int)packetReader.ReadPackedUInt32();
		for (int i = 0; i < num2; i++)
		{
			Unit unit2 = packetReader.ReadBaseGameObject() as Unit;
			if (unit2 == null)
			{
				Debug.LogError("AIGroup::ReadData: Failed to read unit!");
			}
			else
			{
				_unitCache.Add(unit2);
			}
		}
		int num3 = (int)packetReader.ReadPackedUInt32();
		for (int j = 0; j < num3; j++)
		{
			Unit unit3 = packetReader.ReadBaseGameObject() as Unit;
			if (unit3 == null)
			{
				Debug.LogError($"AIGroup::ReadData: Failed to read ignore task unit {j + 1}/{num3}!");
			}
			EIgnoreTaskReason item = (EIgnoreTaskReason)packetReader.ReadByte();
			if (!(unit3 == null))
			{
				_ignoreTaskCache.Add((unit3, item));
			}
		}
		AIGroup orCreateGroupWithID = GetOrCreateGroupWithID(num, team, null);
		if (orCreateGroupWithID == null)
		{
			Debug.LogError($"AIGroup::ReadData: Failed to get group with ID {num}");
			return null;
		}
		if ((bool)unit)
		{
			orCreateGroupWithID.AddUnit(unit);
		}
		foreach (Unit item2 in _unitCache)
		{
			orCreateGroupWithID.AddUnit(item2);
		}
		orCreateGroupWithID.SetLeaderFromNetwork(unit);
		foreach (var item3 in _ignoreTaskCache)
		{
			orCreateGroupWithID.IgnoreTask(item3.unit, item3.reason);
		}
		return orCreateGroupWithID;
	}
}
