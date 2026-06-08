using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace RavenM
{
    public class LanTransport : MonoBehaviour
    {
        public static LanTransport instance;

        private UdpClient _udpClient;
        private UdpClient _discoveryClient;

        public int GamePort = 7777;
        public int DiscoveryPort = 7778;

        private bool _isRunning = false;
        private Thread _receiveThread;
        private Thread _discoveryThread;

        private Dictionary<string, LanConnection> _connections = new Dictionary<string, LanConnection>();
        private Queue<LanPacket> _receivedPackets = new Queue<LanPacket>();
        private object _packetLock = new object();

        public bool IsRunning => _isRunning;

        // Only true on the host — controls whether discovery loop sends responses
        public bool IsHosting { get; set; } = false;

        public struct LanConnection
        {
            public IPEndPoint EndPoint;
            public Guid ClientGUID;
            public DateTime LastHeartbeat;
            public bool IsConnected;
        }

        public struct LanPacket
        {
            public IPEndPoint Source;
            public byte[] Data;
            public int Size;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void StartServer()
        {
            if (_isRunning)
                return;

            try
            {
                _udpClient = new UdpClient(GamePort)
                {
                    EnableBroadcast = true,
                    Ttl = 32
                };

                _discoveryClient = new UdpClient(DiscoveryPort)
                {
                    EnableBroadcast = true,
                    Ttl = 32
                };

                _isRunning = true;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "LAN Transport Receive"
                };
                _receiveThread.Start();

                _discoveryThread = new Thread(DiscoveryLoop)
                {
                    IsBackground = true,
                    Name = "LAN Discovery"
                };
                _discoveryThread.Start();

                Plugin.logger.LogInfo($"LAN транспорт запущен на портах {GamePort} (игра) и {DiscoveryPort} (поиск)");
            }
            catch (Exception ex)
            {
                Plugin.logger.LogError($"Ошибка при запуске LAN транспорта: {ex.Message}");
                _isRunning = false;
            }
        }

        public void Shutdown()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            IsHosting = false;

            try
            {
                _udpClient?.Close();
                _discoveryClient?.Close();

                if (_receiveThread?.IsAlive == true)
                    _receiveThread.Join(1000);

                if (_discoveryThread?.IsAlive == true)
                    _discoveryThread.Join(1000);
            }
            catch (Exception ex)
            {
                Plugin.logger.LogWarning($"Ошибка при завершении LAN транспорта: {ex.Message}");
            }

            _connections.Clear();
            lock (_packetLock)
                _receivedPackets.Clear();
        }

        public bool SendPacket(IPEndPoint endpoint, byte[] data)
        {
            if (!_isRunning || _udpClient == null || data == null || data.Length == 0)
                return false;

            try
            {
                _udpClient.Send(data, data.Length, endpoint);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.logger.LogWarning($"Ошибка отправки пакета на {endpoint}: {ex.Message}");
                return false;
            }
        }

        public bool SendBroadcast(byte[] data, int port = -1)
        {
            if (!_isRunning || data == null || data.Length == 0)
                return false;

            port = port == -1 ? GamePort : port;

            try
            {
                var endpoint = new IPEndPoint(IPAddress.Broadcast, port);
                _udpClient.Send(data, data.Length, endpoint);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.logger.LogWarning($"Ошибка broadcast пакета: {ex.Message}");
                return false;
            }
        }

        public LanPacket[] GetReceivedPackets()
        {
            lock (_packetLock)
            {
                var packets = _receivedPackets.ToArray();
                _receivedPackets.Clear();
                return packets;
            }
        }

        public void RegisterConnection(IPEndPoint endpoint, Guid clientGUID)
        {
            string key = endpoint.ToString();
            _connections[key] = new LanConnection
            {
                EndPoint = endpoint,
                ClientGUID = clientGUID,
                LastHeartbeat = DateTime.Now,
                IsConnected = true
            };
        }

        public void UnregisterConnection(IPEndPoint endpoint)
        {
            string key = endpoint.ToString();
            if (_connections.ContainsKey(key))
                _connections.Remove(key);
        }

        public Dictionary<string, LanConnection> GetConnections()
        {
            return new Dictionary<string, LanConnection>(_connections);
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[65535];

            while (_isRunning && _udpClient != null)
            {
                try
                {
                    EndPoint remoteEPBase = new IPEndPoint(IPAddress.Any, 0);
                    int size = _udpClient.Client.ReceiveFrom(buffer, ref remoteEPBase);
                    IPEndPoint remoteEP = (IPEndPoint)remoteEPBase;

                    if (size > 0)
                    {
                        byte[] data = new byte[size];
                        Array.Copy(buffer, data, size);

                        lock (_packetLock)
                        {
                            _receivedPackets.Enqueue(new LanPacket
                            {
                                Source = remoteEP,
                                Data = data,
                                Size = size
                            });
                        }

                        string key = remoteEP.ToString();
                        if (_connections.ContainsKey(key))
                        {
                            var conn = _connections[key];
                            conn.LastHeartbeat = DateTime.Now;
                            _connections[key] = conn;
                        }
                    }
                }
                catch (SocketException) when (!_isRunning)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Plugin.logger.LogWarning($"Ошибка в ReceiveLoop: {ex.Message}");
                }
            }
        }

        private void DiscoveryLoop()
        {
            // Cache the GUID string once — IngameNetManager must already be initialized by this point
            string ownGuid = IngameNetManager.instance != null
                ? IngameNetManager.instance.OwnGUID.ToString()
                : Guid.NewGuid().ToString();

            byte[] responseData = System.Text.Encoding.UTF8.GetBytes("RAVENM_SERVER:" + ownGuid);
            byte[] buffer = new byte[256];

            while (_isRunning && _discoveryClient != null)
            {
                try
                {
                    EndPoint remoteEPBase = new IPEndPoint(IPAddress.Any, 0);
                    int size = _discoveryClient.Client.ReceiveFrom(buffer, ref remoteEPBase);
                    IPEndPoint remoteEP = (IPEndPoint)remoteEPBase;

                    if (size > 0)
                    {
                        string message = System.Text.Encoding.UTF8.GetString(buffer, 0, size);

                        // Only the host should respond to discovery requests
                        if (message.StartsWith("RAVENM_DISCOVERY") && IsHosting)
                        {
                            _discoveryClient.Send(responseData, responseData.Length, remoteEP);
                            Plugin.logger.LogInfo($"Ответ на discovery запрос от {remoteEP}");
                        }
                    }
                }
                catch (SocketException) when (!_isRunning)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Plugin.logger.LogWarning($"Ошибка в DiscoveryLoop: {ex.Message}");
                }

                Thread.Sleep(100);
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
