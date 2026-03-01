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
        private float[,] _currentMatrix;
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
        private const int WS_POPUP = -2147483648;

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

        public void Create()
        {
            if (_isCreated)
                return;

            IntPtr hInstance = NativeMethods.GetModuleHandle(null);

            int style = WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
            int exStyle = WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;

            _hwnd = NativeMethods.CreateWindowEx(
                exStyle,
                NativeMethods.WC_MAGNIFIER,
                "MagnifierWindow_" + _monitor.Index,
                style,
                _monitor.Bounds.X,
                _monitor.Bounds.Y,
                _monitor.Bounds.Width,
                _monitor.Bounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero
            );

            if (_hwnd == IntPtr.Zero)
            {
                throw new Exception(string.Format("Failed to create magnifier window: {0}", Marshal.GetLastWin32Error()));
            }

            NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST),
                _monitor.Bounds.X, _monitor.Bounds.Y,
                _monitor.Bounds.Width, _monitor.Bounds.Height,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);

            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, LayeredWindowAttributeFlags.LWA_ALPHA);

            var transform = new Transformation(1.0f);
            NativeMethods.MagSetWindowTransform(_hwnd, ref transform);

            var sourceRect = new RECT(_monitor.Bounds.X, _monitor.Bounds.Y,
                _monitor.Bounds.X + _monitor.Bounds.Width,
                _monitor.Bounds.Y + _monitor.Bounds.Height);
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

        private static float[,] CreateBrightnessMatrix(float brightness)
        {
            return new float[,] {
                { 1, 0, 0, 0, brightness },
                { 0, 1, 0, 0, brightness },
                { 0, 0, 1, 0, brightness },
                { 0, 0, 0, 1, 0 },
                { 0, 0, 0, 0, 1 }
            };
        }

        private static float[,] CreateContrastMatrix(float contrast)
        {
            float offset = (1.0f - contrast) / 2.0f;
            return new float[,] {
                { contrast, 0, 0, 0, offset },
                { 0, contrast, 0, 0, offset },
                { 0, 0, contrast, 0, offset },
                { 0, 0, 0, 1, 0 },
                { 0, 0, 0, 0, 1 }
            };
        }

        public void Enable()
        {
            if (!_isCreated || _hwnd == IntPtr.Zero)
            {
                Create();
            }

            var transform = new Transformation();
            transform.m00 = 1.0f;
            transform.m01 = 0.0f;
            transform.m02 = 0.0f;
            transform.m10 = 0.0f;
            transform.m11 = 1.0f;
            transform.m12 = 0.0f;
            transform.m20 = 0.0f;
            transform.m21 = 0.0f;
            transform.m22 = 1.0f;
            NativeMethods.MagSetWindowTransform(_hwnd, ref transform);

            _isEnabled = true;
            SetColorEffect(_baseColorMatrix);

            NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST),
                _monitor.Bounds.X, _monitor.Bounds.Y,
                _monitor.Bounds.Width, _monitor.Bounds.Height,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);
            var sourceRect = new RECT(_monitor.Bounds.X, _monitor.Bounds.Y,
                _monitor.Bounds.X + _monitor.Bounds.Width,
                _monitor.Bounds.Y + _monitor.Bounds.Height);
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
                _isCreated = false;
            }
        }

        public void Show()
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST),
                    _monitor.Bounds.X, _monitor.Bounds.Y,
                    _monitor.Bounds.Width, _monitor.Bounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }
        }

        public void Hide()
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST),
                    0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0080);
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

            var sourceRect = new RECT(newBounds.X, newBounds.Y,
                newBounds.X + newBounds.Width,
                newBounds.Y + newBounds.Height);
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
