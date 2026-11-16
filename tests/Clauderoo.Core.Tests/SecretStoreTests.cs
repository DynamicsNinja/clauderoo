using System.Text;

namespace Clauderoo.Core.Tests;

public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);
    public void Save(string profileDir, string secret) => _secrets[profileDir] = secret;
    public string? Load(string profileDir) => _secrets.TryGetValue(profileDir, out var s) ? s : null;
    public List<string> Deleted { get; } = [];
    public void Delete(string profileDir) { Deleted.Add(profileDir); _secrets.Remove(profileDir); }
    public bool Exists(string profileDir) => _secrets.ContainsKey(profileDir);
}

public class SecretStoreTests
{
    private static string KeyFile(string dir) => Path.Combine(dir, ".clauderoo-apikey");

    [Fact]
    public void Dpapi_RoundTrip()
    {
        using var home = new TempHome();
        var store = new DpapiSecretStore();
        Assert.False(store.Exists(home.Dir));
        store.Save(home.Dir, "sk-ant-secret-123");
        Assert.True(store.Exists(home.Dir));
        Assert.Equal("sk-ant-secret-123", store.Load(home.Dir));
    }

    [Fact]
    public void Dpapi_FileIsNotPlaintext()
    {
        using var home = new TempHome();
        new DpapiSecretStore().Save(home.Dir, "sk-ant-secret-123");
        var bytes = File.ReadAllBytes(KeyFile(home.Dir));
        Assert.True(bytes.Length > 0);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("sk-ant-secret-123", text);
        Assert.DoesNotContain("s\0k\0-\0a\0n\0t\0", text);
    }

    [Fact]
    public void Dpapi_Delete_RemovesFile()
    {
        using var home = new TempHome();
        var store = new DpapiSecretStore();
        store.Save(home.Dir, "x");
        store.Delete(home.Dir);
        Assert.False(File.Exists(KeyFile(home.Dir)));
        Assert.False(store.Exists(home.Dir));
        store.Delete(home.Dir); // no throw when missing
    }

    [Fact]
    public void Dpapi_Load_Missing_ReturnsNull()
    {
        using var home = new TempHome();
        Assert.Null(new DpapiSecretStore().Load(home.Dir));
    }

    [Fact]
    public void Dpapi_Load_Corrupt_ThrowsClauderooException()
    {
        using var home = new TempHome();
        File.WriteAllBytes(KeyFile(home.Dir), [1, 2, 3, 4, 5]);
        var ex = Assert.Throws<ClauderooException>(() => new DpapiSecretStore().Load(home.Dir));
        Assert.Equal("The saved API key can't be read on this account. Enter it again.", ex.Message);
    }
}
