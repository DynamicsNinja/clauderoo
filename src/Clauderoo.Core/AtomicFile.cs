namespace Clauderoo.Core;

internal static class AtomicFile
{
    /// <summary>A temp name unique per process and call, so the tray and CLI never share a tmp file.</summary>
    public static string TmpName(string file) =>
        $"{file}.{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}.tmp";

    public static void WriteAllBytes(string file, byte[] content)
    {
        var tmp = TmpName(file);
        try
        {
            File.WriteAllBytes(tmp, content);
            MoveOver(tmp, file);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    public static void WriteAllText(string file, string content)
    {
        var tmp = TmpName(file);
        try
        {
            File.WriteAllText(tmp, content);
            MoveOver(tmp, file);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>
    /// Replaces <paramref name="file"/> with <paramref name="tmp"/>. Windows refuses the replace (access denied, or a
    /// sharing violation) while any other process has the file open, even a reader that shares delete: another
    /// clauderoo reading the config, an editor or antivirus. Readers hold it only briefly, so the replace is retried
    /// for up to about 3 seconds before giving up.
    /// </summary>
    internal static void MoveOver(string tmp, string file, int attempts = 30)
    {
        for (var i = 1; ; i++)
        {
            try { File.Move(tmp, file, overwrite: true); return; }
            catch (Exception e) when (i < attempts && e is UnauthorizedAccessException or IOException && File.Exists(tmp))
            {
                Thread.Sleep(Math.Min(10 * i, 150));
            }
        }
    }

    /// <summary>
    /// Reads a file other processes may replace at any moment, sharing everything so this read never blocks a writer
    /// more than it must; the writer still waits for it to finish (see <see cref="MoveOver"/>).
    /// </summary>
    public static string ReadAllTextShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }
}
