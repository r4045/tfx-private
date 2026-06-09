using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Tfx;

/// <summary>
/// Shows the native Windows shell context menu (the same one Explorer shows)
/// for a set of files/folders that share a common parent directory.
///
/// Built on the classic shell COM path: bind the parent folder to an
/// <c>IShellFolder</c>, ask it for an <c>IContextMenu</c> over the child PIDLs,
/// populate a popup HMENU via <c>QueryContextMenu</c>, track it with
/// <c>TrackPopupMenuEx</c>, and dispatch the chosen verb through
/// <c>InvokeCommand</c>. Submenu / icon owner-draw messages are forwarded to
/// <c>IContextMenu2/3</c> through a temporary window hook while the menu is up.
///
/// Everything is best-effort: any failure returns false so the caller can fall
/// back to the in-app menu. Virtual paths (e.g. inside-archive) are not shell
/// items and must be filtered out by the caller before calling Show.
/// </summary>
internal static class ShellContextMenu
{
    private const int S_OK = 0;

    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;

    private const int CMIC_MASK_UNICODE = unchecked((int)0x00004000);

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int SW_SHOWNORMAL = 1;

    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;

    private const uint IdCmdFirst = 1;
    private const uint IdCmdLast = 0x7FFF;

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    // Held only for the lifetime of a single (modal) TrackPopupMenuEx call, so a
    // static field is safe: the menu is modal and never re-entrant.
    [ThreadStatic] private static IContextMenu2? _menu2;
    [ThreadStatic] private static IContextMenu3? _menu3;

    /// <summary>
    /// Shows the shell context menu at the current cursor position.
    /// Returns true if the menu was displayed (so the caller should NOT fall
    /// back to its own menu). <paramref name="invoked"/> is set true when the
    /// user actually picked a command (the caller should refresh afterwards).
    /// </summary>
    public static bool Show(IntPtr ownerHwnd, IReadOnlyList<string> paths, bool extendedVerbs, out bool invoked)
    {
        invoked = false;
        if (paths.Count == 0 || ownerHwnd == IntPtr.Zero)
        {
            return false;
        }
        if (!TryGetCommonParent(paths, out var parent))
        {
            return false;
        }

        var parentPidl = IntPtr.Zero;
        var absoluteChildPidls = new List<IntPtr>();
        var childPidls = new IntPtr[paths.Count];
        var childArray = IntPtr.Zero;
        IShellFolder? desktop = null;
        IShellFolder? parentFolder = null;
        IContextMenu? menu = null;

        try
        {
            if (SHGetDesktopFolder(out desktop) != S_OK || desktop is null)
            {
                return false;
            }

            if (SHParseDisplayName(parent, IntPtr.Zero, out parentPidl, 0, out _) != S_OK || parentPidl == IntPtr.Zero)
            {
                return false;
            }

            var shellFolderIid = IID_IShellFolder;
            if (desktop.BindToObject(parentPidl, IntPtr.Zero, ref shellFolderIid, out var parentFolderPtr) != S_OK ||
                parentFolderPtr == IntPtr.Zero)
            {
                return false;
            }
            parentFolder = (IShellFolder)Marshal.GetTypedObjectForIUnknown(parentFolderPtr, typeof(IShellFolder));
            Marshal.Release(parentFolderPtr);

            for (var i = 0; i < paths.Count; i++)
            {
                if (SHParseDisplayName(paths[i], IntPtr.Zero, out var abs, 0, out _) != S_OK || abs == IntPtr.Zero)
                {
                    return false;
                }
                absoluteChildPidls.Add(abs);
                childPidls[i] = ILFindLastID(abs);
                if (childPidls[i] == IntPtr.Zero)
                {
                    return false;
                }
            }

            childArray = Marshal.AllocCoTaskMem(IntPtr.Size * childPidls.Length);
            Marshal.Copy(childPidls, 0, childArray, childPidls.Length);

            var contextMenuIid = IID_IContextMenu;
            if (parentFolder.GetUIObjectOf(ownerHwnd, (uint)childPidls.Length, childArray, ref contextMenuIid, IntPtr.Zero, out var menuPtr) != S_OK ||
                menuPtr == IntPtr.Zero)
            {
                return false;
            }
            menu = (IContextMenu)Marshal.GetTypedObjectForIUnknown(menuPtr, typeof(IContextMenu));
            Marshal.Release(menuPtr);

            return TrackAndInvoke(menu, ownerHwnd, extendedVerbs, out invoked);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (menu is not null && Marshal.IsComObject(menu))
            {
                Marshal.FinalReleaseComObject(menu);
            }
            if (parentFolder is not null && Marshal.IsComObject(parentFolder))
            {
                Marshal.FinalReleaseComObject(parentFolder);
            }
            if (desktop is not null && Marshal.IsComObject(desktop))
            {
                Marshal.FinalReleaseComObject(desktop);
            }
            if (childArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(childArray);
            }
            foreach (var pidl in absoluteChildPidls)
            {
                if (pidl != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
            if (parentPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(parentPidl);
            }
        }
    }

    /// <summary>
    /// Shows the folder-background shell menu (New, Paste, View, ...) for an
    /// empty-area right-click. Uses <c>IShellFolder.CreateViewObject</c> on the
    /// folder itself rather than GetUIObjectOf over selected items.
    /// </summary>
    public static bool ShowForFolderBackground(IntPtr ownerHwnd, string folderPath, bool extendedVerbs, out bool invoked)
    {
        invoked = false;
        if (ownerHwnd == IntPtr.Zero || string.IsNullOrEmpty(folderPath))
        {
            return false;
        }

        var folderPidl = IntPtr.Zero;
        IShellFolder? desktop = null;
        IShellFolder? folder = null;
        IContextMenu? menu = null;

        try
        {
            if (SHGetDesktopFolder(out desktop) != S_OK || desktop is null)
            {
                return false;
            }

            if (SHParseDisplayName(folderPath, IntPtr.Zero, out folderPidl, 0, out _) != S_OK || folderPidl == IntPtr.Zero)
            {
                return false;
            }

            var shellFolderIid = IID_IShellFolder;
            if (desktop.BindToObject(folderPidl, IntPtr.Zero, ref shellFolderIid, out var folderPtr) != S_OK ||
                folderPtr == IntPtr.Zero)
            {
                return false;
            }
            folder = (IShellFolder)Marshal.GetTypedObjectForIUnknown(folderPtr, typeof(IShellFolder));
            Marshal.Release(folderPtr);

            var contextMenuIid = IID_IContextMenu;
            if (folder.CreateViewObject(ownerHwnd, ref contextMenuIid, out var menuPtr) != S_OK || menuPtr == IntPtr.Zero)
            {
                return false;
            }
            menu = (IContextMenu)Marshal.GetTypedObjectForIUnknown(menuPtr, typeof(IContextMenu));
            Marshal.Release(menuPtr);

            return TrackAndInvoke(menu, ownerHwnd, extendedVerbs, out invoked);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (menu is not null && Marshal.IsComObject(menu))
            {
                Marshal.FinalReleaseComObject(menu);
            }
            if (folder is not null && Marshal.IsComObject(folder))
            {
                Marshal.FinalReleaseComObject(folder);
            }
            if (desktop is not null && Marshal.IsComObject(desktop))
            {
                Marshal.FinalReleaseComObject(desktop);
            }
            if (folderPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(folderPidl);
            }
        }
    }

    /// <summary>
    /// Populates a popup from <paramref name="menu"/>, tracks it at the cursor,
    /// and invokes the chosen verb. Forwards owner-draw / submenu messages to
    /// IContextMenu2/3 while the popup is up. The caller owns <paramref
    /// name="menu"/> and releases it.
    /// </summary>
    private static bool TrackAndInvoke(IContextMenu menu, IntPtr ownerHwnd, bool extendedVerbs, out bool invoked)
    {
        invoked = false;
        var hMenu = IntPtr.Zero;
        HwndSource? source = null;
        var hookAdded = false;

        try
        {
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                return false;
            }

            var flags = CMF_NORMAL | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
            var hr = menu.QueryContextMenu(hMenu, 0, IdCmdFirst, IdCmdLast, flags);
            if (hr < 0)
            {
                return false;
            }

            // QueryContextMenu returns (idCmdFirst + count) in the low word; a
            // zero-item menu means nothing useful to show.
            if ((hr & 0xFFFF) == 0)
            {
                return false;
            }

            _menu2 = menu as IContextMenu2;
            _menu3 = menu as IContextMenu3;

            // Forward owner-draw / submenu-init messages to the shell while the
            // popup is up, so cascading submenus and icons render correctly.
            source = HwndSource.FromHwnd(ownerHwnd);
            if (source is not null)
            {
                source.AddHook(MenuMessageHook);
                hookAdded = true;
            }

            GetCursorPos(out var pt);
            var cmd = TrackPopupMenuEx(
                hMenu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                pt.X,
                pt.Y,
                ownerHwnd,
                IntPtr.Zero);

            if (hookAdded && source is not null)
            {
                source.RemoveHook(MenuMessageHook);
                hookAdded = false;
            }

            if (cmd >= IdCmdFirst)
            {
                InvokeVerb(menu, cmd - IdCmdFirst, ownerHwnd);
                invoked = true;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hookAdded && source is not null)
            {
                source.RemoveHook(MenuMessageHook);
            }
            _menu2 = null;
            _menu3 = null;

            if (hMenu != IntPtr.Zero)
            {
                DestroyMenu(hMenu);
            }
        }
    }

    private static void InvokeVerb(IContextMenu menu, uint verbIndex, IntPtr ownerHwnd)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE,
            hwnd = ownerHwnd,
            lpVerb = (IntPtr)verbIndex,
            lpVerbW = (IntPtr)verbIndex,
            nShow = SW_SHOWNORMAL,
        };
        menu.InvokeCommand(ref info);
    }

    private static IntPtr MenuMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INITMENUPOPUP:
            case WM_DRAWITEM:
            case WM_MEASUREITEM:
            case WM_MENUCHAR:
                if (_menu3 is not null)
                {
                    if (_menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == S_OK)
                    {
                        handled = true;
                        return result;
                    }
                }
                else if (_menu2 is not null)
                {
                    if (_menu2.HandleMenuMsg((uint)msg, wParam, lParam) == S_OK)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                break;
        }
        return IntPtr.Zero;
    }

    private static bool TryGetCommonParent(IReadOnlyList<string> paths, out string parent)
    {
        parent = "";
        var common = System.IO.Path.GetDirectoryName(paths[0]) ?? "";
        if (common.Length == 0 ||
            paths.Any(p => !string.Equals(System.IO.Path.GetDirectoryName(p), common, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        parent = common;
        return true;
    }

    // ─── COM interfaces ───────────────────────────────────────────────────

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    // ─── Win32 / shell imports ────────────────────────────────────────────

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
