// Copyright 2011-2017 Melvyn Laïly
// https://zerowidthjoiner.net

// This file is part of NegativeScreen.

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace NegativeScreen
{
    public class MonitorInfo
    {
        public IntPtr Handle { get; set; }
        public string DeviceName { get; set; }
        public string FriendlyName { get; set; }
        public Rectangle Bounds { get; set; }
        public bool IsPrimary { get; set; }
        public int Index { get; set; }
        public Rectangle PhysicalBounds { get; set; }

        /// <summary>
        /// DPI scale factor for this monitor (physical pixels / logical pixels).
        /// E.g., 1.0 for 100% scaling, 1.25 for 125%, 1.5 for 150%.
        /// </summary>
        public float DpiScaleX
        {
            get
            {
                return Bounds.Width > 0 ? (float)PhysicalBounds.Width / Bounds.Width : 1.0f;
            }
        }

        public float DpiScaleY
        {
            get
            {
                return Bounds.Height > 0 ? (float)PhysicalBounds.Height / Bounds.Height : 1.0f;
            }
        }

        public override string ToString()
        {
            string name = !string.IsNullOrEmpty(FriendlyName) ? FriendlyName : DeviceName;
            string primary = IsPrimary ? " (Primary)" : "";
            return string.Format("{0}: {1}{2} [{3}x{4}]", Index + 1, name, primary, Bounds.Width, Bounds.Height);
        }

        public string UniqueId
        {
            get
            {
                return string.Format("{0}_{1}_{2}", DeviceName, Bounds.X, Bounds.Y);
            }
        }
    }

    public static class MonitorManager
    {
        private static List<MonitorInfo> _monitors;
        private static object _lock = new object();

        public static event EventHandler MonitorsChanged;

        public static IReadOnlyList<MonitorInfo> Monitors
        {
            get
            {
                EnsureInitialized();
                return _monitors.AsReadOnly();
            }
        }

        public static void Refresh()
        {
            lock (_lock)
            {
                _monitors = EnumerateMonitors();
            }
            EventHandler handler = MonitorsChanged;
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        private static void EnsureInitialized()
        {
            if (_monitors == null)
            {
                lock (_lock)
                {
                    if (_monitors == null)
                    {
                        _monitors = EnumerateMonitors();
                    }
                }
            }
        }

        private static List<MonitorInfo> EnumerateMonitors()
        {
            var monitors = new List<MonitorInfo>();
            int index = 0;

            MonitorEnumProc callback = delegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
            {
                var monitorInfoEx = new MONITORINFOEX();
                monitorInfoEx.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));

                if (GetMonitorInfo(hMonitor, ref monitorInfoEx))
                {
                    var bounds = new Rectangle(
                        monitorInfoEx.rcMonitor.left,
                        monitorInfoEx.rcMonitor.top,
                        monitorInfoEx.rcMonitor.right - monitorInfoEx.rcMonitor.left,
                        monitorInfoEx.rcMonitor.bottom - monitorInfoEx.rcMonitor.top
                    );

                    string deviceName = monitorInfoEx.szDevice;
                    string friendlyName = GetMonitorFriendlyName(hMonitor, deviceName);

                    DEVMODE devMode = new DEVMODE();
                    devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                    Rectangle physicalBounds = bounds;
                    if (EnumDisplaySettings(deviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref devMode))
                    {
                        physicalBounds = new Rectangle(devMode.dmPositionX, devMode.dmPositionY, devMode.dmPelsWidth, devMode.dmPelsHeight);
                    }

                    monitors.Add(new MonitorInfo
                    {
                        Handle = hMonitor,
                        DeviceName = deviceName,
                        FriendlyName = friendlyName,
                        Bounds = bounds,
                        PhysicalBounds = physicalBounds,
                        IsPrimary = (monitorInfoEx.dwFlags & 1) != 0,
                        Index = index++
                    });
                }
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return monitors;
        }

        private static string GetMonitorFriendlyName(IntPtr hMonitor, string deviceName)
        {
            // Try DisplayConfig API first (returns EDID-based friendly name)
            try
            {
                string displayConfigName = GetFriendlyNameViaDisplayConfig(deviceName);
                if (!string.IsNullOrEmpty(displayConfigName))
                    return displayConfigName;
            }
            catch { }

            // Fallback to EnumDisplayDevices
            try
            {
                DISPLAY_DEVICE d = new DISPLAY_DEVICE();
                d.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));

                if (EnumDisplayDevices(deviceName, 0, ref d, 0))
                {
                    if (!string.IsNullOrEmpty(d.DeviceString))
                    {
                        return d.DeviceString.Trim();
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Use QueryDisplayConfig + DisplayConfigGetDeviceInfo to get the EDID-based
        /// monitor friendly name, which returns the real name (e.g. "Paperlike H D")
        /// instead of "Generic PnP Monitor".
        /// </summary>
        private static string GetFriendlyNameViaDisplayConfig(string deviceName)
        {
            int pathCount, modeCount;
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (err != 0) return null;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err != 0) return null;

            for (int i = 0; i < pathCount; i++)
            {
                // Get source device name to match with our deviceName (e.g. \\.\DISPLAY1)
                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                sourceName.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                sourceName.header.adapterId = paths[i].sourceInfo.adapterId;
                sourceName.header.id = paths[i].sourceInfo.id;

                if (DisplayConfigGetDeviceInfo_Source(ref sourceName) != 0)
                    continue;

                if (!string.Equals(sourceName.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Match found — get target friendly name
                var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                targetName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                targetName.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                targetName.header.adapterId = paths[i].targetInfo.adapterId;
                targetName.header.id = paths[i].targetInfo.id;

                if (DisplayConfigGetDeviceInfo_Target(ref targetName) == 0)
                {
                    string name = targetName.monitorFriendlyDeviceName;
                    if (!string.IsNullOrEmpty(name))
                        return name.Trim();
                }
            }

            return null;
        }

        public static MonitorInfo GetMonitorFromPoint(Point point)
        {
            foreach (var monitor in Monitors)
            {
                if (monitor.Bounds.Contains(point))
                {
                    return monitor;
                }
            }
            return null;
        }

        public static MonitorInfo GetPrimaryMonitor()
        {
            foreach (var monitor in Monitors)
            {
                if (monitor.IsPrimary)
                {
                    return monitor;
                }
            }
            return Monitors.Count > 0 ? Monitors[0] : null;
        }

        #region P/Invoke

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        // DisplayConfig API for EDID-based monitor names
        private const int QDC_ONLY_ACTIVE_PATHS = 2;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(int flags, out int numPathArrayElements, out int numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(int flags, ref int numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref int numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetDeviceInfo_Target(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetDeviceInfo_Source(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] modeData;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        #endregion
    }
}
