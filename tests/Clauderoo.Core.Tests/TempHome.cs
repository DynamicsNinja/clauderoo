namespace Clauderoo.Core.Tests;

public sealed class TempHome : IDisposable
{
    public string Dir { get; }
    public ClauderooPaths Paths { get; }

    public TempHome()
    {
        Dir = Path.Combine(Path.GetTempPath(), "clauderoo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Dir, ".claude"));
        File.WriteAllText(Path.Combine(Dir, ".claude.json"), "{}");
        Paths = new ClauderooPaths(Dir);
    }

    public void Dispose()
    {
        try
        {
            RemoveJunctions(Dir);
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException) { }
    }

    // Remove junctions (non-recursively) before the recursive delete so targets are never touched.
    private static void RemoveJunctions(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (new DirectoryInfo(sub).LinkTarget != null) Directory.Delete(sub);
            else RemoveJunctions(sub);
        }
    }
}
