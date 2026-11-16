namespace Clauderoo.Core.Tests;

public class JunctionTests
{
    [Fact]
    public void Junction_CreateAndGetTarget()
    {
        using var h = new TempHome();
        var target = Path.Combine(h.Dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "x");
        var link = Path.Combine(h.Dir, "link");

        Junction.Create(link, target);

        Assert.Equal(target, Junction.GetTarget(link));
        Assert.True(File.Exists(Path.Combine(link, "a.txt")));
    }

    [Fact]
    public void Junction_GetTarget_RealDir_ReturnsNull()
    {
        using var h = new TempHome();
        var dir = Path.Combine(h.Dir, "real");
        Directory.CreateDirectory(dir);
        Assert.Null(Junction.GetTarget(dir));
        Assert.Null(Junction.GetTarget(Path.Combine(h.Dir, "missing")));
    }

    [Fact]
    public void TempHomeDispose_DoesNotDeleteJunctionTarget()
    {
        var outside = Path.Combine(Path.GetTempPath(), "clauderoo-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        try
        {
            using (var h = new TempHome())
                Junction.Create(Path.Combine(h.Dir, "link"), outside);
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
}
