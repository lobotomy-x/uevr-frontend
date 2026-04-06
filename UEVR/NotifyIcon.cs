using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

/*
    Copyright lobotomyx 2025-2026
    This file, the corresponding XAML class, and all code within fall under MIT License and are free to use, modify, distribute, etc. under those terms. 
    The same terms may or may not apply to the rest of the repository which is owned and maintained by praydog.
*/

namespace UEVR
{
    internal class NotifyIcon
    {

        #region native


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(ref Win32Point pt);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Win32Point { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public Int32 left;
            public Int32 top;
            public Int32 right;
            public Int32 bottom;
        }

        // Define the NOTIFYICONIDENTIFIER structure
        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd; // Can be IntPtr.Zero if using guidItem
            public int uID;    // Can be 0 if using guidItem
            public Guid guidItem; // Use the same GUID used when calling Shell_NotifyIcon
        }


        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int Shell_NotifyIconGetRect(
            [In] ref NOTIFYICONIDENTIFIER identifier,
            [Out] out RECT iconLocation);



        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            // Required info
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public NotifyIconFlags uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public Guid guidItem;
        }


        [Flags]
        private enum NotifyIconFlags
        {
            Message = 0x01,
            Icon = 0x02,
            Tip = 0x04
        }

        private enum NotifyIconMessage
        {
            Add = 0x00,
            Modify = 0x01,
            Delete = 0x02
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(NotifyIconMessage dwMessage, ref NotifyIconData lpData);

        private const int WM_USER = 0x0400;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_TRAYICON = WM_USER + 1;
        #endregion

        private MainWindow? _window;
        private NotifyIconData _notifyIconData;
        private static TrayContextMenu? ctxMenu;
        private NOTIFYICONIDENTIFIER _notifyIconIdentifier;
        private string defaultTip = "UEVR Injector";
        private IntPtr defaultIcon;
        //public readonly Guid guid = new("2382DAAC-4171-4BCF-8725-A88E928B0084");
        // store a few icons with string keys to switch states
        public Dictionary<string, IntPtr> IconDictionary = new Dictionary<string, IntPtr>();

        // Message-only window for receiving tray icon callbacks
        private HwndSource? _messageWindow;
        private HwndSourceHook? _messageHook;
        private IntPtr _hWnd;



        /*      Register additional icons that can be switched to by name
                 The main use for this is to mimic behaviors like status badges
                 For UEVR we are just using paused, connected, default 
                 Icons must be passed to this class as handles.You can add Icon resources and just pass the.Handle
                 Or as an image and use.GetHicon
                 If using .ico it has to be a properly sized file, e.g. 256x256 or visual studio won't take it
                 png is much easier and more flexible in terms of sizing but the scaling happens automatically.
                 I think I did a good enough job to make any changes unnoticeable to users
                 but the icon actually shrinks slightly when switching to the connected version due to this
                 Now you won't be able to unsee it either, sorry
        */
        public void RegisterAlternateIcon(string name, IntPtr icon, bool update = true)
        {
            if (IconDictionary.ContainsKey(name))
            {
                if (update)
                {
                    Debug.WriteLine($"Key {name} already exists. Removing old value");
                    IconDictionary.Remove(name);
                }
                else
                {
                    Debug.WriteLine($"Key {name} already exists. Skipping entry.");
                    return;
                }
            }
            IconDictionary.Add(name, icon);
        }

        // idk why you would need this but you never know
        public void RemoveIconFromDictionary(string name)
        {
            IconDictionary.Remove(name);
        }

        // we could also get the MainWindow instance through Application.Current
        // but its convenient to just hold a handle to it since the mainwindow initializes our icon
        public void InitializeTrayIcon(MainWindow window)
        {
            _window = window;
            defaultIcon = UEVR.Properties.Resources.UEVR.GetHicon();
            RegisterAlternateIcon("default", UEVR.Properties.Resources.UEVR.GetHicon());
            RegisterAlternateIcon("_paused", UEVR.Properties.Resources.UEVRPaused.GetHicon());
            RegisterAlternateIcon("_connected", UEVR.Properties.Resources.UEVRConnected.GetHicon());

            var parameters = new HwndSourceParameters("TrayIconMessageWindow")
            {
                PositionX = 0,
                PositionY = 0,
                Height = 0,
                Width = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0
            };

            _messageHook = new HwndSourceHook(WndProc);
            _messageWindow = new HwndSource(parameters);
            _messageWindow.AddHook(_messageHook);
            _hWnd = _messageWindow.Handle;

            // Fill NotifyIconData struct
            _notifyIconData = new NotifyIconData
            {
                cbSize = Marshal.SizeOf(typeof(NotifyIconData)),
                hWnd = _hWnd,
                uID = 1,
                uFlags = NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip,
                uCallbackMessage = WM_TRAYICON,
                hIcon = defaultIcon,
                szTip = defaultTip,
                guidItem = Guid.Empty
            };
            _notifyIconIdentifier = new NOTIFYICONIDENTIFIER
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
                hWnd = _hWnd,
                uID = 1,
                guidItem = Guid.Empty
            };
            // Make the icon. Its now out of our hands and is technically being rendered by explorer.exe
            bool result = Shell_NotifyIcon(NotifyIconMessage.Add, ref _notifyIconData);
            if (!result)
            {
                System.Diagnostics.Debug.WriteLine("Failed to add tray icon.");
            }
        }


        public void ShowConnectionOptions()
        {
            Shell_NotifyIconGetRect(ref _notifyIconIdentifier, out RECT iconLocation);
            if (_window is null)
            {
                var mw = Application.Current.MainWindow;
                if (mw.IsActive)
                {
                    _window = (MainWindow)mw;
                }
                else
                {
                    // If mainwindow has crashed without crashing this thread somehow we do need to make sure to remove the icon
                    // It actually ends up as a handle owned by a random explorer process so it will just stay there if we don't clean it up
                    this.RemoveTrayIcon();
                    Application.Current.Shutdown();
                }
            }
            else
            {
                // yeah lets not even bother with checking the last state
                if (ctxMenu is not null)
                {
                    try { ctxMenu.Close(); } catch { }
                    ctxMenu = null;
                }
                ctxMenu = new TrayContextMenu(_window, _window.IsConnected(), _window.IsInjectionPaused());


                if (iconLocation.left == iconLocation.right && iconLocation.top == iconLocation.bottom)
                {
                    Win32Point mousePos = new Win32Point();
                    GetCursorPos(ref mousePos);
                    ctxMenu.ShowAtMouse(mousePos);
                }
                else
                    ctxMenu.ShowAtIcon(iconLocation);

                // we need to know how many buttons are displayed to offset correctly, this is the main reason for this setup
                ctxMenu.OffsetWindowPos();
                ctxMenu.Activate();
            }
        }

        // this is kind of here as a just in case thing I guess 
        // because the context menu is set to always close on deactivation anyway
        public void CloseTrayMenu()
        {
            if (ctxMenu is not null)
            {
                try { ctxMenu.Close(); } catch { }
                ctxMenu = null;
            }
        }


        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            Shell_NotifyIconGetRect(ref _notifyIconIdentifier, out RECT iconLocation);
            if (_window is not null)
            {
                // Use a 64-bit safe conversion for lParam
                int lParamInt = unchecked((int)lParam.ToInt64());

                // On single click we activate and focus the window 
                // This also will restore the window if it is minimized but not closed
                // i.e. the normal taskbar icon is still displayed
                // and if the window is open it will activate and focus it
                // This mimics behavior of popular apps like Steam and EGS
                if (lParamInt == WM_LBUTTONUP)
                {
                    _window.Show();
                    _window.Activate();
                    handled = true;
                }
                // On double click we restore the window if its been closed
                // If its not closed this will call ActivateMainWindow
                else if (lParamInt == WM_LBUTTONDBLCLK)
                {
                    _window.Show();
                    _window.Activate();
                    handled = true;
                }
                // WM_CONTEXTMENU doesn't actually strictly have anything to do with context menus,
                // it covers right click and enter if the system tray has keyboard focus
                else if (lParamInt == WM_RBUTTONUP || lParamInt == WM_CONTEXTMENU)
                {

                    if (ctxMenu is not null)
                    {
                        try { ctxMenu.Close(); } catch { }
                        ctxMenu = null;
                    }

                    ctxMenu = new TrayContextMenu(_window, _window.IsConnected(), _window.IsInjectionPaused());
                    // if we somehow lost the handle we'll use the mouse cursor pos
                    if (iconLocation.left == iconLocation.right && iconLocation.top == iconLocation.bottom)
                    {
                        Win32Point mousePos = new Win32Point();
                        GetCursorPos(ref mousePos);
                        ctxMenu.ShowAtMouse(mousePos);
                    }
                    else
                        ctxMenu.ShowAtIcon(iconLocation);

                    ctxMenu.OffsetWindowPos();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }
        public void RemoveTrayIcon()
        {
            // Remove the icon from the system tray
            Shell_NotifyIcon(NotifyIconMessage.Delete, ref _notifyIconData);

            if (_messageWindow is not null)
            {
                if (_messageHook is not null)
                {
                    _messageWindow.RemoveHook(_messageHook);
                    _messageHook = null;
                }
                if (ctxMenu is not null) try { ctxMenu.Close(); } catch { }
                _messageWindow.Dispose();
                _messageWindow = null;
            }
        }

        public void GetIconPosition(out RECT position)
        {
            Shell_NotifyIconGetRect(ref _notifyIconIdentifier, out RECT iconLocation);
            position = iconLocation;
        }

        // Call without args to reset to default
        // this changes the text seen when hovering on the icon
        // for uevr we change to the name of the game
        public void ModifyToolTip(string? message = null)
        {
            // we can freely alter the ref struct but we have to send messages to see the updates
            _notifyIconData.szTip = message is not null ? message : defaultTip;
            Shell_NotifyIcon(NotifyIconMessage.Modify, ref _notifyIconData);
        }

        public string GetIconType()
        {
            var icon = _notifyIconData.hIcon;
            if (icon == defaultIcon || IconDictionary.Count == 0)
                return "default";
            foreach (var key in IconDictionary.Keys)
                if (IconDictionary.TryGetValue(key, out IntPtr value))
                    if (value == icon) return key;
            return "";
        }

        // icon should be registered in Dictionary ahead of time
        public void ModifyTrayIcon(string iconKey)
        {
            if (IconDictionary.TryGetValue(iconKey, out IntPtr icon))
            {
                _notifyIconData.hIcon = icon;
                Shell_NotifyIcon(NotifyIconMessage.Modify, ref _notifyIconData);
            }
        }

        public void ResetTrayIcon()
        {
            _notifyIconData.hIcon = defaultIcon;
            Shell_NotifyIcon(NotifyIconMessage.Modify, ref _notifyIconData);
        }
    }
}
