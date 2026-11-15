// See-through window background, so the panel can have rounded corners and an opacity.
// Adapted from WinUIEx's TransparentTintBackdrop (https://github.com/dotMorten/WinUIEx),
// MIT License, Copyright (c) 2021 Morten Nielsen.
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace WinMonitor.UI
{
    internal sealed class TransparentBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
    {
        private static Windows.UI.Composition.Compositor? _compositor;
        private static IntPtr _dqController;
        private IntPtr _blackBrush;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(target, xamlRoot);
            target.SystemBackdrop = Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            var hwnd = (IntPtr)(long)xamlRoot.ContentIslandEnvironment.AppWindowId.Value;
            ConfigureDwm(hwnd);
            var hook = new WndProcHook(hwnd);
            hook.Add((msg, w, l) =>
            {
                if (msg == Native.WM_ERASEBKGND && ClearBackground(hwnd, w)) return (IntPtr)1;
                if (msg == Native.WM_DWMCOMPOSITIONCHANGED) { ConfigureDwm(hwnd); return IntPtr.Zero; }
                return null;
            });
            var dc = Native.GetDC(hwnd);
            ClearBackground(hwnd, dc);
            Native.ReleaseDC(hwnd, dc);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            var b = target.SystemBackdrop;
            target.SystemBackdrop = null;
            b?.Dispose();
            base.OnTargetDisconnected(target);
        }

        private static void ConfigureDwm(IntPtr hwnd)
        {
            var m = new Native.MARGINS();
            Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
            var bb = new Native.DWM_BLURBEHIND { dwFlags = 3, fEnable = true, hRgnBlur = Native.CreateRectRgn(-2, -2, -1, -1) };
            Native.DwmEnableBlurBehindWindow(hwnd, ref bb);
        }

        private bool ClearBackground(IntPtr hwnd, IntPtr dc)
        {
            if (!Native.GetClientRect(hwnd, out var r)) return false;
            if (_blackBrush == IntPtr.Zero) _blackBrush = Native.CreateSolidBrush(0);
            Native.FillRect(dc, ref r, _blackBrush);
            return true;
        }

        /// <summary>A system (Windows.UI) compositor: the backdrop brush must come from it.</summary>
        private static Windows.UI.Composition.Compositor Compositor
        {
            get
            {
                if (_compositor == null)
                {
                    if (Windows.System.DispatcherQueue.GetForCurrentThread() == null && _dqController == IntPtr.Zero)
                    {
                        var opt = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
                        CreateDispatcherQueueController(opt, out _dqController);
                    }
                    _compositor = new Windows.UI.Composition.Compositor();
                }
                return _compositor;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    }
}
