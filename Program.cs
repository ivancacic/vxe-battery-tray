// VxeBatteryTray - system-tray battery monitor for ATK GEAR / VXE R1 SE+
// Reads battery straight from the wireless dongle over raw HID (no vendor software).
// Compiled with the in-box .NET Framework csc.exe (C# 5) via build.ps1 -> WinExe, no console.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VxeBatteryTray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mtx = new System.Threading.Mutex(true, "VxeBatteryTray_SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                    Application.Run();
            }
        }
    }

    public class Reading
    {
        public bool Found;
        public int Percent;
        public bool Charging;
        public double Volts;
        public string Reason;
    }

    public class TrayApp : IDisposable
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string APP_NAME = "VxeBatteryTray";

        int pollSeconds = 300;   // configurable
        int lowThreshold = 15;   // configurable

        NotifyIcon ni;
        Timer timer;
        ToolStripMenuItem statusItem, startupItem;
        Icon currentIcon;
        bool lowNotified;

        public TrayApp()
        {
            LoadSettings();

            ni = new NotifyIcon();
            ni.Text = "VXE R1 SE+ battery";
            ni.Visible = true;
            ni.DoubleClick += delegate { Poll(); };

            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("Reading...");
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Refresh now", null, delegate { Poll(); }));
            menu.Items.Add(new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); }));
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            startupItem.Checked = IsStartupEnabled();
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ni.Visible = false; Application.Exit(); }));
            ni.ContextMenuStrip = menu;

            timer = new Timer();
            timer.Interval = pollSeconds * 1000;
            timer.Tick += delegate { Poll(); };
            timer.Start();

            Poll();
        }

        void Poll()
        {
            Reading r = Read();
            UpdateIcon(r);

            if (r.Found)
            {
                statusItem.Text = string.Format("VXE R1 SE+: {0}%  {1}  {2:N2} V",
                    r.Percent, r.Charging ? "charging" : "on battery", r.Volts);
                ni.Text = Trunc(string.Format("VXE R1 SE+: {0}% {1} {2:N2}V",
                    r.Percent, r.Charging ? "charging" : "on battery", r.Volts), 63);

                if (!r.Charging && r.Percent <= lowThreshold)
                {
                    if (!lowNotified)
                    {
                        ni.ShowBalloonTip(5000, "VXE R1 SE+ battery low",
                            string.Format("{0}% remaining - time to charge.", r.Percent), ToolTipIcon.Warning);
                        lowNotified = true;
                    }
                }
                else if (r.Charging || r.Percent > lowThreshold + 5)
                {
                    lowNotified = false;
                }
            }
            else
            {
                statusItem.Text = r.Reason;
                ni.Text = Trunc(r.Reason, 63);
            }
        }

        static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

        // ---------- icon rendering (filled pill + bold white number) ----------
        void UpdateIcon(Reading r)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color fill;
                string txt;
                if (!r.Found)
                {
                    fill = Color.FromArgb(120, 120, 120);
                    txt = "?";
                }
                else
                {
                    fill = r.Percent >= 50 ? Color.FromArgb(46, 160, 67)
                         : r.Percent >= 20 ? Color.FromArgb(212, 150, 0)
                         : Color.FromArgb(206, 52, 52);
                    txt = r.Percent >= 100 ? "100" : r.Percent.ToString();
                }

                using (var path = RoundRect(0.5f, 0.5f, 31f, 31f, 11f))
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, path);

                DrawFitText(g, txt, Color.White);

                if (r.Found && r.Charging)
                    DrawBolt(g);

                SetIconFromBitmap(bmp);
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float d)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawFitText(Graphics g, string txt, Color color)
        {
            var fmt = StringFormat.GenericTypographic;
            float size = 26f;
            Font f = null;
            for (; size > 7f; size -= 1f)
            {
                f = new Font("Segoe UI Semibold", size, FontStyle.Bold, GraphicsUnit.Pixel);
                SizeF m = g.MeasureString(txt, f, PointF.Empty, fmt);
                if (m.Width <= 28f && m.Height <= 30f) break;
                f.Dispose();
            }
            SizeF mm = g.MeasureString(txt, f, PointF.Empty, fmt);
            float x = (32f - mm.Width) / 2f;
            float y = (32f - mm.Height) / 2f;
            using (var b = new SolidBrush(color))
                g.DrawString(txt, f, b, x, y, fmt);
            f.Dispose();
        }

        void DrawBolt(Graphics g)
        {
            var bolt = new Point[] {
                new Point(24,16), new Point(20,24), new Point(23,24),
                new Point(19,32), new Point(30,21), new Point(25,21), new Point(28,16)
            };
            using (var yb = new SolidBrush(Color.FromArgb(255, 214, 0)))
            using (var pen = new Pen(Color.FromArgb(40, 40, 40), 1f))
            {
                g.FillPolygon(yb, bolt);
                g.DrawPolygon(pen, bolt);
            }
        }

        void SetIconFromBitmap(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var tmp = Icon.FromHandle(h))
                {
                    Icon old = currentIcon;
                    currentIcon = (Icon)tmp.Clone();
                    ni.Icon = currentIcon;
                    if (old != null) old.Dispose();
                }
            }
            finally { DestroyIcon(h); }
        }

        // ---------- settings ----------
        static string SettingsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VxeBatteryTray");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.ini");
        }

        void LoadSettings()
        {
            try
            {
                string p = SettingsPath();
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int n;
                    if (!int.TryParse(v, out n)) continue;
                    if (k == "PollSeconds") pollSeconds = Clamp(n, 15, 3600);
                    else if (k == "LowThreshold") lowThreshold = Clamp(n, 5, 50);
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath(),
                    "PollSeconds=" + pollSeconds + Environment.NewLine +
                    "LowThreshold=" + lowThreshold + Environment.NewLine);
            }
            catch { }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        void ShowSettings()
        {
            using (var f = new SettingsForm(pollSeconds, lowThreshold))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    pollSeconds = f.PollSeconds;
                    lowThreshold = f.LowThreshold;
                    timer.Interval = pollSeconds * 1000;
                    SaveSettings();
                    lowNotified = false;
                    Poll();
                }
            }
        }

        // ---------- startup registry ----------
        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                return k != null && k.GetValue(APP_NAME) as string != null;
        }

        void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true) ?? Registry.CurrentUser.CreateSubKey(RUN_KEY))
            {
                if (startupItem.Checked)
                {
                    k.DeleteValue(APP_NAME, false);
                    startupItem.Checked = false;
                }
                else
                {
                    k.SetValue(APP_NAME, "\"" + Application.ExecutablePath + "\"");
                    startupItem.Checked = true;
                }
            }
        }

        public void Dispose()
        {
            if (timer != null) timer.Dispose();
            if (ni != null) { ni.Visible = false; ni.Dispose(); }
            if (currentIcon != null) currentIcon.Dispose();
        }

        // ---------- HID battery read ----------
        static Reading Read()
        {
            string path = Hid.FindCommsPath(0x373B, 0x1085);
            if (path == null)
                return new Reading { Found = false, Reason = "VXE dongle not found (turn mouse on / plug receiver)" };
            byte[] resp = Hid.Query(path, 0x04, 800);
            if (resp == null)
                return new Reading { Found = false, Reason = "No reply - mouse may be asleep (wiggle it)" };
            if (resp[1] != 0x04)
                return new Reading { Found = false, Reason = "Unexpected reply from dongle" };
            int mv = (resp[8] << 8) | resp[9];
            return new Reading
            {
                Found = true,
                Percent = Math.Min(100, (int)resp[6]),
                Charging = resp[7] == 1,
                Volts = Math.Round(mv / 1000.0, 2)
            };
        }
    }

    // ---------- settings dialog ----------
    public class SettingsForm : Form
    {
        public int PollSeconds;
        public int LowThreshold;
        NumericUpDown pollBox, lowBox;

        public SettingsForm(int poll, int low)
        {
            Text = "VXE Battery - Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(288, 132);

            var l1 = new Label { Text = "Poll interval (seconds):", Left = 14, Top = 20, Width = 165, AutoSize = true };
            pollBox = new NumericUpDown { Left = 183, Top = 16, Width = 90, Minimum = 15, Maximum = 3600, Increment = 15 };
            pollBox.Value = Math.Max(15, Math.Min(3600, poll));

            var l2 = new Label { Text = "Low battery alert (%):", Left = 14, Top = 56, Width = 165, AutoSize = true };
            lowBox = new NumericUpDown { Left = 183, Top = 52, Width = 90, Minimum = 5, Maximum = 50, Increment = 1 };
            lowBox.Value = Math.Max(5, Math.Min(50, low));

            var ok = new Button { Text = "OK", Left = 116, Top = 94, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 198, Top = 94, Width = 75, DialogResult = DialogResult.Cancel };
            ok.Click += delegate { PollSeconds = (int)pollBox.Value; LowThreshold = (int)lowBox.Value; };

            Controls.Add(l1); Controls.Add(pollBox);
            Controls.Add(l2); Controls.Add(lowBox);
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // Raw-HID plumbing (SetupAPI + hid.dll). Vendor channel: UsagePage 0xFF02, 17-byte In/Out reports, report id 0x08.
    static class Hid
    {
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        [StructLayout(LayoutKind.Sequential)] struct DID { public int cbSize; public Guid g; public uint f; public IntPtr r; }
        [StructLayout(LayoutKind.Sequential)] struct CAPS
        {
            public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort n1, n2, n3, n4, n5, n6, n7, n8, n9;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr h, uint f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, uint i, ref DID a);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref DID a, IntPtr d, uint sz, ref uint req, IntPtr dd);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr h, byte[] b, uint n, out uint w, byte[] o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(IntPtr h, byte[] b, uint n, out uint r, byte[] o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateEvent(IntPtr a, bool manual, bool init, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(IntPtr h, byte[] o, out uint n, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIo(IntPtr h);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out CAPS c);

        public static string FindCommsPath(ushort vid, ushort pid)
        {
            string match = string.Format("vid_{0:x4}&pid_{1:x4}", vid, pid);
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            var did = new DID(); did.cbSize = Marshal.SizeOf(did);
            string best = null;
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                uint req = 0; SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                IntPtr det = Marshal.AllocHGlobal((int)req); Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                if (SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero))
                {
                    string path = Marshal.PtrToStringUni((IntPtr)(det.ToInt64() + 4));
                    if (path != null && path.ToLower().Contains(match))
                    {
                        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h != (IntPtr)(-1))
                        {
                            IntPtr pp;
                            if (HidD_GetPreparsedData(h, out pp))
                            {
                                CAPS c; HidP_GetCaps(pp, out c); HidD_FreePreparsedData(pp);
                                if (c.OutLen == 17 && c.InLen == 17)
                                {
                                    best = path;
                                    if (c.UsagePage == 0xFF02) { CloseHandle(h); Marshal.FreeHGlobal(det); break; }
                                }
                            }
                            CloseHandle(h);
                        }
                    }
                }
                Marshal.FreeHGlobal(det);
            }
            SetupDiDestroyDeviceInfoList(set);
            return best;
        }

        public static byte[] Query(string path, byte cmd, int timeoutMs)
        {
            const int len = 17;
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == (IntPtr)(-1)) return null;
            try
            {
                byte[] req = new byte[len]; req[0] = 0x08; req[1] = cmd; req[len - 1] = (byte)(0x4D - cmd);
                int ovSize = 8 * 2 + 8 + IntPtr.Size;

                byte[] ovW = new byte[ovSize]; IntPtr evW = CreateEvent(IntPtr.Zero, true, false, null);
                BitConverter.GetBytes(evW.ToInt64()).CopyTo(ovW, ovSize - IntPtr.Size);
                uint w;
                if (!WriteFile(h, req, len, out w, ovW) && Marshal.GetLastWin32Error() == 997)
                { WaitForSingleObject(evW, (uint)timeoutMs); GetOverlappedResult(h, ovW, out w, false); }

                byte[] resp = new byte[len]; byte[] ovR = new byte[ovSize]; IntPtr evR = CreateEvent(IntPtr.Zero, true, false, null);
                BitConverter.GetBytes(evR.ToInt64()).CopyTo(ovR, ovSize - IntPtr.Size);
                uint r;
                if (!ReadFile(h, resp, len, out r, ovR) && Marshal.GetLastWin32Error() == 997)
                {
                    if (WaitForSingleObject(evR, (uint)timeoutMs) == 0) GetOverlappedResult(h, ovR, out r, false);
                    else { CancelIo(h); return null; }
                }
                return resp;
            }
            finally { CloseHandle(h); }
        }
    }
}
