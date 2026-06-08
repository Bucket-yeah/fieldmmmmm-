using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using SimpleJSON;

namespace RavenM
{
    public class LanLobbySystem : MonoBehaviour
    {
        public static LanLobbySystem instance;

        public static readonly string[] TEAM_NAMES  = { "Eagle", "Raven" };
        public static readonly Color[]  TEAM_COLORS = { new Color(0.35f, 0.65f, 1f), new Color(1f, 0.35f, 0.35f) };
        public const int MAX_TEAMS = 2;

        // ── Data classes ─────────────────────────────────────────────────

        [System.Serializable]
        public class LanLobbyInfo
        {
            public string   LobbyID;
            public string   HostName;
            public IPAddress HostIP;
            public int      HostPort     = 7777;
            public int      PlayerCount  = 1;
            public int      MaxPlayers   = 8;
            public long     CreatedTime;
            public long     LastUpdateTime;

            // Game settings (synced from host IAM)
            public string   MapName              = "Default";
            public int      MapIndex             = 0;
            public int      GameModeIndex        = 0;
            public string   GameModeName         = "Skirmish";
            public bool     NightMode            = false;
            public bool     PlayerHasAllWeapons  = false;
            public bool     ReverseMode          = false;
            public string   BotCount             = "16";
            public float    Balance              = 1f;
            public string   RespawnTime          = "120";
            public int      GameLength           = 1;
            public bool     NameTagsForTeamOnly  = true;
            public string   Mods                 = "";
        }

        [System.Serializable]
        public class LanPlayerInfo
        {
            public string    PlayerName;
            public Guid      PlayerGUID;
            public IPEndPoint PlayerEndPoint;
            public int       Team;
            public bool      IsHost;
            public bool      IsReady;
            public long      JoinTime;
        }

        // ── State ────────────────────────────────────────────────────────

        public bool   IsHostingLobby  = false;
        public bool   IsInLobby       = false;
        public bool   LanReadyToPlay  = false;
        public string CurrentLobbyID  = string.Empty;

        public int    MyTeam          = 0;
        public string MyPlayerName    = "Player";

        // GUID string → team int (populated at GAME_START)
        public Dictionary<string, int> TeamAssignments = new Dictionary<string, int>();

        // Client-side mirror of the player list (set by LOBBY_STATE)
        public List<LanPlayerInfo> CachedPlayerList = new List<LanPlayerInfo>();

        public bool         HasModMismatch = false;
        public List<string> MissingMods    = new List<string>();

        private LanLobbyInfo _currentLobbyInfo;
        private Dictionary<string, LanPlayerInfo> _lobbyPlayers
            = new Dictionary<string, LanPlayerInfo>();
        private List<LanLobbyInfo> _discoveredLobbies = new List<LanLobbyInfo>();

        private float _lobbyUpdateTimer = 0f;
        private const float LOBBY_UPDATE_INTERVAL = 2f;

        private bool  _isDiscovering    = false;
        private float _discoveryEndTime = 0f;
        private const float DISCOVERY_DURATION = 3f;

        public bool IsDiscovering => _isDiscovering;

        // ── Unity lifecycle ───────────────────────────────────────────────

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (LanTransport.instance == null)
            {
                var go = new GameObject("LanTransport");
                go.AddComponent<LanTransport>();
            }
        }

        private void Update()
        {
            if (LanTransport.instance != null && LanTransport.instance.IsRunning)
            {
                var packets = LanTransport.instance.GetReceivedPackets();
                foreach (var pkt in packets) ProcessIncomingPacket(pkt);
            }

            if (_isDiscovering && Time.time >= _discoveryEndTime)
            {
                _isDiscovering = false;
                Plugin.logger.LogInfo($"LAN discovery done. Found: {_discoveredLobbies.Count}");
                if (!IsInLobby && LanTransport.instance != null && LanTransport.instance.IsRunning)
                    LanTransport.instance.Shutdown();
            }

            // Client auto-starts when GAME_START received
            if (LanReadyToPlay && !IngameNetManager.instance.IsClient)
            {
                LanReadyToPlay = false;
                // Set name-tag visibility BEFORE scene loads so GameUI.Awake picks it up
                LobbySystem.instance.nameTagsEnabled = true;
                LobbySystem.instance.nameTagsForTeamOnly =
                    _currentLobbyInfo?.NameTagsForTeamOnly ?? true;
                // Set own team on the IAM dropdown
                if (InstantActionMaps.instance != null)
                {
                    InstantActionMaps.instance.teamDropdown.value = MyTeam;
                    InstantActionMaps.instance.botNumberField.text = "0";
                }
                InstantActionMaps.instance.StartGame();
            }

            if (!IsInLobby) return;

            _lobbyUpdateTimer += Time.deltaTime;
            if (_lobbyUpdateTimer >= LOBBY_UPDATE_INTERVAL)
            {
                _lobbyUpdateTimer = 0f;
                if (IsHostingLobby) BroadcastLobbyState();
                else                SendHeartbeat();
            }
        }

        // ── Lobby management ─────────────────────────────────────────────

        public void CreateLobby(string playerName, int maxPlayers = 8)
        {
            if (IsHostingLobby || IsInLobby) { Plugin.logger.LogWarning("Already in a lobby."); return; }
            try
            {
                MyPlayerName   = playerName;
                CurrentLobbyID = Guid.NewGuid().ToString().Substring(0, 8);

                _currentLobbyInfo = new LanLobbyInfo
                {
                    LobbyID      = CurrentLobbyID,
                    HostName     = playerName,
                    HostIP       = GetLocalIPAddress(),
                    HostPort     = 7777,
                    PlayerCount  = 1,
                    MaxPlayers   = maxPlayers,
                    CreatedTime  = DateTime.Now.Ticks,
                    LastUpdateTime = DateTime.Now.Ticks,
                    NameTagsForTeamOnly = true,
                };
                ReadSettingsFromIAM(_currentLobbyInfo);

                _lobbyPlayers["HOST"] = new LanPlayerInfo
                {
                    PlayerName    = playerName,
                    PlayerGUID    = IngameNetManager.instance.OwnGUID,
                    PlayerEndPoint = null,
                    Team          = MyTeam,
                    IsHost        = true,
                    IsReady       = true,
                    JoinTime      = DateTime.Now.Ticks,
                };

                IsHostingLobby = true;
                IsInLobby      = true;

                LanTransport.instance.StartServer();
                LanTransport.instance.IsHosting = true;

                Plugin.logger.LogInfo($"LAN lobby created: {CurrentLobbyID} ({_currentLobbyInfo.HostIP})");
                ChatManager.instance.PushLobbyChatMessage(
                    $"LAN lobby created. ID: {CurrentLobbyID}  IP: {_currentLobbyInfo.HostIP}");
            }
            catch (Exception ex)
            {
                Plugin.logger.LogError($"CreateLobby error: {ex.Message}");
                IsHostingLobby = false;
                IsInLobby      = false;
            }
        }

        public void JoinLobby(string hostIP, int port, string playerName)
        {
            if (IsInLobby) { Plugin.logger.LogWarning("Already in a lobby."); return; }
            try
            {
                MyPlayerName = playerName;
                IPAddress ipAddr = IPAddress.Parse(hostIP);
                CurrentLobbyID   = "CLIENT_" + Guid.NewGuid().ToString().Substring(0, 8);

                _currentLobbyInfo = new LanLobbyInfo { HostIP = ipAddr, HostPort = port };

                IsInLobby      = true;
                IsHostingLobby = false;
                HasModMismatch = false;
                MissingMods.Clear();

                LanTransport.instance.StartServer();
                LanTransport.instance.IsHosting = false;

                SendLobbyJoinRequest(new IPEndPoint(ipAddr, port), playerName);

                Plugin.logger.LogInfo($"Joining LAN lobby at {hostIP}:{port}");
                ChatManager.instance.PushLobbyChatMessage($"Connecting to {hostIP}...");
            }
            catch (Exception ex)
            {
                Plugin.logger.LogError($"JoinLobby error: {ex.Message}");
                IsInLobby     = false;
                _currentLobbyInfo = null;
            }
        }

        public void LeaveLobby()
        {
            if (!IsInLobby) return;
            try
            {
                if (IsHostingLobby) BroadcastLobbyClose();
                else                SendLobbyLeaveNotification();

                _currentLobbyInfo = null;
                _lobbyPlayers.Clear();
                CachedPlayerList.Clear();
                CurrentLobbyID  = string.Empty;
                IsInLobby       = false;
                IsHostingLobby  = false;
                HasModMismatch  = false;
                MissingMods.Clear();
                TeamAssignments.Clear();

                LanTransport.instance.Shutdown();
                ChatManager.instance.PushLobbyChatMessage("Left LAN lobby.");
            }
            catch (Exception ex) { Plugin.logger.LogError($"LeaveLobby error: {ex.Message}"); }
        }

        // Set own team and notify host
        public void SetMyTeam(int team)
        {
            if (team < 0 || team >= MAX_TEAMS) return;
            MyTeam = team;
            if (IsHostingLobby)
            {
                if (_lobbyPlayers.ContainsKey("HOST"))
                    _lobbyPlayers["HOST"].Team = team;
            }
            else if (IsInLobby)
            {
                SendPlayerUpdate();
            }
        }

        // ── Discovery ─────────────────────────────────────────────────────

        public void StartDiscovery()
        {
            if (_isDiscovering) return;
            _discoveredLobbies.Clear();
            _isDiscovering    = true;
            _discoveryEndTime = Time.time + DISCOVERY_DURATION;
            try
            {
                if (LanTransport.instance == null) return;
                if (!LanTransport.instance.IsRunning)
                    LanTransport.instance.StartServer();
                LanTransport.instance.SendBroadcast(Encoding.UTF8.GetBytes("RAVENM_DISCOVERY"), 7778);
            }
            catch (Exception ex)
            {
                Plugin.logger.LogError($"Discovery error: {ex.Message}");
                _isDiscovering = false;
            }
        }

        public List<LanLobbyInfo>   GetDiscoveredLobbies() => new List<LanLobbyInfo>(_discoveredLobbies);
        public List<LanPlayerInfo>  GetLobbyPlayers()      => new List<LanPlayerInfo>(_lobbyPlayers.Values);
        public LanLobbyInfo         GetCurrentLobbyInfo()  => _currentLobbyInfo;

        // ── Incoming packet dispatch ──────────────────────────────────────

        private void ProcessIncomingPacket(LanTransport.LanPacket packet)
        {
            try
            {
                string message = Encoding.UTF8.GetString(packet.Data, 0, packet.Size);
                if (message.StartsWith("RAVENM_SERVER:"))
                {
                    if (_isDiscovering) ProcessDiscoveryResponse(packet.Source, message);
                    return;
                }

                var node = JSON.Parse(message);
                if (node == null) return;

                switch ((string)node["type"])
                {
                    case "JOIN_REQUEST":
                        if (IsHostingLobby) HandleJoinRequest(packet.Source, node); break;
                    case "JOIN_ACCEPT":
                        if (IsInLobby && !IsHostingLobby) HandleJoinAccept(node); break;
                    case "JOIN_REJECT":
                        if (IsInLobby && !IsHostingLobby) HandleJoinReject(node); break;
                    case "LEAVE_NOTIFICATION":
                        if (IsHostingLobby) HandleLeaveNotification(node); break;
                    case "HEARTBEAT":
                        if (IsHostingLobby) HandleHeartbeat(packet.Source, node); break;
                    case "LOBBY_STATE":
                        if (IsInLobby && !IsHostingLobby) HandleLobbyState(node); break;
                    case "LOBBY_CLOSE":
                        if (IsInLobby && !IsHostingLobby) HandleLobbyClose(node); break;
                    case "GAME_START":
                        if (IsInLobby && !IsHostingLobby) HandleGameStart(node); break;
                    case "PLAYER_UPDATE":
                        if (IsHostingLobby) HandlePlayerUpdate(packet.Source, node); break;
                }
            }
            catch (Exception ex)
            {
                Plugin.logger.LogWarning($"Packet error: {ex.Message}");
            }
        }

        private void ProcessDiscoveryResponse(IPEndPoint source, string message)
        {
            string guid = message.Substring("RAVENM_SERVER:".Length).Trim();
            foreach (var e in _discoveredLobbies)
                if (e.HostIP != null && e.HostIP.Equals(source.Address)) return;

            _discoveredLobbies.Add(new LanLobbyInfo
            {
                LobbyID       = guid,
                HostName      = source.Address.ToString(),
                HostIP        = source.Address,
                HostPort      = source.Port,
                LastUpdateTime = DateTime.Now.Ticks,
            });
        }

        private void HandleJoinRequest(IPEndPoint source, JSONNode node)
        {
            if (_currentLobbyInfo == null) return;
            if (_lobbyPlayers.Count >= _currentLobbyInfo.MaxPlayers)
            {
                var rej = new JSONObject();
                rej["type"] = "JOIN_REJECT"; rej["reason"] = "Lobby is full";
                LanTransport.instance.SendPacket(source, Encoding.UTF8.GetBytes(rej.ToString()));
                return;
            }

            string playerName = node["playerName"];
            Guid   playerGUID = Guid.TryParse(node["playerGUID"], out Guid g) ? g : Guid.NewGuid();
            int    team       = Mathf.Clamp(node["team"].AsInt, 0, MAX_TEAMS - 1);
            string key        = source.ToString();

            _lobbyPlayers[key] = new LanPlayerInfo
            {
                PlayerName    = playerName,
                PlayerGUID    = playerGUID,
                PlayerEndPoint = source,
                Team          = team,
                IsHost        = false,
                IsReady       = false,
                JoinTime      = DateTime.Now.Ticks,
            };
            _currentLobbyInfo.PlayerCount = _lobbyPlayers.Count;

            var acc = new JSONObject();
            acc["type"] = "JOIN_ACCEPT"; acc["lobbyID"] = CurrentLobbyID;
            LanTransport.instance.SendPacket(source, Encoding.UTF8.GetBytes(acc.ToString()));
            LanTransport.instance.RegisterConnection(source, playerGUID);

            Plugin.logger.LogInfo($"LAN: {playerName} joined ({source}) team={team}");
            ChatManager.instance.PushLobbyChatMessage($"{playerName} joined the lobby");

            // Send current state immediately to the new player
            BroadcastLobbyState();
        }

        private void HandleJoinAccept(JSONNode node)
        {
            CurrentLobbyID = node["lobbyID"];
            Plugin.logger.LogInfo($"Accepted into lobby {CurrentLobbyID}");
            ChatManager.instance.PushLobbyChatMessage($"Joined LAN lobby {CurrentLobbyID}");
            // Immediately send our team to host
            SendPlayerUpdate();
        }

        private void HandleJoinReject(JSONNode node)
        {
            Plugin.logger.LogWarning($"Rejected: {(string)node["reason"]}");
            ChatManager.instance.PushLobbyChatMessage($"Join rejected: {(string)node["reason"]}");
            IsInLobby = false;
            _currentLobbyInfo = null;
            LanTransport.instance.Shutdown();
        }

        private void HandleLeaveNotification(JSONNode node)
        {
            string guidStr = node["playerGUID"];
            string remove  = null;
            foreach (var kv in _lobbyPlayers)
                if (kv.Value.PlayerGUID.ToString() == guidStr) { remove = kv.Key; break; }
            if (remove == null) return;
            string name = _lobbyPlayers[remove].PlayerName;
            _lobbyPlayers.Remove(remove);
            if (_currentLobbyInfo != null) _currentLobbyInfo.PlayerCount = _lobbyPlayers.Count;
            ChatManager.instance.PushLobbyChatMessage($"{name} left the lobby");
        }

        private void HandleHeartbeat(IPEndPoint source, JSONNode node)
        {
            string key = source.ToString();
            if (_lobbyPlayers.ContainsKey(key))
                LanTransport.instance.RegisterConnection(source, _lobbyPlayers[key].PlayerGUID);
        }

        private void HandlePlayerUpdate(IPEndPoint source, JSONNode node)
        {
            string key = source.ToString();
            if (!_lobbyPlayers.ContainsKey(key)) return;

            int    team = Mathf.Clamp(node["team"].AsInt, 0, MAX_TEAMS - 1);
            string name = node["name"];
            _lobbyPlayers[key].Team = team;
            if (!string.IsNullOrEmpty(name)) _lobbyPlayers[key].PlayerName = name;

            Plugin.logger.LogInfo($"LAN: {_lobbyPlayers[key].PlayerName} updated team → {team}");
        }

        private void HandleLobbyState(JSONNode node)
        {
            if (_currentLobbyInfo == null) _currentLobbyInfo = new LanLobbyInfo();

            _currentLobbyInfo.LobbyID             = node["lobbyID"];
            _currentLobbyInfo.HostName             = node["hostName"];
            _currentLobbyInfo.PlayerCount          = node["playerCount"].AsInt;
            _currentLobbyInfo.MaxPlayers           = node["maxPlayers"].AsInt;
            _currentLobbyInfo.MapName              = node["mapName"];
            _currentLobbyInfo.MapIndex             = node["mapIndex"].AsInt;
            _currentLobbyInfo.GameModeIndex        = node["gameModeIndex"].AsInt;
            _currentLobbyInfo.GameModeName         = node["gameModeName"];
            _currentLobbyInfo.NightMode            = node["nightMode"].AsBool;
            _currentLobbyInfo.PlayerHasAllWeapons  = node["playerHasAllWeapons"].AsBool;
            _currentLobbyInfo.ReverseMode          = node["reverseMode"].AsBool;
            _currentLobbyInfo.BotCount             = node["botCount"];
            _currentLobbyInfo.Balance              = node["balance"].AsFloat;
            _currentLobbyInfo.RespawnTime          = node["respawnTime"];
            _currentLobbyInfo.GameLength           = node["gameLength"].AsInt;
            _currentLobbyInfo.NameTagsForTeamOnly  = node["nameTagsForTeamOnly"].AsBool;
            _currentLobbyInfo.Mods                 = node["mods"];
            _currentLobbyInfo.LastUpdateTime       = DateTime.Now.Ticks;

            // Mirror settings on the IAM UI so the client sees what the host selected
            ApplySettingsToIAM(_currentLobbyInfo);

            // Rebuild cached player list
            CachedPlayerList.Clear();
            var arr = node["players"].AsArray;
            if (arr != null)
            {
                foreach (JSONNode p in arr)
                {
                    CachedPlayerList.Add(new LanPlayerInfo
                    {
                        PlayerName = p["name"],
                        PlayerGUID = Guid.TryParse(p["guid"], out Guid pg) ? pg : Guid.Empty,
                        Team       = p["team"].AsInt,
                        IsHost     = p["isHost"].AsBool,
                        IsReady    = p["isReady"].AsBool,
                    });
                }
            }

            // Mod mismatch check
            HasModMismatch = false;
            MissingMods.Clear();
            if (!string.IsNullOrEmpty(_currentLobbyInfo.Mods))
            {
                var localMods = new HashSet<string>(
                    ModManager.instance.GetActiveMods().Select(m => m.title));
                foreach (string m in _currentLobbyInfo.Mods.Split(','))
                {
                    if (!string.IsNullOrEmpty(m) && !localMods.Contains(m))
                    {
                        HasModMismatch = true;
                        MissingMods.Add(m);
                    }
                }
            }
        }

        private void HandleLobbyClose(JSONNode node)
        {
            Plugin.logger.LogInfo("LAN: Lobby closed by host");
            ChatManager.instance.PushLobbyChatMessage("Lobby closed by host");
            _currentLobbyInfo = null;
            _lobbyPlayers.Clear();
            CachedPlayerList.Clear();
            CurrentLobbyID = string.Empty;
            IsInLobby      = false;
            IsHostingLobby = false;
            LanTransport.instance.Shutdown();
        }

        private void HandleGameStart(JSONNode node)
        {
            Plugin.logger.LogInfo("LAN: Received GAME_START");
            if (_currentLobbyInfo != null)
            {
                string ipStr = node["hostIP"];
                if (!string.IsNullOrEmpty(ipStr) && IPAddress.TryParse(ipStr, out IPAddress ip))
                    _currentLobbyInfo.HostIP = ip;
            }

            // Store team assignments
            TeamAssignments.Clear();
            var teamsArr = node["teams"].AsArray;
            if (teamsArr != null)
            {
                foreach (JSONNode t in teamsArr)
                {
                    TeamAssignments[t["guid"]] = t["team"].AsInt;
                    Plugin.logger.LogInfo($"Team: {t["name"]} → team {t["team"].AsInt}");
                }
            }

            // Resolve own team
            string ownGuid = IngameNetManager.instance.OwnGUID.ToString();
            if (TeamAssignments.TryGetValue(ownGuid, out int myTeam))
                MyTeam = myTeam;

            LanReadyToPlay = true;
        }

        // ── Outgoing packets ──────────────────────────────────────────────

        private void SendLobbyJoinRequest(IPEndPoint ep, string playerName)
        {
            var j = new JSONObject();
            j["type"]       = "JOIN_REQUEST";
            j["playerName"] = playerName;
            j["playerGUID"] = IngameNetManager.instance.OwnGUID.ToString();
            j["team"]       = MyTeam;
            j["timestamp"]  = DateTime.Now.Ticks.ToString();
            LanTransport.instance.SendPacket(ep, Encoding.UTF8.GetBytes(j.ToString()));
        }

        private void SendLobbyLeaveNotification()
        {
            if (_currentLobbyInfo == null) return;
            var j = new JSONObject();
            j["type"]       = "LEAVE_NOTIFICATION";
            j["playerGUID"] = IngameNetManager.instance.OwnGUID.ToString();
            LanTransport.instance.SendPacket(
                new IPEndPoint(_currentLobbyInfo.HostIP, _currentLobbyInfo.HostPort),
                Encoding.UTF8.GetBytes(j.ToString()));
        }

        private void SendHeartbeat()
        {
            if (_currentLobbyInfo == null) return;
            var j = new JSONObject();
            j["type"]       = "HEARTBEAT";
            j["playerGUID"] = IngameNetManager.instance.OwnGUID.ToString();
            LanTransport.instance.SendPacket(
                new IPEndPoint(_currentLobbyInfo.HostIP, _currentLobbyInfo.HostPort),
                Encoding.UTF8.GetBytes(j.ToString()));
        }

        public void SendPlayerUpdate()
        {
            if (!IsInLobby || IsHostingLobby || _currentLobbyInfo == null) return;
            var j = new JSONObject();
            j["type"] = "PLAYER_UPDATE";
            j["team"] = MyTeam;
            j["name"] = MyPlayerName;
            LanTransport.instance.SendPacket(
                new IPEndPoint(_currentLobbyInfo.HostIP, _currentLobbyInfo.HostPort),
                Encoding.UTF8.GetBytes(j.ToString()));
        }

        private void BroadcastLobbyState()
        {
            if (_currentLobbyInfo == null) return;
            ReadSettingsFromIAM(_currentLobbyInfo);

            var j = new JSONObject();
            j["type"]               = "LOBBY_STATE";
            j["lobbyID"]            = CurrentLobbyID;
            j["hostName"]           = _currentLobbyInfo.HostName;
            j["playerCount"]        = _lobbyPlayers.Count;
            j["maxPlayers"]         = _currentLobbyInfo.MaxPlayers;
            j["mapName"]            = _currentLobbyInfo.MapName;
            j["mapIndex"]           = _currentLobbyInfo.MapIndex;
            j["gameModeIndex"]      = _currentLobbyInfo.GameModeIndex;
            j["gameModeName"]       = _currentLobbyInfo.GameModeName;
            j["nightMode"]          = _currentLobbyInfo.NightMode;
            j["playerHasAllWeapons"] = _currentLobbyInfo.PlayerHasAllWeapons;
            j["reverseMode"]        = _currentLobbyInfo.ReverseMode;
            j["botCount"]           = _currentLobbyInfo.BotCount;
            j["balance"]            = _currentLobbyInfo.Balance.ToString("F2", CultureInfo.InvariantCulture);
            j["respawnTime"]        = _currentLobbyInfo.RespawnTime;
            j["gameLength"]         = _currentLobbyInfo.GameLength;
            j["nameTagsForTeamOnly"] = _currentLobbyInfo.NameTagsForTeamOnly;
            j["mods"]               = _currentLobbyInfo.Mods;

            var playersArr = new JSONArray();
            foreach (var kv in _lobbyPlayers)
            {
                var p = new JSONObject();
                p["name"]    = kv.Value.PlayerName;
                p["guid"]    = kv.Value.PlayerGUID.ToString();
                p["team"]    = kv.Value.Team;
                p["isHost"]  = kv.Value.IsHost;
                p["isReady"] = kv.Value.IsReady;
                playersArr.Add(p);
            }
            j["players"] = playersArr;

            byte[] data = Encoding.UTF8.GetBytes(j.ToString());
            foreach (var p in _lobbyPlayers.Values)
            {
                if (p.PlayerEndPoint == null) continue;
                try { LanTransport.instance.SendPacket(p.PlayerEndPoint, data); }
                catch (Exception ex) { Plugin.logger.LogWarning("BroadcastLobbyState: " + ex.Message); }
            }
        }

        public void BroadcastGameStart()
        {
            if (!IsHostingLobby || _currentLobbyInfo == null) return;

            var j = new JSONObject();
            j["type"]   = "GAME_START";
            j["hostIP"] = _currentLobbyInfo.HostIP?.ToString() ?? "";

            var teamsArr = new JSONArray();
            TeamAssignments.Clear();
            foreach (var kv in _lobbyPlayers)
            {
                var t = new JSONObject();
                t["guid"] = kv.Value.PlayerGUID.ToString();
                t["team"] = kv.Value.Team;
                t["name"] = kv.Value.PlayerName;
                teamsArr.Add(t);
                TeamAssignments[kv.Value.PlayerGUID.ToString()] = kv.Value.Team;
            }
            j["teams"] = teamsArr;

            byte[] data = Encoding.UTF8.GetBytes(j.ToString());
            int sent = 0;
            foreach (var p in _lobbyPlayers.Values)
            {
                if (p.PlayerEndPoint == null) continue;
                try { LanTransport.instance.SendPacket(p.PlayerEndPoint, data); sent++; }
                catch (Exception ex) { Plugin.logger.LogWarning("BroadcastGameStart: " + ex.Message); }
            }
            Plugin.logger.LogInfo($"LAN: GAME_START sent to {sent} players");
        }

        private void BroadcastLobbyClose()
        {
            var j = new JSONObject();
            j["type"]    = "LOBBY_CLOSE";
            j["lobbyID"] = CurrentLobbyID;
            LanTransport.instance.SendBroadcast(Encoding.UTF8.GetBytes(j.ToString()));
        }

        // ── IAM helpers ───────────────────────────────────────────────────

        private void ReadSettingsFromIAM(LanLobbyInfo info)
        {
            if (InstantActionMaps.instance == null) return;
            try
            {
                var iam = InstantActionMaps.instance;
                info.GameModeIndex       = iam.gameModeDropdown.value;
                info.GameModeName        = iam.gameModeDropdown.options.Count > info.GameModeIndex
                    ? iam.gameModeDropdown.options[info.GameModeIndex].text : "Unknown";
                info.NightMode           = iam.nightToggle.isOn;
                info.PlayerHasAllWeapons = iam.playerHasAllWeaponsToggle.isOn;
                info.ReverseMode         = iam.reverseToggle.isOn;
                info.BotCount            = iam.botNumberField.text;
                info.Balance             = iam.balanceSlider.value;
                info.RespawnTime         = iam.respawnTimeField.text;
                info.GameLength          = iam.gameLengthDropdown.value;
                info.MapIndex            = iam.mapDropdown.value;
                info.MapName             = iam.mapDropdown.options.Count > info.MapIndex
                    ? iam.mapDropdown.options[info.MapIndex].text : "Unknown";

                // Active workshop mods by title
                info.Mods = string.Join(",",
                    ModManager.instance.GetActiveMods()
                        .Where(m => m.workshopItemId.ToString() != "0")
                        .Select(m => m.title));
            }
            catch (Exception ex) { Plugin.logger.LogWarning("ReadSettingsFromIAM: " + ex.Message); }
        }

        private void ApplySettingsToIAM(LanLobbyInfo info)
        {
            if (InstantActionMaps.instance == null) return;
            try
            {
                var iam = InstantActionMaps.instance;
                iam.gameModeDropdown.value = info.GameModeIndex;
                iam.nightToggle.isOn       = info.NightMode;
                iam.playerHasAllWeaponsToggle.isOn = info.PlayerHasAllWeapons;
                iam.reverseToggle.isOn     = info.ReverseMode;
                iam.botNumberField.text    = info.BotCount;
                iam.balanceSlider.value    = info.Balance;
                iam.respawnTimeField.text  = info.RespawnTime;
                iam.gameLengthDropdown.value = info.GameLength;
                if (info.MapIndex >= 0 && info.MapIndex < iam.mapDropdown.options.Count)
                    iam.mapDropdown.value  = info.MapIndex;
                // Keep player's own team (don't override with host's team)
                iam.teamDropdown.value     = MyTeam;
            }
            catch (Exception ex) { Plugin.logger.LogWarning("ApplySettingsToIAM: " + ex.Message); }
        }

        private IPAddress GetLocalIPAddress()
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                s.Connect("8.8.8.8", 65530);
                return (s.LocalEndPoint as IPEndPoint).Address;
            }
        }
    }
}
