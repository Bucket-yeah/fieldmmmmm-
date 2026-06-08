using System.Collections.Generic;
using System.Net;
using UnityEngine;

namespace RavenM
{
    public class LanLobbyUI : MonoBehaviour
    {
        private string _playerName  = "Player";
        private string _hostIP      = "192.168.1.100";
        private string _hostPort    = "7777";
        private string _maxPlayers  = "8";
        private bool   _showLanMenu = false;

        private Vector2 _menuScroll  = Vector2.zero;
        private Vector2 _lobbyScroll = Vector2.zero;

        private static readonly string[] GAME_LENGTH_NAMES = { "Short", "Medium", "Long" };

        // Cached team colors/names from LanLobbySystem
        private static readonly string[] TEAM_NAMES  = LanLobbySystem.TEAM_NAMES;
        private static readonly Color[]  TEAM_COLORS = LanLobbySystem.TEAM_COLORS;

        private void OnGUI()
        {
            if (LanLobbySystem.instance == null) return;

            bool inLanLobby = LanLobbySystem.instance.IsInLobby;
            bool inGame     = GameManager.IsIngame();
            bool inSteam    = LobbySystem.instance.InLobby;

            if (!inSteam && !inGame)
            {
                if (inLanLobby)
                {
                    DrawInLobbyUI();
                }
                else
                {
                    if (GUI.Button(new Rect(Screen.width - 150, 10, 140, 30), "LAN Mode"))
                        _showLanMenu = !_showLanMenu;
                    if (_showLanMenu) DrawLanMenu();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Pre-lobby menu: create / join / discover
        // ─────────────────────────────────────────────────────────────────

        private void DrawLanMenu()
        {
            GUILayout.BeginArea(new Rect(Screen.width - 410, 50, 400, 540));
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.Label("═══ LAN MULTIPLAYER ═══");
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Player Name:", GUILayout.Width(110));
            _playerName = GUILayout.TextField(_playerName, GUILayout.Width(260));
            GUILayout.EndHorizontal();

            // ── CREATE ────────────────────────────────────────────────────
            GUILayout.Space(6);
            GUILayout.Label("─── CREATE LOBBY ───");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Max Players:", GUILayout.Width(110));
            _maxPlayers = GUILayout.TextField(_maxPlayers, GUILayout.Width(260));
            GUILayout.EndHorizontal();

            if (GUILayout.Button("CREATE LAN LOBBY", GUILayout.Height(30)))
            {
                if (int.TryParse(_maxPlayers, out int mp) && mp >= 2 && mp <= 32)
                {
                    LanLobbySystem.instance.MyPlayerName = _playerName;
                    LanLobbySystem.instance.CreateLobby(_playerName, mp);
                    _showLanMenu = false;
                }
                else
                    ChatManager.instance.PushLobbyChatMessage("Max players must be 2-32");
            }

            // ── JOIN BY IP ────────────────────────────────────────────────
            GUILayout.Space(6);
            GUILayout.Label("─── JOIN BY IP ───");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host IP:", GUILayout.Width(110));
            _hostIP = GUILayout.TextField(_hostIP, GUILayout.Width(260));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port:", GUILayout.Width(110));
            _hostPort = GUILayout.TextField(_hostPort, GUILayout.Width(260));
            GUILayout.EndHorizontal();

            if (GUILayout.Button("JOIN", GUILayout.Height(30)))
            {
                if (IPAddress.TryParse(_hostIP, out _) && int.TryParse(_hostPort, out int port))
                {
                    LanLobbySystem.instance.MyPlayerName = _playerName;
                    LanLobbySystem.instance.JoinLobby(_hostIP, port, _playerName);
                    _showLanMenu = false;
                }
                else
                    ChatManager.instance.PushLobbyChatMessage("Invalid IP or port");
            }

            // ── DISCOVER ──────────────────────────────────────────────────
            GUILayout.Space(6);
            GUILayout.Label("─── DISCOVER LOBBIES ───");

            bool scanning = LanLobbySystem.instance.IsDiscovering;
            GUI.enabled = !scanning;
            if (GUILayout.Button(scanning ? "SCANNING..." : "SCAN NETWORK", GUILayout.Height(28)))
                LanLobbySystem.instance.StartDiscovery();
            GUI.enabled = true;

            var found = LanLobbySystem.instance.GetDiscoveredLobbies();
            GUILayout.Label(scanning ? "Scanning…" : $"Lobbies found: {found.Count}");

            _menuScroll = GUILayout.BeginScrollView(_menuScroll, GUILayout.Height(120));
            foreach (var lobby in found)
            {
                string lbl = $"{lobby.HostName} — {lobby.PlayerCount}/{lobby.MaxPlayers}  {lobby.MapName}";
                if (GUILayout.Button(lbl, GUILayout.Height(26)))
                {
                    LanLobbySystem.instance.MyPlayerName = _playerName;
                    LanLobbySystem.instance.JoinLobby(lobby.HostIP.ToString(), lobby.HostPort, _playerName);
                    _showLanMenu = false;
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            if (GUILayout.Button("CLOSE", GUILayout.Height(26)))
                _showLanMenu = false;

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // ─────────────────────────────────────────────────────────────────
        // In-lobby panel
        // ─────────────────────────────────────────────────────────────────

        private void DrawInLobbyUI()
        {
            var sys    = LanLobbySystem.instance;
            var info   = sys.GetCurrentLobbyInfo();
            bool isHost = sys.IsHostingLobby;

            float panelW = 500f;
            float panelH = Mathf.Min(Screen.height - 60f, 620f);
            float panelX = Screen.width  - panelW - 10f;
            float panelY = 10f;

            GUILayout.BeginArea(new Rect(panelX, panelY, panelW, panelH));
            GUILayout.BeginVertical(GUI.skin.box);

            // Header
            string role  = isHost ? "HOST" : "GUEST";
            string title = $"═══ LAN LOBBY ({role}) ═══";
            GUILayout.Label(title);
            if (info != null)
                GUILayout.Label($"  ID: {info.LobbyID ?? sys.CurrentLobbyID}   Players: {info.PlayerCount}/{info.MaxPlayers}");

            GUILayout.Space(4);

            _lobbyScroll = GUILayout.BeginScrollView(_lobbyScroll, GUILayout.ExpandHeight(true));

            // ── PLAYERS ───────────────────────────────────────────────────
            GUILayout.Label("─── PLAYERS ───");

            List<LanLobbySystem.LanPlayerInfo> players = isHost
                ? sys.GetLobbyPlayers()
                : sys.CachedPlayerList;

            foreach (var p in players)
            {
                bool isSelf   = (p.PlayerGUID == IngameNetManager.instance.OwnGUID);
                Color teamCol = (p.Team >= 0 && p.Team < TEAM_COLORS.Length)
                    ? TEAM_COLORS[p.Team] : Color.white;
                string teamName = (p.Team >= 0 && p.Team < TEAM_NAMES.Length)
                    ? TEAM_NAMES[p.Team] : p.Team.ToString();

                GUILayout.BeginHorizontal();

                // Star for host, arrow for self
                string prefix = p.IsHost ? "★ " : (isSelf ? "► " : "  ");
                string label  = isSelf ? $"{prefix}{p.PlayerName} (you)" : $"{prefix}{p.PlayerName}";

                // Coloured name
                var prevColor = GUI.contentColor;
                GUI.contentColor = teamCol;
                GUILayout.Label(label, GUILayout.Width(200));
                GUI.contentColor = prevColor;

                // Team badge
                GUI.contentColor = teamCol;
                GUILayout.Label($"[{teamName}]", GUILayout.Width(65));
                GUI.contentColor = prevColor;

                // Team selection buttons (own player only)
                if (isSelf)
                {
                    for (int t = 0; t < LanLobbySystem.MAX_TEAMS; t++)
                    {
                        bool selected = (sys.MyTeam == t);
                        GUI.backgroundColor = selected ? TEAM_COLORS[t] : new Color(0.25f, 0.25f, 0.25f);
                        if (GUILayout.Button(TEAM_NAMES[t], GUILayout.Width(58)))
                            sys.SetMyTeam(t);
                        GUI.backgroundColor = Color.white;
                    }
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);

            // ── SETTINGS ─────────────────────────────────────────────────
            GUILayout.Label("─── SETTINGS ───");

            if (info != null)
            {
                if (isHost)
                {
                    // Host: editable toggle for team-only names; all other settings are in IAM UI
                    GUILayout.Label("(Map / bots / mode — use the panel below)");
                    GUILayout.Space(4);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Enemy names hidden:", GUILayout.Width(160));
                    bool newFlag = GUILayout.Toggle(info.NameTagsForTeamOnly, info.NameTagsForTeamOnly ? "YES" : "NO");
                    if (newFlag != info.NameTagsForTeamOnly)
                        info.NameTagsForTeamOnly = newFlag;
                    GUILayout.EndHorizontal();
                }
                else
                {
                    // Client: read-only mirror of host settings
                    DrawSettingRow("Map",          info.MapName);
                    DrawSettingRow("Mode",         info.GameModeName);
                    DrawSettingRow("Bots",         info.BotCount);
                    DrawSettingRow("Night mode",   info.NightMode   ? "On" : "Off");
                    DrawSettingRow("All weapons",  info.PlayerHasAllWeapons ? "On" : "Off");
                    DrawSettingRow("Reverse",      info.ReverseMode ? "On" : "Off");
                    DrawSettingRow("Balance",      info.Balance.ToString("F1"));
                    DrawSettingRow("Respawn",      info.RespawnTime + "s");
                    string glName = (info.GameLength >= 0 && info.GameLength < GAME_LENGTH_NAMES.Length)
                        ? GAME_LENGTH_NAMES[info.GameLength] : info.GameLength.ToString();
                    DrawSettingRow("Length",       glName);
                    DrawSettingRow("Enemy names",  info.NameTagsForTeamOnly ? "Team only" : "All visible");
                }
            }

            GUILayout.Space(8);

            // ── MODS ─────────────────────────────────────────────────────
            GUILayout.Label("─── MODS ───");

            if (isHost)
            {
                var active = ModManager.instance.GetActiveMods();
                if (active.Count == 0)
                    GUILayout.Label("  No active mods");
                else
                    foreach (var m in active)
                        GUILayout.Label($"  • {m.title}");
            }
            else
            {
                if (sys.HasModMismatch)
                {
                    var prev = GUI.contentColor;
                    GUI.contentColor = new Color(1f, 0.85f, 0f);
                    GUILayout.Label("  ⚠  Mod mismatch — you are missing:");
                    foreach (var m in sys.MissingMods)
                        GUILayout.Label($"  • {m}");
                    GUI.contentColor = prev;
                }
                else if (info != null && !string.IsNullOrEmpty(info.Mods))
                {
                    GUI.contentColor = new Color(0.5f, 1f, 0.5f);
                    GUILayout.Label("  ✓ Mods match");
                    GUI.contentColor = Color.white;
                }
                else
                {
                    GUILayout.Label("  No mods required");
                }
            }

            GUILayout.EndScrollView();

            GUILayout.Space(4);

            // ── Action buttons ────────────────────────────────────────────
            GUILayout.BeginHorizontal();

            if (isHost)
            {
                if (GUILayout.Button("▶  START GAME", GUILayout.Height(36)))
                {
                    // Set own team dropdown and kick off the start
                    if (InstantActionMaps.instance != null)
                        InstantActionMaps.instance.teamDropdown.value = sys.MyTeam;
                    InstantActionMaps.instance?.StartGame();
                }
            }
            else
            {
                // Guest: grey out area where START would be
                GUI.enabled = false;
                GUILayout.Button("Waiting for host…", GUILayout.Height(36));
                GUI.enabled = true;
            }

            if (GUILayout.Button("✖  LEAVE", GUILayout.Width(100), GUILayout.Height(36)))
                sys.LeaveLobby();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private static void DrawSettingRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", GUILayout.Width(130));
            GUILayout.Label(value);
            GUILayout.EndHorizontal();
        }
    }
}
