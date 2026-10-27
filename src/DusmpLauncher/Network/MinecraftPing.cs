using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DusmpLauncher.Network;

/// <summary>
/// Minimal Server List Ping client, so the main screen can show whether the
/// server is up and how many players are on without depending on a web API.
/// Implements the modern (1.7+) handshake/status exchange only — the pack is
/// 1.21, and supporting the legacy ping would be dead code.
/// </summary>
public static class MinecraftPing
{
    /// <summary>Protocol version sent in the handshake. -1 means "just tell me your status".</summary>
    private const int StatusProtocolVersion = -1;

    public sealed record PingResult(int LatencyMs, int? PlayersOnline, int? PlayersMax, string? Description);

    /// <summary>
    /// Returns null when the server did not answer in time. Never throws: an
    /// offline server is a normal state to display, not an error to handle.
    /// </summary>
    public static async Task<PingResult?> TryPingAsync(
        string host,
        int port,
        CancellationToken ct = default,
        int timeoutMs = 3000)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            // Handshake: packet id 0x00, protocol version, host, port, next state 1 (status).
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, StatusProtocolVersion);
            WriteString(handshake, host);
            var portBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
            handshake.Write(portBytes);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), timeout.Token).ConfigureAwait(false);

            // Status request: packet id 0x00 with an empty body.
            var request = new MemoryStream();
            WriteVarInt(request, 0x00);
            await WritePacketAsync(stream, request.ToArray(), timeout.Token).ConfigureAwait(false);

            var payload = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            if (payload is null)
                return null;

            var cursor = 0;
            _ = ReadVarInt(payload, ref cursor);          // packet id, expected 0x00
            var jsonLength = ReadVarInt(payload, ref cursor);
            if (jsonLength <= 0 || cursor + jsonLength > payload.Length)
                return null;

            var json = Encoding.UTF8.GetString(payload, cursor, jsonLength);
            return Parse(json, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            // Unreachable, refused, DNS failure, malformed reply — all just "offline".
            return null;
        }
    }

    private static PingResult? Parse(string json, int latencyMs)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            int? online = null;
            int? max = null;
            if (root.TryGetProperty("players", out var players))
            {
                if (players.TryGetProperty("online", out var onlineElement) &&
                    onlineElement.TryGetInt32(out var onlineValue))
                    online = onlineValue;

                if (players.TryGetProperty("max", out var maxElement) &&
                    maxElement.TryGetInt32(out var maxValue))
                    max = maxValue;
            }

            string? description = null;
            if (root.TryGetProperty("description", out var descriptionElement))
            {
                description = descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : descriptionElement.TryGetProperty("text", out var text) ? text.GetString() : null;
            }

            return new PingResult(latencyMs, online, max, description);
        }
        catch (JsonException)
        {
            // A reply we cannot parse still proves the port answered.
            return new PingResult(latencyMs, null, null, null);
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var framed = new MemoryStream();
        WriteVarInt(framed, body.Length);
        framed.Write(body);
        var buffer = framed.ToArray();
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        // Cap the reply: a hostile or broken server must not be able to make us
        // allocate unbounded memory on a status ping.
        if (length is null or <= 0 or > 2 * 1024 * 1024)
            return null;

        var buffer = new byte[length.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (chunk == 0)
                return null;
            read += chunk;
        }
        return buffer;
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;
        while (true)
        {
            if ((unsigned & ~0x7Fu) == 0)
            {
                stream.WriteByte((byte)unsigned);
                return;
            }
            stream.WriteByte((byte)((unsigned & 0x7F) | 0x80));
            unsigned >>= 7;
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static int ReadVarInt(byte[] buffer, ref int cursor)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (cursor >= buffer.Length)
                return result;

            var current = buffer[cursor++];
            result |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                break;
        }
        return result;
    }

    private static async Task<int?> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var single = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (await stream.ReadAsync(single, ct).ConfigureAwait(false) == 0)
                return null;

            result |= (single[0] & 0x7F) << shift;
            if ((single[0] & 0x80) == 0)
                return result;
        }
        return null;
    }
}
