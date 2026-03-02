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
using System.Drawing;
using System.Runtime.InteropServices;

namespace NegativeScreen
{
    public class MagnifierWindow : IDisposable
    {
        private IntPtr _hwnd;
        private MonitorInfo _monitor;
        private bool _isCreated;
        private bool _isEnabled;
        private float _brightness = 0.0f;
        private float _contrast = 1.0f;
        private float[,] _baseColorMatrix;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_POPUP = -2147483648;

        private class MagnifierHostForm : System.Windows.Forms.Form
        {
            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_LAYERED;
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                    cp.ExStyle |= WS_EX_NOACTIVATE;
                    cp.ExStyle |= WS_EX_TOOLWINDOW;
                    return cp;
                }
            }
        }

        private MagnifierHostForm _hostForm;

        private const int HWND_TOPMOST = -1;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SWP_NOACTIVATE = 0x0010;

        public MonitorInfo Monitor
        {
            get { return _monitor; }
        }

        public bool IsEnabled
        {
            get { return _isEnabled; }
        }

        public float Brightness
        {
            get { return _brightness; }
            set
            {
                _brightness = value;
                UpdateEffect();
            }
        }

        public float Contrast
        {
            get { return _contrast; }
            set
            {
                _contrast = value;
                UpdateEffect();
            }
        }

        public MagnifierWindow(MonitorInfo monitor)
        {
            _monitor = monitor;
            _baseColorMatrix = BuiltinMatrices.Identity;
        }

        /// <summary>
        /// Get the magnifier scale for this (non-primary) monitor.
        /// When UseManualMagnifierWorkarounds is true, uses the config values.
        /// Otherwise, auto-computes as: thisMonitor.DpiScale / primaryMonitor.DpiScale.
        /// </summary>
        private float GetMagnifierScaleX()
        {
            if (Configuration.Current.UseManualMagnifierWorkarounds)
                return Configuration.Current.MagnifierScaleX;
            var primary = MonitorManager.GetPrimaryMonitor();
            if (primary == null || primary.DpiScaleX == 0) return 1.0f;
            return primary.DpiScaleX / _monitor.DpiScaleX;
        }

        private float GetMagnifierScaleY()
        {
            if (Configuration.Current.UseManualMagnifierWorkarounds)
                return Configuration.Current.MagnifierScaleY;
            var primary = MonitorManager.GetPrimaryMonitor();
            if (primary == null || primary.DpiScaleY == 0) return 1.0f;
            return primary.DpiScaleY / _monitor.DpiScaleY;
        }

        private bool ShouldApplyMagnifierWorkarounds()
        {
            if (_monitor.IsPrimary) return false;
            if (Configuration.Current.UseManualMagnifierWorkarounds) return true;
            // Auto-detect: apply workarounds if DPI differs from primary
            var primary = MonitorManager.GetPrimaryMonitor();
            if (primary == null) return false;
            return Math.Abs(_monitor.DpiScaleX - primary.DpiScaleX) > 0.01f
                || Math.Abs(_monitor.DpiScaleY - primary.DpiScaleY) > 0.01f;
        }

        public void Create()
        {
            if (_isCreated)
                return;

            IntPtr hInstance = NativeMethods.GetModuleHandle(null);

            _hostForm = new MagnifierHostForm();
            _hostForm.Text = "MagnifierHost_" + _monitor.Index;
            _hostForm.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            _hostForm.ShowInTaskbar = false;
            _hostForm.TopMost = true;
            _hostForm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            _hostForm.Bounds = new Rectangle(_monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height);
            _hostForm.BackColor = Color.Black;
            _hostForm.Show();

            NativeMethods.SetLayeredWindowAttributes(_hostForm.Handle, 0, 255, LayeredWindowAttributeFlags.LWA_ALPHA);

            int magStyle = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

            _hwnd = NativeMethods.CreateWindowEx(
                0,
                NativeMethods.WC_MAGNIFIER,
                "MagnifierWindow_" + _monitor.Index,
                magStyle,
                0, 0,
                _monitor.Bounds.Width,
                _monitor.Bounds.Height,
                _hostForm.Handle,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero
            );

            if (_hwnd == IntPtr.Zero)
            {
                throw new Exception(string.Format("Failed to create magnifier window: {0}", Marshal.GetLastWin32Error()));
            }

            NativeMethods.SetWindowPos(_hostForm.Handle, new IntPtr(HWND_TOPMOST),
                _monitor.Bounds.X, _monitor.Bounds.Y,
                _monitor.Bounds.Width, _monitor.Bounds.Height,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);

            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, LayeredWindowAttributeFlags.LWA_ALPHA);

            var transform = new Transformation(1.0f);
            if (ShouldApplyMagnifierWorkarounds())
            {
                transform.m00 = GetMagnifierScaleX();
                transform.m11 = GetMagnifierScaleY();
            }
            NativeMethods.MagSetWindowTransform(_hwnd, ref transform);

            int sx = _monitor.PhysicalBounds.X;
            int sy = _monitor.PhysicalBounds.Y;
            int sw = _monitor.PhysicalBounds.Width;
            int sh = _monitor.PhysicalBounds.Height;

            if (ShouldApplyMagnifierWorkarounds())
            {
                sx += Configuration.Current.MagnifierOffsetX;
                sy += Configuration.Current.MagnifierOffsetY;
                sw = (int)(sw / GetMagnifierScaleX());
                sh = (int)(sh / GetMagnifierScaleY());
            }

            var sourceRect = new RECT(sx, sy, sx + sw, sy + sh);
            NativeMethods.MagSetWindowSource(_hwnd, sourceRect);

            _isCreated = true;
        }

        public void SetColorEffect(float[,] matrix)
        {
            _baseColorMatrix = matrix;
            UpdateEffect();
        }

        public void SetBrightnessContrast(float brightness, float contrast)
        {
            _brightness = brightness;
            _contrast = contrast;
            UpdateEffect();
        }

        private void UpdateEffect()
        {
            if (!_isCreated || _hwnd == IntPtr.Zero)
                return;

            float[,] finalMatrix = ComputeFinalMatrix();

            ColorEffect effect = new ColorEffect(finalMatrix);
            NativeMethods.MagSetColorEffect(_hwnd, ref effect);

            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        }

        private float[,] ComputeFinalMatrix()
        {
            float[,] brightnessMatrix = CreateBrightnessMatrix(_brightness);
            float[,] contrastMatrix = CreateContrastMatrix(_contrast);

            float[,] result = BuiltinMatrices.Multiply(contrastMatrix, brightnessMatrix);
            result = BuiltinMatrices.Multiply(result, _baseColorMatrix);

            return result;
        }

        /// <summary>
        /// Brightness matrix for the Magnification API 5x5 color matrix.
        /// Uses the translation row (row 4) to shift RGB channels.
        /// Range optimized for e-ink: -0.3 to +0.3 provides fine control
        /// without washing out to full white.
        /// </summary>
        private static float[,] CreateBrightnessMatrix(float brightness)
        {
            return new float[,] {
                { 1, 0, 0, 0, 0 },
                { 0, 1, 0, 0, 0 },
                { 0, 0, 1, 0, 0 },
                { 0, 0, 0, 1, 0 },
                { brightness, brightness, brightness, 0, 1 }
            };
        }

        /// <summary>
        /// Contrast matrix optimized for e-ink displays.
        /// Scales RGB around 0.5 midpoint, preserving blacker blacks
        /// while enhancing text/background separation.
        /// Range: 0.5 (low contrast, washed out) to 2.0 (high contrast, sharp edges)
        /// </summary>
        private static float[,] CreateContrastMatrix(float contrast)
        {
            float offset = 0.5f * (1.0f - contrast);
            return new float[,] {
                { contrast, 0, 0, 0, 0 },
                { 0, contrast, 0, 0, 0 },
                { 0, 0, contrast, 0, 0 },
                { 0, 0, 0, 1, 0 },
                { offset, offset, offset, 0, 1 }
            };
        }

        public void Enable()
        {
            if (!_isCreated || _hwnd == IntPtr.Zero)
            {
                Create();
            }

            var transform = new Transformation(1.0f);
            if (ShouldApplyMagnifierWorkarounds())
            {
                transform.m00 = GetMagnifierScaleX();
                transform.m11 = GetMagnifierScaleY();
            }
            NativeMethods.MagSetWindowTransform(_hwnd, ref transform);

            _isEnabled = true;
            SetColorEffect(_baseColorMatrix);

            if (_hostForm != null)
            {
                NativeMethods.SetWindowPos(_hostForm.Handle, new IntPtr(HWND_TOPMOST),
                    _monitor.Bounds.X, _monitor.Bounds.Y,
                    _monitor.Bounds.Width, _monitor.Bounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }

            int sx = _monitor.PhysicalBounds.X;
            int sy = _monitor.PhysicalBounds.Y;
            int sw = _monitor.PhysicalBounds.Width;
            int sh = _monitor.PhysicalBounds.Height;

            if (ShouldApplyMagnifierWorkarounds())
            {
                sx += Configuration.Current.MagnifierOffsetX;
                sy += Configuration.Current.MagnifierOffsetY;
                sw = (int)(sw / GetMagnifierScaleX());
                sh = (int)(sh / GetMagnifierScaleY());
            }

            var sourceRect = new RECT(sx, sy, sx + sw, sy + sh);
            NativeMethods.MagSetWindowSource(_hwnd, sourceRect);
        }

        public void Disable()
        {
            _isEnabled = false;
            if (_hwnd != IntPtr.Zero)
            {
                SetColorEffect(BuiltinMatrices.Identity);
                var emptyRect = new RECT(0, 0, 0, 0);
                NativeMethods.MagSetWindowSource(_hwnd, emptyRect);
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_hostForm != null)
            {
                if (!_hostForm.IsDisposed)
                {
                    _hostForm.Close();
                    _hostForm.Dispose();
                }
                _hostForm = null;
            }
            _isCreated = false;
        }

        /// <summary>
        /// Re-set the source rect to force DWM to re-capture the screen.
        /// WC_MAGNIFIER only captures one frame per MagSetWindowSource call.
        /// This MUST be called repeatedly on a timer for live screen updates.
        /// </summary>
        public void Refresh()
        {
            if (!_isCreated || !_isEnabled || _hwnd == IntPtr.Zero)
                return;

            int sx = _monitor.PhysicalBounds.X;
            int sy = _monitor.PhysicalBounds.Y;
            int sw = _monitor.PhysicalBounds.Width;
            int sh = _monitor.PhysicalBounds.Height;

            if (ShouldApplyMagnifierWorkarounds())
            {
                sx += Configuration.Current.MagnifierOffsetX;
                sy += Configuration.Current.MagnifierOffsetY;
                sw = (int)(sw / GetMagnifierScaleX());
                sh = (int)(sh / GetMagnifierScaleY());
            }

            var sourceRect = new RECT(sx, sy, sx + sw, sy + sh);
            NativeMethods.MagSetWindowSource(_hwnd, sourceRect);

            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        }

        public void Show()
        {
            if (_hostForm != null && !_hostForm.IsDisposed)
            {
                NativeMethods.SetWindowPos(_hostForm.Handle, new IntPtr(HWND_TOPMOST),
                    _monitor.Bounds.X, _monitor.Bounds.Y,
                    _monitor.Bounds.Width, _monitor.Bounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }
        }

        public void Hide()
        {
            if (_hostForm != null && !_hostForm.IsDisposed)
            {
                NativeMethods.SetWindowPos(_hostForm.Handle, new IntPtr(HWND_TOPMOST),
                    0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0080); // SWP_NOSIZE | SWP_NOMOVE | SWP_HIDEWINDOW
            }
        }

        public void UpdateMonitorBounds(Rectangle newBounds)
        {
            if (!_isCreated || _hwnd == IntPtr.Zero)
                return;

            NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST),
                newBounds.X, newBounds.Y,
                newBounds.Width, newBounds.Height,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);

            int sx = _monitor.PhysicalBounds.X;
            int sy = _monitor.PhysicalBounds.Y;
            int sw = _monitor.PhysicalBounds.Width;
            int sh = _monitor.PhysicalBounds.Height;

            if (ShouldApplyMagnifierWorkarounds())
            {
                sx += Configuration.Current.MagnifierOffsetX;
                sy += Configuration.Current.MagnifierOffsetY;
                sw = (int)(sw / GetMagnifierScaleX());
                sh = (int)(sh / GetMagnifierScaleY());
            }

            var sourceRect = new RECT(sx, sy, sx + sw, sy + sh);
            NativeMethods.MagSetWindowSource(_hwnd, sourceRect);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            _isCreated = false;
            _isEnabled = false;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);
    }
}
