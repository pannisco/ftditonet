// FTDI to NET v. 1.8
// Made by Pannisco Software (Italy)
// https://github.com/pannisco/ftditonet

using LXProtocols.Acn.Rdm;
using LXProtocols.Acn.Sockets;
using LXProtocols.ArtNet.Packets;
using LXProtocols.ArtNet.Sockets;
using System;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;

namespace dmxtonet
{
    public class AppSettings
    {
        public string InterfaceName { get; set; } = "Ethernet 2"; // Interface 
        public string TargetIp { get; set; } = "2.0.2.1"; // Target IP 
        public int Universe { get; set; } = 0; // ArtNET Universe 
        public bool ManualCOM { get; set; } = false; // Manual COM port 
    }

    // Manage settings 
    public static class SettingsManager
    {
        private static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static void Save(AppSettings settings)
        {
            string json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(FilePath, json);
        }

        public static AppSettings Load()
        {
            if (!File.Exists(FilePath))
            {
                var defaultSettings = new AppSettings();
                Save(defaultSettings);
                return defaultSettings;
            }
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
    }

    class Program
    {
        // VARs
        static SerialPort _serialPort = null!;
        static byte[] _dmxArray = new byte[512];
        static readonly object _lock = new object();
        static bool _keepRunning = true;
        static string interfaceName = SettingsManager.Load().InterfaceName;
        static string targetIp = SettingsManager.Load().TargetIp;
        static int universe = SettingsManager.Load().Universe;
        static bool com = SettingsManager.Load().ManualCOM;
        static IPAddress? localIp = GetInterfaceIp(interfaceName);
        static ArtNetSocket socket = null!;
        static readonly ArtNetDmxPacket _reusablePacket = new ArtNetDmxPacket();
        static readonly byte[] _tempDmxBuffer = new byte[512];

        // Diagnostica
        static int corruptedPackets = 0;
        static int validPackets = 0;

        static void Main(string[] args)
        {
            Console.Title = "FTDI to ArtNET";
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Welcome to FTDI to Artnet!");
            Console.WriteLine("\nVersion 1.7");
            Console.WriteLine("\nhttps://github.com/pannisco/ftditonet");
            Console.WriteLine();


            // Search serial ports
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ERROR]: No COM ports found!");
                return;
            }

            string selectedPort;
            if (com)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine("\n[INFO] Enter COM port (ex. COM3): ");
                selectedPort = Console.ReadLine() ?? ports[0];
            }
            else
            {
                selectedPort = ports[0];
            }

            // Serial settings
            _serialPort = new SerialPort(selectedPort, 57600);
            _serialPort.NewLine = "\r\n";
            _serialPort.ReadTimeout = 100;
            _serialPort.WriteTimeout = 100;
            _serialPort.DtrEnable = true;
            _serialPort.RtsEnable = false;
            _serialPort.Handshake = Handshake.None;
            _serialPort.ReadBufferSize = 8192;
            _serialPort.WriteBufferSize = 2048;
            _serialPort.ReceivedBytesThreshold = 1;

            // Assign an UID and a Socket 
            var rdmId = new UId(0x1234, 0x56789ABC);
            socket = new ArtNetSocket(rdmId);

            // Open the socket 
            try
            {
                if (localIp != null)
                {
                    socket.Open(localIp, IPAddress.Parse(targetIp));
                }
                else
                {
                    socket.Open(IPAddress.Any, IPAddress.Parse(targetIp));
                }
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[INFO]: ArtNET socket opened on {localIp ?? IPAddress.Any} -> {targetIp}");
                Console.WriteLine($"[INFO]: ArtNET Universe: {universe}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR]: {ex.Message}");
                try
                {
                    socket.Open(IPAddress.Any, IPAddress.Parse(targetIp));
                }
                catch (Exception ex2)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ERROR]: {ex2.Message}");
                    Console.ReadKey();
                    return;
                }
            }

            // Open serial port
            try
            {
                _serialPort.Open();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[OK] Serial port {selectedPort} opened!");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR]: {ex.Message}");
                return;
            }

            // Process priority
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine("[INFO] Process priority set to HIGH");
            }
            catch { }

            // Serial read thread
            Thread readThread = new Thread(ReadSerialData);
            readThread.IsBackground = true;
            readThread.Priority = ThreadPriority.Highest;
            readThread.Start();

            // UI
            Console.CursorVisible = false;
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine("[OK] Service started.");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("[INFO] Press CTRL + C to stop.");
            Console.ReadKey();

        }



        // Read data
        private static void ReadSerialData()
        {
            int consecutiveErrors = 0;

            while (_serialPort.IsOpen && _keepRunning)
            {
                try
                {
                    string rawData = _serialPort.ReadLine();

                    // VALIDAZIONE 1: Controllo base
                    if (string.IsNullOrWhiteSpace(rawData) || !rawData.Contains(';'))
                    {
                        continue;
                    }

                    // VALIDAZIONE 2: Estrai dati puliti
                    int semicolonIndex = rawData.IndexOf(';');
                    string cleanData = semicolonIndex >= 0
                        ? rawData.Substring(0, semicolonIndex).Trim()
                        : rawData.Trim();

                    // VALIDAZIONE 3: Verifica formato
                    if (string.IsNullOrEmpty(cleanData))
                    {
                        continue;
                    }

                    string[] values = cleanData.Split(',');

                    // VALIDAZIONE 4: Numero di canali ragionevole
                    if (values.Length < 1 || values.Length > 512)
                    {
                        corruptedPackets++;
                        continue;
                    }

                    // VALIDAZIONE 5: Parse con controllo errori
                    bool isValid = true;
                    int parsedCount = 0;

                    // Azzera buffer temporaneo
                    Array.Clear(_tempDmxBuffer, 0, 512);

                    for (int i = 0; i < values.Length && i < 512; i++)
                    {
                        string val = values[i].Trim();

                        // VALIDAZIONE 6: Controlla che sia un numero valido
                        if (string.IsNullOrEmpty(val) || val.Length > 3)
                        {
                            isValid = false;
                            break;
                        }

                        if (byte.TryParse(val, out byte byteVal))
                        {
                            _tempDmxBuffer[i] = byteVal;
                            parsedCount++;
                        }
                        else
                        {
                            isValid = false;
                            break;
                        }
                    }

                    // VALIDAZIONE 7: Invia solo se tutto valido
                    if (isValid && parsedCount > 0)
                    {
                        lock (_lock)
                        {
                            Buffer.BlockCopy(_tempDmxBuffer, 0, _dmxArray, 0, 512);
                        }

                        SendPacket(socket, _dmxArray, universe);
                        validPackets++;
                    }
                    else
                    {
                        corruptedPackets++;
                    }

                    consecutiveErrors = 0;
                }
                catch (TimeoutException)
                {
                    consecutiveErrors++;
                    if (consecutiveErrors > 100)
                    {
                        Console.WriteLine("[WARNING] No data received");
                        consecutiveErrors = 0;
                    }
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    if (consecutiveErrors > 10)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[ERROR]: {ex.Message}");
                        consecutiveErrors = 0;
                    }
                }
            }
        }

        // Send packets 
        static void SendPacket(ArtNetSocket socket, byte[] dmxData, int universe)
        {
            _reusablePacket.Universe = (short)universe;
            _reusablePacket.DmxData = dmxData;
            socket.Send(_reusablePacket);
        }


        // Get interface ip
        static IPAddress? GetInterfaceIp(string name)
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && i.OperationalStatus == OperationalStatus.Up);
            if (ni == null) return null;
            return ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
                .Address;
        }
    }
}