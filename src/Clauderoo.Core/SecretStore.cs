using System.Runtime.InteropServices;
using System.Text;

namespace Clauderoo.Core;

public interface ISecretStore
{
    void Save(string profileDir, string secret);
    string? Load(string profileDir);
    void Delete(string profileDir);
    bool Exists(string profileDir);

    /// <summary>When the secret was saved (non-secret metadata), or null when unknown.</summary>
    DateTimeOffset? SavedAt(string profileDir) => null;
}

/// <summary>Stores one kind of profile secret (API key, Claude token) as a DPAPI (CurrentUser) blob in the profile folder.</summary>
public sealed class DpapiSecretStore(string fileName = DpapiSecretStore.FileName, string kind = "API key") : ISecretStore
{
    public const string FileName = ".clauderoo-apikey";
    public const string TokenFileName = ".clauderoo-token";
    private string UnreadableMessage => $"The saved {kind} can't be read on this account. Enter it again.";
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("clauderoo");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private string PathFor(string profileDir) => Path.Combine(profileDir, fileName);

    /// <summary>The plain-text sidecar holding only the saved-at time (never the secret), e.g. ".clauderoo-token.meta".</summary>
    private string MetaFor(string profileDir) => PathFor(profileDir) + MetaSuffix;
    public const string MetaSuffix = ".meta";

    /// <summary>The clock Save stamps the sidecar with (tests set it).</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public void Save(string profileDir, string secret)
    {
        var blob = Transform(Encoding.UTF8.GetBytes(secret), protect: true)
            ?? throw new ClauderooException($"Couldn't encrypt the {kind} on this account.");
        AtomicFile.WriteAllBytes(PathFor(profileDir), blob);
        // Best effort: a missing sidecar falls back to the blob's own write time.
        try
        {
            AtomicFile.WriteAllText(MetaFor(profileDir), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["savedAt"] = Clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The sidecar's saved-at time; for a blob saved before sidecars existed (or whose sidecar is unreadable), the
    /// blob's last write time. Null when no secret is saved.
    /// </summary>
    public DateTimeOffset? SavedAt(string profileDir)
    {
        var path = PathFor(profileDir);
        if (!File.Exists(path)) return null;
        try
        {
            var meta = MetaFor(profileDir);
            if (File.Exists(meta))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(meta));
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("savedAt", out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                    return at.ToUniversalTime();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? Load(string profileDir)
    {
        var path = PathFor(profileDir);
        if (!File.Exists(path)) return null;
        var plain = Transform(File.ReadAllBytes(path), protect: false)
            ?? throw new ClauderooException(UnreadableMessage);
        return Encoding.UTF8.GetString(plain);
    }

    public void Delete(string profileDir)
    {
        var path = PathFor(profileDir);
        if (File.Exists(path)) File.Delete(path);
        var meta = MetaFor(profileDir);
        if (File.Exists(meta)) File.Delete(meta);
    }

    public bool Exists(string profileDir) => File.Exists(PathFor(profileDir));

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var dataIn = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var ent = new DataBlob { cbData = Entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) return null;
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            inHandle.Free();
            entHandle.Free();
        }
    }
}
