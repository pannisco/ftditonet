// (c) 2026 PANNISCO SW. // VER 1.0 15-01-26 
using LXProtocols.Acn.Rdm;
using LXProtocols.Acn.Sockets;
using LXProtocols.ArtNet;
using LXProtocols.ArtNet.Packets;
using LXProtocols.ArtNet.Sockets;
using System;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json;
using System.Threading;
using System.Transactions;
using FTD2XX_NET;

//Default settings 
public class AppSettings
{
    public string interfaceName { get; set; } = "Ethernet 2"; //Interface 
    public string targetIp { get; set; } = "2.0.2.1"; //Target IP 
    public int universe { get; set; } = 0; //ArtNET Universe 
    public bool log { get; set; } = true; //Use log 
}

//Manage settings 
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

//Main 
public class DmxToNet
{
    private static bool _isSynced = false;
    static DateTime _lastByteTime = DateTime.MinValue;
    static bool _waitingStartCode = true;

    static void Main(string[] args)
    {
        //Import all settings 
        var settingstemp = SettingsManager.Load();
        string interfaceName = settingstemp.interfaceName;
        string targetIp = settingstemp.targetIp;
        int universe = settingstemp.universe;
        bool log = settingstemp.log;

        IPAddress localIp = GetInterfaceIp(interfaceName); //Get LocalIP 

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Welcome!");

        //Check if interface exist 
        if (localIp == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR]: Interface '{interfaceName}' doesn't exist!");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("\n[INFO]: Available interfaces:");
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    var ip = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ip != null) Console.WriteLine($" - {ni.Name}: {ip.Address}");
                }
            }
            Console.WriteLine("\n[INFO] Press any key to exit");
            Console.ReadKey();
            return;
        }

        //Serial port selection 
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("\n=== Mode Selection ===");
        Console.WriteLine("1. Listen from serial port");
        Console.WriteLine("2. Generate random data");
        Console.Write("\nSelect mode (1 or 2): ");
        string choice = Console.ReadLine();
        bool useSerial = (choice == "1");
        SerialPort serialPort = null;

        //Search for COM ports 
        if (useSerial)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nAvailable COM ports:");
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" - No COM ports found");
                Console.WriteLine("\n[INFO] Switching to random mode...");
                useSerial = false;
            }
            else
            {
                for (int i = 0; i < ports.Length; i++)
                {
                    if (OperatingSystem.IsWindows()) //Windows 
                    {
                        try
                        {
                            using (var searcher = new ManagementObjectSearcher(
                                $"SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%{ports[i]}%'"))
                            {
                                foreach (var device in searcher.Get())
                                {
                                    string caption = device["Caption"]?.ToString() ?? ports[i];
                                    Console.WriteLine($" {i + 1}. {ports[i]}: {caption}");
                                }
                            }
                        }
                        catch
                        {
                            Console.WriteLine($" {i + 1}. {ports[i]}");
                        }
                    }
                }
                Console.Write("\nSelect COM port (1-" + ports.Length + "): ");
                string portChoice = Console.ReadLine();

                //Open serial port 
                if (int.TryParse(portChoice, out int portIndex) && portIndex >= 1 && portIndex <= ports.Length)
                {
                    string selectedPort = ports[portIndex - 1];
                    serialPort = new SerialPort(selectedPort, 250000, Parity.None, 8, StopBits.Two);
                    serialPort.ReadTimeout = 1000;
                    try
                    {
                        serialPort.Open();
                        serialPort.ErrorReceived += (sender, e) =>
                        {
                            serialPort.DiscardInBuffer();
                            _isSynced = true;
                        };
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.WriteLine($"\n[INFO]: Serial port {selectedPort} opened (250000 baud, 8N2)");
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[ERROR]: Cannot open {selectedPort}: {ex.Message}");
                        Console.WriteLine("\n[INFO] Switching to random mode...");
                        useSerial = false;
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[WARNING]: Invalid selection. Switching to random mode...");
                    useSerial = false;
                }
            }
        }

        //Assign an UID and a Socket 
        var rdmId = new UId(0x1234, 0x56789ABC);
        var socket = new ArtNetSocket(rdmId);
        socket.NewPacket += Socket_NewPacket;

        //Open the socket 
        try
        {
            socket.Open(localIp, IPAddress.Parse(targetIp));
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            if (!useSerial)
            {
                Console.WriteLine();
            }
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

        if (useSerial)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("[MODE]: Listen from serial port");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("[MODE]: Random Data Generation");
        }

        Console.WriteLine("\nPress Ctrl+C to stop\n");

        byte[] dmxData = new byte[512];
        int frameCount = 0;
        Random rng = new Random();

        try
        {
            if (useSerial && serialPort != null)
            {
                //serial mode 
                while (true)
                {
                    try
                    {
                        if (ReadDmxFrame(serialPort, dmxData))
                        {
                            SendPacket(socket, dmxData, universe);
                            frameCount++;
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.Write($"\r[{DateTime.Now:HH:mm:ss.ff}]: Packet sended");
                        }
                    }
                    catch (TimeoutException)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"\n[{DateTime.Now:HH:mm:ss.ff}]: Waiting for serial signal... \n");
                    }
                }
            }
            else
            {
                //Random mode 
                while (true)
                {
                    rng.NextBytes(dmxData);
                    SendPacket(socket, dmxData, universe);
                    frameCount++;
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write($"\r[{DateTime.Now:HH:mm:ss.ff}]: Packet sended");
                    Thread.Sleep(22);
                }
            }
        }
        finally
        {
            if (serialPort != null && serialPort.IsOpen)
                serialPort.Close();
            socket.Close();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("\n\n[INFO]: Converter stopped.");
        }
    }

    //New packet 
    static void Socket_NewPacket(object? sender, NewPacketEventArgs<ArtNetPacket> e)
    {
    }

    //Send packets 
    static void SendPacket(ArtNetSocket socket, byte[] dmxData, int universe)
    {
        var packet = new ArtNetDmxPacket();
        packet.Universe = (short)universe;
        packet.DmxData = dmxData;
        socket.Send(packet);
        Console.ForegroundColor = ConsoleColor.DarkGreen;
    }

    //Blackout (All 0) 
    static void Blackout(ArtNetSocket socket, int universe)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("\n[INFO]: Sending blackout...");
        byte[] dmxData = new byte[512];
        SendPacket(socket, dmxData, universe);
    }

    //interpret DMX Data 
    static bool ReadDmxFrame(SerialPort port, byte[] dmxData)
    {
        try
        {
            while (port.BytesToRead > 0)
            {
                int b = port.ReadByte();
                var now = DateTime.UtcNow;

                // BREAK detection via timing (>1ms gap) 
                if (_lastByteTime != DateTime.MinValue && (now - _lastByteTime).TotalMilliseconds > 1)
                {
                    _waitingStartCode = true;
                }
                _lastByteTime = now;

                // Wait for the start code (doesn't work)
                if (_waitingStartCode)
                {
                    if (b == 0x00)
                    {
                        _waitingStartCode = false;
                        int offset = 0;
                        while (offset < 512)
                        {
                            int read = port.Read(dmxData, offset, 512 - offset);
                            if (read <= 0) return false;
                            offset += read;
                        }
                        return true;
                    }
                    continue;
                }
            }
        }
        catch
        {
            _waitingStartCode = true;
        }
        return false;
    }

    //Get IP Address 
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