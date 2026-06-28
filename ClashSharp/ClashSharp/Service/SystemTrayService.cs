/*
 * System Tray Service
 * Owns the native notification-area icon and minimal Clash# tray menu
 *
 * @author: WaterRun
 * @file: Service/SystemTrayService.cs
 * @date: 2026-06-24
 */

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ClashSharp.Model;
using DrawingIcon = System.Drawing.Icon;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace ClashSharp.Service;

/// <summary>Owns the native notification-area icon and minimal Clash# tray menu.</summary>
public sealed class SystemTrayService : IDisposable
{
    /// <summary>Tray callback message sent to the owner window.</summary>
    public const uint TrayCallbackMessage = 0x8001;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmLbuttondblclk = 0x0203;
    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfChecked = 0x00000008;
    private const uint MfPopup = 0x00000010;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;

    private const uint ModeDisabledCommandId = 1001;
    private const uint ModeStandbyCommandId = 1002;
    private const uint ModeRuleTakeoverCommandId = 1003;
    private const uint ModeFullTakeoverCommandId = 1004;
    private const uint TransparentProxyCommandId = 1101;
    private const uint SettingsCommandId = 1201;
    private const uint SafeExitCommandId = 1301;
    private const uint PageCommandBaseId = 1400;

    /// <summary>Owner window handle receiving tray callback messages.</summary>
    private readonly nint _ownerWindowHandle;

    /// <summary>Callback that builds current menu state.</summary>
    private readonly Func<TrayMenuState> _getState;

    /// <summary>Callback that opens a shell page by navigation tag.</summary>
    private readonly Action<string> _openPage;

    /// <summary>Callback that safely exits the app.</summary>
    private readonly Action _safeExit;

    /// <summary>Callback that applies a Clash# mode.</summary>
    private readonly Action<ClashSharpMode> _applyMode;

    /// <summary>Callback that toggles transparent proxy preference.</summary>
    private readonly Action<bool> _setTransparentProxy;

    /// <summary>Loaded tray icon resource.</summary>
    private DrawingIcon _icon;

    /// <summary>True after the service is disposed.</summary>
    private bool _disposed;

    /// <summary>Initializes a system tray service.</summary>
    public SystemTrayService(
        nint ownerWindowHandle,
        Func<TrayMenuState> getState,
        Action<string> openPage,
        Action safeExit,
        Action<ClashSharpMode> applyMode,
        Action<bool> setTransparentProxy)
    {
        if (ownerWindowHandle == 0)
        {
            throw new ArgumentException("Owner window handle must be non-zero.", nameof(ownerWindowHandle));
        }

        _ownerWindowHandle = ownerWindowHandle;
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _openPage = openPage ?? throw new ArgumentNullException(nameof(openPage));
        _safeExit = safeExit ?? throw new ArgumentNullException(nameof(safeExit));
        _applyMode = applyMode ?? throw new ArgumentNullException(nameof(applyMode));
        _setTransparentProxy = setTransparentProxy ?? throw new ArgumentNullException(nameof(setTransparentProxy));
        _icon = LoadTrayIcon(isInactive: false, useMonochrome: false, fade: false);
        AddTrayIcon();
        RefreshTrayIcon();
    }

    /// <summary>Refreshes menu check marks and enabled states.</summary>
    public void RefreshMenu()
    {
        RefreshTrayIcon();
    }

    /// <summary>Refreshes the tray icon according to the current proxy mode and inactive icon settings.</summary>
    public void RefreshTrayIcon()
    {
        if (_disposed)
        {
            return;
        }

        TrayMenuState state = _getState();
        bool isProxyActive = state.ModeItems.Any(static item =>
            item.IsChecked && item.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover);
        DrawingIcon nextIcon = LoadTrayIcon(
            isInactive: !isProxyActive,
            useMonochrome: AppSettingsService.Instance.TrayUseMonochromeInactiveIcon,
            fade: AppSettingsService.Instance.TrayFadeInactiveIcon);
        DrawingIcon previousIcon = _icon;
        _icon = nextIcon;
        NOTIFYICONDATA data = CreateNotifyIconData();
        Shell_NotifyIcon(NimModify, ref data);
        previousIcon.Dispose();
    }

    /// <summary>Handles one owner-window message when it belongs to the tray icon.</summary>
    /// <param name="message">Window message id.</param>
    /// <param name="wParam">Message WPARAM.</param>
    /// <param name="lParam">Message LPARAM.</param>
    /// <returns>True when the message was handled.</returns>
    public bool TryHandleWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (_disposed || message != TrayCallbackMessage)
        {
            return false;
        }

        uint mouseMessage = unchecked((uint)lParam.ToInt64());
        if (mouseMessage == WmLbuttondblclk)
        {
            _openPage("MasterControl");
            return true;
        }

        if (mouseMessage == WmRbuttonup)
        {
            ShowContextMenu();
            return true;
        }

        return true;
    }

    /// <summary>Shows the native context menu.</summary>
    private void ShowContextMenu()
    {
        TrayMenuState state = _getState();
        nint menu = CreatePopupMenu();
        if (state.ShowStatus)
        {
            nint statusMenu = CreatePopupMenu();
            foreach (TrayStatusMenuItem statusItem in state.StatusItems)
            {
                uint statusFlags = MfString | (statusItem.IsEnabled ? 0 : MfGrayed);
                AppendMenu(statusMenu, statusFlags, nint.Zero, statusItem.Label);
            }

            AppendMenu(menu, MfPopup, statusMenu, state.StatusMenuLabel);
            AppendMenu(menu, MfSeparator, nint.Zero, string.Empty);
        }

        if (state.ShowMode)
        {
            nint modeMenu = CreatePopupMenu();
            foreach (TrayModeMenuItem modeItem in state.ModeItems)
            {
                AppendMenu(modeMenu, MfString | (modeItem.IsChecked ? MfChecked : 0), new nint(MapModeCommand(modeItem.Mode)), modeItem.Label);
            }

            AppendMenu(menu, MfPopup, modeMenu, state.ModeMenuLabel);
            AppendMenu(menu, MfSeparator, nint.Zero, string.Empty);
        }

        if (state.ShowPages)
        {
            nint pageMenu = CreatePopupMenu();
            for (int index = 0; index < state.PageItems.Count; index++)
            {
                TrayPageMenuItem pageItem = state.PageItems[index];
                AppendMenu(pageMenu, MfString, new nint(PageCommandBaseId + (uint)index), pageItem.Label);
            }

            AppendMenu(menu, MfPopup, pageMenu, state.PagesMenuLabel);
            AppendMenu(menu, MfSeparator, nint.Zero, string.Empty);
        }

        if (state.ShowTransparentProxy)
        {
            uint transparentFlags = MfString
                | (state.TransparentProxyItem.IsChecked ? MfChecked : 0)
                | (state.TransparentProxyItem.IsEnabled ? 0 : MfGrayed);
            AppendMenu(menu, transparentFlags, new nint(TransparentProxyCommandId), state.TransparentProxyItem.Label);
        }

        if (state.ShowSettings)
        {
            AppendMenu(menu, MfString, new nint(SettingsCommandId), state.SettingsLabel);
        }

        if (state.ShowSafeExit)
        {
            AppendMenu(menu, MfSeparator, nint.Zero, string.Empty);
            AppendMenu(menu, MfString, new nint(SafeExitCommandId), state.SafeExitLabel);
        }

        GetCursorPos(out POINT point);
        SetForegroundWindow(_ownerWindowHandle);
        uint commandId = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand, point.x, point.y, 0, _ownerWindowHandle, nint.Zero);
        DestroyMenu(menu);
        HandleMenuCommand(commandId, state);
    }

    /// <summary>Disposes the tray icon and removes it from the notification area.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveTrayIcon();
        _icon.Dispose();
    }

    /// <summary>Adds the tray icon.</summary>
    private void AddTrayIcon()
    {
        NOTIFYICONDATA data = CreateNotifyIconData();
        Shell_NotifyIcon(NimAdd, ref data);
    }

    /// <summary>Removes the tray icon.</summary>
    private void RemoveTrayIcon()
    {
        NOTIFYICONDATA data = CreateNotifyIconData();
        Shell_NotifyIcon(NimDelete, ref data);
    }

    /// <summary>Builds notification icon data for Shell_NotifyIcon.</summary>
    private NOTIFYICONDATA CreateNotifyIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _ownerWindowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _icon.Handle,
            szTip = "Clash#",
        };
    }

    /// <summary>Handles one popup menu command.</summary>
    private void HandleMenuCommand(uint commandId, TrayMenuState state)
    {
        switch (commandId)
        {
            case ModeDisabledCommandId:
                _applyMode(ClashSharpMode.Disabled);
                break;
            case ModeStandbyCommandId:
                _applyMode(ClashSharpMode.Standby);
                break;
            case ModeRuleTakeoverCommandId:
                _applyMode(ClashSharpMode.RuleTakeover);
                break;
            case ModeFullTakeoverCommandId:
                _applyMode(ClashSharpMode.FullTakeover);
                break;
            case TransparentProxyCommandId:
                if (state.TransparentProxyItem.IsEnabled)
                {
                    _setTransparentProxy(!state.TransparentProxyItem.IsChecked);
                }

                break;
            case SettingsCommandId:
                _openPage("Settings");
                break;
            case SafeExitCommandId:
                _safeExit();
                break;
            default:
                string? pageTag = MapPageCommand(commandId, state);
                if (pageTag is not null)
                {
                    _openPage(pageTag);
                }

                break;
        }
    }

    /// <summary>Maps a mode to a popup menu command id.</summary>
    private static uint MapModeCommand(ClashSharpMode mode)
    {
        return mode switch
        {
            ClashSharpMode.Disabled => ModeDisabledCommandId,
            ClashSharpMode.Standby => ModeStandbyCommandId,
            ClashSharpMode.RuleTakeover => ModeRuleTakeoverCommandId,
            ClashSharpMode.FullTakeover => ModeFullTakeoverCommandId,
            _ => ModeDisabledCommandId,
        };
    }

    private static string? MapPageCommand(uint commandId, TrayMenuState state)
    {
        if (commandId < PageCommandBaseId)
        {
            return null;
        }

        int index = (int)(commandId - PageCommandBaseId);
        return index >= 0 && index < state.PageItems.Count
            ? state.PageItems[index].Tag
            : null;
    }

    /// <summary>Loads the Clash# logo as a tray icon.</summary>
    private static DrawingIcon LoadTrayIcon(bool isInactive, bool useMonochrome, bool fade)
    {
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.png");
        if (!File.Exists(logoPath))
        {
            return (DrawingIcon)DrawingSystemIcons.Application.Clone();
        }

        using DrawingBitmap bitmap = new(logoPath);
        using DrawingBitmap iconBitmap = isInactive && (useMonochrome || fade)
            ? CreateInactiveBitmap(bitmap, useMonochrome, fade)
            : new DrawingBitmap(bitmap);
        nint handle = iconBitmap.GetHicon();
        try
        {
            using DrawingIcon icon = DrawingIcon.FromHandle(handle);
            return (DrawingIcon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static DrawingBitmap CreateInactiveBitmap(DrawingBitmap source, bool useMonochrome, bool fade)
    {
        DrawingBitmap target = new(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                DrawingColor color = source.GetPixel(x, y);
                int alpha = fade ? (int)(color.A * 0.55d) : color.A;
                if (useMonochrome)
                {
                    int gray = (int)((color.R * 0.299d) + (color.G * 0.587d) + (color.B * 0.114d));
                    color = DrawingColor.FromArgb(alpha, gray, gray, gray);
                }
                else
                {
                    color = DrawingColor.FromArgb(alpha, color.R, color.G, color.B);
                }

                target.SetPixel(x, y, color);
            }
        }

        return target;
    }

    /// <summary>Releases an unmanaged icon handle created by Bitmap.GetHicon.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
}
