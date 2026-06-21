using System.IO;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CobblemonLegacy;

internal static class MinecraftServerStatusClient
{
    private const int ProtocolVersion = 767;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    public static async Task<MinecraftServerStatus> QueryAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = ServerEndpoint.Parse(address);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QueryTimeout);

            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);

            await using var stream = client.GetStream();
            await SendHandshakeAsync(stream, endpoint, timeout.Token);
            await SendStatusRequestAsync(stream, timeout.Token);

            var packetLength = await ReadVarIntAsync(stream, timeout.Token);
            if (packetLength <= 0)
                return MinecraftServerStatus.Unavailable("Servidor sem resposta.");

            var packetId = await ReadVarIntAsync(stream, timeout.Token);
            if (packetId != 0)
                return MinecraftServerStatus.Unavailable("Resposta invalida do servidor.");

            var jsonLength = await ReadVarIntAsync(stream, timeout.Token);
            var jsonBytes = await ReadExactAsync(stream, jsonLength, timeout.Token);
            stopwatch.Stop();
            return ParseStatusJson(Encoding.UTF8.GetString(jsonBytes), stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MinecraftServerStatus.Unavailable("Servidor demorou para responder.");
        }
        catch (Exception ex)
        {
            return MinecraftServerStatus.Unavailable($"Status indisponivel: {ex.Message}");
        }
    }

    private static async Task SendHandshakeAsync(NetworkStream stream, ServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        WriteVarInt(payload, ProtocolVersion);
        WriteString(payload, endpoint.Host);
        WriteUnsignedShort(payload, endpoint.Port);
        WriteVarInt(payload, 1);

        await SendPacketAsync(stream, 0, payload.ToArray(), cancellationToken);
    }

    private static Task SendStatusRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        return SendPacketAsync(stream, 0, [], cancellationToken);
    }

    private static async Task SendPacketAsync(NetworkStream stream, int packetId, byte[] payload, CancellationToken cancellationToken)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, packetId);
        packet.Write(payload);

        var packetBytes = packet.ToArray();
        using var framed = new MemoryStream();
        WriteVarInt(framed, packetBytes.Length);
        framed.Write(packetBytes);

        await stream.WriteAsync(framed.ToArray(), cancellationToken);
    }

    private static MinecraftServerStatus ParseStatusJson(string json, long latencyMs)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("players", out var players))
            return MinecraftServerStatus.Unavailable("Servidor nao informou jogadores.");

        var versionName = root.TryGetProperty("version", out var version)
            && version.TryGetProperty("name", out var versionNameElement)
            ? versionNameElement.GetString() ?? ""
            : "";
        var motd = root.TryGetProperty("description", out var description)
            ? FlattenDescription(description).Trim()
            : "";

        var online = players.TryGetProperty("online", out var onlineElement)
            ? onlineElement.GetInt32()
            : 0;
        var max = players.TryGetProperty("max", out var maxElement)
            ? maxElement.GetInt32()
            : 0;
        var names = new List<string>();

        if (players.TryGetProperty("sample", out var sample) && sample.ValueKind == JsonValueKind.Array)
        {
            foreach (var player in sample.EnumerateArray())
            {
                if (player.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                    names.Add(name.GetString()!);
            }
        }

        return MinecraftServerStatus.Available(online, max, names, latencyMs, motd, versionName);
    }

    private static string FlattenDescription(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";

        if (element.ValueKind != JsonValueKind.Object)
            return "";

        var builder = new StringBuilder();
        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            builder.Append(text.GetString());

        if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in extra.EnumerateArray())
                builder.Append(FlattenDescription(child));
        }

        return builder.ToString();
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = 0;
        var position = 0;

        while (true)
        {
            var currentByte = await ReadByteAsync(stream, cancellationToken);
            value |= (currentByte & 0x7F) << position;

            if ((currentByte & 0x80) == 0)
                return value;

            position += 7;
            if (position >= 32)
                throw new InvalidDataException("VarInt muito grande.");
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();

            offset += read;
        }

        return buffer;
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = await ReadExactAsync(stream, 1, cancellationToken);
        return buffer[0];
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        while (true)
        {
            if ((value & ~0x7F) == 0)
            {
                stream.WriteByte((byte)value);
                return;
            }

            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value = (int)((uint)value >> 7);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteUnsignedShort(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }
}

internal sealed record MinecraftServerStatus(
    bool IsAvailable,
    int Online,
    int Max,
    IReadOnlyList<string> Sample,
    string Error,
    long LatencyMs,
    string Motd,
    string VersionName)
{
    public static MinecraftServerStatus Available(
        int online,
        int max,
        IReadOnlyList<string> sample,
        long latencyMs,
        string motd,
        string versionName)
    {
        return new MinecraftServerStatus(true, online, max, sample, "", latencyMs, motd, versionName);
    }

    public static MinecraftServerStatus Unavailable(string error)
    {
        return new MinecraftServerStatus(false, 0, 0, [], error, 0, "", "");
    }

    public string ToDisplayText()
    {
        if (!IsAvailable)
            return "Online: indisponivel";

        var capacity = Max > 0 ? $"/{Max}" : "";
        if (Sample.Count == 0)
            return $"Online: {Online}{capacity} | {LatencyMs}ms";

        var visibleNames = string.Join(", ", Sample.Take(4));
        var suffix = Online > Sample.Count ? "..." : "";
        return $"Online: {Online}{capacity} - {visibleNames}{suffix}";
    }

    public string ToToolTipText()
    {
        if (!IsAvailable)
            return Error;

        var lines = new List<string>
        {
            $"Online: {Online}/{Max}",
            $"Ping: {LatencyMs}ms"
        };

        if (!string.IsNullOrWhiteSpace(VersionName))
            lines.Add($"Versao: {VersionName}");

        if (!string.IsNullOrWhiteSpace(Motd))
            lines.Add($"MOTD: {Motd}");

        lines.Add(Sample.Count == 0
            ? "O servidor nao enviou nomes, apenas a quantidade online."
            : $"Players: {string.Join(", ", Sample)}");

        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed record ServerEndpoint(string Host, int Port)
{
    public static ServerEndpoint Parse(string address)
    {
        var parts = address.Split(':', 2, StringSplitOptions.TrimEntries);
        var host = parts[0];
        var port = parts.Length == 2 && int.TryParse(parts[1], out var parsedPort)
            ? parsedPort
            : 25565;

        return new ServerEndpoint(host, port);
    }
}
