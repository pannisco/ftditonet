// FTDI to NET v. 1.7
// Made by Pannisco Software (Italy)
// https://github.com/pannisco/ftditonet

using LXProtocols.Acn.Rdm;
using LXProtocols.Acn.Sockets;
using LXProtocols.ArtNet.Packets;
using LXProtocols.ArtNet.Sockets;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

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
        static SerialPort _serialPort;
        static byte[] _dmxArray = new byte[512];
        static readonly object _lock = new object();
        static bool _keepRunning = true;
        static string interfaceName = SettingsManager.Load().InterfaceName;
        static string targetIp = SettingsManager.Load().TargetIp;
        static int universe = SettingsManager.Load().Universe;
        static bool com = SettingsManager.Load().ManualCOM;
        static IPAddress localIp = GetInterfaceIp(interfaceName);
        static ArtNetSocket socket;
        static readonly ArtNetDmxPacket _reusablePacket = new ArtNetDmxPacket();

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
                selectedPort = Console.ReadLine();
            }
            else
            {
                selectedPort = ports[0];
            }

            // Serial settings
            _serialPort = new SerialPort(selectedPort, 57600);
            _serialPort.NewLine = "\r\n";       
            _serialPort.ReadTimeout = 2000;     
            _serialPort.DtrEnable = true;

            // Assign an UID and a Socket 
            var rdmId = new UId(0x1234, 0x56789ABC);
            socket = new ArtNetSocket(rdmId);

            // Open the socket 
            try
            {
                socket.Open(localIp, IPAddress.Parse(targetIp));
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[INFO]: ArtNET socket opened on {localIp} -> {targetIp}");
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
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[OK] Serial port {selectedPort} opened!");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR]: {ex.Message}");
                return;
            }

            // Serial read thread
            Thread readThread = new Thread(ReadSerialData);
            readThread.IsBackground = true;
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
            while (_serialPort.IsOpen)
            {
                try
                {
                    string rawData = _serialPort.ReadLine();
                    if (!string.IsNullOrWhiteSpace(rawData) && rawData.Contains(";"))
                    {
                        string cleanData = rawData.Trim().Replace(";", "");
                        string[] stringValues = cleanData.Split(',');

                        lock (_lock)
                        {
                            int limit = Math.Min(stringValues.Length, 512);
                            for (int i = 0; i < limit; i++)
                            {
                                if (byte.TryParse(stringValues[i], out byte val))
                                {
                                    _dmxArray[i] = val;
                                }
                            }
                            SendPacket(socket, _dmxArray, universe);
                        }
                    }
                }
                catch (TimeoutException)
                {
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[ERROR]: Data is not valid.");
                }
                catch (Exception ex)
                {
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ERROR]: {ex}.");
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