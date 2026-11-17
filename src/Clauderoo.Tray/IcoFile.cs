namespace Clauderoo.Tray;

/// <summary>
/// Minimal reader/writer for the .ico container with PNG-compressed frames (Windows Vista and later).
/// Shared with <c>tools/IconGen</c> as a linked source file.
/// </summary>
internal static class IcoFile
{
    /// <summary>One image in an .ico file; <see cref="Data"/> is the PNG (or, for foreign files, DIB) payload.</summary>
    public readonly record struct Frame(int Width, int Height, byte[] Data);

    public static void Write(Stream output, IReadOnlyList<Frame> frames)
    {
        using var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            w.Write((byte)(f.Width >= 256 ? 0 : f.Width));
            w.Write((byte)(f.Height >= 256 ? 0 : f.Height));
            w.Write((byte)0);          // palette size
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(f.Data.Length);
            w.Write(offset);
            offset += f.Data.Length;
        }
        foreach (var f in frames) w.Write(f.Data);
    }

    public static List<Frame> Read(byte[] data)
    {
        if (data.Length < 6 || BitConverter.ToUInt16(data, 0) != 0 || BitConverter.ToUInt16(data, 2) != 1)
            throw new InvalidDataException("Not an .ico file.");
        int count = BitConverter.ToUInt16(data, 4);
        if (data.Length < 6 + 16 * count) throw new InvalidDataException("Truncated .ico directory.");
        var frames = new List<Frame>(count);
        for (var i = 0; i < count; i++)
        {
            var e = 6 + 16 * i;
            var width = data[e] == 0 ? 256 : data[e];
            var height = data[e + 1] == 0 ? 256 : data[e + 1];
            var size = BitConverter.ToInt32(data, e + 8);
            var offset = BitConverter.ToInt32(data, e + 12);
            if (size < 0 || offset < 0 || (long)offset + size > data.Length)
                throw new InvalidDataException("Truncated .ico frame.");
            frames.Add(new Frame(width, height, data.AsSpan(offset, size).ToArray()));
        }
        return frames;
    }
}
