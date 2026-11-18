namespace Clauderoo.Core;

/// <summary>
/// Folder size and permanent deletion that never follow a junction or symbolic link. A link is removed itself
/// (non-recursively); whatever it points to (home's shared folders, for example) is never entered or touched.
/// </summary>
public static class SafeTree
{
    // Hidden and system files must be seen too, or the folder can't be emptied.
    private static readonly EnumerationOptions All = new()
    {
        AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false,
    };

    private static bool IsLink(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>The bytes of the files under <paramref name="dir"/>, not counting anything behind a link. Best effort.</summary>
    public static long Size(string dir)
    {
        var root = new DirectoryInfo(dir);
        if (!root.Exists || IsLink(root)) return 0;
        long total = 0;
        var pending = new Stack<DirectoryInfo>([root]);
        while (pending.Count > 0)
        {
            var d = pending.Pop();
            try
            {
                foreach (var e in d.EnumerateFileSystemInfos("*", All))
                {
                    if (IsLink(e)) continue;
                    if (e is DirectoryInfo sub) pending.Push(sub);
                    else if (e is FileInfo f) total += f.Length;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* skip what can't be read */ }
        }
        return total;
    }

    /// <summary>
    /// Permanently deletes <paramref name="dir"/> and everything in it. A junction or symbolic link anywhere inside
    /// (or <paramref name="dir"/> itself, when it is one) is removed as a link only: its target is never entered.
    /// Read-only files are deleted too. Throws on the first item that can't be deleted.
    /// </summary>
    public static void Delete(string dir)
    {
        var root = new DirectoryInfo(dir);
        if (!root.Exists && !IsLinkPath(dir)) return;
        if (IsLink(root)) { root.Delete(); return; } // non-recursive: the link only
        DeleteContents(root);
        ClearReadOnly(root);
        root.Delete();
    }

    // A dangling link reports Exists = false but still has to go.
    private static bool IsLinkPath(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void DeleteContents(DirectoryInfo dir)
    {
        foreach (var e in dir.EnumerateFileSystemInfos("*", All).ToList())
        {
            if (IsLink(e))
            {
                // Directory.Delete / File.Delete on a reparse point remove the link, never its target.
                if (e is DirectoryInfo link) link.Delete();
                else e.Delete();
            }
            else if (e is DirectoryInfo sub)
            {
                DeleteContents(sub);
                ClearReadOnly(sub);
                sub.Delete();
            }
            else
            {
                ClearReadOnly(e);
                e.Delete();
            }
        }
    }

    private static void ClearReadOnly(FileSystemInfo i)
    {
        if ((i.Attributes & FileAttributes.ReadOnly) != 0) i.Attributes &= ~FileAttributes.ReadOnly;
    }
}
