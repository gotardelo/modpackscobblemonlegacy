using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CobblemonLegacy;

internal static class MinecraftProfileConfigurator
{
    private const int CurrentPerformancePresetVersion = 5;

    public static async Task ConfigureAsync(
        string gameDir,
        LauncherSettings settings,
        Action<string>? log = null)
    {
        EnsureServerList(gameDir, log);
        await ApplyPerformancePresetOnceAsync(gameDir, settings, log);
    }

    private static void EnsureServerList(string gameDir, Action<string>? log)
    {
        var serversPath = Path.Combine(gameDir, "servers.dat");
        var root = File.Exists(serversPath)
            ? NbtFile.ReadRoot(serversPath)
            : new NbtCompound("", []);

        var servers = root.GetList("servers");
        if (servers is null || servers.ElementType != NbtTagType.Compound)
        {
            servers = new NbtList("servers", NbtTagType.Compound, []);
            root.Set(servers);
        }

        var existing = servers.Items
            .OfType<NbtCompound>()
            .FirstOrDefault(server => string.Equals(server.GetString("ip"), LauncherRuntime.ServerIp, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            servers.Items.Insert(0, CreateServerEntry());
            NbtFile.WriteRoot(serversPath, root);
            log?.Invoke("Servidor Cobblemon Legacy adicionado ao Multiplayer.");
            return;
        }

        existing.Set(new NbtString("name", LauncherRuntime.LauncherName));
        existing.Set(new NbtString("ip", LauncherRuntime.ServerIp));
        NbtFile.WriteRoot(serversPath, root);
        log?.Invoke("Servidor Cobblemon Legacy confirmado no Multiplayer.");
    }

    private static NbtCompound CreateServerEntry()
    {
        return new NbtCompound("", new List<NbtTag>
        {
            new NbtString("name", LauncherRuntime.LauncherName),
            new NbtString("ip", LauncherRuntime.ServerIp),
            new NbtByte("acceptTextures", 1)
        });
    }

    private static async Task ApplyPerformancePresetOnceAsync(string gameDir, LauncherSettings settings, Action<string>? log)
    {
        if (settings.PerformancePresetVersion >= CurrentPerformancePresetVersion)
            return;

        var optionsPath = Path.Combine(gameDir, "options.txt");
        var options = File.Exists(optionsPath)
            ? await File.ReadAllLinesAsync(optionsPath, Encoding.UTF8)
            : [];

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var line in options)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator];
            map[key] = line[(separator + 1)..];
            if (!order.Contains(key, StringComparer.OrdinalIgnoreCase))
                order.Add(key);
        }

        var preset = PerformancePreset.ForProfile(settings.PerformanceProfile, LauncherRuntime.GetPerformanceTier());
        await LauncherRuntime.BackupUserConfigurationAsync(gameDir, log);

        SetOption(map, order, "graphicsMode", "0");
        SetOption(map, order, "renderDistance", preset.RenderDistance.ToString());
        SetOption(map, order, "simulationDistance", preset.SimulationDistance.ToString());
        SetOption(map, order, "mipmapLevels", preset.MipmapLevels.ToString());
        SetOption(map, order, "particles", preset.Particles.ToString());
        SetOption(map, order, "renderClouds", "\"false\"");
        SetOption(map, order, "entityShadows", "false");
        SetOption(map, order, "entityDistanceScaling", preset.EntityDistanceScaling);
        SetOption(map, order, "biomeBlendRadius", preset.BiomeBlendRadius.ToString());
        SetOption(map, order, "maxFps", preset.MaxFps.ToString());
        SetOption(map, order, "enableVsync", "true");
        SetOption(map, order, "ao", preset.AmbientOcclusion ? "true" : "false");
        SetOption(map, order, "fancyGraphics", "false");
        SetOption(map, order, "fovEffectScale", "0.0");
        SetOption(map, order, "darknessEffectScale", "0.0");
        SetOption(map, order, "glintSpeed", "0.0");
        SetOption(map, order, "glintStrength", "0.25");
        SetOption(map, order, "menuBackgroundBlurriness", "0");
        SetOption(map, order, "lastServer", LauncherRuntime.ServerIp);

        var output = order.Select(key => $"{key}:{map[key]}").ToArray();
        await File.WriteAllLinesAsync(optionsPath, output, Encoding.UTF8);

        settings.PerformancePresetVersion = CurrentPerformancePresetVersion;
        await settings.SaveAsync(LauncherRuntime.JsonOptions);
        log?.Invoke($"Preset de performance aplicado: {preset.Name}.");
    }

    private static void SetOption(Dictionary<string, string> map, List<string> order, string key, string value)
    {
        map[key] = value;
        if (!order.Contains(key, StringComparer.OrdinalIgnoreCase))
            order.Add(key);
    }

    private sealed record PerformancePreset(
        string Name,
        int RenderDistance,
        int SimulationDistance,
        int MipmapLevels,
        int Particles,
        string EntityDistanceScaling,
        int BiomeBlendRadius,
        int MaxFps,
        bool AmbientOcclusion)
    {
        public static PerformancePreset ForProfile(string profile, PerformanceTier detectedTier)
        {
            return profile switch
            {
                PerformanceProfiles.Low => new PerformancePreset("PC fraco", 4, 3, 0, 2, "0.45", 0, 45, false),
                PerformanceProfiles.Balanced => new PerformancePreset("equilibrado", 6, 4, 1, 1, "0.65", 0, 60, false),
                PerformanceProfiles.High => new PerformancePreset("alto desempenho", 10, 6, 2, 0, "0.9", 1, 120, true),
                _ => detectedTier switch
                {
                    PerformanceTier.LowEnd => new PerformancePreset("automatico leve", 4, 3, 0, 2, "0.45", 0, 45, false),
                    PerformanceTier.Balanced => new PerformancePreset("automatico equilibrado", 6, 4, 1, 1, "0.65", 0, 60, false),
                    _ => new PerformancePreset("automatico padrao", 8, 5, 2, 1, "0.75", 1, 90, true)
                }
            };
        }
    }
}

internal enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12
}

internal abstract class NbtTag(string name)
{
    public string Name { get; set; } = name;
    public abstract NbtTagType Type { get; }
}

internal sealed class NbtByte(string name, byte value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Byte;
    public byte Value { get; set; } = value;
}

internal sealed class NbtShort(string name, short value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Short;
    public short Value { get; set; } = value;
}

internal sealed class NbtInt(string name, int value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Int;
    public int Value { get; set; } = value;
}

internal sealed class NbtLong(string name, long value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Long;
    public long Value { get; set; } = value;
}

internal sealed class NbtFloat(string name, float value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Float;
    public float Value { get; set; } = value;
}

internal sealed class NbtDouble(string name, double value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Double;
    public double Value { get; set; } = value;
}

internal sealed class NbtByteArray(string name, byte[] value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.ByteArray;
    public byte[] Value { get; set; } = value;
}

internal sealed class NbtString(string name, string value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.String;
    public string Value { get; set; } = value;
}

internal sealed class NbtList(string name, NbtTagType elementType, List<NbtTag> items) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.List;
    public NbtTagType ElementType { get; set; } = elementType;
    public List<NbtTag> Items { get; } = items;
}

internal sealed class NbtCompound(string name, List<NbtTag> tags) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.Compound;
    public List<NbtTag> Tags { get; } = tags;

    public NbtList? GetList(string name)
    {
        return Tags.OfType<NbtList>().FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public string? GetString(string name)
    {
        return Tags.OfType<NbtString>().FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    public void Set(NbtTag tag)
    {
        var index = Tags.FindIndex(existing => string.Equals(existing.Name, tag.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            Tags[index] = tag;
        else
            Tags.Add(tag);
    }
}

internal sealed class NbtIntArray(string name, int[] value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.IntArray;
    public int[] Value { get; set; } = value;
}

internal sealed class NbtLongArray(string name, long[] value) : NbtTag(name)
{
    public override NbtTagType Type => NbtTagType.LongArray;
    public long[] Value { get; set; } = value;
}

internal static class NbtFile
{
    public static NbtCompound ReadRoot(string path)
    {
        using var file = File.OpenRead(path);
        using Stream stream = IsGzip(file) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var type = (NbtTagType)reader.ReadByte();
        if (type != NbtTagType.Compound)
            throw new InvalidDataException("servers.dat nao contem um compound NBT valido.");

        var name = ReadString(reader);
        return (NbtCompound)ReadPayload(reader, type, name);
    }

    public static void WriteRoot(string path, NbtCompound root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: false);
        writer.Write((byte)NbtTagType.Compound);
        WriteString(writer, root.Name);
        WritePayload(writer, root);
    }

    private static bool IsGzip(Stream stream)
    {
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = 0;
        return first == 0x1F && second == 0x8B;
    }

    private static NbtTag ReadPayload(BinaryReader reader, NbtTagType type, string name)
    {
        return type switch
        {
            NbtTagType.Byte => new NbtByte(name, reader.ReadByte()),
            NbtTagType.Short => new NbtShort(name, ReadInt16(reader)),
            NbtTagType.Int => new NbtInt(name, ReadInt32(reader)),
            NbtTagType.Long => new NbtLong(name, ReadInt64(reader)),
            NbtTagType.Float => new NbtFloat(name, ReadSingle(reader)),
            NbtTagType.Double => new NbtDouble(name, ReadDouble(reader)),
            NbtTagType.ByteArray => new NbtByteArray(name, reader.ReadBytes(ReadInt32(reader))),
            NbtTagType.String => new NbtString(name, ReadString(reader)),
            NbtTagType.List => ReadList(reader, name),
            NbtTagType.Compound => ReadCompound(reader, name),
            NbtTagType.IntArray => new NbtIntArray(name, ReadIntArray(reader)),
            NbtTagType.LongArray => new NbtLongArray(name, ReadLongArray(reader)),
            _ => throw new InvalidDataException($"Tipo NBT nao suportado: {type}")
        };
    }

    private static NbtList ReadList(BinaryReader reader, string name)
    {
        var elementType = (NbtTagType)reader.ReadByte();
        var count = ReadInt32(reader);
        var items = new List<NbtTag>(count);
        for (var i = 0; i < count; i++)
            items.Add(ReadPayload(reader, elementType, ""));

        return new NbtList(name, elementType, items);
    }

    private static NbtCompound ReadCompound(BinaryReader reader, string name)
    {
        var tags = new List<NbtTag>();
        while (true)
        {
            var type = (NbtTagType)reader.ReadByte();
            if (type == NbtTagType.End)
                break;

            var tagName = ReadString(reader);
            tags.Add(ReadPayload(reader, type, tagName));
        }

        return new NbtCompound(name, tags);
    }

    private static int[] ReadIntArray(BinaryReader reader)
    {
        var length = ReadInt32(reader);
        var values = new int[length];
        for (var i = 0; i < length; i++)
            values[i] = ReadInt32(reader);
        return values;
    }

    private static long[] ReadLongArray(BinaryReader reader)
    {
        var length = ReadInt32(reader);
        var values = new long[length];
        for (var i = 0; i < length; i++)
            values[i] = ReadInt64(reader);
        return values;
    }

    private static void WritePayload(BinaryWriter writer, NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte value:
                writer.Write(value.Value);
                break;
            case NbtShort value:
                WriteInt16(writer, value.Value);
                break;
            case NbtInt value:
                WriteInt32(writer, value.Value);
                break;
            case NbtLong value:
                WriteInt64(writer, value.Value);
                break;
            case NbtFloat value:
                WriteSingle(writer, value.Value);
                break;
            case NbtDouble value:
                WriteDouble(writer, value.Value);
                break;
            case NbtByteArray value:
                WriteInt32(writer, value.Value.Length);
                writer.Write(value.Value);
                break;
            case NbtString value:
                WriteString(writer, value.Value);
                break;
            case NbtList value:
                writer.Write((byte)value.ElementType);
                WriteInt32(writer, value.Items.Count);
                foreach (var item in value.Items)
                    WritePayload(writer, item);
                break;
            case NbtCompound value:
                foreach (var child in value.Tags)
                {
                    writer.Write((byte)child.Type);
                    WriteString(writer, child.Name);
                    WritePayload(writer, child);
                }
                writer.Write((byte)NbtTagType.End);
                break;
            case NbtIntArray value:
                WriteInt32(writer, value.Value.Length);
                foreach (var item in value.Value)
                    WriteInt32(writer, item);
                break;
            case NbtLongArray value:
                WriteInt32(writer, value.Value.Length);
                foreach (var item in value.Value)
                    WriteInt64(writer, item);
                break;
            default:
                throw new InvalidDataException($"Tipo NBT nao suportado: {tag.Type}");
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = ReadUInt16(reader);
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
            throw new InvalidDataException("String NBT muito longa.");

        WriteUInt16(writer, (ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static short ReadInt16(BinaryReader reader)
    {
        return BinaryPrimitives.ReadInt16BigEndian(reader.ReadBytes(sizeof(short)));
    }

    private static ushort ReadUInt16(BinaryReader reader)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(sizeof(ushort)));
    }

    private static int ReadInt32(BinaryReader reader)
    {
        return BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(sizeof(int)));
    }

    private static long ReadInt64(BinaryReader reader)
    {
        return BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(sizeof(long)));
    }

    private static float ReadSingle(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(sizeof(float));
        Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    private static double ReadDouble(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(sizeof(double));
        Array.Reverse(bytes);
        return BitConverter.ToDouble(bytes, 0);
    }

    private static void WriteInt16(BinaryWriter writer, short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteUInt16(BinaryWriter writer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteInt32(BinaryWriter writer, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteInt64(BinaryWriter writer, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteSingle(BinaryWriter writer, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        writer.Write(bytes);
    }

    private static void WriteDouble(BinaryWriter writer, double value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        writer.Write(bytes);
    }
}
