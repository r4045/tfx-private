using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Tfx;

/// <summary>
/// One copy / move request. <see cref="NewName"/> is the final name inside
/// <see cref="DestinationDirectory"/>; conflicts are resolved by the caller
/// beforehand, so the shell only has to overwrite when
/// <see cref="Overwrite"/> is set.
/// </summary>
internal sealed record FileTransferItem(
    string Source,
    string DestinationDirectory,
    string NewName,
    bool IsMove,
    bool Overwrite)
{
    public string Target => System.IO.Path.Combine(DestinationDirectory, NewName);
}

/// <summary>
/// Copies / moves through the Windows shell's IFileOperation — the same
/// engine Explorer uses — on a dedicated STA thread. Compared with the old
/// File.Copy / VbFileSystem calls on the UI thread this gives:
/// <list type="bullet">
/// <item>no UI freeze ("Not responding") on large or network copies,</item>
/// <item>Explorer's progress dialog with pause / cancel,</item>
/// <item>undo via Explorer (FOF_ALLOWUNDO), elevation prompts, and</item>
/// <item>the shell's own error dialogs (retry / skip) per item.</item>
/// </list>
/// </summary>
internal static class ShellFileOperation
{
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOFX_SHOWELEVATIONPROMPT = 0x00040000;

    // Returned by PerformOperations when the user cancels.
    private const int HResultCancelled = unchecked((int)0x800704C7);
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    /// <summary>
    /// Runs all <paramref name="items"/> as one shell operation (one progress
    /// dialog). Returns true when the user cancelled or the shell aborted any
    /// part. Per-item failures are not reported here; the caller verifies the
    /// file system afterwards.
    /// </summary>
    public static Task<bool> RunAsync(IReadOnlyList<FileTransferItem> items, IntPtr ownerWindow)
    {
        if (items.Count == 0)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Perform(items, ownerWindow));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "tfx file operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool Perform(IReadOnlyList<FileTransferItem> items, IntPtr ownerWindow)
    {
        var type = Type.GetTypeFromCLSID(CLSID_FileOperation, throwOnError: true)!;
        var operation = (IFileOperation)Activator.CreateInstance(type)!;
        var created = new List<object>();
        try
        {
            var flags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOFX_SHOWELEVATIONPROMPT;
            if (items.Any(i => i.Overwrite))
            {
                // The caller already asked the user. Targets of non-overwrite
                // items have unique names, so this only answers "replace" for
                // the items the user chose to overwrite.
                flags |= FOF_NOCONFIRMATION;
            }
            operation.SetOperationFlags(flags);
            if (ownerWindow != IntPtr.Zero)
            {
                operation.SetOwnerWindow(ownerWindow);
            }

            var folders = new Dictionary<string, IShellItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var source = CreateItem(item.Source);
                created.Add(source);
                if (!folders.TryGetValue(item.DestinationDirectory, out var destination))
                {
                    destination = CreateItem(item.DestinationDirectory);
                    created.Add(destination);
                    folders.Add(item.DestinationDirectory, destination);
                }

                if (item.IsMove)
                {
                    operation.MoveItem(source, destination, item.NewName, null);
                }
                else
                {
                    operation.CopyItem(source, destination, item.NewName, null);
                }
            }

            try
            {
                operation.PerformOperations();
            }
            catch (COMException ex) when (ex.HResult is HResultCancelled or CopyEngineUserCancelled)
            {
                return true;
            }
            catch (COMException ex)
            {
                // Any other failure HRESULT means some items were skipped or
                // failed after the shell already showed its own error UI.
                // The caller checks each target, so report it as partial.
                CrashLog.Write("IFileOperation.PerformOperations", ex);
                return true;
            }

            operation.GetAnyOperationsAborted(out var aborted);
            return aborted;
        }
        finally
        {
            foreach (var comObject in created)
            {
                Marshal.FinalReleaseComObject(comObject);
            }
            Marshal.FinalReleaseComObject(operation);
        }
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        return item;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        // Only passed through to IFileOperation; its methods are never called,
        // so the vtable slots are declared as placeholders in order.
        void BindToHandler();
        void GetParent();
        void GetDisplayName();
        void GetAttributes();
        void Compare();
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IntPtr pfops);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.IUnknown)] object? pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, [MarshalAs(UnmanagedType.IUnknown)] object? pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }
}
