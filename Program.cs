using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace JoinForce
{
    // ---------------------------------------------------------------
    //  Entry point — decides GUI vs Service vs install/uninstall
    // ---------------------------------------------------------------
    static class Program
    {
        internal static readonly string ExePath =
            System.Reflection.Assembly.GetExecutingAssembly().Location;

        internal static readonly string ExeDir =
            Path.GetDirectoryName(ExePath);

        internal static readonly string ConfigPath =
            Path.Combine(ExeDir, "joinforce.cfg");

        internal static readonly string ServiceName = "JoinForce";

        [STAThread]
        static void Main(string[] args)
        {
            var arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            switch (arg)
            {
                case "--service":
                    ServiceBase.Run(new JoinForceService());
                    break;

                case "--install":
                    ServiceHelper.Install();
                    break;

                case "--uninstall":
                    ServiceHelper.Uninstall();
                    break;

                default:
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                    break;
            }
        }
    }

    // ---------------------------------------------------------------
    //  Shared IGMP join engine — used by both GUI and Service
    // ---------------------------------------------------------------
    class JoinEngine
    {
        private List<Socket> _sockets = new List<Socket>();
        private System.Threading.Timer _timer;
        private IPAddress _nicAddress;
        private List<string> _groups;
        private int _intervalMs;

        public event Action<string> OnLog;

        public void Start(IPAddress nicAddress, List<string> groups, int intervalMinutes)
        {
            _nicAddress = nicAddress;
            _groups = groups;
            _intervalMs = intervalMinutes * 60 * 1000;

            JoinAll();
            _timer = new System.Threading.Timer(OnTick, null, _intervalMs, _intervalMs);
            Log("Engine started. Re-joining every " + intervalMinutes + " min on " + _nicAddress);
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            DropAll();
            Log("Engine stopped.");
        }

        private void OnTick(object state)
        {
            Log("Timer tick — re-joining groups...");
            DropAll();
            JoinAll();
        }

        private void JoinAll()
        {
            foreach (var groupStr in _groups)
            {
                try
                {
                    var groupAddr = IPAddress.Parse(groupStr);
                    var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    sock.Bind(new IPEndPoint(_nicAddress, 0));
                    sock.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(groupAddr, _nicAddress));
                    _sockets.Add(sock);
                    Log("Joined " + groupStr + " on " + _nicAddress);
                }
                catch (Exception ex)
                {
                    Log("Failed to join " + groupStr + ": " + ex.Message);
                }
            }
        }

        private void DropAll()
        {
            foreach (var sock in _sockets)
            {
                try { sock.Close(); } catch { }
            }
            _sockets.Clear();
        }

        private void Log(string msg)
        {
            var handler = OnLog;
            if (handler != null)
                handler(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg);
        }

        // --- Config file reader (shared between GUI load and service) ---
        public static void ReadConfig(out string nicIp, out int intervalMinutes, out List<string> groups)
        {
            nicIp = "";
            intervalMinutes = 5;
            groups = new List<string>();

            if (!File.Exists(Program.ConfigPath)) return;

            foreach (var line in File.ReadAllLines(Program.ConfigPath))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();

                if (key == "nic")
                    nicIp = val;
                else if (key == "interval")
                {
                    int v;
                    if (int.TryParse(val, out v) && v >= 1 && v <= 1440)
                        intervalMinutes = v;
                }
                else if (key == "group" && !string.IsNullOrEmpty(val))
                    groups.Add(val);
            }
        }
    }

    // ---------------------------------------------------------------
    //  Windows Service
    // ---------------------------------------------------------------
    class JoinForceService : ServiceBase
    {
        private JoinEngine _engine;
        private string _logPath;

        public JoinForceService()
        {
            ServiceName = Program.ServiceName;
            CanStop = true;
            CanPauseAndContinue = false;
        }

        protected override void OnStart(string[] args)
        {
            _logPath = Path.Combine(Program.ExeDir, "joinforce_service.log");

            string nicIp;
            int interval;
            List<string> groups;
            JoinEngine.ReadConfig(out nicIp, out interval, out groups);

            if (groups.Count == 0)
            {
                WriteLog("No multicast groups in config. Stopping.");
                Stop();
                return;
            }

            IPAddress nicAddr;
            if (!IPAddress.TryParse(nicIp, out nicAddr))
            {
                WriteLog("Invalid or missing NIC address in config: " + nicIp);
                Stop();
                return;
            }

            _engine = new JoinEngine();
            _engine.OnLog += WriteLog;
            _engine.Start(nicAddr, groups, interval);
        }

        protected override void OnStop()
        {
            if (_engine != null)
            {
                _engine.Stop();
                _engine = null;
            }
            WriteLog("Service stopped.");
        }

        private void WriteLog(string msg)
        {
            try
            {
                File.AppendAllText(_logPath, msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------
    //  Service install / uninstall helper (uses sc.exe)
    // ---------------------------------------------------------------
    static class ServiceHelper
    {
        public static bool IsInstalled()
        {
            try
            {
                var sc = ServiceController.GetServices()
                    .Any(s => s.ServiceName.Equals(Program.ServiceName, StringComparison.OrdinalIgnoreCase));
                return sc;
            }
            catch { return false; }
        }

        public static void Install()
        {
            var binPath = "\"" + Program.ExePath + "\" --service";
            var result = RunSc("create " + Program.ServiceName +
                " binPath= " + binPath +
                " start= auto" +
                " DisplayName= \"JoinForce IGMP Join Service\"");
            // Set description
            RunSc("description " + Program.ServiceName +
                " \"Periodically sends IGMP join requests for configured multicast groups.\"");
            // Set recovery: restart on failure
            RunSc("failure " + Program.ServiceName + " reset= 60 actions= restart/5000");
            Console.WriteLine(result);
        }

        public static void Uninstall()
        {
            // Stop first, ignore errors
            RunSc("stop " + Program.ServiceName);
            System.Threading.Thread.Sleep(1500);
            var result = RunSc("delete " + Program.ServiceName);
            Console.WriteLine(result);
        }

        internal static string RunSc(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return output.Trim();
            }
            catch (Exception ex)
            {
                return "Error running sc.exe: " + ex.Message;
            }
        }
    }

    // ---------------------------------------------------------------
    //  GUI Form
    // ---------------------------------------------------------------
    class MainForm : Form
    {
        private ComboBox _nicCombo;
        private TextBox _multicastInput;
        private Button _addBtn;
        private ListBox _groupList;
        private Button _removeBtn;
        private NumericUpDown _intervalNum;
        private Button _startStopBtn;
        private TextBox _logBox;
        private Button _saveBtn;
        private Button _loadBtn;
        private Label _statusLabel;
        private Button _installSvcBtn;
        private Button _uninstallSvcBtn;
        private Button _startSvcBtn;
        private Button _stopSvcBtn;
        private Label _svcStatusLabel;
        private Button _applySvcBtn;

        private JoinEngine _engine;
        private bool _running;

        public MainForm()
        {
            Text = "JoinForce — IGMP Join Sender — By Lucas Elliott";
            Size = new Size(560, 600);
            MinimumSize = new Size(520, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            BuildUI();
            PopulateNics();
            LoadConfig();
            UpdateServiceStatus();

            FormClosing += (s, e) => StopLocal();
        }

        private void BuildUI()
        {
            var pad = 10;
            var y = pad;

            // --- NIC selection ---
            AddLabel("Network Interface:", pad, ref y, 20);

            _nicCombo = new ComboBox
            {
                Location = new Point(pad, y),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _nicCombo.Width = ClientSize.Width - pad * 2;
            Controls.Add(_nicCombo);
            y += 30;

            // --- Multicast address input ---
            AddLabel("Multicast Group (e.g. 239.1.1.1):", pad, ref y, 20);

            _multicastInput = new TextBox { Location = new Point(pad, y), Width = 200 };
            _multicastInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { AddGroup(); e.SuppressKeyPress = true; }
            };
            Controls.Add(_multicastInput);

            _addBtn = new Button { Text = "Add", Location = new Point(220, y - 1), Width = 60 };
            _addBtn.Click += (s, e) => AddGroup();
            Controls.Add(_addBtn);

            _removeBtn = new Button { Text = "Remove", Location = new Point(290, y - 1), Width = 70 };
            _removeBtn.Click += (s, e) => RemoveGroup();
            Controls.Add(_removeBtn);
            y += 28;

            _groupList = new ListBox
            {
                Location = new Point(pad, y),
                Height = 100,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _groupList.Width = ClientSize.Width - pad * 2;
            Controls.Add(_groupList);
            y += 108;

            // --- Interval ---
            var intervalLabel = new Label
            {
                Text = "Re-join interval (minutes):",
                Location = new Point(pad, y + 3),
                AutoSize = true
            };
            Controls.Add(intervalLabel);

            _intervalNum = new NumericUpDown
            {
                Location = new Point(200, y),
                Width = 70,
                Minimum = 1,
                Maximum = 1440,
                Value = 5,
                DecimalPlaces = 0
            };
            Controls.Add(_intervalNum);
            y += 32;

            // --- Save / Load / Start (local) ---
            _saveBtn = new Button { Text = "Save Config", Location = new Point(pad, y), Width = 90 };
            _saveBtn.Click += (s, e) => SaveConfig();
            Controls.Add(_saveBtn);

            _loadBtn = new Button { Text = "Load Config", Location = new Point(110, y), Width = 90 };
            _loadBtn.Click += (s, e) => LoadConfig();
            Controls.Add(_loadBtn);

            _startStopBtn = new Button
            {
                Text = "Start",
                Location = new Point(ClientSize.Width - pad - 120, y),
                Width = 120,
                Height = 28,
                BackColor = Color.FromArgb(40, 160, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _startStopBtn.Click += (s, e) => ToggleStartStop();
            Controls.Add(_startStopBtn);
            y += 36;

            // --- Status ---
            _statusLabel = new Label
            {
                Text = "Local: Stopped",
                Location = new Point(pad, y),
                AutoSize = true,
                ForeColor = Color.Gray
            };
            Controls.Add(_statusLabel);
            y += 24;

            // --- Service management ---
            var svcBox = new GroupBox
            {
                Text = "Windows Service",
                Location = new Point(pad, y),
                Width = ClientSize.Width - pad * 2,
                Height = 62,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(svcBox);

            _installSvcBtn = new Button { Text = "Install", Location = new Point(10, 22), Width = 70, Height = 26 };
            _installSvcBtn.Click += (s, e) => InstallService();
            svcBox.Controls.Add(_installSvcBtn);

            _uninstallSvcBtn = new Button { Text = "Uninstall", Location = new Point(86, 22), Width = 70, Height = 26 };
            _uninstallSvcBtn.Click += (s, e) => UninstallService();
            svcBox.Controls.Add(_uninstallSvcBtn);

            _startSvcBtn = new Button { Text = "Start Svc", Location = new Point(170, 22), Width = 74, Height = 26 };
            _startSvcBtn.Click += (s, e) => StartService();
            svcBox.Controls.Add(_startSvcBtn);

            _stopSvcBtn = new Button { Text = "Stop Svc", Location = new Point(250, 22), Width = 74, Height = 26 };
            _stopSvcBtn.Click += (s, e) => StopService();
            svcBox.Controls.Add(_stopSvcBtn);

            _applySvcBtn = new Button { Text = "Apply && Restart", Location = new Point(330, 22), Width = 100, Height = 26 };
            _applySvcBtn.Click += (s, e) => ApplyAndRestartService();
            svcBox.Controls.Add(_applySvcBtn);

            _svcStatusLabel = new Label
            {
                Text = "",
                Location = new Point(436, 27),
                AutoSize = true,
                ForeColor = Color.Gray
            };
            svcBox.Controls.Add(_svcStatusLabel);

            y += 70;

            // --- Log ---
            _logBox = new TextBox
            {
                Location = new Point(pad, y),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(200, 220, 200),
                Font = new Font("Consolas", 8.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            _logBox.Width = ClientSize.Width - pad * 2;
            _logBox.Height = ClientSize.Height - y - pad;
            Controls.Add(_logBox);
        }

        private void AddLabel(string text, int x, ref int y, int advance)
        {
            Controls.Add(new Label { Text = text, Location = new Point(x, y), AutoSize = true });
            y += advance;
        }

        // ---- NIC ----

        private void PopulateNics()
        {
            _nicCombo.Items.Clear();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!nic.SupportsMulticast) continue;

                var ipProps = nic.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        _nicCombo.Items.Add(new NicEntry(nic.Name, addr.Address, nic.Description));
                }
            }
            if (_nicCombo.Items.Count > 0)
                _nicCombo.SelectedIndex = 0;
        }

        // ---- Group list ----

        private void AddGroup()
        {
            var text = _multicastInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            IPAddress addr;
            if (!IPAddress.TryParse(text, out addr))
            {
                Log("Invalid IP address: " + text);
                return;
            }

            var bytes = addr.GetAddressBytes();
            if (bytes[0] < 224 || bytes[0] > 239)
            {
                Log("Not a multicast address (must be 224.0.0.0 - 239.255.255.255): " + text);
                return;
            }

            if (_groupList.Items.Cast<string>().Any(g => g == text))
            {
                Log("Group already in list: " + text);
                return;
            }

            _groupList.Items.Add(text);
            _multicastInput.Clear();
            Log("Added group: " + text);
        }

        private void RemoveGroup()
        {
            if (_groupList.SelectedIndex < 0) return;
            var removed = _groupList.SelectedItem.ToString();
            _groupList.Items.RemoveAt(_groupList.SelectedIndex);
            Log("Removed group: " + removed);
        }

        // ---- Local start/stop (in-process, for testing) ----

        private void ToggleStartStop()
        {
            if (_running) StopLocal();
            else StartLocal();
        }

        private void StartLocal()
        {
            if (_groupList.Items.Count == 0) { Log("No multicast groups configured."); return; }
            if (_nicCombo.SelectedItem == null) { Log("No network interface selected."); return; }

            var nic = (NicEntry)_nicCombo.SelectedItem;
            var groups = new List<string>();
            foreach (var item in _groupList.Items) groups.Add(item.ToString());

            _engine = new JoinEngine();
            _engine.OnLog += LogFromEngine;
            _engine.Start(nic.Address, groups, (int)_intervalNum.Value);

            _running = true;
            _startStopBtn.Text = "Stop";
            _startStopBtn.BackColor = Color.FromArgb(200, 50, 50);
            _statusLabel.Text = "Local: Running";
            _statusLabel.ForeColor = Color.Green;
            _nicCombo.Enabled = false;
        }

        private void StopLocal()
        {
            if (_engine != null)
            {
                _engine.Stop();
                _engine = null;
            }
            _running = false;
            _startStopBtn.Text = "Start";
            _startStopBtn.BackColor = Color.FromArgb(40, 160, 80);
            _statusLabel.Text = "Local: Stopped";
            _statusLabel.ForeColor = Color.Gray;
            _nicCombo.Enabled = true;
        }

        // ---- Service management ----

        private void InstallService()
        {
            SaveConfig();
            Log("Installing service...");
            var binPath = "\"" + Program.ExePath + "\" --service";
            var r1 = ServiceHelper.RunSc("create " + Program.ServiceName +
                " binPath= " + binPath +
                " start= auto" +
                " DisplayName= \"JoinForce IGMP Join Service\"");
            Log(r1);
            ServiceHelper.RunSc("description " + Program.ServiceName +
                " \"Periodically sends IGMP join requests for configured multicast groups.\"");
            ServiceHelper.RunSc("failure " + Program.ServiceName + " reset= 60 actions= restart/5000");
            UpdateServiceStatus();
        }

        private void UninstallService()
        {
            Log("Uninstalling service...");
            ServiceHelper.RunSc("stop " + Program.ServiceName);
            System.Threading.Thread.Sleep(1500);
            var r = ServiceHelper.RunSc("delete " + Program.ServiceName);
            Log(r);
            UpdateServiceStatus();
        }

        private void StartService()
        {
            SaveConfig();
            Log("Starting service...");
            var r = ServiceHelper.RunSc("start " + Program.ServiceName);
            Log(r);
            System.Threading.Thread.Sleep(500);
            UpdateServiceStatus();
        }

        private void StopService()
        {
            Log("Stopping service...");
            var r = ServiceHelper.RunSc("stop " + Program.ServiceName);
            Log(r);
            System.Threading.Thread.Sleep(500);
            UpdateServiceStatus();
        }

        private void ApplyAndRestartService()
        {
            SaveConfig();
            Log("Applying config and restarting service...");
            ServiceHelper.RunSc("stop " + Program.ServiceName);
            System.Threading.Thread.Sleep(2000);
            var r = ServiceHelper.RunSc("start " + Program.ServiceName);
            Log(r);
            System.Threading.Thread.Sleep(500);
            UpdateServiceStatus();
        }

        private void UpdateServiceStatus()
        {
            try
            {
                if (!ServiceHelper.IsInstalled())
                {
                    _svcStatusLabel.Text = "Not installed";
                    _svcStatusLabel.ForeColor = Color.Gray;
                    _startSvcBtn.Enabled = false;
                    _stopSvcBtn.Enabled = false;
                    _applySvcBtn.Enabled = false;
                    _uninstallSvcBtn.Enabled = false;
                    _installSvcBtn.Enabled = true;
                    return;
                }

                _installSvcBtn.Enabled = false;
                _uninstallSvcBtn.Enabled = true;

                using (var sc = new ServiceController(Program.ServiceName))
                {
                    _svcStatusLabel.Text = sc.Status.ToString();
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        _svcStatusLabel.ForeColor = Color.Green;
                        _startSvcBtn.Enabled = false;
                        _stopSvcBtn.Enabled = true;
                        _applySvcBtn.Enabled = true;
                    }
                    else if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        _svcStatusLabel.ForeColor = Color.FromArgb(200, 50, 50);
                        _startSvcBtn.Enabled = true;
                        _stopSvcBtn.Enabled = false;
                        _applySvcBtn.Enabled = false;
                    }
                    else
                    {
                        _svcStatusLabel.ForeColor = Color.Orange;
                        _startSvcBtn.Enabled = false;
                        _stopSvcBtn.Enabled = false;
                        _applySvcBtn.Enabled = false;
                    }
                }
            }
            catch
            {
                _svcStatusLabel.Text = "Unknown";
                _svcStatusLabel.ForeColor = Color.Gray;
            }
        }

        // ---- Config ----

        private void SaveConfig()
        {
            try
            {
                var lines = new List<string>();
                var nic = _nicCombo.SelectedItem as NicEntry;
                lines.Add("nic=" + (nic != null ? nic.Address.ToString() : ""));
                lines.Add("interval=" + _intervalNum.Value);
                foreach (var item in _groupList.Items)
                    lines.Add("group=" + item);
                File.WriteAllLines(Program.ConfigPath, lines);
                Log("Config saved.");
            }
            catch (Exception ex)
            {
                Log("Save failed: " + ex.Message);
            }
        }

        private void LoadConfig()
        {
            string nicIp;
            int interval;
            List<string> groups;
            JoinEngine.ReadConfig(out nicIp, out interval, out groups);

            _groupList.Items.Clear();
            foreach (var g in groups)
                _groupList.Items.Add(g);

            _intervalNum.Value = interval;

            for (int i = 0; i < _nicCombo.Items.Count; i++)
            {
                if (((NicEntry)_nicCombo.Items[i]).Address.ToString() == nicIp)
                {
                    _nicCombo.SelectedIndex = i;
                    break;
                }
            }

            Log("Config loaded. " + groups.Count + " group(s).");
        }

        // ---- Logging ----

        private void LogFromEngine(string msg)
        {
            if (_logBox.InvokeRequired)
                _logBox.BeginInvoke((Action<string>)AppendLog, msg);
            else
                AppendLog(msg);
        }

        private void Log(string msg)
        {
            AppendLog(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
        }

        private void AppendLog(string line)
        {
            _logBox.AppendText(line + Environment.NewLine);
            if (_logBox.TextLength > 50000)
            {
                _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 30000);
                _logBox.SelectionStart = _logBox.TextLength;
            }
        }
    }

    // ---------------------------------------------------------------
    //  NIC combo item
    // ---------------------------------------------------------------
    class NicEntry
    {
        public string Name;
        public IPAddress Address;
        public string Description;

        public NicEntry(string name, IPAddress address, string description)
        {
            Name = name;
            Address = address;
            Description = description;
        }

        public override string ToString()
        {
            return Name + " - " + Address + " (" + Description + ")";
        }
    }
}
