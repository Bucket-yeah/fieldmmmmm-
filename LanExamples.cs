using System;
using System.Net;
using UnityEngine;

namespace RavenM
{
    /// <summary>
    /// Примеры использования LAN системы для разработчиков.
    /// </summary>
    public class LanExamples : MonoBehaviour
    {
        /// <summary>
        /// Пример 1: Создание LAN сервера (хост)
        /// </summary>
        public void Example_CreateLanServer()
        {
            // Получаем LAN лобби систему
            var lanLobby = LanLobbySystem.instance;
            
            // Создаем новое лобби с 8 игроками
            lanLobby.CreateLobby(
                playerName: "MyHostName",
                maxPlayers: 8
            );
            
            // Лобби ID и IP адрес будут доступны в:
            // lanLobby.CurrentLobbyID
            // lanLobby.GetCurrentLobbyInfo().HostIP
        }

        /// <summary>
        /// Пример 2: Присоединение к LAN серверу (клиент)
        /// </summary>
        public void Example_JoinLanServer()
        {
            var lanLobby = LanLobbySystem.instance;
            
            // Присоединяемся к серверу по IP
            lanLobby.JoinLobby(
                hostIP: "192.168.1.100",
                port: 7777,
                playerName: "ClientPlayer"
            );
        }

        /// <summary>
        /// Пример 3: Поиск доступных лобби в сети
        /// </summary>
        public void Example_DiscoverLobbies()
        {
            var lanLobby = LanLobbySystem.instance;

            // Запускаем неблокирующий поиск; результаты появятся через ~3 сек в GetDiscoveredLobbies()
            lanLobby.StartDiscovery();

            // Для просмотра текущих результатов (можно вызвать позже):
            foreach (var lobby in lanLobby.GetDiscoveredLobbies())
            {
                Debug.Log($"Найдено лобби: {lobby.HostName} " +
                    $"({lobby.PlayerCount}/{lobby.MaxPlayers}) " +
                    $"IP: {lobby.HostIP}:{lobby.HostPort}");
            }
        }

        /// <summary>
        /// Пример 4: Получение информации о текущем лобби
        /// </summary>
        public void Example_GetLobbyInfo()
        {
            var lanLobby = LanLobbySystem.instance;
            
            if (lanLobby.IsInLobby)
            {
                var lobbyInfo = lanLobby.GetCurrentLobbyInfo();
                var players = lanLobby.GetLobbyPlayers();
                
                Debug.Log($"Лобби ID: {lobbyInfo.LobbyID}");
                Debug.Log($"Хост: {lobbyInfo.HostName}");
                Debug.Log($"Игроки: {players.Count}/{lobbyInfo.MaxPlayers}");
                
                foreach (var player in players)
                {
                    Debug.Log($"  - {player.PlayerName} (готов: {player.IsReady})");
                }
            }
        }

        /// <summary>
        /// Пример 5: Выход из лобби
        /// </summary>
        public void Example_LeaveLobby()
        {
            var lanLobby = LanLobbySystem.instance;
            lanLobby.LeaveLobby();
        }

        /// <summary>
        /// Пример 6: Прямое использование LAN транспорта для отправки пакетов
        /// </summary>
        public void Example_SendCustomPacket()
        {
            var transport = LanTransport.instance;
            
            // Создаем кастомный пакет
            var packet = System.Text.Encoding.UTF8.GetBytes("Hello LAN!");
            
            // Отправляем на конкретный IP
            var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 7777);
            transport.SendPacket(endpoint, packet);
        }

        /// <summary>
        /// Пример 7: Broadcast отправка пакета всем в сети
        /// </summary>
        public void Example_BroadcastPacket()
        {
            var transport = LanTransport.instance;
            
            // Отправляем пакет в broadcast
            var packet = System.Text.Encoding.UTF8.GetBytes("Hello everyone!");
            transport.SendBroadcast(packet);
        }

        /// <summary>
        /// Пример 8: Получение пакетов из сети
        /// </summary>
        public void Example_ReceivePackets()
        {
            var transport = LanTransport.instance;
            
            // Получаем все полученные пакеты
            var packets = transport.GetReceivedPackets();
            
            foreach (var packet in packets)
            {
                var message = System.Text.Encoding.UTF8.GetString(
                    packet.Data, 0, packet.Size);
                Debug.Log($"От {packet.Source}: {message}");
            }
        }

        /// <summary>
        /// Пример 9: Использование LAN адаптера с IngameNetManager
        /// </summary>
        public void Example_UseLanAdapter()
        {
            var adapter = LanNetworkAdapter.GetInstance();
            
            // Включаем LAN режим
            adapter.EnableLanMode(
                serverIP: IPAddress.Parse("192.168.1.100"),
                port: 7777
            );
            
            // Проверяем, включен ли LAN режим
            if (adapter.IsLanModeEnabled)
            {
                Debug.Log("LAN режим активирован");
                
                // Отправляем пакет через адаптер
                byte[] data = new byte[] { 1, 2, 3, 4 };
                adapter.SendPacket(data);
            }
        }

        /// <summary>
        /// Пример 10: Обработка LAN пакетов в игре
        /// </summary>
        private void Update()
        {
            var transport = LanTransport.instance;
            
            // Получаем пакеты каждый фрейм
            var packets = transport.GetReceivedPackets();
            
            foreach (var packet in packets)
            {
                // Обрабатываем пакет как игровой пакет
                HandleLanGamePacket(packet);
            }
        }

        private void HandleLanGamePacket(LanTransport.LanPacket packet)
        {
            // Парсим пакет и обрабатываем его как обычный игровой пакет
            // Это интегрируется с IngameNetManager
            
            Debug.Log($"Получен пакет от {packet.Source}: {packet.Size} байт");
        }
    }

    /// <summary>
    /// Интеграция LAN с IngameNetManager - как использовать параллельно со Steam
    /// </summary>
    public class LanIngameIntegration
    {
        /// <summary>
        /// Получить правильный метод отправки пакета в зависимости от режима
        /// </summary>
        public static void SendGamePacket(byte[] packetData, PacketType type)
        {
            var adapter = LanNetworkAdapter.GetInstance();
            
            if (adapter.IsLanModeEnabled)
            {
                // Отправляем через LAN
                adapter.SendPacket(packetData);
                Plugin.logger.LogInfo($"Пакет отправлен через LAN: {type}");
            }
            else
            {
                // Отправляем через Steam (обычный способ)
                IngameNetManager.instance.SendPacketToServer(
                    packetData, type, Steamworks.Constants.k_nSteamNetworkingSend_Reliable);
                Plugin.logger.LogInfo($"Пакет отправлен через Steam: {type}");
            }
        }

        /// <summary>
        /// Обработка входящих пакетов из обоих источников
        /// </summary>
        public static void ProcessIncomingPackets()
        {
            var adapter = LanNetworkAdapter.GetInstance();
            
            if (adapter.IsLanModeEnabled)
            {
                // Обрабатываем LAN пакеты
                var lanPackets = adapter.ReceivePackets();
                foreach (var packet in lanPackets)
                {
                    ProcessLanPacket(packet);
                }
            }
            // Steam пакеты обрабатываются в IngameNetManager.FixedUpdate()
        }

        private static void ProcessLanPacket(LanTransport.LanPacket packet)
        {
            // Десериализуем и обрабатываем пакет как обычно
            Plugin.logger.LogInfo($"Обработка LAN пакета от {packet.Source}");
        }
    }
}
