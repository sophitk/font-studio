using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FontStudio.Services;

public sealed record FontFileInspection(
    bool IsValid,
    string Format,
    long SizeBytes,
    string Sha256,
    string Message,
    string FamilyName = "",
    string SubfamilyName = "",
    string Version = "",
    int TableCount = 0,
    bool IsVariable = false,
    bool HasColorGlyphs = false);

public static class FontFileInspector
{
    private const long MaximumFontBytes = 256L * 1024 * 1024;
    private const int MaximumTableCount = 512;
    private const int MaximumCollectionFonts = 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc"
    };

    public static FontFileInspection Inspect(string path)
    {
        if (!File.Exists(path)) return Invalid("File not found.");
        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension)) return Invalid("Only TTF, OTF and TTC files are supported.");

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 12) return Invalid("The file is too small to be a font.");
            if (stream.Length > MaximumFontBytes) return Invalid("The font exceeds the 256 MB safety limit.");

            Span<byte> signature = stackalloc byte[4];
            stream.ReadExactly(signature);
            var isCollection = signature.SequenceEqual("ttcf"u8);
            var format = isCollection ? "TTC" : GetSingleFontFormat(signature);
            if (format is null) return Invalid("The file signature is not a supported OpenType/TrueType font.");

            long sfntOffset = 0;
            if (isCollection)
            {
                stream.Position = 8;
                var fontCount = ReadUInt32(stream);
                if (fontCount is 0 or > MaximumCollectionFonts) return Invalid("The font collection has an invalid font count.");
                if (12L + fontCount * 4L > stream.Length) return Invalid("The font collection directory is truncated.");
                sfntOffset = ReadUInt32(stream);
            }

            var tables = ReadTableDirectory(stream, sfntOffset);
            if (tables is null) return Invalid("The OpenType table directory is invalid or truncated.");

            var names = tables.TryGetValue("name", out var nameTable)
                ? ReadNames(stream, nameTable.Offset, nameTable.Length)
                : new Dictionary<ushort, string>();

            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return new(
                true,
                format,
                stream.Length,
                hash,
                "OpenType table boundaries look valid.",
                names.GetValueOrDefault((ushort)1, string.Empty),
                names.GetValueOrDefault((ushort)2, string.Empty),
                names.GetValueOrDefault((ushort)5, string.Empty),
                tables.Count,
                tables.ContainsKey("fvar"),
                tables.Keys.Any(tag => tag is "COLR" or "CPAL" or "CBDT" or "CBLC" or "sbix" or "SVG "));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return Invalid($"Could not safely parse the font: {ex.Message}");
        }
    }

    private static string? GetSingleFontFormat(ReadOnlySpan<byte> signature)
    {
        if (signature.SequenceEqual("OTTO"u8)) return "OTF";
        if (signature.SequenceEqual(new byte[] { 0x00, 0x01, 0x00, 0x00 }) || signature.SequenceEqual("true"u8)) return "TTF";
        return null;
    }

    private static Dictionary<string, TableRange>? ReadTableDirectory(Stream stream, long sfntOffset)
    {
        if (sfntOffset < 0 || sfntOffset + 12 > stream.Length) return null;
        stream.Position = sfntOffset + 4;
        var tableCount = ReadUInt16(stream);
        if (tableCount is 0 or > MaximumTableCount) return null;
        if (sfntOffset + 12L + tableCount * 16L > stream.Length) return null;

        stream.Position = sfntOffset + 12;
        var tables = new Dictionary<string, TableRange>(StringComparer.Ordinal);
        Span<byte> entry = stackalloc byte[16];
        for (var index = 0; index < tableCount; index++)
        {
            stream.ReadExactly(entry);
            var tag = Encoding.ASCII.GetString(entry[..4]);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..12]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..16]);
            if (offset > stream.Length || length > stream.Length || (long)offset + length > stream.Length) return null;
            tables.TryAdd(tag, new TableRange(offset, length));
        }
        return tables;
    }

    private static Dictionary<ushort, string> ReadNames(Stream stream, uint tableOffset, uint tableLength)
    {
        var result = new Dictionary<ushort, string>();
        if (tableLength < 6 || (long)tableOffset + tableLength > stream.Length) return result;
        stream.Position = tableOffset;
        _ = ReadUInt16(stream);
        var count = ReadUInt16(stream);
        var stringsOffset = ReadUInt16(stream);
        if (count > 4096 || 6L + count * 12L > tableLength || stringsOffset > tableLength) return result;

        var records = new List<NameRecord>(count);
        for (var index = 0; index < count; index++)
        {
            records.Add(new NameRecord(
                ReadUInt16(stream),
                ReadUInt16(stream),
                ReadUInt16(stream),
                ReadUInt16(stream),
                ReadUInt16(stream),
                ReadUInt16(stream)));
        }

        foreach (var nameId in new ushort[] { 1, 2, 5 })
        {
            var candidates = records
                .Where(record => record.NameId == nameId)
                .OrderBy(record => record.PlatformId == 3 ? 0 : record.PlatformId == 0 ? 1 : 2)
                .ThenBy(record => record.LanguageId == 0x0409 ? 0 : 1);
            foreach (var record in candidates)
            {
                var absoluteOffset = (long)tableOffset + stringsOffset + record.Offset;
                if (absoluteOffset < tableOffset || absoluteOffset + record.Length > (long)tableOffset + tableLength) continue;
                stream.Position = absoluteOffset;
                var bytes = new byte[record.Length];
                stream.ReadExactly(bytes);
                var value = DecodeName(record.PlatformId, bytes).Trim('\0', ' ', '\r', '\n');
                if (string.IsNullOrWhiteSpace(value)) continue;
                result[nameId] = value;
                break;
            }
        }
        return result;
    }

    private static string DecodeName(ushort platformId, byte[] bytes)
    {
        if (platformId is 0 or 3)
        {
            if (bytes.Length % 2 != 0) return string.Empty;
            for (var index = 0; index < bytes.Length; index += 2)
                (bytes[index], bytes[index + 1]) = (bytes[index + 1], bytes[index]);
            return Encoding.Unicode.GetString(bytes);
        }
        return Encoding.Latin1.GetString(bytes);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static FontFileInspection Invalid(string message) => new(false, "Unknown", 0, string.Empty, message);
    private sealed record TableRange(uint Offset, uint Length);
    private sealed record NameRecord(ushort PlatformId, ushort EncodingId, ushort LanguageId, ushort NameId, ushort Length, ushort Offset);
}
