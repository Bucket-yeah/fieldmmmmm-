using System;
using System.Net;
using UnityEngine;

namespace RavenM
{
    /// <summary>
    /// Расширение для IngameNetManager для поддержки LAN-транспорта.
    /// Позволяет использовать UDP вместо SteamNetworkingSockets для игры по локальной сети.
    /// </summary>
    public class LanNetworkAdapter
    {
        public static LanNetworkAdapter instance;

        private IPEndPoint _serverEndPoint;
        private IPEndPoint _clientEndPoint;
        private bool _useLanTransport = false;

        public static LanNetworkAdapter GetInstance()
        {
            if (instance == null)
                instance = new LanNetworkAdapter();
            return instance;
        }

        public void EnableLanMode(IPAddress serverIP, int port = 7777)
        {
            _serverEndPoint = new IPEndPoint(serverIP, port);
            _useLanTransport = true;
            Plugin.logger.LogInfo($"LAN режим активирован. Сервер: {_serverEndPoint}");
        }

        public void DisableLanMode()
        {
            _useLanTransport = false;
            _serverEndPoint = null;
            _clientEndPoint = null;
        }

        public bool IsLanModeEnabled => _useLanTransport;

        public void SendPacket(byte[] data, int sendFlags = 0)
        {
            if (!_useLanTransport || _serverEndPoint == null)
                return;

            if (!LanTransport.instance.SendPacket(_serverEndPoint, data))
            {
                Plugin.logger.LogError($"Не удалось отправить пакет на {_serverEndPoint}");
            }
        }

        public void BroadcastPacket(byte[] data)
        {
            if (!_useLanTransport)
                return;

            if (!LanTransport.instance.SendBroadcast(data))
            {
                Plugin.logger.LogError("Не удалось отправить broadcast пакет");
            }
        }

        public LanTransport.LanPacket[] ReceivePackets()
        {
            if (!_useLanTransport)
                return new LanTransport.LanPacket[0];

            return LanTransport.instance.GetReceivedPackets();
        }

        public IPEndPoint GetServerEndPoint() => _serverEndPoint;
        public IPEndPoint GetClientEndPoint() => _clientEndPoint;

        public void RegisterClientEndPoint(IPEndPoint endpoint)
        {
            _clientEndPoint = endpoint;
            LanTransport.instance.RegisterConnection(endpoint, IngameNetManager.instance.OwnGUID);
        }

        public void UnregisterClientEndPoint(IPEndPoint endpoint)
        {
            LanTransport.instance.UnregisterConnection(endpoint);
            _clientEndPoint = null;
        }
    }

    /// <summary>
    /// Расширение класса Plugin для инициализации LAN компонентов.
    /// </summary>
    public static class LanInitializer
    {
        public static void InitializeLanComponents()
        {
            // Убедимся, что компоненты созданы
            if (LanTransport.instance == null)
            {
                var go = new GameObject("LanTransport");
                go.AddComponent<LanTransport>();
            }

            if (LanLobbySystem.instance == null)
            {
                var go = new GameObject("LanLobbySystem");
                go.AddComponent<LanLobbySystem>();
            }

            Plugin.logger.LogInfo("LAN компоненты инициализированы");
        }
    }
}
