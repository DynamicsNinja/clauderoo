using System.Runtime.InteropServices;
using System.Text;

namespace Clauderoo.Core;

public interface ISecretStore
{
    void Save(string profileDir, string secret);
    string? Load(string profileDir);
    void Delete(string profileDir);
    bool Exists(string profileDir);
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

    public void Save(string profileDir, string secret)
    {
        var blob = Transform(Encoding.UTF8.GetBytes(secret), protect: true)
            ?? throw new ClauderooException($"Couldn't encrypt the {kind} on this account.");
        AtomicFile.WriteAllBytes(PathFor(profileDir), blob);
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
