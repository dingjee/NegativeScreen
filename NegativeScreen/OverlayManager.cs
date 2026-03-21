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
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Collections.Concurrent;
using System.Linq;

namespace NegativeScreen
{
    partial class OverlayManager : Form
    {
        private AboutBox aboutForm = new AboutBox();

        private bool mainLoopPaused = false;
        private bool magInitialized = false;
        private object magInitLock = new object();
        private volatile bool brightnessContrastNeedsUpdate = false;
        private bool exiting = false;
        private float[,] currentMatrix = null;

        /// <summary>
        /// Whether the color inversion effect is currently active.
        /// When false, brightness/contrast may still be active via Identity matrix.
        /// </summary>
        private bool colorEffectActive = false;
        /// <summary>
        /// Saved color matrix when color effect is toggled off, so it can be restored on toggle on.
        /// </summary>
        private float[,] savedColorMatrix = null;

        private bool usePerMonitorMode = false;
        private Dictionary<string, MagnifierWindow> magnifierWindows = new Dictionary<string, MagnifierWindow>();
        private Dictionary<string, MonitorSettings> monitorSettings = new Dictionary<string, MonitorSettings>();
        private HashSet<string> enabledMonitors = new HashSet<string>();

        private float globalBrightness = 0.0f;
        private float globalContrast = 1.0f;

        private const string TargetMonitorName = "Paperlike";

        private MonitorInfo FindPaperlikeMonitor()
        {
            return MonitorManager.Monitors.FirstOrDefault(
                m => m.FriendlyName != null && m.FriendlyName.IndexOf(TargetMonitorName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsPaperlikeConnected()
        {
            return FindPaperlikeMonitor() != null;
        }

        #region Inter-thread color effect calls

        private ScreenColorEffect invokeColorEffect;
        private bool shouldInvokeColorEffect;
        private object invokeColorEffectLock = new object();

        private void InvokeColorEffect(ScreenColorEffect colorEffect)
        {
            lock (invokeColorEffectLock)
            {
                invokeColorEffect = colorEffect;
                shouldInvokeColorEffect = true;
            }
        }

        private void DoMagnifierApiInvoke()
        {
            lock (invokeColorEffectLock)
            {
                if (shouldInvokeColorEffect)
                {
                    SafeChangeColorEffect(invokeColorEffect.Matrix);
                }
                shouldInvokeColorEffect = false;
            }
        }

        #endregion

        private static OverlayManager _Instance;
        public static OverlayManager Instance
        {
            get
            {
                Initialize();
                return _Instance;
            }
        }

        public static void Initialize()
        {
            if (_Instance == null)
            {
                _Instance = new OverlayManager();
            }
        }

        private OverlayManager()
        {
            InitializeComponent();
            this.Icon = Properties.Resources.Icon;
            trayIcon.Icon = Properties.Resources.Icon;

            TryRegisterHotKeys();

            toggleInversionToolStripMenuItem.ShortcutKeyDisplayString = Configuration.Current.ToggleKey.ToString();
            exitToolStripMenuItem.ShortcutKeyDisplayString = Configuration.Current.ExitKey.ToString();

            currentMatrix = BuiltinMatrices.Identity;

            // Load saved brightness/contrast from conf BEFORE building the UI
            // so that the slider positions reflect the saved values.
            globalBrightness = Configuration.Current.SavedBrightness;
            globalContrast = Configuration.Current.SavedContrast;

            InitializeContextMenu();

            InitializeMonitorSettings();
            InitializeControlLoop();

            // Auto-detect Paperlike HD and start overlay
            AutoDetectAndStartPaperlike();
        }

        private void AutoDetectAndStartPaperlike()
        {
            MonitorManager.Refresh();
            var paperlike = FindPaperlikeMonitor();
            if (paperlike != null)
            {
                usePerMonitorMode = true;
                enabledMonitors.Clear();
                enabledMonitors.Add(paperlike.UniqueId);
                colorEffectActive = false;
                currentMatrix = BuiltinMatrices.Identity;
                mainLoopPaused = false;
                StartEffect();
            }
            else
            {
                mainLoopPaused = true;
                colorEffectActive = false;
            }
            BuildMonitorMenu();
            UpdateMonitorMenuChecks();
        }

        private void InitializeMonitorSettings()
        {
            MonitorManager.Refresh();
            foreach (var monitor in MonitorManager.Monitors)
            {
                string id = monitor.UniqueId;
                if (!monitorSettings.ContainsKey(id))
                {
                    monitorSettings[id] = new MonitorSettings
                    {
                        MonitorId = id,
                        Brightness = 0.0f,
                        Contrast = 1.0f,
                        ColorEffect = currentMatrix,
                        IsEnabled = true
                    };
                }
            }
        }

        public void ShowBalloonTip(int timeout, string title, string message, ToolTipIcon icon)
        {
            trayIcon.ShowBalloonTip(timeout, title, message, icon);
        }

        private void TryRegisterHotKeys()
        {
            StringBuilder sb = new StringBuilder("Unable to register one or more hot keys:\n");
            bool success = true;
            success &= TryRegisterHotKeyAppendError(Configuration.Current.ToggleKey, sb);
            success &= TryRegisterHotKeyAppendError(Configuration.Current.ExitKey, sb);
            foreach (var item in Configuration.Current.ColorEffects)
            {
                if (item.Key != HotKey.Empty)
                {
                    success &= TryRegisterHotKeyAppendError(item.Key, sb);
                }
            }
            if (!success)
            {
                ShowBalloonTip(4000, "Warning", sb.ToString(), ToolTipIcon.Warning);
            }
        }

        private bool TryRegisterHotKeyAppendError(HotKey hotkey, StringBuilder appendErrorTo)
        {
            AlreadyRegisteredHotKeyException ex;
            if (!TryRegisterHotKey(hotkey, out ex))
            {
                appendErrorTo.AppendFormat(" - \"{0}\" : {1}", ex.HotKey, (ex.InnerException == null ? "" : ex.InnerException.Message));
                return false;
            }
            return true;
        }

        // Monitor menu items inserted at this index in the context menu
        private int monitorMenuInsertIndex = -1;
        private int monitorMenuItemCount = 0;

        // Inline brightness/contrast slider hosts
        private TrackBar brightnessTrackBar;
        private TrackBar contrastTrackBar;
        private Label brightnessValueLabel;
        private Label contrastValueLabel;

        private void InitializeContextMenu()
        {
            trayIconContextMenuStrip.Items.Clear();

            // Toggle Inversion
            trayIconContextMenuStrip.Items.Add(toggleInversionToolStripMenuItem);

            // Separator
            trayIconContextMenuStrip.Items.Add(toolStripSeparator1);

            // Monitor items will be inserted here
            monitorMenuInsertIndex = trayIconContextMenuStrip.Items.Count;
            BuildMonitorMenu();

            // Separator before sliders
            trayIconContextMenuStrip.Items.Add(toolStripSeparator2);

            // Brightness slider
            BuildBrightnessSlider();

            // Contrast slider
            BuildContrastSlider();

            // Separator
            trayIconContextMenuStrip.Items.Add(toolStripSeparator3);

            // Edit Configuration
            trayIconContextMenuStrip.Items.Add(editConfigurationToolStripMenuItem);

            // About
            trayIconContextMenuStrip.Items.Add(aboutToolStripMenuItem);

            // Separator + Exit
            var sep4 = new ToolStripSeparator();
            trayIconContextMenuStrip.Items.Add(sep4);
            trayIconContextMenuStrip.Items.Add(exitToolStripMenuItem);

            // Hide magnifier windows when context menu opens to fix positioning/scaling
            trayIconContextMenuStrip.Opening += (s, e) =>
            {
                foreach (var window in magnifierWindows.Values)
                {
                    if (window.IsEnabled) window.Hide();
                }
            };
            trayIconContextMenuStrip.Closed += (s, e) =>
            {
                foreach (var window in magnifierWindows.Values)
                {
                    if (window.IsEnabled) window.Show();
                }
            };
        }

        private void BuildBrightnessSlider()
        {
            // Label row
            var brightnessLabelItem = new ToolStripMenuItem("Brightness")
            {
                Enabled = false
            };
            trayIconContextMenuStrip.Items.Add(brightnessLabelItem);

            // Slider + value in a panel
            var panel = new Panel { Width = 230, Height = 30 };
            panel.Padding = new Padding(0);

            // Slider direction: positive slider = brighter for user = negative internal value
            brightnessTrackBar = new TrackBar
            {
                Minimum = -30,
                Maximum = 30,
                Value = (int)(-globalBrightness * 100),
                TickFrequency = 5,
                SmallChange = 5,
                LargeChange = 5,
                Location = new Point(0, 2),
                Size = new Size(170, 26),
                AutoSize = false
            };
            brightnessTrackBar.Scroll += (s, e) =>
            {
                globalBrightness = -brightnessTrackBar.Value / 100.0f;
                brightnessValueLabel.Text = brightnessTrackBar.Value > 0
                    ? "+" + (brightnessTrackBar.Value / 100.0f).ToString("F2")
                    : (brightnessTrackBar.Value / 100.0f).ToString("F2");
                ApplyCurrentEffectToAllMonitors();
                Configuration.SaveValue("SavedBrightness", globalBrightness.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            };

            float displayVal = -globalBrightness;
            brightnessValueLabel = new Label
            {
                Text = displayVal >= 0 ? "+" + displayVal.ToString("F2") : displayVal.ToString("F2"),
                Location = new Point(172, 6),
                Size = new Size(55, 18),
                TextAlign = System.Drawing.ContentAlignment.MiddleRight
            };

            panel.Controls.Add(brightnessTrackBar);
            panel.Controls.Add(brightnessValueLabel);

            var host = new ToolStripControlHost(panel)
            {
                AutoSize = false,
                Size = new Size(230, 30)
            };
            trayIconContextMenuStrip.Items.Add(host);
        }

        private void BuildContrastSlider()
        {
            var contrastLabelItem = new ToolStripMenuItem("Contrast")
            {
                Enabled = false
            };
            trayIconContextMenuStrip.Items.Add(contrastLabelItem);

            var panel = new Panel { Width = 220, Height = 30 };
            panel.Padding = new Padding(0);

            int savedContrastVal = (int)(globalContrast * 100);
            if (savedContrastVal < 70) savedContrastVal = 70;
            if (savedContrastVal > 130) savedContrastVal = 130;
            globalContrast = savedContrastVal / 100.0f;

            contrastTrackBar = new TrackBar
            {
                Minimum = 70,
                Maximum = 130,
                Value = savedContrastVal,
                TickFrequency = 5,
                SmallChange = 1,
                LargeChange = 5,
                Location = new Point(0, 2),
                Size = new Size(170, 26),
                AutoSize = false
            };
            contrastTrackBar.Scroll += (s, e) =>
            {
                globalContrast = contrastTrackBar.Value / 100.0f;
                contrastValueLabel.Text = globalContrast.ToString("F2");
                ApplyCurrentEffectToAllMonitors();
                Configuration.SaveValue("SavedContrast", globalContrast.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            };

            contrastValueLabel = new Label
            {
                Text = globalContrast.ToString("F2"),
                Location = new Point(172, 6),
                Size = new Size(45, 18),
                TextAlign = System.Drawing.ContentAlignment.MiddleRight
            };

            panel.Controls.Add(contrastTrackBar);
            panel.Controls.Add(contrastValueLabel);

            var host = new ToolStripControlHost(panel)
            {
                AutoSize = false,
                Size = new Size(220, 30)
            };
            trayIconContextMenuStrip.Items.Add(host);
        }

        private void BuildMonitorMenu()
        {
            // Remove old monitor items
            for (int i = 0; i < monitorMenuItemCount; i++)
            {
                trayIconContextMenuStrip.Items.RemoveAt(monitorMenuInsertIndex);
            }
            monitorMenuItemCount = 0;

            // Only show Paperlike HD when it's connected
            var paperlike = FindPaperlikeMonitor();
            if (paperlike != null)
            {
                string id = paperlike.UniqueId;
                bool isChecked = enabledMonitors.Contains(id);
                var monitorItem = new ToolStripMenuItem(paperlike.ToString())
                {
                    Tag = id,
                    Checked = isChecked,
                    Enabled = false // Always selected, no manual toggle
                };
                trayIconContextMenuStrip.Items.Insert(monitorMenuInsertIndex, monitorItem);
                monitorMenuItemCount = 1;
            }
        }

        // Monitor menu item is display-only for Paperlike HD (no click handler needed)

        private void UpdateMonitorMenuChecks()
        {
            for (int i = monitorMenuInsertIndex; i < monitorMenuInsertIndex + monitorMenuItemCount; i++)
            {
                var menuItem = trayIconContextMenuStrip.Items[i] as ToolStripMenuItem;
                if (menuItem != null)
                {
                    string tag = menuItem.Tag as string;
                    if (tag != null)
                    {
                        menuItem.Checked = enabledMonitors.Contains(tag);
                    }
                }
            }
        }

        private void CreateMagnifierWindowsForEnabledMonitors()
        {
            MonitorManager.Refresh();

            foreach (var monitor in MonitorManager.Monitors)
            {
                string id = monitor.UniqueId;
                if (enabledMonitors.Contains(id))
                {
                    if (!magnifierWindows.ContainsKey(id))
                    {
                        var window = new MagnifierWindow(monitor);
                        window.Create();
                        magnifierWindows[id] = window;
                    }

                    MonitorSettings settings;
                    if (monitorSettings.TryGetValue(id, out settings))
                    {
                        magnifierWindows[id].SetColorEffect(settings.ColorEffect);
                        magnifierWindows[id].SetBrightnessContrast(settings.Brightness, settings.Contrast);
                    }
                    else
                    {
                        magnifierWindows[id].SetColorEffect(currentMatrix);
                        magnifierWindows[id].SetBrightnessContrast(globalBrightness, globalContrast);
                    }
                    magnifierWindows[id].Enable();
                }
            }
        }

        private void RefreshMagnifierWindows()
        {
            foreach (var kvp in magnifierWindows)
            {
                if (kvp.Value.IsEnabled)
                {
                    kvp.Value.Refresh();
                }
            }
        }

        private void DisableAllMagnifierWindows()
        {
            foreach (var window in magnifierWindows.Values)
            {
                window.Disable();
            }
        }

        /// <summary>
        /// 在 ControlLoop 线程上同步 magnifierWindows 与 enabledMonitors。
        /// 确保所有窗口的创建和销毁都在同一个线程上，避免跨线程 DestroyWindow 失败。
        /// </summary>
        private void SyncMagnifierWindowsWithEnabledMonitors()
        {
            // 禁用不再启用的显示器的窗口
            foreach (var kvp in magnifierWindows.ToList())
            {
                if (!enabledMonitors.Contains(kvp.Key) && kvp.Value.IsEnabled)
                {
                    kvp.Value.Disable();
                }
            }

            // 为新启用的显示器创建/启用窗口
            foreach (var monitorId in enabledMonitors.ToList())
            {
                MagnifierWindow window;
                if (!magnifierWindows.TryGetValue(monitorId, out window) || !window.IsEnabled)
                {
                    var monitor = MonitorManager.Monitors.FirstOrDefault(m => m.UniqueId == monitorId);
                    if (monitor != null)
                    {
                        if (window == null)
                        {
                            window = new MagnifierWindow(monitor);
                            magnifierWindows[monitorId] = window;
                        }
                        window.SetColorEffect(currentMatrix);
                        window.SetBrightnessContrast(globalBrightness, globalContrast);
                        window.Enable();
                    }
                }
            }
        }

        private void DisableMagnifierWindow(string monitorId)
        {
            MagnifierWindow window;
            if (magnifierWindows.TryGetValue(monitorId, out window))
            {
                window.Disable();
            }
        }

        /// <summary>
        /// Returns true if brightness or contrast differ from their defaults (0.0 and 1.0).
        /// </summary>
        private bool HasNonDefaultBrightnessContrast()
        {
            return Math.Abs(globalBrightness) > 0.001f || Math.Abs(globalContrast - 1.0f) > 0.001f;
        }

        private void ApplyCurrentEffectToAllMonitors()
        {
            if (mainLoopPaused && HasNonDefaultBrightnessContrast())
            {
                // Brightness/contrast changed while paused — need to activate overlay with Identity color
                mainLoopPaused = false;
                colorEffectActive = false;
                currentMatrix = BuiltinMatrices.Identity;
                StartEffect();
                return;
            }

            if (usePerMonitorMode)
            {
                brightnessContrastNeedsUpdate = true;
            }
            else
            {
                float[,] finalMatrix = BuiltinMatrices.ApplyBrightnessContrast(currentMatrix, globalBrightness, globalContrast);
                SafeChangeColorEffect(finalMatrix);
            }
        }

        public bool TryRegisterHotKey(HotKey hotkey, out AlreadyRegisteredHotKeyException exception)
        {
            bool ok = NativeMethods.RegisterHotKey(this.Handle, hotkey.Id, hotkey.Modifiers, hotkey.Key);
            if (!ok)
            {
                exception = new AlreadyRegisteredHotKeyException(hotkey, Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                return false;
            }
            else
            {
                exception = null;
                return true;
            }
        }

        private void UnregisterHotKeys()
        {
            try
            {
                NativeMethods.UnregisterHotKey(this.Handle, Configuration.Current.ToggleKey.Id);
                NativeMethods.UnregisterHotKey(this.Handle, Configuration.Current.ExitKey.Id);
            }
            catch (Exception) { }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case (int)WindowMessage.WM_HOTKEY:
                    int HotKeyId = (int)m.WParam;
                    switch (HotKeyId)
                    {
                        case HotKey.ExitKeyId:
                            Exit();
                            break;
                        case HotKey.ToggleKeyId:
                            Toggle();
                            break;
                        default:
                            foreach (var item in Configuration.Current.ColorEffects)
                            {
                                if (item.Key.Id == HotKeyId)
                                {
                                    InvokeColorEffect(item.Value);
                                }
                            }
                            break;
                    }
                    break;
                case (int)WindowMessage.WM_DISPLAYCHANGE:
                    HandleDisplayChange();
                    break;
            }
            base.WndProc(ref m);
        }

        private Timer logicTimer;

        private void InitializeControlLoop()
        {
            logicTimer = new Timer();
            logicTimer.Interval = Configuration.Current.MainLoopRefreshTime;
            logicTimer.Tick += LogicTimer_Tick;
            logicTimer.Start();
        }

        private void LogicTimer_Tick(object sender, EventArgs e)
        {
            DoMagnifierApiInvoke();

            if (!exiting && !mainLoopPaused)
            {
                if (usePerMonitorMode)
                {
                    SyncMagnifierWindowsWithEnabledMonitors();
                    if (brightnessContrastNeedsUpdate)
                    {
                        foreach (var window in magnifierWindows.Values)
                        {
                            window.SetColorEffect(currentMatrix);
                            window.SetBrightnessContrast(globalBrightness, globalContrast);
                        }
                        brightnessContrastNeedsUpdate = false;
                    }
                    RefreshMagnifierWindows();
                }
            }
        }

        private void HandleDisplayChange()
        {
            MonitorManager.Refresh();

            // Update bounds for existing windows, remove disconnected ones
            foreach (var kvp in magnifierWindows.ToList())
            {
                var monitor = MonitorManager.Monitors.FirstOrDefault(m => m.UniqueId == kvp.Key);
                if (monitor != null)
                {
                    kvp.Value.UpdateMonitorBounds(monitor.Bounds);
                }
                else
                {
                    kvp.Value.Dispose();
                    magnifierWindows.Remove(kvp.Key);
                    enabledMonitors.Remove(kvp.Key);
                }
            }

            var paperlike = FindPaperlikeMonitor();
            if (paperlike != null && !enabledMonitors.Contains(paperlike.UniqueId))
            {
                // Paperlike HD just connected — auto-start overlay
                AutoDetectAndStartPaperlike();
            }
            else if (paperlike == null && enabledMonitors.Count > 0)
            {
                // Paperlike HD disconnected — destroy overlay
                mainLoopPaused = true;
                colorEffectActive = false;
                StopEffect();
                enabledMonitors.Clear();
            }

            BuildMonitorMenu();
            UpdateMonitorMenuChecks();
        }

        public void Toggle()
        {
            // Do nothing if Paperlike HD is not connected / overlay not running
            if (mainLoopPaused || enabledMonitors.Count == 0)
                return;

            if (colorEffectActive)
            {
                // Turning OFF color inversion — keep overlay for brightness/contrast
                colorEffectActive = false;
                savedColorMatrix = currentMatrix;
                currentMatrix = BuiltinMatrices.Identity;
                ApplyCurrentEffectToAllMonitors();
            }
            else
            {
                // Turning ON color inversion
                colorEffectActive = true;
                currentMatrix = savedColorMatrix ?? Configuration.Current.InitialColorEffect.Matrix;
                savedColorMatrix = null;
                ApplyCurrentEffectToAllMonitors();
            }
        }

        private void StartEffect()
        {
            lock (magInitLock)
            {
                if (!magInitialized)
                {
                    if (!NativeMethods.MagInitialize())
                    {
                        throw new Exception("MagInitialize()", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                    }
                    magInitialized = true;
                }
            }

            // Ensure fullscreen transform is identity so primary screen UI is not distorted
            NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);

            try
            {
                if (usePerMonitorMode)
                {
                    CreateMagnifierWindowsForEnabledMonitors();
                    RefreshMagnifierWindows();
                }
                else
                {
                    ToggleColorEffect(fromNormal: true);
                }
            }
            catch (CannotChangeColorEffectException)
            {
                mainLoopPaused = true;
            }
        }

        private void StopEffect()
        {
            try
            {
                if (usePerMonitorMode)
                {
                    DisableAllMagnifierWindows();
                }
                else
                {
                    ToggleColorEffect(fromNormal: false);
                }
            }
            catch (CannotChangeColorEffectException) { }

            NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);
        }

        /// <summary>
        /// Stop the color inversion effect but keep the overlay running
        /// so that brightness/contrast remain effective.
        /// </summary>
        private void StopColorEffectKeepOverlay()
        {
            try
            {
                if (usePerMonitorMode)
                {
                    // Switch all magnifier windows to Identity + brightness/contrast
                    foreach (var window in magnifierWindows.Values)
                    {
                        window.SetColorEffect(BuiltinMatrices.Identity);
                        window.SetBrightnessContrast(globalBrightness, globalContrast);
                    }
                }
                else
                {
                    // Apply Identity with brightness/contrast
                    float[,] bcMatrix = BuiltinMatrices.ApplyBrightnessContrast(
                        BuiltinMatrices.Identity, globalBrightness, globalContrast);
                    BuiltinMatrices.ChangeColorEffect(bcMatrix);
                }
            }
            catch (CannotChangeColorEffectException) { }
        }

        public void Enable()
        {
            this.mainLoopPaused = false;
        }

        public void Disable()
        {
            this.mainLoopPaused = true;
        }

        public bool TrySetColorEffectByName(string colorEffectName)
        {
            var effect = Configuration.Current.ColorEffects.Where(x => x.Value.Description == colorEffectName);
            if (effect.Any())
            {
                InvokeColorEffect(effect.First().Value);
                return true;
            }
            else
            {
                return false;
            }
        }

        private void ToggleColorEffect(bool fromNormal)
        {
            // 确保全屏放大倍率为 1.0（不放大），仅应用颜色效果
            NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);

            if (fromNormal)
            {
                float[,] finalMatrix = BuiltinMatrices.ApplyBrightnessContrast(currentMatrix, globalBrightness, globalContrast);

                if (Configuration.Current.SmoothToggles)
                {
                    BuiltinMatrices.InterpolateColorEffect(BuiltinMatrices.Identity, finalMatrix);
                }
                else
                {
                    BuiltinMatrices.ChangeColorEffect(finalMatrix);
                }
            }
            else
            {
                if (Configuration.Current.SmoothToggles)
                {
                    BuiltinMatrices.InterpolateColorEffect(currentMatrix, BuiltinMatrices.Identity);
                }
                else
                {
                    BuiltinMatrices.ChangeColorEffect(BuiltinMatrices.Identity);
                }
            }
        }

        private void SafeChangeColorEffect(float[,] matrix)
        {
            if (!mainLoopPaused && !exiting)
            {
                try
                {
                    float[,] finalMatrix = BuiltinMatrices.ApplyBrightnessContrast(matrix, globalBrightness, globalContrast);

                    if (Configuration.Current.SmoothTransitions)
                    {
                        BuiltinMatrices.InterpolateColorEffect(currentMatrix, finalMatrix);
                    }
                    else
                    {
                        BuiltinMatrices.ChangeColorEffect(finalMatrix);
                    }
                }
                catch (CannotChangeColorEffectException)
                {
                    return;
                }
            }
            currentMatrix = matrix;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
                UnregisterHotKeys();

                foreach (var window in magnifierWindows.Values)
                {
                    window.Dispose();
                }
                magnifierWindows.Clear();

                NativeMethods.MagUninitialize();
            }
            base.Dispose(disposing);
        }


        #region Event Handlers

        private void OverlayManager_FormClosed(object sender, FormClosedEventArgs e)
        {
            Exit();
        }

        public void Exit()
        {
            this.exiting = true;

            if (logicTimer != null)
            {
                logicTimer.Stop();
                logicTimer.Dispose();
                logicTimer = null;
            }

            StopEffect();
            UnregisterHotKeys();

            lock (magInitLock)
            {
                if (magInitialized)
                {
                    NativeMethods.MagUninitialize();
                    magInitialized = false;
                }
            }

            this.trayIcon.Visible = false;
            Application.Exit();
        }

        private void toggleInversionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Toggle();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Exit();
        }

        private void editConfigurationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Configuration.UserEditCurrentConfiguration();
        }

        private void trayIcon_MouseClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Toggle();
            }
        }

        #endregion

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!aboutForm.Visible)
            {
                aboutForm.ShowDialog();
            }
        }
    }

    public class MonitorSettings
    {
        public string MonitorId { get; set; }
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float[,] ColorEffect { get; set; }
        public bool IsEnabled { get; set; }
    }
}
