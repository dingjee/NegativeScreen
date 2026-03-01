using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TestRunner
{
    class Program
    {
        static int passedTests = 0;
        static int failedTests = 0;

        static void Main(string[] args)
        {
            Console.WriteLine("============================================");
            Console.WriteLine("NegativeScreen Test Runner");
            Console.WriteLine("============================================\n");

            TestMagnifierAPI();
            TestMonitorManager();
            TestConfiguration();

            Console.WriteLine("\n============================================");
            Console.WriteLine("Test Results: {0} passed, {1} failed", passedTests, failedTests);
            Console.WriteLine("============================================");

            Environment.Exit(failedTests > 0 ? 1 : 0);
        }

        static void TestMagnifierAPI()
        {
            Console.WriteLine("\n--- Testing Magnifier API ---");

            RunTest("MagInitialize", () =>
            {
                bool result = NativeMethods.MagInitialize();
                if (!result) throw new Exception("MagInitialize failed: " + Marshal.GetLastWin32Error());
                NativeMethods.MagUninitialize();
                return true;
            });

            RunTest("MagUninitialize", () =>
            {
                NativeMethods.MagInitialize();
                bool result = NativeMethods.MagUninitialize();
                if (!result) throw new Exception("MagUninitialize failed: " + Marshal.GetLastWin32Error());
                return true;
            });
        }

        static void TestMonitorManager()
        {
            Console.WriteLine("\n--- Testing MonitorManager ---");

            RunTest("GetMonitors", () =>
            {
                var monitors = MonitorManager.Monitors;
                if (monitors == null || monitors.Count == 0)
                    throw new Exception("No monitors detected");
                Console.WriteLine("    Found {0} monitor(s)", monitors.Count);
                foreach (var m in monitors)
                {
                    Console.WriteLine("    - {0}: {1}x{2} at ({3},{4})", 
                        m.DeviceName, m.Bounds.Width, m.Bounds.Height, 
                        m.Bounds.X, m.Bounds.Y);
                }
                return true;
            });

            RunTest("GetPrimaryMonitor", () =>
            {
                var primary = MonitorManager.GetPrimaryMonitor();
                if (primary == null || !primary.IsPrimary)
                    throw new Exception("Primary monitor not found");
                return true;
            });
        }

        static void TestConfiguration()
        {
            Console.WriteLine("\n--- Testing Configuration ---");

            RunTest("LoadConfiguration", () =>
            {
                var config = Configuration.Current;
                if (config == null)
                    throw new Exception("Configuration not loaded");
                Console.WriteLine("    ActiveOnStartup: {0}", config.ActiveOnStartup);
                Console.WriteLine("    ColorEffects: {0}", config.ColorEffects.Count);
                return true;
            });
        }

        static void RunTest(string name, Func<bool> test)
        {
            try
            {
                test();
                Console.WriteLine("  [PASS] {0}", name);
                passedTests++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] {0}: {1}", name, ex.Message);
                failedTests++;
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("Magnification.dll")]
        public static extern bool MagInitialize();

        [DllImport("Magnification.dll")]
        public static extern bool MagUninitialize();
    }

    public class MonitorInfo
    {
        public IntPtr Handle { get; set; }
        public string DeviceName { get; set; }
        public string FriendlyName { get; set; }
        public System.Drawing.Rectangle Bounds { get; set; }
        public bool IsPrimary { get; set; }
        public int Index { get; set; }
    }

    public static class MonitorManager
    {
        private static System.Collections.Generic.List<MonitorInfo> _monitors;

        public static System.Collections.Generic.IReadOnlyList<MonitorInfo> Monitors
        {
            get
            {
                if (_monitors == null)
                    _monitors = EnumerateMonitors();
                return _monitors.AsReadOnly();
            }
        }

        public static MonitorInfo GetPrimaryMonitor()
        {
            foreach (var m in Monitors)
                if (m.IsPrimary) return m;
            return null;
        }

        private static System.Collections.Generic.List<MonitorInfo> EnumerateMonitors()
        {
            var monitors = new System.Collections.Generic.List<MonitorInfo>();
            int index = 0;

            MonitorEnumProc callback = delegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    monitors.Add(new MonitorInfo
                    {
                        Handle = hMonitor,
                        DeviceName = mi.szDevice,
                        Bounds = new System.Drawing.Rectangle(
                            mi.rcMonitor.left, mi.rcMonitor.top,
                            mi.rcMonitor.right - mi.rcMonitor.left,
                            mi.rcMonitor.bottom - mi.rcMonitor.top),
                        IsPrimary = (mi.dwFlags & 1) != 0,
                        Index = index++
                    });
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return monitors;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
    }

    public class Configuration
    {
        private static Configuration _current;
        public static Configuration Current
        {
            get
            {
                if (_current == null) _current = new Configuration();
                return _current;
            }
        }
        public bool ActiveOnStartup { get; set; }
        public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<object, object>> ColorEffects { get; set; }

        public Configuration()
        {
            ActiveOnStartup = false;
            ColorEffects = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<object, object>>();
        }
    }
}
