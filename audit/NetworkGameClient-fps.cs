using Silica.UI;
using Silica.Utilities;
using Steamworks;
using UnityEngine;

[DisallowMultipleComponent]
public class NetworkGameClient : MonoBehaviour
{
	protected Callback<SteamNetConnectionStatusChangedCallback_t> m_OnConnectionChange;

	private HSteamNetConnection m_ServerNetConnection = HSteamNetConnection.Invalid;

	private NetworkID m_ServerID;

	private NetworkID m_PlayerID;

	private bool m_Connecting;

	private bool m_WaitingForServerLevelLoad;

	private bool m_WaitingForFrameAfterLevelLoad;

	private string m_LoadLevelFromServer = "";

	private int m_MaxPlayers = 16;

	private string m_ServerName = string.Empty;

	private float m_TimeSinceHearbeat;

	protected Callback<P2PSessionRequest_t> m_CallbackP2PSessionRequest;

	private GameObject NetStatus;

	public static NetworkGameClient Instance { get; private set; }

	public static NetworkID GetServerID()
	{
		if (!Instance)
		{
			return NetworkID.Nil;
		}
		return Instance.m_ServerID;
	}

	public static string GetServerName()
	{
		if (!GetInServer())
		{
			return string.Empty;
		}
		return Instance.m_ServerName;
	}

	public static HSteamNetConnection GetServerNetConnection()
	{
		if (!Instance)
		{
			return HSteamNetConnection.Invalid;
		}
		return Instance.m_ServerNetConnection;
	}

	public static bool GetServerIDValid(NetworkID IDRemote)
	{
		if (!Instance)
		{
			return false;
		}
		if (Instance.m_Connecting)
		{
			return true;
		}
		return Instance.m_ServerID == IDRemote;
	}

	public static int GetPlayersNum()
	{
		if ((bool)Instance)
		{
			return Player.Players.Count;
		}
		return 0;
	}

	public static int GetPlayersMax()
	{
		if ((bool)Instance)
		{
			return Instance.m_MaxPlayers;
		}
		return 0;
	}

	public static Player GetPlayer()
	{
		if (!Instance)
		{
			return null;
		}
		return Player.CurrentPlayer;
	}

	public static NetworkID GetPlayerID()
	{
		if (!Instance)
		{
			return NetworkID.Nil;
		}
		return Instance.m_PlayerID;
	}

	public static int GetWaitingForServerTimeLeft()
	{
		if (!GetWaitingForServer())
		{
			return 0;
		}
		if (Instance.m_WaitingForServerLevelLoad)
		{
			return Mathf.RoundToInt(180f - Player.CurrentPlayer.NetLastHeartbeatTime);
		}
		return Mathf.RoundToInt(60f - Player.CurrentPlayer.NetLastHeartbeatTime);
	}

	public static bool GetWaitingForServer()
	{
		if (!GetInServer())
		{
			return false;
		}
		if (!Instance.m_WaitingForServerLevelLoad)
		{
			return Game.GetIsLaggingShowWaitingForServer();
		}
		return true;
	}

	public static bool GetWaitingForServerToLoadLevel()
	{
		if (!GetInServer())
		{
			return false;
		}
		return Instance.m_WaitingForServerLevelLoad;
	}

	public static bool GetInServer()
	{
		if (!Instance)
		{
			return false;
		}
		if (!Instance.m_ServerID.IsValid() || Instance.m_ServerID == NetworkID.Nil)
		{
			return false;
		}
		if (Instance.m_ServerNetConnection == HSteamNetConnection.Invalid)
		{
			return false;
		}
		return true;
	}

	public static bool GetJoiningServer()
	{
		if (!Instance)
		{
			return false;
		}
		return Instance.m_Connecting;
	}

	public static bool CreateClient()
	{
		if (!SteamManager.Initialized)
		{
			return false;
		}
		new GameObject("NetworkGameClient").AddComponent<NetworkGameClient>();
		NetworkLayer.OnInitClient();
		if ((bool)Instance)
		{
			return true;
		}
		return false;
	}

	public static void CloseClient()
	{
		if ((bool)Instance)
		{
			Object.Destroy(Instance.gameObject);
		}
	}

	public static void InformLevelLoadedToServer()
	{
		if (GetInServer())
		{
			Debug.Log("InformLevelLoadedToServer");
			NetworkLayer.SendPlayerConnectRequest(ENetworkPlayerConnectType.LevelLoaded, GetPlayerID(), 0, GetServerID());
			NetworkLayer.SendPlayerChangeName(GetPlayerID(), 0, SteamFriends.GetPersonaName());
		}
	}

	public static void RequestConnectToServer(NetworkID serverID, string password)
	{
		if (!Instance)
		{
			return;
		}
		Instance.m_Connecting = false;
		if (Instance.m_ServerID != serverID)
		{
			DisconnectFromServer();
			if (serverID.IsValid() && serverID != NetworkID.Nil)
			{
				SteamNetworkingIdentity identityRemote = serverID.GetIdentity();
				Debug.Log("NetworkGameClient::RequestConnectToServer - STEAM ID: " + identityRemote.GetSteamID64());
				Instance.m_ServerNetConnection = SteamNetworkingSockets.ConnectP2P(ref identityRemote, 0, NetworkLayer.DefaultConnectionOptions.Length, NetworkLayer.DefaultConnectionOptions);
				NetworkLayer.ConfigureDefaultLanes(Instance.m_ServerNetConnection, server: false);
				NetworkLayer.SendPlayerConnectRequest(ENetworkPlayerConnectType.RequestConnect, GetPlayerID(), 0, serverID, password);
				Instance.m_Connecting = true;
			}
		}
	}

	public static void RequestDisconnectFromServer()
	{
		if (GetInServer())
		{
			NetworkLayer.SendPlayerConnectRequest(ENetworkPlayerConnectType.RequestDisconnect, GetPlayerID(), 0, GetServerID());
		}
	}

	public static void DisconnectFromServer(bool skipInformServer = false)
	{
		if ((bool)Instance)
		{
			if (!skipInformServer && GetInServer())
			{
				NetworkLayer.SendPlayerConnectRequest(ENetworkPlayerConnectType.InformDisconnect, GetPlayerID(), 0, GetServerID());
			}
			Player.ClearPlayers();
			if (Instance.m_ServerNetConnection != HSteamNetConnection.Invalid)
			{
				SteamNetworkingSockets.CloseConnection(Instance.m_ServerNetConnection, 0, "Disconnected", bEnableLinger: true);
			}
			Instance.m_ServerID = NetworkID.Nil;
			Instance.m_ServerNetConnection = HSteamNetConnection.Invalid;
			Instance.m_Connecting = false;
			Instance.m_OnConnectionChange.Dispose();
			Instance.m_CallbackP2PSessionRequest.Dispose();
		}
	}

	public static void OnLevelLoaded(string scenePath)
	{
		if ((bool)Instance)
		{
			if (Instance.m_LoadLevelFromServer == scenePath)
			{
				Instance.m_WaitingForFrameAfterLevelLoad = true;
			}
			Debug.Log("OnLevelLoaded " + scenePath + ", now waiting for first frame to send loaded to server...");
		}
	}

	private void OnNetworkConnectionChanged(SteamNetConnectionStatusChangedCallback_t param)
	{
		ulong steamID = param.m_info.m_identityRemote.GetSteamID64();
		ESteamNetworkingConnectionState eState = param.m_info.m_eState;
		uint hSteamListenSocket = param.m_info.m_hListenSocket.m_HSteamListenSocket;
		Debug.Log("NetworkGameClient::OnNetworkConnectionChanged: " + eState.ToString() + " remote ID " + steamID + " socket: " + hSteamListenSocket);
		if (eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
		{
			SteamNetworkingSockets.AcceptConnection(param.m_hConn);
		}
		else if (GetInServer() && (eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Dead || eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally))
		{
			GameMessage.Hide();
			DisconnectFromServer();
			GameLevelLoader.ReturnToMainMenu();
			LocalizationUtility.TryGetTranslation("Miscellaneous", "DisconnectedServer", null, out var translation);
			ConfirmationPopup.RequestConfirmation(translation, ConfirmationOption.Confirm());
		}
	}

	private void OnP2PSessionRequest(P2PSessionRequest_t pCallback)
	{
		CSteamID steamIDRemote = pCallback.m_steamIDRemote;
		Debug.LogError("Another player is trying to connect via Peer to Peer, this is likely undesireable and has been ignored. SteamIDRemote: " + steamIDRemote.ToString());
	}

	public static void OnPacket_PlayerList(GameByteStreamReader packetReader)
	{
		if (!Instance)
		{
			return;
		}
		Player.ClearPlayers();
		int num = packetReader.ReadUInt16();
		for (int i = 0; i < num; i++)
		{
			NetworkID networkID = packetReader.ReadNetworkID();
			packetReader.ReadByte();
			Team team = packetReader.ReadTeam();
			short kills = packetReader.ReadInt16();
			short deaths = packetReader.ReadInt16();
			int score = packetReader.ReadInt32();
			EAdminLevel adminLevel = (EAdminLevel)packetReader.ReadByte();
			Player player = Player.AddPlayer(networkID, "...");
			player.Team = team;
			player.Kills = kills;
			player.Deaths = deaths;
			player.Score = score;
			player.AdminLevel = adminLevel;
			if (networkID == GetPlayerID())
			{
				Player.CurrentPlayer = player;
			}
		}
	}

	public static void OnPacket_PlayerUnitList(GameByteStreamReader packetReader)
	{
		if (!Instance)
		{
			return;
		}
		for (int i = 0; i < Player.GetNumPlayers(); i++)
		{
			Player player = Player.Players[i];
			player.ControlledUnit = null;
			uint num = packetReader.ReadPackedUInt32();
			if (num == 0)
			{
				continue;
			}
			NetworkComponent netObject = NetworkComponent.GetNetObject(num);
			if ((bool)netObject)
			{
				Unit component = netObject.gameObject.GetComponent<Unit>();
				if ((bool)component)
				{
					player.ControlledUnit = component;
				}
			}
		}
	}

	public static void OnPacket_PlayerConnect(GameByteStreamReader packetReader)
	{
		if (!Instance)
		{
			return;
		}
		NetworkID networkID = packetReader.ReadNetworkID();
		int channel = packetReader.ReadByte();
		switch ((ENetworkPlayerConnectType)packetReader.ReadByte())
		{
		case ENetworkPlayerConnectType.InformConnect:
			Debug.Log("CLIENT - InformConnect");
			Instance.m_ServerID = networkID;
			Instance.m_Connecting = false;
			Instance.m_MaxPlayers = packetReader.ReadUInt16();
			Instance.m_ServerName = packetReader.ReadString();
			break;
		case ENetworkPlayerConnectType.InformDisconnect:
			Debug.Log("CLIENT - InformDisconnect");
			DisconnectFromServer();
			GameLevelLoader.ReturnToMainMenu();
			break;
		case ENetworkPlayerConnectType.Connected:
		{
			Debug.Log("CLIENT - Connected: " + SteamFriends.GetFriendPersonaName(networkID.SteamID) + ", Channel: " + channel);
			Player obj = Player.AddPlayer(networkID, "...");
			GameEvents.OnPlayerJoined?.Invoke(obj);
			break;
		}
		case ENetworkPlayerConnectType.Disconnected:
		{
			Debug.Log("CLIENT - Disconnected: " + SteamFriends.GetFriendPersonaName(networkID.SteamID) + ", Channel: " + channel);
			if ((bool)Player.CurrentPlayer && Player.CurrentPlayer == Player.FindPlayer(networkID, channel))
			{
				DisconnectFromServer();
				GameLevelLoader.ReturnToMainMenu();
				break;
			}
			Player player = Player.FindPlayer(networkID, channel);
			if ((bool)player)
			{
				GameEvents.OnPlayerLeft?.Invoke(player);
				Player.RemovePlayer(networkID);
			}
			break;
		}
		case ENetworkPlayerConnectType.WrongPassword:
		{
			Debug.Log("CLIENT - WrongPassword");
			GameMessage.Hide();
			DisconnectFromServer();
			LocalizationUtility.TryGetTranslation("Miscellaneous", "IncorrectPassword", null, out var translation2);
			ConfirmationPopup.RequestConfirmation(translation2, ConfirmationOption.Confirm());
			break;
		}
		case ENetworkPlayerConnectType.Full:
		{
			Debug.Log("CLIENT - Full");
			GameMessage.Hide();
			DisconnectFromServer();
			LocalizationUtility.TryGetTranslation("Miscellaneous", "FullServerMessage", null, out var translation3);
			ConfirmationPopup.RequestConfirmation(translation3, ConfirmationOption.Confirm());
			break;
		}
		case ENetworkPlayerConnectType.Kicked:
		{
			Debug.Log("CLIENT - Kicked");
			GameMessage.Hide();
			DisconnectFromServer();
			if (!Game.InMainMenu)
			{
				GameLevelLoader.ReturnToMainMenu();
			}
			LocalizationUtility.TryGetTranslation("Miscellaneous", "KickedFromServer", null, out var translation5);
			ConfirmationPopup.RequestConfirmation(translation5, ConfirmationOption.Confirm());
			break;
		}
		case ENetworkPlayerConnectType.Banned:
		{
			Debug.Log("CLIENT - Banned");
			GameMessage.Hide();
			DisconnectFromServer();
			if (!Game.InMainMenu)
			{
				GameLevelLoader.ReturnToMainMenu();
			}
			LocalizationUtility.TryGetTranslation("Miscellaneous", "BannedFromServer", null, out var translation4);
			ConfirmationPopup.RequestConfirmation(translation4, ConfirmationOption.Confirm());
			break;
		}
		case ENetworkPlayerConnectType.ChecksumFail:
		{
			Debug.Log("CLIENT - Checksum Failed");
			GameMessage.Hide();
			DisconnectFromServer();
			if (!Game.InMainMenu)
			{
				GameLevelLoader.ReturnToMainMenu();
			}
			LocalizationUtility.TryGetTranslation("Miscellaneous", "ValidationFailed", null, out var translation);
			ConfirmationPopup.RequestConfirmation(translation, ConfirmationOption.Confirm());
			ContentManager.QueueClientLoad = false;
			break;
		}
		case ENetworkPlayerConnectType.LevelLoaded:
		case ENetworkPlayerConnectType.InformWorkshopIDs:
		case ENetworkPlayerConnectType.InformChecksum:
			break;
		}
	}

	public static void OnPacket_ObjectCreate(GameByteStreamReader packetReader)
	{
		if (!Instance)
		{
			return;
		}
		uint netID = packetReader.ReadPackedUInt32();
		packetReader.SetNetID(netID);
		packetReader.SetFunction("NetworkGameClient::OnPacket_ObjectCreate");
		NetworkID networkID = packetReader.ReadNetworkID();
		int channel = packetReader.ReadByte();
		int num = (int)packetReader.ReadPackedUInt32();
		GameObject spawnablePrefab = GameDatabase.GetSpawnablePrefab(num);
		if (!spawnablePrefab)
		{
			Debug.LogError("Failed to find prefab - prefab index: " + num);
			return;
		}
		NetworkComponent netObject = NetworkComponent.GetNetObject(netID);
		if ((bool)netObject)
		{
			Debug.LogError("Network ID '" + netID + "' already used by '" + netObject.GetObjectName() + "'! Cannot create prefab - " + spawnablePrefab.ToString());
			return;
		}
		GameObject gameObject = PrefabPool.Instantiate(spawnablePrefab);
		if (!gameObject)
		{
			Debug.LogError("Failed to create new object from prefab - " + spawnablePrefab.ToString());
			return;
		}
		NetworkComponent component = gameObject.GetComponent<NetworkComponent>();
		if (!component)
		{
			Debug.LogError("Spawned prefab does not have a NetworkComponent! Deleting - " + gameObject.ToString());
			Object.Destroy(gameObject);
			return;
		}
		Player player = null;
		if (networkID != NetworkID.Nil)
		{
			player = Player.FindPlayer(networkID, channel);
			if (!player)
			{
				Debug.LogError("Cannot find player ID: " + networkID.ToString() + ", Channel: " + channel + " for spawned prefab - " + gameObject.ToString());
			}
		}
		component.SetNetID(netID);
		component.OwnerPlayer = player;
		component.SetNetSource((uint)num);
		component.Initialized = false;
		component.IsDynamic = true;
	}

	public static void OnPacket_LoadLevel(GameByteStreamReader packetReader)
	{
		if ((bool)Instance)
		{
			Instance.m_WaitingForFrameAfterLevelLoad = false;
			Instance.m_WaitingForServerLevelLoad = packetReader.ReadBool();
			Instance.m_LoadLevelFromServer = packetReader.ReadString();
			int num = packetReader.ReadByte() - 1;
			GameModeInfo gameMode = ((num >= 0) ? GameDatabase.Database.AllGameModes[num] : null);
			GameLevelLoader.StartLoadLevelCoroutine(Instance.m_LoadLevelFromServer, gameMode);
		}
	}

	public static void OnPacket_LoadLevelFinished()
	{
		if ((bool)Instance)
		{
			Instance.m_WaitingForServerLevelLoad = false;
		}
	}

	private void OnEnable()
	{
		Debug.Log("NetworkGameClient: OnEnable");
		if ((bool)Instance)
		{
			Object.Destroy(Instance.gameObject);
		}
		Instance = this;
		Object.DontDestroyOnLoad(gameObject);
		if (SteamManager.Initialized)
		{
			m_ServerID = NetworkID.Nil;
			m_PlayerID = NetworkID.CurrentUserID;
			Instance.m_OnConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(Instance.OnNetworkConnectionChanged);
			m_CallbackP2PSessionRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
		}
	}

	private void OnDisable()
	{
		Debug.Log("NetworkGameClient: OnDisable");
		DisconnectFromServer();
		if ((bool)NetStatus)
		{
			Object.Destroy(NetStatus);
		}
	}

	private void Update()
	{
		NetworkLayer.RetrieveNetworkData();
		if (GetInServer())
		{
			if (m_WaitingForFrameAfterLevelLoad)
			{
				m_WaitingForFrameAfterLevelLoad = false;
				Debug.Log("Update: Ready to play after loading level " + m_LoadLevelFromServer + ", informing server");
				InformLevelLoadedToServer();
				m_LoadLevelFromServer = "";
			}
			if (!NetStatus)
			{
				NetStatus = (GameObject)Object.Instantiate(Resources.Load("NetworkStatus", typeof(GameObject)));
			}
			if (!m_WaitingForServerLevelLoad && Player.CurrentPlayer.NetLastHeartbeatTime >= 60f)
			{
				GameLevelLoader.ReturnToMainMenu();
			}
			else if (m_WaitingForServerLevelLoad && Player.CurrentPlayer.NetLastHeartbeatTime >= 180f)
			{
				GameLevelLoader.ReturnToMainMenu();
			}
			m_TimeSinceHearbeat += Time.unscaledDeltaTime;
			if (m_TimeSinceHearbeat >= 0.2f)
			{
				m_TimeSinceHearbeat -= 0.2f;
				NetworkLayer.SendHeartbeat();
			}
		}
		else
		{
			if ((bool)NetStatus)
			{
				Object.Destroy(NetStatus);
			}
			m_TimeSinceHearbeat = 0f;
		}
	}

	private void OnApplicationQuit()
	{
		CloseClient();
	}

	private void OnDestroy()
	{
		OverrideManager.ClearAll();
	}
}
