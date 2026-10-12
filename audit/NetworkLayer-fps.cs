using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DebugTools;
using Silica.AI;
using Silica.UI;
using Silica.Utilities;
using Steamworks;
using UnityEngine;

public static class NetworkLayer
{
	public const int MAX_PACKET_SIZE = 2400;

	public const int DROP_PACKET_SIZE = 4800;

	public const int MAX_QUEUEBUFFER_SIZE = 4096;

	public const int MAX_MESSAGES = 50;

	public const float BUFFER_FULL_NO_SEND_UNRELIABLE_TIME = 3f;

	public const float BUFFER_NO_REPORT_EXCEEDED_TIME = 5f;

	private static byte[] s_PacketBuffer = new byte[2400];

	private static IntPtr[] s_MessageBuffer = new IntPtr[50];

	private static SteamNetworkingConfigValue_t[] s_DefaultConnectionOptions = null;

	private static float NetNoUnreliableTime = 0f;

	private static float NetNoReportBufferExceededTime = 0f;

	private static float NetBitsTime = 0f;

	private static uint NetBitsSent = 0u;

	private static uint NetBitsReceived = 0u;

	private static IntPtr[] s_MessagesToSend = new IntPtr[1] { IntPtr.Zero };

	private static long[] s_MessageSendResults = new long[1];

	public static SteamNetworkingConfigValue_t[] DefaultConnectionOptions
	{
		get
		{
			if (s_DefaultConnectionOptions == null)
			{
				s_DefaultConnectionOptions = new SteamNetworkingConfigValue_t[3];
				SteamNetworkingConfigValue_t steamNetworkingConfigValue_t = new SteamNetworkingConfigValue_t
				{
					m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
					m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
					m_val = 
					{
						m_int32 = 4194304
					}
				};
				s_DefaultConnectionOptions[0] = steamNetworkingConfigValue_t;
				steamNetworkingConfigValue_t = new SteamNetworkingConfigValue_t
				{
					m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
					m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
					m_val = 
					{
						m_int32 = 0
					}
				};
				s_DefaultConnectionOptions[1] = steamNetworkingConfigValue_t;
				steamNetworkingConfigValue_t = new SteamNetworkingConfigValue_t
				{
					m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime,
					m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
					m_val = 
					{
						m_int32 = 15000
					}
				};
				s_DefaultConnectionOptions[2] = steamNetworkingConfigValue_t;
			}
			return s_DefaultConnectionOptions;
		}
	}

	public static float NetBitsAvgUpload { get; private set; } = 0f;

	public static float NetBitsAvgDownload { get; private set; } = 0f;

	public static bool IsHeavyLoad => NetNoUnreliableTime <= 0f;

	public static void Update()
	{
		float unscaledDeltaTime = Time.unscaledDeltaTime;
		if (NetNoReportBufferExceededTime > 0f)
		{
			NetNoReportBufferExceededTime -= unscaledDeltaTime;
			if (NetNoReportBufferExceededTime < 0f)
			{
				NetNoReportBufferExceededTime = 0f;
			}
		}
		NetBitsTime += unscaledDeltaTime;
		if (NetBitsTime > 1f)
		{
			NetBitsAvgUpload = (float)NetBitsSent / NetBitsTime;
			NetBitsAvgDownload = (float)NetBitsReceived / NetBitsTime;
			NetBitsTime = 0f;
			NetBitsSent = 0u;
			NetBitsReceived = 0u;
		}
		if (!(NetNoUnreliableTime <= 0f))
		{
			NetNoUnreliableTime -= unscaledDeltaTime;
		}
	}

	public static void Clear()
	{
		NetNoUnreliableTime = 0f;
		NetNoReportBufferExceededTime = 0f;
		NetBitsTime = 0f;
		NetBitsSent = 0u;
		NetBitsReceived = 0u;
	}

	public static GameByteStreamWriter CreateObjectPacketWriter(NetworkComponent callerObject, ENetworkPacketType packetType)
	{
		if (callerObject.NetID == 0)
		{
			return null;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(callerObject.NetID, "NetworkLayer::CreateObjectPacketWriter");
		gameByteStreamWriter.WriteByte((byte)packetType);
		gameByteStreamWriter.WritePackedUInt32(callerObject.NetID);
		return gameByteStreamWriter;
	}

	public static GameByteStreamWriter CreateTeamPacketWriter(Team callerTeam, ENetworkPacketType packetType)
	{
		int index = callerTeam.Index;
		if (index == -1)
		{
			return null;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::CreateTeamPacketWriter");
		gameByteStreamWriter.WriteByte((byte)packetType);
		gameByteStreamWriter.WriteByte((byte)index);
		return gameByteStreamWriter;
	}

	public static void ConfigureDefaultLanes(HSteamNetConnection connection, bool server)
	{
		EResult eResult = ((!server) ? SteamNetworkingSockets.ConfigureConnectionLanes(connection, 2, new int[2] { 1, 1 }, new ushort[2] { 3, 1 }) : SteamGameServerNetworkingSockets.ConfigureConnectionLanes(connection, 2, new int[2] { 1, 1 }, new ushort[2] { 3, 1 }));
		if (eResult != EResult.k_EResultOK)
		{
			HSteamNetConnection hSteamNetConnection = connection;
			Debug.LogError("NetworkLayer::ConfigureDefaultLanes: Failed to configure default lanes for connection " + hSteamNetConnection.ToString() + ", reason: " + eResult);
			NetNoUnreliableTime = 3f;
		}
	}

	public static void RetrieveNetworkData()
	{
		bool isServer = Game.GetIsServer();
		bool flag = true;
		while (flag)
		{
			flag = false;
			int num = 0;
			num = ((!isServer) ? SteamNetworkingSockets.ReceiveMessagesOnConnection(NetworkGameClient.GetServerNetConnection(), s_MessageBuffer, 50) : SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(NetworkGameServer.PollGroup, s_MessageBuffer, 50));
			if (num == 50)
			{
				flag = true;
			}
			for (int i = 0; i < num; i++)
			{
				SteamNetworkingMessage_t steamNetworkingMessage_t = SteamNetworkingMessage_t.FromIntPtr(s_MessageBuffer[i]);
				uint num2 = (uint)steamNetworkingMessage_t.m_cbSize;
				Marshal.Copy(steamNetworkingMessage_t.m_pData, s_PacketBuffer, 0, (int)num2);
				if (num2 > 4800)
				{
					Debug.LogError("Received a very large packet (" + num2 + "), may be malicious, dropping...");
					continue;
				}
				if (num2 > 2400)
				{
					Debug.LogError("Received a packet too large to read (" + num2 + "), size limit is " + 2400 + "! Truncating packet...");
					num2 = 2400u;
				}
				NetBitsReceived += num2 * 8;
				uint num3 = 0u;
				NetworkID networkID = new NetworkID(steamNetworkingMessage_t.m_identityPeer.GetSteamID64());
				if ((!isServer || NetworkGameServer.GetClientIDValid(networkID)) && (isServer || NetworkGameClient.GetServerIDValid(networkID)))
				{
					num3 = (uint)steamNetworkingMessage_t.m_cbSize;
					if (num3 == 0)
					{
						Debug.LogError("Received message from " + networkID.m_ID + " that is 0 bytes in size!");
					}
					else
					{
						ProcessMessage(GameByteStreamReader.GetGameByteStreamReader(s_PacketBuffer, (int)num3, 0u, "NetworkLayer::RetrieveNetworkData"), num3, networkID, isServer);
					}
					SteamNetworkingMessage_t.Release(s_MessageBuffer[i]);
				}
			}
		}
	}

	private static void ProcessMessage(GameByteStreamReader packetReader, uint packetLength, NetworkID remoteID, bool isServer)
	{
		ENetworkPacketType eNetworkPacketType = (ENetworkPacketType)packetReader.ReadByte();
		Player player = Player.FindPlayer(remoteID);
		if (isServer)
		{
			if ((bool)player)
			{
				player.NetLastHeartbeatTime = 0f;
			}
		}
		else
		{
			Player.CurrentPlayer.NetLastHeartbeatTime = 0f;
		}
		switch (eNetworkPacketType)
		{
		case ENetworkPacketType.Heartbeat:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:Heartbeat");
			if (isServer)
			{
				Player player5 = Player.FindPlayer(remoteID);
				if ((bool)player5)
				{
					player5.NetLastHeartbeatTime = 0f;
				}
			}
			else
			{
				Player.CurrentPlayer.NetLastHeartbeatTime = 0f;
			}
			break;
		case ENetworkPacketType.PlayerConnect:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerConnect");
			if (isServer)
			{
				NetworkGameServer.OnPacket_PlayerConnect(packetReader);
			}
			else
			{
				NetworkGameClient.OnPacket_PlayerConnect(packetReader);
			}
			break;
		case ENetworkPacketType.PlayerList:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerList");
			if (!isServer)
			{
				NetworkGameClient.OnPacket_PlayerList(packetReader);
			}
			break;
		case ENetworkPacketType.PlayerUnitList:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerUnitList");
			if (!isServer)
			{
				NetworkGameClient.OnPacket_PlayerUnitList(packetReader);
			}
			break;
		case ENetworkPacketType.PlayerSelectUnit:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerSelectUnit");
			NetworkID playerID6 = packetReader.ReadNetworkID();
			int channel5 = packetReader.ReadByte();
			Player player8 = Player.FindPlayer(playerID6, channel5);
			if (!player8)
			{
				break;
			}
			Unit unit3 = null;
			uint num8 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(num8);
			if (num8 != 0)
			{
				NetworkComponent netObject = NetworkComponent.GetNetObject(num8);
				if (!netObject)
				{
					break;
				}
				unit3 = netObject.gameObject.GetComponent<Unit>();
				if (!unit3)
				{
					break;
				}
			}
			if (isServer)
			{
				if (player8.GetCanSwitchToUnit(unit3))
				{
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, null);
					player8.ControlledUnit = unit3;
				}
			}
			else
			{
				player8.ControlledUnit = unit3;
			}
			break;
		}
		case ENetworkPacketType.PlayerSelectTeam:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerSelectTeam");
			NetworkID playerID10 = packetReader.ReadNetworkID();
			int channel9 = packetReader.ReadByte();
			Team team3 = packetReader.ReadTeam();
			Player player11 = Player.FindPlayer(playerID10, channel9);
			if ((bool)player11)
			{
				_ = player11.Team;
				if (isServer)
				{
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, null);
				}
				player11.Team = team3;
			}
			break;
		}
		case ENetworkPacketType.PlayerChangeName:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerChangeName");
			bool flag4 = packetReader.ReadBool();
			NetworkID playerID8 = packetReader.ReadNetworkID();
			int channel7 = packetReader.ReadByte();
			string playerName = packetReader.ReadString();
			if (isServer && !flag4)
			{
				break;
			}
			Player player9 = Player.FindPlayer(playerID8, channel7);
			if ((bool)player9)
			{
				player9.PlayerName = playerName;
				if (isServer)
				{
					Debug.Log("Player ID: " + player9.PlayerID.ToString() + ", channel: " + player9.PlayerChannel + " set name to '" + player9.PlayerName + "'");
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, null);
				}
			}
			break;
		}
		case ENetworkPacketType.PlayerScore:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerScore");
			NetworkID playerID9 = packetReader.ReadNetworkID();
			int channel8 = packetReader.ReadByte();
			short kills = packetReader.ReadInt16();
			short deaths = packetReader.ReadInt16();
			int score = packetReader.ReadInt32();
			Player player10 = Player.FindPlayer(playerID9, channel8);
			if ((bool)player10 && !isServer)
			{
				player10.Kills = kills;
				player10.Deaths = deaths;
				player10.Score = score;
			}
			break;
		}
		case ENetworkPacketType.PlayerAdminLevel:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:PlayerAdminLevel");
			NetworkID playerID2 = packetReader.ReadNetworkID();
			int channel2 = packetReader.ReadByte();
			Player player2 = Player.FindPlayer(playerID2, channel2);
			EAdminLevel adminLevel = (EAdminLevel)packetReader.ReadByte();
			if ((bool)player2 && !isServer)
			{
				player2.AdminLevel = adminLevel;
			}
			break;
		}
		case ENetworkPacketType.LoadLevel:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:LoadLevel");
			if (!isServer)
			{
				NetworkGameClient.OnPacket_LoadLevel(packetReader);
			}
			break;
		case ENetworkPacketType.LoadLevelFinished:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:LoadLevelFinished");
			if (!isServer)
			{
				NetworkGameClient.OnPacket_LoadLevelFinished();
			}
			break;
		case ENetworkPacketType.ChatMessage:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ChatMessage");
			NetworkID playerID4 = packetReader.ReadNetworkID();
			int num7 = packetReader.ReadByte();
			string text = packetReader.ReadString();
			bool flag = packetReader.ReadBool();
			bool flag2 = packetReader.ReadBool();
			Player player6 = (flag2 ? null : Player.FindPlayer(playerID4, num7));
			if (flag2 || (bool)player6)
			{
				bool flag3 = !flag || ((bool)player6 && Player.CurrentPlayer.Team == player6.Team);
				if (isServer)
				{
					SendChatMessage(playerID4, num7, text, flag, flag2);
					Player.LogChatMessage(player6, text, flag, flag2);
				}
				else if (flag3)
				{
					Player.LogChatMessage(player6, text, flag, flag2);
				}
				if (flag3)
				{
					GameEvents.OnChatMessage?.Invoke(player6, text, flag);
				}
			}
			break;
		}
		case ENetworkPacketType.ObjectCreate:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectCreate");
			if (!isServer)
			{
				NetworkGameClient.OnPacket_ObjectCreate(packetReader);
			}
			break;
		case ENetworkPacketType.ObjectInit:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectInit");
			uint netID2 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID2);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID2);
			if ((bool)netObject && !isServer)
			{
				netObject.ProcessNetInit(packetReader);
			}
			else if (!netObject)
			{
				Debug.LogError("Cannot initialize object with network ID '" + netID2 + "', not found");
			}
			break;
		}
		case ENetworkPacketType.ObjectDestroy:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectDestroy");
			uint netID7 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID7);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID7);
			if ((bool)netObject)
			{
				if (isServer)
				{
					if ((bool)netObject.OwnerPlayer && netObject.OwnerPlayer.PlayerID == remoteID)
					{
						UnityEngine.Object.Destroy(netObject.gameObject);
					}
					else
					{
						Debug.LogWarning("Cannot delete object with network ID '" + netID7 + "', the requester is not the owner!");
					}
				}
				else
				{
					UnityEngine.Object.Destroy(netObject.gameObject);
				}
			}
			else
			{
				Debug.LogError("Cannot delete object with network ID '" + netID7 + "', not found");
			}
			break;
		}
		case ENetworkPacketType.ObjectRPC:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectRPC");
			uint netID5 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID5);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID5);
			if ((bool)netObject)
			{
				if (isServer)
				{
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, netObject.OwnerPlayer);
				}
				netObject.ProcessNetRPC(packetReader);
			}
			break;
		}
		case ENetworkPacketType.ObjectUpdate:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectUpdate");
			uint netID3 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID3);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID3);
			if ((bool)netObject)
			{
				if (isServer)
				{
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, netObject.OwnerPlayer);
				}
				netObject.ProcessNetUpdate(packetReader);
			}
			break;
		}
		case ENetworkPacketType.ObjectChangeOwner:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectChangeOwner");
			if (!isServer)
			{
				uint netID = packetReader.ReadPackedUInt32();
				packetReader.SetNetID(netID);
				NetworkID playerID = packetReader.ReadNetworkID();
				int channel = packetReader.ReadByte();
				NetworkComponent netObject = NetworkComponent.GetNetObject(netID);
				if ((bool)netObject)
				{
					netObject.SetPlayerOwner(Player.FindPlayer(playerID, channel));
				}
			}
			break;
		case ENetworkPacketType.ObjectClientDamageHit:
		{
			if (!isServer)
			{
				break;
			}
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectClientDamageHit");
			uint netID9 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID9);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID9);
			if ((bool)netObject)
			{
				DamageManager component2 = netObject.gameObject.GetComponent<DamageManager>();
				if ((bool)component2)
				{
					component2.OnReceiveClientDamageHitPacket(packetReader);
				}
			}
			break;
		}
		case ENetworkPacketType.ObjectSetHealth:
		{
			if (isServer)
			{
				break;
			}
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectSetHealth");
			uint netID8 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID8);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID8);
			if ((bool)netObject)
			{
				DamageManager component = netObject.gameObject.GetComponent<DamageManager>();
				if ((bool)component)
				{
					component.GetSetHealthPacket(packetReader);
				}
			}
			break;
		}
		case ENetworkPacketType.ObjectSetTeam:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectSetTeam");
			uint netID6 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID6);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID6);
			if ((bool)netObject)
			{
				Team team = packetReader.ReadTeam();
				if (isServer)
				{
					netObject.Owner.Team = team;
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, null);
				}
				else if (!isServer)
				{
					netObject.Owner.Team = team;
				}
			}
			break;
		}
		case ENetworkPacketType.ObjectSpecialAction:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ObjectSpecialAction");
			uint netID4 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(netID4);
			NetworkComponent netObject = NetworkComponent.GetNetObject(netID4);
			if (!netObject)
			{
				break;
			}
			if (isServer)
			{
				if (!netObject.Owner.HasSpecialAction())
				{
					break;
				}
				netObject.SendSpecialActionPacket();
			}
			netObject.Owner.PerformSpecialAction();
			break;
		}
		case ENetworkPacketType.GameModeInit:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:GameModeInit");
			if (!isServer && (bool)GameMode.CurrentGameMode)
			{
				GameMode.CurrentGameMode.ReadNetInit(packetReader);
			}
			break;
		case ENetworkPacketType.GameModeRPC:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:GameModeRPC");
			if ((bool)GameMode.CurrentGameMode)
			{
				Player ignorePlayer = Player.FindPlayer(remoteID, packetReader.ReadByte());
				if (isServer)
				{
					RelayPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, ignorePlayer);
				}
				GameMode.CurrentGameMode.ProcessNetRPC(packetReader, packetReader.ReadByte());
			}
			break;
		case ENetworkPacketType.Time:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:Time");
			if (!isServer && (bool)GameManager.Instance && (bool)TimeManager.Instance)
			{
				packetReader.ReadBool();
				float targetSpeed = packetReader.ReadFloat();
				float currentSpeed = packetReader.ReadFloat();
				TimeManager.Instance.SetSpeed(targetSpeed, currentSpeed);
			}
			break;
		case ENetworkPacketType.TickMultiplier:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:TickMultiplier");
			if (!isServer && (bool)GameManager.Instance)
			{
				GameManager.Instance.Net_TickMult = packetReader.ReadFloat();
			}
			break;
		case ENetworkPacketType.Cheats:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:Cheats");
			if (!isServer)
			{
				Game.CheatsEnabled = packetReader.ReadBool();
			}
			break;
		case ENetworkPacketType.TeamInit:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:TeamInit");
			if (!isServer)
			{
				int num3 = packetReader.ReadByte();
				Debug.Log("Reading Init data for " + num3 + " teams...");
				for (int i = 0; i < num3; i++)
				{
					Team team2 = Team.Teams[i];
					Debug.Log("Reading Init data for team index " + i + " '" + team2.TeamShortName + "'");
					team2.OnNetInit(packetReader);
				}
			}
			break;
		case ENetworkPacketType.TeamRPC:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:TeamRPC");
			if (!isServer)
			{
				byte index = packetReader.ReadByte();
				byte rpcIndex = packetReader.ReadByte();
				Team.GetTeamByIndex(index).OnNetRPC(rpcIndex, packetReader);
			}
			break;
		case ENetworkPacketType.VoteKick:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:VoteKick");
			Player player4 = Player.FindPlayer(packetReader.ReadNetworkID(), packetReader.ReadByte());
			if ((bool)player4)
			{
				bool kick = packetReader.ReadBool();
				if ((bool)GameMode.CurrentGameMode && (!isServer || !GameMode.CurrentGameMode.VoteKickPlayer || !(GameMode.CurrentGameMode.VoteKickPlayer != player4)))
				{
					GameMode.CurrentGameMode.OnVoteKick(player4, kick);
				}
			}
			break;
		}
		case ENetworkPacketType.Ping:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:Ping");
			NetworkID playerID11 = packetReader.ReadNetworkID();
			int channel10 = packetReader.ReadByte();
			Player player12 = Player.FindPlayer(playerID11, channel10);
			if (!player12)
			{
				break;
			}
			Target target = null;
			uint num10 = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(num10);
			if (num10 != 0)
			{
				NetworkComponent netObject = NetworkComponent.GetNetObject(num10);
				if (!netObject)
				{
					break;
				}
				BaseGameObject baseGameObject = netObject.gameObject.GetBaseGameObject();
				if ((bool)baseGameObject)
				{
					target = baseGameObject.Target;
				}
			}
			Vector3 position = packetReader.ReadVector3();
			if (isServer)
			{
				SendPlayerPing(player12, position, target);
			}
			PingManager.Ping(player12, position, target);
			break;
		}
		case ENetworkPacketType.RemoteCommand:
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:RemoteCommand");
			if ((bool)player && isServer)
			{
				DebugConsole.TryExecuteCommand(packetReader.ReadString(), allowPartialMatch: true, player);
			}
			break;
		case ENetworkPacketType.RemoteCommandResult:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:RemoteCommandResult");
			NetworkID playerID7 = packetReader.ReadNetworkID();
			int channel6 = packetReader.ReadByte();
			if ((bool)Player.FindPlayer(playerID7, channel6) && !isServer)
			{
				int k = 0;
				for (int num9 = packetReader.ReadInt16(); k < num9; k++)
				{
					DebugConsole.Log(packetReader.ReadString(), (DebugConsole.LogLevel)packetReader.ReadByte());
				}
			}
			break;
		}
		case ENetworkPacketType.VoiceStream:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:VoiceStream");
			NetworkID playerID5 = packetReader.ReadNetworkID();
			int channel4 = packetReader.ReadByte();
			bool proximityOnly = packetReader.ReadBool();
			Player player7 = Player.FindPlayer(playerID5, channel4);
			if ((bool)player7)
			{
				if (isServer)
				{
					RelayVoiceStreamPacket(s_PacketBuffer, packetLength, ENetworkPacketSend.Reliable, player7, proximityOnly);
				}
				Player currentPlayer = Player.CurrentPlayer;
				if ((bool)currentPlayer && currentPlayer.GetCanReceiveVoice(player7, proximityOnly))
				{
					VoiceManager.DecompressVoice(packetReader, player7, proximityOnly);
				}
			}
			break;
		}
		case ENetworkPacketType.SynchHealth:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:SynchHealth");
			if (isServer)
			{
				break;
			}
			int num4 = packetReader.ReadByte();
			for (int j = 0; j < num4; j++)
			{
				uint num5 = packetReader.ReadPackedUInt32();
				uint num6 = packetReader.ReadPackedUInt32();
				byte hitAng = packetReader.ReadByte();
				packetReader.SetNetID(num5);
				float health = packetReader.ReadFloat();
				if (num5 == 0)
				{
					continue;
				}
				NetworkComponent netObject = NetworkComponent.GetNetObject(num5);
				if (!netObject)
				{
					break;
				}
				DamageManager damageManager = netObject.transform.GetDamageManager();
				if (!damageManager)
				{
					continue;
				}
				GameObject instigator = null;
				if (num6 != 0)
				{
					NetworkComponent netObject2 = NetworkComponent.GetNetObject(num6);
					if ((bool)netObject2)
					{
						instigator = netObject2.gameObject;
					}
				}
				damageManager.OnSynchHealth(health, instigator, hitAng);
			}
			break;
		}
		case ENetworkPacketType.SynchLongRangeMovement:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:SynchLongRangeMovement");
			if (isServer)
			{
				break;
			}
			uint num2 = packetReader.ReadPackedUInt32();
			if (num2 == 0)
			{
				break;
			}
			NetworkComponent netObject = NetworkComponent.GetNetObject(num2);
			if (!netObject)
			{
				break;
			}
			Unit unit2 = netObject.Owner as Unit;
			if ((bool)unit2)
			{
				AIBaseAgent aIAgent = unit2.AIAgent;
				if ((bool)aIAgent)
				{
					aIAgent.OnSynchLongRangeMovement(packetReader);
				}
			}
			break;
		}
		case ENetworkPacketType.Suicide:
		{
			packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:Suicide");
			NetworkID playerID3 = packetReader.ReadNetworkID();
			int channel3 = packetReader.ReadByte();
			Player player3 = Player.FindPlayer(playerID3, channel3);
			if (!player3 || !isServer)
			{
				break;
			}
			Unit unit = null;
			uint num = packetReader.ReadPackedUInt32();
			packetReader.SetNetID(num);
			if (num == 0)
			{
				break;
			}
			NetworkComponent netObject = NetworkComponent.GetNetObject(num);
			if ((bool)netObject)
			{
				unit = netObject.gameObject.GetComponent<Unit>();
				if ((bool)unit && !(unit != player3.ControlledUnit))
				{
					unit.Suicide();
				}
			}
			break;
		}
		case ENetworkPacketType.UnitGroup:
			if (!isServer)
			{
				packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:UnitGroup");
				AIGroup.OnReceivePacket(packetReader);
			}
			break;
		case ENetworkPacketType.AITask:
			if (!isServer)
			{
				packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:AITask");
				AITaskBase.OnReceivePacket(packetReader);
			}
			break;
		case ENetworkPacketType.SynchNavmeshRecalc:
			if (!isServer)
			{
				packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:SynchNavmeshRecalc");
				GameAI.OnNetSynchNavmeshRecalc(packetReader.ReadBool());
			}
			break;
		case ENetworkPacketType.WorkshopIDs:
			if (!isServer)
			{
				packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:WorkshopIDs");
				ContentManager.OnReconcileWorkshopIds(packetReader);
			}
			break;
		case ENetworkPacketType.ClientChecksum:
			if (isServer && NetworkGameServer.IsConnecting(remoteID))
			{
				packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:ClientChecksum");
				byte[] array = new byte[packetReader.ReadInt16()];
				packetReader.ReadByteArray(array);
				ContentManager.CompareChecksumAndNotify(remoteID, array);
			}
			break;
		case ENetworkPacketType.Overrides:
			if (!isServer)
			{
				packetReader.SetFunction("NetworkLayer::RetrieveNetworkData:Overrides");
				OverrideManager.OnReceiveOverrides(packetReader);
			}
			break;
		}
	}

	public static bool AcceptSessionWithUser(NetworkID userID)
	{
		return false;
	}

	public static bool ServerCloseChannelWithUser(NetworkID userID, int channel)
	{
		return NetworkGameServer.RemoveConnection(userID);
	}

	public static void OnInitClient()
	{
		NetNoUnreliableTime = 0f;
		NetNoReportBufferExceededTime = 0f;
		SteamNetworkingUtils.InitRelayNetworkAccess();
	}

	public static void OnInitServer()
	{
		NetNoUnreliableTime = 0f;
		NetNoReportBufferExceededTime = 0f;
		SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
	}

	private static bool SendPacket(HSteamNetConnection connection, bool server, byte[] pubData, uint cubData, ENetworkPacketSend sendType)
	{
		int nFlags = ((sendType == ENetworkPacketSend.Reliable) ? 8 : 0);
		IntPtr intPtr = IntPtr.Zero;
		int num = (int)cubData;
		try
		{
			intPtr = ((!server) ? SteamNetworkingUtils.AllocateMessage(num) : SteamGameServerNetworkingUtils.AllocateMessage(num));
			SteamNetworkingMessage_t structure = SteamNetworkingMessage_t.FromIntPtr(intPtr);
			Marshal.Copy(pubData, 0, structure.m_pData, num);
			structure.m_nFlags = nFlags;
			structure.m_idxLane = ((sendType != ENetworkPacketSend.Reliable) ? ((ushort)1) : ((ushort)0));
			structure.m_conn = connection;
			Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
			s_MessagesToSend[0] = intPtr;
		}
		catch (Exception)
		{
			s_MessagesToSend[0] = IntPtr.Zero;
			SteamNetworkingMessage_t.Release(intPtr);
			return false;
		}
		if (server)
		{
			SteamGameServerNetworkingSockets.SendMessages(1, s_MessagesToSend, s_MessageSendResults);
		}
		else
		{
			SteamNetworkingSockets.SendMessages(1, s_MessagesToSend, s_MessageSendResults);
		}
		EResult eResult = ((s_MessageSendResults[0] >= 1) ? EResult.k_EResultOK : ((EResult)(-s_MessageSendResults[0])));
		switch (eResult)
		{
		case EResult.k_EResultLimitExceeded:
			if (NetNoReportBufferExceededTime <= 0f)
			{
				Debug.LogError("NetworkLayer::SendPacket: Failed to send " + sendType.ToString() + " (" + cubData + "b) packet, reason: " + eResult.ToString() + ", not showing again for the next " + 5f + "s...");
				NetNoReportBufferExceededTime = 5f;
			}
			NetNoUnreliableTime = 3f;
			break;
		default:
			Debug.LogWarning("NetworkLayer::SendPacket: Failed to send " + sendType.ToString() + " (" + cubData + "b) packet, reason: " + eResult);
			if (eResult != EResult.k_EResultNoConnection)
			{
				break;
			}
			if (Game.GetIsServer())
			{
				NetworkID clientNetConnection = NetworkGameServer.GetClientNetConnection(connection);
				if (!NetworkGameServer.RemoveConnection(clientNetConnection))
				{
					NetworkID networkID = clientNetConnection;
					Debug.LogError("NetworkLayer::SendPacket: Failed to remove dead connection for player ID " + networkID.ToString() + ", the connection handle is " + ((connection == HSteamNetConnection.Invalid) ? "INVALID!" : ((object)connection)));
				}
				NetworkGameServer.DisconnectPlayer(clientNetConnection, 0);
			}
			else
			{
				GameMessage.Hide();
				NetworkGameClient.DisconnectFromServer(skipInformServer: true);
				GameLevelLoader.ReturnToMainMenu();
				LocalizationUtility.TryGetTranslation("Miscellaneous", "DisconnectedServer", null, out var translation);
				MessageHandler.QueueHint(translation, null, 5f);
			}
			break;
		case EResult.k_EResultOK:
			break;
		}
		return eResult == EResult.k_EResultOK;
	}

	private static bool SendClientPacket(NetworkID IDRemote, byte[] pubData, uint cubData, ENetworkPacketSend sendType, int nChannel = 0)
	{
		if (NetNoUnreliableTime > 0f && sendType == ENetworkPacketSend.Unreliable)
		{
			return false;
		}
		HSteamNetConnection serverNetConnection = NetworkGameClient.GetServerNetConnection();
		if (serverNetConnection == HSteamNetConnection.Invalid)
		{
			return false;
		}
		return SendPacket(serverNetConnection, server: false, pubData, cubData, sendType);
	}

	private static bool SendServerPacket(NetworkID IDRemote, byte[] pubData, uint cubData, ENetworkPacketSend sendType, int nChannel = 0)
	{
		if (NetNoUnreliableTime > 0f && sendType == ENetworkPacketSend.Unreliable)
		{
			return false;
		}
		_ = 1;
		HSteamNetConnection clientNetConnection = NetworkGameServer.GetClientNetConnection(IDRemote);
		if (clientNetConnection == HSteamNetConnection.Invalid)
		{
			return false;
		}
		return SendPacket(clientNetConnection, server: true, pubData, cubData, sendType);
	}

	public static void SendHeartbeat()
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendHeartbeat");
		gameByteStreamWriter.WriteByte(0);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void RelayPacket(byte[] packet, uint packetBytesSize, ENetworkPacketSend sendType, Player ignorePlayer)
	{
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == ignorePlayer) && !(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += packetBytesSize * 8;
				SendServerPacket(player.PlayerID, packet, packetBytesSize, sendType, player.PlayerChannel);
			}
		}
	}

	public static void RelayVoiceStreamPacket(byte[] packet, uint packetBytesSize, ENetworkPacketSend sendType, Player senderPlayer, bool proximityOnly)
	{
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == senderPlayer) && !(player == NetworkGameServer.GetServerPlayer()) && player.GetCanReceiveVoice(senderPlayer, proximityOnly))
			{
				NetBitsSent += packetBytesSize * 8;
				SendServerPacket(player.PlayerID, packet, packetBytesSize, sendType, player.PlayerChannel);
			}
		}
	}

	public static bool SendPacket(GameByteStreamWriter packetWriter, ENetworkPacketSend sendType)
	{
		if (packetWriter == null)
		{
			return false;
		}
		if (!Game.GetIsMultiplayer())
		{
			return false;
		}
		bool isServer = Game.GetIsServer();
		byte[] byteData = packetWriter.GetByteData();
		uint byteDataSize = (uint)packetWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, sendType, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, sendType);
		}
		return true;
	}

	public static bool SendPacketToPlayer(GameByteStreamWriter packetWriter, ENetworkPacketSend sendType, Player player)
	{
		if (packetWriter == null)
		{
			return false;
		}
		if (!Game.GetIsMultiplayer())
		{
			return false;
		}
		if (!Game.GetIsServer())
		{
			return false;
		}
		byte[] byteData = packetWriter.GetByteData();
		uint byteDataSize = (uint)packetWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendServerPacket(player.PlayerID, byteData, byteDataSize, sendType, player.PlayerChannel);
		return true;
	}

	public static void SendPlayerConnect(ENetworkPlayerConnectType connectType, NetworkID playerID, int playerChannel)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerConnect");
		gameByteStreamWriter.WriteByte(1);
		gameByteStreamWriter.WriteNetworkID(playerID);
		gameByteStreamWriter.WriteByte((byte)playerChannel);
		gameByteStreamWriter.WriteByte((byte)connectType);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendPlayerConnectResponse(ENetworkPlayerConnectType connectType, NetworkID playerID, int playerChannel, NetworkID serverID)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerConnectResponse");
		gameByteStreamWriter.WriteByte(1);
		gameByteStreamWriter.WriteNetworkID(serverID);
		gameByteStreamWriter.WriteByte(0);
		gameByteStreamWriter.WriteByte((byte)connectType);
		if (connectType == ENetworkPlayerConnectType.InformConnect)
		{
			gameByteStreamWriter.WriteUInt16((ushort)NetworkGameServer.GetPlayersMax());
			gameByteStreamWriter.WriteString(NetworkGameServer.GetServerName());
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendServerPacket(playerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, playerChannel);
	}

	public static void SendPlayerConnectRequest(ENetworkPlayerConnectType connectType, NetworkID playerID, int playerChannel, NetworkID serverID, string password = null)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerConnectRequest");
		gameByteStreamWriter.WriteByte(1);
		gameByteStreamWriter.WriteNetworkID(playerID);
		gameByteStreamWriter.WriteByte((byte)playerChannel);
		gameByteStreamWriter.WriteByte((byte)connectType);
		if (connectType == ENetworkPlayerConnectType.RequestConnect)
		{
			gameByteStreamWriter.WriteString((password != null) ? password : string.Empty);
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendClientPacket(serverID, byteData, byteDataSize, ENetworkPacketSend.Reliable);
	}

	public static void SendPlayerList(Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerList");
		gameByteStreamWriter.WriteByte(2);
		gameByteStreamWriter.WriteUInt16((ushort)Player.GetNumPlayers());
		for (int i = 0; i < Player.GetNumPlayers(); i++)
		{
			Player player2 = Player.Players[i];
			gameByteStreamWriter.WriteNetworkID(player2.PlayerID);
			gameByteStreamWriter.WriteByte((byte)player2.PlayerChannel);
			gameByteStreamWriter.WriteTeam(player2.Team);
			gameByteStreamWriter.WriteInt16(player2.Kills);
			gameByteStreamWriter.WriteInt16(player2.Deaths);
			gameByteStreamWriter.WriteInt32(player2.Score);
			gameByteStreamWriter.WriteByte((byte)player2.AdminLevel);
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
	}

	public static void SendPlayerOverrides(Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerOverrides");
		gameByteStreamWriter.WriteByte(42);
		Dictionary<string, Dictionary<string, OverrideManager.MemberOverride>> overrides = OverrideManager.GetOverrides();
		gameByteStreamWriter.WriteUInt16((ushort)overrides.Count);
		foreach (KeyValuePair<string, Dictionary<string, OverrideManager.MemberOverride>> item in overrides)
		{
			gameByteStreamWriter.WriteString(item.Key);
			ushort num = 0;
			foreach (KeyValuePair<string, OverrideManager.MemberOverride> item2 in item.Value)
			{
				if (item2.Value.Enabled)
				{
					num++;
				}
			}
			gameByteStreamWriter.WriteUInt16(num);
			foreach (KeyValuePair<string, OverrideManager.MemberOverride> item3 in item.Value)
			{
				if (item3.Value.Enabled)
				{
					OverrideManager.WriteMemberData(gameByteStreamWriter, item3.Value);
				}
			}
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
	}

	public static void SendOverride(string target, OverrideManager.MemberOverride memberOverride, bool revert = false, bool revertAll = false)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendOverride");
		gameByteStreamWriter.WriteByte(42);
		gameByteStreamWriter.WriteUInt16(1);
		if (!revertAll)
		{
			gameByteStreamWriter.WriteString(target);
			gameByteStreamWriter.WriteUInt16(1);
			OverrideManager.WriteMemberData(gameByteStreamWriter, memberOverride, revert);
		}
		else
		{
			gameByteStreamWriter.WriteString("r");
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendPlayerUnitList(Player player)
	{
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerUnitList");
		gameByteStreamWriter.WriteByte(3);
		for (int i = 0; i < Player.GetNumPlayers(); i++)
		{
			Unit controlledUnit = Player.Players[i].ControlledUnit;
			if (!controlledUnit)
			{
				gameByteStreamWriter.WritePackedUInt32(0u);
			}
			else
			{
				gameByteStreamWriter.WritePackedUInt32(controlledUnit.gameObject.GetComponent<NetworkComponent>().NetID);
			}
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
	}

	public static void SendPlayerSelectUnit(Player playerSelect, Unit unit)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		if (playerSelect == null || !playerSelect.GetCanSwitchToUnit(unit))
		{
			return;
		}
		NetworkComponent networkComponent = null;
		if ((bool)unit)
		{
			networkComponent = unit.gameObject.GetComponent<NetworkComponent>();
			if (networkComponent == null)
			{
				return;
			}
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(networkComponent ? networkComponent.NetID : 0u, "NetworkLayer::SendPlayerSelectUnit");
		gameByteStreamWriter.WriteByte(4);
		gameByteStreamWriter.WriteNetworkID(playerSelect.PlayerID);
		gameByteStreamWriter.WriteByte((byte)playerSelect.PlayerChannel);
		if (!unit)
		{
			gameByteStreamWriter.WritePackedUInt32(0u);
		}
		else
		{
			gameByteStreamWriter.WritePackedUInt32(networkComponent.NetID);
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendPlayerSelectTeam(Player playerSelect, Team team)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		if (playerSelect == null)
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerSelectTeam");
		gameByteStreamWriter.WriteByte(5);
		gameByteStreamWriter.WriteNetworkID(playerSelect.PlayerID);
		gameByteStreamWriter.WriteByte((byte)playerSelect.PlayerChannel);
		gameByteStreamWriter.WriteTeam(team);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendPlayerScore(Player forPlayer)
	{
		if (!Game.GetIsServer() || forPlayer == null)
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerScore");
		gameByteStreamWriter.WriteByte(7);
		gameByteStreamWriter.WriteNetworkID(forPlayer.PlayerID);
		gameByteStreamWriter.WriteByte((byte)forPlayer.PlayerChannel);
		gameByteStreamWriter.WriteInt16(forPlayer.Kills);
		gameByteStreamWriter.WriteInt16(forPlayer.Deaths);
		gameByteStreamWriter.WriteInt32(forPlayer.Score);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendPlayerAdminLevel(Player forPlayer)
	{
		if (!Game.GetIsServer() || forPlayer == null)
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerAdminLevel");
		gameByteStreamWriter.WriteByte(8);
		gameByteStreamWriter.WriteNetworkID(forPlayer.PlayerID);
		gameByteStreamWriter.WriteByte((byte)forPlayer.PlayerChannel);
		gameByteStreamWriter.WriteByte((byte)forPlayer.AdminLevel);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendVoteKick(Player kickPlayer, bool kick)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		if (kickPlayer == null)
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendVoteKick");
		gameByteStreamWriter.WriteByte(29);
		gameByteStreamWriter.WriteNetworkID(kickPlayer.PlayerID);
		gameByteStreamWriter.WriteByte((byte)kickPlayer.PlayerChannel);
		gameByteStreamWriter.WriteBool(kick);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendPlayerInitName(Player toPlayer, Player playerGetName)
	{
		if (Game.GetIsServer() && (bool)playerGetName && (bool)toPlayer && !(toPlayer == playerGetName))
		{
			GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerInitName");
			gameByteStreamWriter.WriteByte(6);
			gameByteStreamWriter.WriteBool(value: false);
			gameByteStreamWriter.WriteNetworkID(playerGetName.PlayerID);
			gameByteStreamWriter.WriteByte((byte)playerGetName.PlayerChannel);
			gameByteStreamWriter.WriteString(playerGetName.PlayerName);
			byte[] byteData = gameByteStreamWriter.GetByteData();
			uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
			NetBitsSent += byteDataSize * 8;
			SendServerPacket(toPlayer.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, toPlayer.PlayerChannel);
		}
	}

	public static void SendPlayerChangeName(NetworkID playerID, int playerChannel, string newName)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendPlayerChangeName");
		gameByteStreamWriter.WriteByte(6);
		gameByteStreamWriter.WriteBool(value: true);
		gameByteStreamWriter.WriteNetworkID(playerID);
		gameByteStreamWriter.WriteByte((byte)playerChannel);
		gameByteStreamWriter.WriteString(newName);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendChatMessage(NetworkID playerID, int playerChannel, string message, bool teamOnly, bool isServerMessage = false)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		if (isServerMessage && !isServer)
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendChatMessage");
		gameByteStreamWriter.WriteByte(11);
		gameByteStreamWriter.WriteNetworkID(playerID);
		gameByteStreamWriter.WriteByte((byte)playerChannel);
		gameByteStreamWriter.WriteString(message);
		gameByteStreamWriter.WriteBool(teamOnly);
		gameByteStreamWriter.WriteBool(isServerMessage);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			Player player = Player.FindPlayer(playerID, playerChannel);
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player2 = Player.Players[i];
				if ((!teamOnly || !(player2.Team != player.Team)) && !(player2 == NetworkGameServer.GetServerPlayer()))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player2.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player2.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendPlayerPing(Player pingPlayer, Vector3 position, Target target)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		bool isServer = Game.GetIsServer();
		if (pingPlayer == null)
		{
			return;
		}
		NetworkComponent networkComponent = null;
		if ((bool)target)
		{
			networkComponent = target.NetworkComponent;
			if (networkComponent == null)
			{
				return;
			}
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(networkComponent ? networkComponent.NetID : 0u, "NetworkLayer::SendPlayerPing");
		gameByteStreamWriter.WriteByte(30);
		gameByteStreamWriter.WriteNetworkID(pingPlayer.PlayerID);
		gameByteStreamWriter.WriteByte((byte)pingPlayer.PlayerChannel);
		if (!target)
		{
			gameByteStreamWriter.WritePackedUInt32(0u);
		}
		else
		{
			gameByteStreamWriter.WritePackedUInt32(networkComponent.NetID);
		}
		gameByteStreamWriter.WriteVector3(position);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		if (isServer)
		{
			int i = 0;
			for (int count = Player.Players.Count; i < count; i++)
			{
				Player player = Player.Players[i];
				if (!(player == pingPlayer) && !(player == NetworkGameServer.GetServerPlayer()) && !Team.GetTeamsAreEnemy(player.Team, pingPlayer.Team))
				{
					NetBitsSent += byteDataSize * 8;
					SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
				}
			}
		}
		else
		{
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendTimeEvent(bool slowTime, float timeScaleTarget, float timeScaleCurrent)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendTimeEvent");
		gameByteStreamWriter.WriteByte(24);
		gameByteStreamWriter.WriteBool(slowTime);
		gameByteStreamWriter.WriteFloat(timeScaleTarget);
		gameByteStreamWriter.WriteFloat(timeScaleCurrent);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendTickMult(float tickMult)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendTickMult");
		gameByteStreamWriter.WriteByte(25);
		gameByteStreamWriter.WriteFloat(tickMult);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendCheatsEvent(bool enableCheats)
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendCheatsEvent");
		gameByteStreamWriter.WriteByte(26);
		gameByteStreamWriter.WriteBool(enableCheats);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendTeamsInit()
	{
		if (!Game.GetIsMultiplayer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendTeamsInit");
		gameByteStreamWriter.WriteByte(27);
		int count = Team.Teams.Count;
		Debug.Log("Sending Init data for " + count + " teams...");
		gameByteStreamWriter.WriteByte((byte)count);
		for (int i = 0; i < count; i++)
		{
			Team team = Team.Teams[i];
			Debug.Log("Sending Init data for team index " + i + " '" + team.TeamShortName + "'");
			team.OnSendNetInit(gameByteStreamWriter);
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int j = 0;
		for (int count2 = Player.Players.Count; j < count2; j++)
		{
			Player player = Player.Players[j];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendLoadLevelFinished()
	{
		if (!Game.GetIsServer())
		{
			return;
		}
		Debug.Log("Loading finished, advertising server...");
		NetworkGameServer.SetAdvertiseServer(show: true);
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendLoadLevelFinished");
		gameByteStreamWriter.WriteByte(10);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendLoadLevel(string levelName, GameModeInfo gameMode)
	{
		if (!Game.GetIsServer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendLoadLevel");
		gameByteStreamWriter.WriteByte(9);
		gameByteStreamWriter.WriteBool(value: true);
		gameByteStreamWriter.WriteString(levelName);
		gameByteStreamWriter.WriteByte((byte)(GameDatabase.Database.AllGameModes.IndexOf(gameMode) + 1));
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				player.NetIsReady = false;
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendLoadLevel(string levelName, GameModeInfo gameMode, NetworkID playerID, int playerChannel)
	{
		if (Game.GetIsServer())
		{
			GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendLoadLevel");
			gameByteStreamWriter.WriteByte(9);
			gameByteStreamWriter.WriteBool(value: false);
			gameByteStreamWriter.WriteString(levelName);
			gameByteStreamWriter.WriteByte((byte)(GameDatabase.Database.AllGameModes.IndexOf(gameMode) + 1));
			Player player = Player.FindPlayer(playerID, playerChannel);
			if ((bool)player)
			{
				player.NetIsReady = false;
			}
			byte[] byteData = gameByteStreamWriter.GetByteData();
			uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
			NetBitsSent += byteDataSize * 8;
			SendServerPacket(playerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, playerChannel);
		}
	}

	public static void SendWorkshopIDs(NetworkID playerID, int playerChannel)
	{
		if (Game.GetIsServer())
		{
			Debug.Log("SERVER - SendWorkshopIDs");
			GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendWorkshopIDs");
			gameByteStreamWriter.WriteByte(40);
			ulong[] workshopClientIds = ContentManager.GetWorkshopClientIds();
			gameByteStreamWriter.WriteUInt16((ushort)workshopClientIds.Length);
			gameByteStreamWriter.WriteUInt64Array(workshopClientIds);
			Player.FindPlayer(playerID, playerChannel);
			byte[] byteData = gameByteStreamWriter.GetByteData();
			uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
			NetBitsSent += byteDataSize * 8;
			SendServerPacket(playerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, playerChannel);
		}
	}

	public static void SendChecksum(byte[] checksum)
	{
		if (NetworkGameClient.GetJoiningServer())
		{
			Debug.Log("CLIENT - SendChecksum");
			GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendChecksum");
			gameByteStreamWriter.WriteByte(41);
			gameByteStreamWriter.WriteInt16((short)checksum.Length);
			gameByteStreamWriter.WriteByteArray(checksum);
			byte[] byteData = gameByteStreamWriter.GetByteData();
			uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendVoiceStream(float[] voicePacketBuffer, int startOffset, bool silence, bool proximityOnly)
	{
	}

	public static void SendRemoteCommand(string command)
	{
		if (Game.GetIsClient() && (bool)Player.CurrentPlayer)
		{
			GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendRemoteCommand");
			gameByteStreamWriter.WriteByte(31);
			gameByteStreamWriter.WriteString(command);
			byte[] byteData = gameByteStreamWriter.GetByteData();
			uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
			NetBitsSent += byteDataSize * 8;
			SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
		}
	}

	public static void SendRemoteCommandResult(Player remotePlayer, List<string> commandResult, List<DebugConsole.LogLevel> logLevel)
	{
		if (Game.GetIsServer() && (bool)remotePlayer)
		{
			GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendRemoteCommandResult");
			gameByteStreamWriter.WriteByte(32);
			gameByteStreamWriter.WriteNetworkID(remotePlayer.PlayerID);
			gameByteStreamWriter.WriteByte((byte)remotePlayer.PlayerChannel);
			gameByteStreamWriter.WriteInt16((short)commandResult.Count);
			int i = 0;
			for (int count = commandResult.Count; i < count; i++)
			{
				gameByteStreamWriter.WriteString(commandResult[i]);
				gameByteStreamWriter.WriteByte((byte)logLevel[i]);
			}
			byte[] byteData = gameByteStreamWriter.GetByteData();
			uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
			NetBitsSent += byteDataSize * 8;
			SendServerPacket(remotePlayer.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, remotePlayer.PlayerChannel);
		}
	}

	public static void SendSynchHealth(List<DamageManager> synchHealthList, List<GameObject> synchInstigatorsList, List<byte> synchHitAngList, int sendMax)
	{
		if (!Game.GetIsServer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendSynchHealth");
		gameByteStreamWriter.WriteByte(34);
		int num = Mathf.Min(sendMax, synchHealthList.Count);
		if (num == 0)
		{
			return;
		}
		gameByteStreamWriter.WriteByte((byte)num);
		for (int i = 0; i < num; i++)
		{
			DamageManager damageManager = synchHealthList[0];
			GameObject gameObject = synchInstigatorsList[0];
			byte value = synchHitAngList[0];
			NetworkComponent networkComponent = null;
			if ((bool)gameObject)
			{
				networkComponent = gameObject.GetComponent<NetworkComponent>();
			}
			synchHealthList.RemoveAt(0);
			synchInstigatorsList.RemoveAt(0);
			synchHitAngList.RemoveAt(0);
			gameByteStreamWriter.WritePackedUInt32(damageManager.NetworkComponent.NetID);
			gameByteStreamWriter.WritePackedUInt32(networkComponent ? networkComponent.NetID : 0u);
			gameByteStreamWriter.WriteByte(value);
			gameByteStreamWriter.WriteFloat(damageManager.Health);
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int j = 0;
		for (int count = Player.Players.Count; j < count; j++)
		{
			Player player = Player.Players[j];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendSynchHealth(DamageManager synchHealthDM, GameObject synchInstigator, byte synchHitAng)
	{
		if (!Game.GetIsServer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendSynchHealth");
		gameByteStreamWriter.WriteByte(34);
		gameByteStreamWriter.WriteByte(1);
		NetworkComponent networkComponent = null;
		if ((bool)synchInstigator)
		{
			networkComponent = synchInstigator.GetComponent<NetworkComponent>();
		}
		gameByteStreamWriter.WritePackedUInt32(synchHealthDM.NetworkComponent.NetID);
		gameByteStreamWriter.WritePackedUInt32(networkComponent ? networkComponent.NetID : 0u);
		gameByteStreamWriter.WriteByte(synchHitAng);
		gameByteStreamWriter.WriteFloat(synchHealthDM.Health);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendSynchAIPathPacket(GameByteStreamWriter packetWriter, List<Player> toPlayers)
	{
		if (toPlayers == null || toPlayers.Count == 0 || packetWriter == null)
		{
			return;
		}
		byte[] byteData = packetWriter.GetByteData();
		uint byteDataSize = (uint)packetWriter.GetByteDataSize();
		int i = 0;
		for (int count = toPlayers.Count; i < count; i++)
		{
			Player player = toPlayers[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}

	public static void SendSuicide()
	{
		if (!Game.GetIsClient())
		{
			return;
		}
		Player currentPlayer = Player.CurrentPlayer;
		if (!currentPlayer)
		{
			return;
		}
		Unit controlledUnit = currentPlayer.ControlledUnit;
		if (!controlledUnit)
		{
			return;
		}
		NetworkComponent networkComponent = null;
		if ((bool)controlledUnit)
		{
			networkComponent = controlledUnit.gameObject.GetComponent<NetworkComponent>();
			if (networkComponent == null)
			{
				return;
			}
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(networkComponent ? networkComponent.NetID : 0u, "NetworkLayer::SendSuicide");
		gameByteStreamWriter.WriteByte(36);
		gameByteStreamWriter.WriteNetworkID(currentPlayer.PlayerID);
		gameByteStreamWriter.WriteByte((byte)currentPlayer.PlayerChannel);
		if (!controlledUnit)
		{
			gameByteStreamWriter.WritePackedUInt32(0u);
		}
		else
		{
			gameByteStreamWriter.WritePackedUInt32(networkComponent.NetID);
		}
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		NetBitsSent += byteDataSize * 8;
		SendClientPacket(NetworkGameClient.GetServerID(), byteData, byteDataSize, ENetworkPacketSend.Reliable);
	}

	public static void SendSynchNavmeshRecalc(bool isAnyGraphUpdateInProgress)
	{
		if (!Game.GetIsServer())
		{
			return;
		}
		GameByteStreamWriter gameByteStreamWriter = GameByteStreamWriter.GetGameByteStreamWriter(0u, "NetworkLayer::SendSynchNavmeshRecalc");
		gameByteStreamWriter.WriteByte(39);
		gameByteStreamWriter.WriteBool(isAnyGraphUpdateInProgress);
		byte[] byteData = gameByteStreamWriter.GetByteData();
		uint byteDataSize = (uint)gameByteStreamWriter.GetByteDataSize();
		int i = 0;
		for (int count = Player.Players.Count; i < count; i++)
		{
			Player player = Player.Players[i];
			if (!(player == NetworkGameServer.GetServerPlayer()))
			{
				NetBitsSent += byteDataSize * 8;
				SendServerPacket(player.PlayerID, byteData, byteDataSize, ENetworkPacketSend.Reliable, player.PlayerChannel);
			}
		}
	}
}
