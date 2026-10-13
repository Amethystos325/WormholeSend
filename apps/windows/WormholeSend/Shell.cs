using System.Diagnostics;
using System.IO;

namespace WormholeSend;

static class Shell
{
    /// Opens File Explorer with `paths` selected; they share a folder.
    public static void Reveal(IReadOnlyList<string> paths)
    {
        var folder = Path.GetDirectoryName(paths[0]);
        if (folder is null) return;
        var folderId = Native.ILCreateFromPath(folder);
        var items = paths.Select(Native.ILCreateFromPath).Where(id => id != IntPtr.Zero).ToArray();
        try
        {
            if (folderId == IntPtr.Zero || Native.SHOpenFolderAndSelectItems(folderId, (uint)items.Length, items, 0) != 0)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{paths[0]}\"") { UseShellExecute = false });
        }
        finally
        {
            foreach (var id in items) Native.ILFree(id);
            if (folderId != IntPtr.Zero) Native.ILFree(folderId);
        }
    }
}
