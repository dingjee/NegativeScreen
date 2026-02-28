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
        private bool exiting = false;
        private float[,] currentMatrix = null;

        private bool usePerMonitorMode = false;
        private Dictionary<string, MagnifierWindow> magnifierWindows = new Dictionary<string, MagnifierWindow>();
        private Dictionary<string, MonitorSettings> monitorSettings = new Dictionary<string, MonitorSettings>();
        private HashSet<string> enabledMonitors = new HashSet<string>();

        private float globalBrightness = 0.0f;
        private float globalContrast = 1.0f;

        #region Inter-thread color effect calls

        private ScreenColorEffect invokeColorEffect;
        private bool shouldInvokeColorEffect;
        private object invokeColorEffectLock = new object();

        private void InvokeColorEffect(ScreenColorEffect colorEffect)
        {
            lock (invokeColorEffectLock)
            {
                invokeColorEffect = colorEffect;
                SynchronizeMenuItemCheckboxesWithEffect(colorEffect);
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
            InitializeContextMenu();

            currentMatrix = Configuration.Current.InitialColorEffect.Matrix;
            SynchronizeMenuItemCheckboxesWithEffect(Configuration.Current.InitialColorEffect);

            InitializeMonitorSettings();
            InitializeControlLoop();
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

        private void InitializeContextMenu()
        {
            foreach (var item in Configuration.Current.ColorEffects)
            {
                var menuItem = new ToolStripMenuItem(item.Value.Description)
                {
                    Tag = item.Value,
                    ShortcutKeyDisplayString = item.Key.ToString()
                };
                menuItem.Click += (s, e) =>
                {
                    var effect = (ScreenColorEffect)((ToolStripMenuItem)s).Tag;
                    InvokeColorEffect(effect);
                };
                this.changeModeToolStripMenuItem.DropDownItems.Add(menuItem);
            }

            BuildMonitorMenu();
        }

        private void BuildMonitorMenu()
        {
            selectMonitorsToolStripMenuItem.DropDownItems.Clear();

            var allMonitorsItem = new ToolStripMenuItem("All Monitors")
            {
                Tag = "all",
                Checked = !usePerMonitorMode
            };
            allMonitorsItem.Click += (s, e) =>
            {
                usePerMonitorMode = false;
                enabledMonitors.Clear();
                UpdateMonitorMenuChecks();
                ApplyCurrentEffectToAllMonitors();
            };
            selectMonitorsToolStripMenuItem.DropDownItems.Add(allMonitorsItem);

            selectMonitorsToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());

            foreach (var monitor in MonitorManager.Monitors)
            {
                string id = monitor.UniqueId;
                var monitorItem = new ToolStripMenuItem(monitor.ToString())
                {
                    Tag = id,
                    Checked = enabledMonitors.Contains(id) || !usePerMonitorMode
                };
                monitorItem.Click += (s, e) =>
                {
                    usePerMonitorMode = true;
                    var item = (ToolStripMenuItem)s;
                    string monitorId = (string)item.Tag;

                    if (enabledMonitors.Contains(monitorId))
                    {
                        enabledMonitors.Remove(monitorId);
                        DisableMagnifierWindow(monitorId);
                    }
                    else
                    {
                        enabledMonitors.Add(monitorId);
                        EnableMagnifierWindow(monitorId);
                    }

                    UpdateMonitorMenuChecks();
                };
                selectMonitorsToolStripMenuItem.DropDownItems.Add(monitorItem);
            }

            selectMonitorsToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());

            var brightnessItem = new ToolStripMenuItem("Adjust Brightness/Contrast...");
            brightnessItem.Click += (s, e) =>
            {
                ShowBrightnessContrastDialog();
            };
            selectMonitorsToolStripMenuItem.DropDownItems.Add(brightnessItem);
        }

        private void UpdateMonitorMenuChecks()
        {
            foreach (ToolStripMenuItem item in selectMonitorsToolStripMenuItem.DropDownItems)
            {
                string tag = item.Tag as string;
                if (tag != null)
                {
                    if (tag == "all")
                    {
                        item.Checked = !usePerMonitorMode;
                    }
                    else
                    {
                        item.Checked = enabledMonitors.Contains(tag) || !usePerMonitorMode;
                    }
                }
            }
        }

        private void ShowBrightnessContrastDialog()
        {
            using (var dialog = new BrightnessContrastDialog(globalBrightness, globalContrast))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    globalBrightness = dialog.Brightness;
                    globalContrast = dialog.Contrast;
                    ApplyCurrentEffectToAllMonitors();
                }
            }
        }

        private void InitializeControlLoop()
        {
            System.Threading.Thread t = new System.Threading.Thread(ControlLoop);
            t.SetApartmentState((System.Threading.ApartmentState.STA));
            t.Start();
        }

        private void ControlLoop()
        {
            if (!Configuration.Current.ActiveOnStartup)
            {
                mainLoopPaused = true;
                PauseLoop();
            }

            while (!exiting)
            {
                if (!NativeMethods.MagInitialize())
                {
                    throw new Exception("MagInitialize()", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }

                try
                {
                    if (usePerMonitorMode)
                    {
                        CreateMagnifierWindowsForEnabledMonitors();
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

                while (!exiting)
                {
                    System.Threading.Thread.Sleep(Configuration.Current.MainLoopRefreshTime);
                    DoMagnifierApiInvoke();
                    RefreshMagnifierWindows();

                    if (mainLoopPaused)
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
                        catch (CannotChangeColorEffectException)
                        {
                        }
                        if (!NativeMethods.MagUninitialize())
                        {
                            throw new Exception("MagUninitialize()", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                        }
                        PauseLoop();
                        break;
                    }
                }
            }
            this.Invoke((Action)(() =>
            {
                this.Dispose();
                Application.Exit();
            }));
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
                    kvp.Value.Show();
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

        private void EnableMagnifierWindow(string monitorId)
        {
            var monitor = MonitorManager.Monitors.FirstOrDefault(m => m.UniqueId == monitorId);
            if (monitor != null)
            {
                if (!magnifierWindows.ContainsKey(monitorId))
                {
                    var window = new MagnifierWindow(monitor);
                    window.Create();
                    magnifierWindows[monitorId] = window;
                }

                MonitorSettings settings;
                if (monitorSettings.TryGetValue(monitorId, out settings))
                {
                    magnifierWindows[monitorId].SetColorEffect(settings.ColorEffect);
                    magnifierWindows[monitorId].SetBrightnessContrast(settings.Brightness, settings.Contrast);
                }
                else
                {
                    magnifierWindows[monitorId].SetColorEffect(currentMatrix);
                    magnifierWindows[monitorId].SetBrightnessContrast(globalBrightness, globalContrast);
                }
                magnifierWindows[monitorId].Enable();
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

        private void ApplyCurrentEffectToAllMonitors()
        {
            if (usePerMonitorMode)
            {
                foreach (var monitorId in enabledMonitors)
                {
                    MagnifierWindow window;
                    if (magnifierWindows.TryGetValue(monitorId, out window))
                    {
                        float[,] finalMatrix = BuiltinMatrices.ApplyBrightnessContrast(currentMatrix, globalBrightness, globalContrast);
                        window.SetColorEffect(currentMatrix);
                        window.SetBrightnessContrast(globalBrightness, globalContrast);
                    }
                }
            }
            else
            {
                float[,] finalMatrix = BuiltinMatrices.ApplyBrightnessContrast(currentMatrix, globalBrightness, globalContrast);
                SafeChangeColorEffect(finalMatrix);
            }
        }

        private void PauseLoop()
        {
            while (mainLoopPaused && !exiting)
            {
                System.Threading.Thread.Sleep(Configuration.Current.MainLoopRefreshTime);
                DoMagnifierApiInvoke();
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

        private void HandleDisplayChange()
        {
            MonitorManager.Refresh();
            BuildMonitorMenu();

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
                }
            }
        }

        public void Exit()
        {
            if (!mainLoopPaused)
            {
                mainLoopPaused = true;
            }
            this.exiting = true;
        }

        public void Toggle()
        {
            this.mainLoopPaused = !mainLoopPaused;
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

        private void SynchronizeMenuItemCheckboxesWithEffect(ScreenColorEffect effect)
        {
            ToolStripMenuItem currentItem = null;
            foreach (ToolStripMenuItem effectItem in this.changeModeToolStripMenuItem.DropDownItems)
            {
                effectItem.Checked = false;
                var castItem = (ScreenColorEffect)effectItem.Tag;
                if (castItem.Matrix == effect.Matrix) currentItem = effectItem;
            }
            if (currentItem != null)
            {
                currentItem.Checked = true;
            }
        }

        #region Event Handlers

        private void OverlayManager_FormClosed(object sender, FormClosedEventArgs e)
        {
            Exit();
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

    public class BrightnessContrastDialog : Form
    {
        private TrackBar brightnessTrackBar;
        private TrackBar contrastTrackBar;
        private Label brightnessLabel;
        private Label contrastLabel;
        private Button okButton;
        private Button cancelButton;

        public float Brightness { get; private set; }
        public float Contrast { get; private set; }

        public BrightnessContrastDialog(float currentBrightness, float currentContrast)
        {
            Brightness = currentBrightness;
            Contrast = currentContrast;

            InitializeComponents();
        }

        private void InitializeComponents()
        {
            this.Text = "Adjust Brightness/Contrast";
            this.Size = new Size(350, 200);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var brightnessLabelTitle = new Label
            {
                Text = "Brightness:",
                Location = new Point(20, 20),
                Size = new Size(80, 20)
            };

            brightnessTrackBar = new TrackBar
            {
                Minimum = -100,
                Maximum = 100,
                Value = (int)(Brightness * 100),
                Location = new Point(100, 20),
                Size = new Size(150, 45)
            };
            brightnessTrackBar.Scroll += BrightnessTrackBar_Scroll;

            brightnessLabel = new Label
            {
                Text = Brightness.ToString("F2"),
                Location = new Point(260, 20),
                Size = new Size(60, 20)
            };

            var contrastLabelTitle = new Label
            {
                Text = "Contrast:",
                Location = new Point(20, 70),
                Size = new Size(80, 20)
            };

            contrastTrackBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 200,
                Value = (int)(Contrast * 100),
                Location = new Point(100, 70),
                Size = new Size(150, 45)
            };
            contrastTrackBar.Scroll += ContrastTrackBar_Scroll;

            contrastLabel = new Label
            {
                Text = Contrast.ToString("F2"),
                Location = new Point(260, 70),
                Size = new Size(60, 20)
            };

            okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(150, 130),
                Size = new Size(75, 25)
            };

            cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(240, 130),
                Size = new Size(75, 25)
            };

            this.Controls.AddRange(new Control[] {
                brightnessLabelTitle, brightnessTrackBar, brightnessLabel,
                contrastLabelTitle, contrastTrackBar, contrastLabel,
                okButton, cancelButton
            });
        }

        private void BrightnessTrackBar_Scroll(object sender, EventArgs e)
        {
            Brightness = brightnessTrackBar.Value / 100.0f;
            brightnessLabel.Text = Brightness.ToString("F2");
        }

        private void ContrastTrackBar_Scroll(object sender, EventArgs e)
        {
            Contrast = contrastTrackBar.Value / 100.0f;
            contrastLabel.Text = Contrast.ToString("F2");
        }
    }
}
