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
            File.Move(tmp, file, overwrite: true);
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
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }
}
