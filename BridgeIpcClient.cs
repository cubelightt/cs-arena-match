// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArenaMatch;

public sealed record LoadedMatch(MatchContract Contract, string Sha256);
public sealed record FetchedMatch(byte[] Json, string Sha256);
public sealed record LoadStatus(string Status, bool Acked);
public sealed class BridgeIpcException(string code) : IOException($"bridge IPC rejected request: {code}")
{
    public string Code { get; } = code;
}

// 只在工作线程调用；调用方应用比赛状态后才可报告装载成功。
public static class BridgeIpcClient
{
    private const int MaxFrameBytes = 2 * MatchContract.MaxJsonBytes + 4096;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<LoadedMatch> LoadAsync(string socketPath, long matchId, CancellationToken cancellationToken = default)
    {
        var fetched = await FetchAsync(socketPath, matchId, cancellationToken);
        return new LoadedMatch(MatchContract.Parse(fetched.Json, matchId), fetched.Sha256);
    }

    // 单独取得已校验摘要的原文，使 JSON 应用失败仍可带绑定摘要回报。
    public static async Task<FetchedMatch> FetchAsync(string socketPath, long matchId, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) throw new ArgumentOutOfRangeException(nameof(matchId));
        using var response = await ExchangeAsync(socketPath, new { version = 1, op = "load_match", matchId }, cancellationToken);
        var root = response.RootElement;
        CheckEnvelope(root, matchId);
        var sha256 = StringField(root, "sha256").ToLowerInvariant();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(StringField(root, "jsonBase64")); }
        catch (FormatException ex) { throw new FormatException("invalid bridge IPC JSON base64", ex); }
        if (bytes.Length > MatchContract.MaxJsonBytes) throw new FormatException("match JSON exceeds 1 MiB");
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(sha256, actual)) throw new FormatException("match JSON digest mismatch");
        return new FetchedMatch(bytes, actual);
    }

    public static async Task ReportLoadAsync(string socketPath, long matchId, string sha256,
        bool success, string? code = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) throw new ArgumentOutOfRangeException(nameof(matchId));
        if (success && (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("success requires a SHA-256 digest", nameof(sha256));
        using var response = await ExchangeAsync(socketPath,
            new { version = 1, op = "load_result", matchId, sha256, ok = success, code, reason }, cancellationToken);
        CheckEnvelope(response.RootElement, matchId);
    }

    public static async Task<LoadStatus> GetLoadStatusAsync(string socketPath, long matchId, string sha256,
        CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) throw new ArgumentOutOfRangeException(nameof(matchId));
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("invalid SHA-256 digest", nameof(sha256));
        using var response = await ExchangeAsync(socketPath,
            new { version = 1, op = "load_status", matchId, sha256 }, cancellationToken);
        var root = response.RootElement;
        CheckEnvelope(root, matchId);
        if (!root.TryGetProperty("acked", out var acked) || acked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("bridge IPC acked missing");
        return new LoadStatus(StringField(root, "status"), acked.GetBoolean());
    }

    // 调用方必须在工作线程上等待；失败时保留原事件，由调用方决定重试。
    public static async Task SendEventAsync(string socketPath, long matchId, JsonElement matchEvent,
        CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) throw new ArgumentOutOfRangeException(nameof(matchId));
        if (matchEvent.ValueKind != JsonValueKind.Object) throw new ArgumentException("event must be an object", nameof(matchEvent));
        using var response = await ExchangeAsync(socketPath,
            new { version = 1, op = "event", matchId, @event = matchEvent }, cancellationToken);
        CheckEnvelope(response.RootElement, matchId);
    }

    // 桥先持久记录任务才确认；HTTP 上传在桥后台进行。
    public static async Task QueueDemoAsync(string socketPath, long matchId, int mapNumber,
        int roundNumber, string relativePath, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) throw new ArgumentOutOfRangeException(nameof(matchId));
        if (mapNumber < 0 || roundNumber < 0) throw new ArgumentOutOfRangeException(nameof(mapNumber));
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("demo path missing", nameof(relativePath));
        using var response = await ExchangeAsync(socketPath,
            new { version = 1, op = "demo_ready", matchId, mapNumber, roundNumber, path = relativePath }, cancellationToken);
        CheckEnvelope(response.RootElement, matchId);
    }

    private static async Task<JsonDocument> ExchangeAsync(string socketPath, object request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
        await stream.WriteAsync(requestBytes, deadline.Token);
        await stream.FlushAsync(deadline.Token);
        using var frame = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, deadline.Token);
            if (n == 0) throw new IOException("bridge closed before IPC response");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, n);
            var count = newline >= 0 ? newline : n;
            if (frame.Length + count > MaxFrameBytes) throw new FormatException("bridge IPC response too large");
            frame.Write(buffer, 0, count);
            if (newline >= 0) break;
        }
        try { return JsonDocument.Parse(frame.ToArray()); }
        catch (JsonException ex) { throw new FormatException("invalid bridge IPC response", ex); }
    }

    private static void CheckEnvelope(JsonElement root, long matchId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)
            throw new FormatException("unsupported bridge IPC version");
        if (!root.TryGetProperty("matchId", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var responseId) || responseId != matchId)
            throw new FormatException("bridge IPC matchId mismatch");
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("bridge IPC result missing");
        if (!ok.GetBoolean())
        {
            var code = root.TryGetProperty("code", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() : "bridge_error";
            throw new BridgeIpcException(code ?? "bridge_error");
        }
    }

    private static string StringField(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString())) throw new FormatException($"bridge IPC {key} missing");
        return value.GetString()!;
    }
}
