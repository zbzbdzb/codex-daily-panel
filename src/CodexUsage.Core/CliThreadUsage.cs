using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsage.Core;

public sealed record CliThreadChoice(string Id, string Label, DateTimeOffset? CreatedAt, string NamespaceKey);
public sealed record CliThreadCatalog(IReadOnlyList<CliThreadChoice> Threads, string? NextCursor, DateTimeOffset ReceivedAt);
public sealed record CliThreadTokenGroup(long? Input, long? CachedInput, long? NetNewInput, long? Output, long? Total);
public sealed record CliThreadTokenSnapshot(IReadOnlyList<CliThreadTokenGroup> Groups, DateTimeOffset ReceivedAt);
public sealed record CliThreadResult<T>(T? Value, AccountQuotaStatus? Error = null, bool SourceChanged = false) where T : class;

// This provider only reads CLI-visible metadata and optional server-estimated usage.
// It has no file-path model, resume/start method, Desktop selection claim or token estimator.
public sealed class CliThreadUsageClient(Func<IAccountRpcSession> createSession)
{
    public const int PageSize = 20;
    public Task<CliThreadResult<CliThreadCatalog>> ListAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        if (cursor is { Length: > 4096 }) throw new ArgumentException("Invalid cursor.", nameof(cursor));
        return WithSession<CliThreadCatalog>(async (session, scope, token) =>
        {
            var response = await session.RequestAsync("thread/list", new
            {
                limit = PageSize, cursor, useStateDbOnly = true, sortKey = "updated_at",
                sourceKinds = new[] { "cli", "vscode", "appServer", "exec", "unknown", "subAgent", "subAgentReview", "subAgentCompact", "subAgentThreadSpawn", "subAgentOther" }
            }, token).ConfigureAwait(false);
            return new(CliThreadUsageParser.Catalog(response, scope, DateTimeOffset.UtcNow));
        }, cancellationToken);
    }
    public Task<CliThreadResult<CliThreadTokenSnapshot>> ReadAsync(CliThreadChoice choice, CancellationToken cancellationToken = default)
    {
        if (choice.Id.Length is 0 or > 128 || choice.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return Task.FromResult(new CliThreadResult<CliThreadTokenSnapshot>(null, AccountQuotaStatus.ProtocolError));
        return WithSession<CliThreadTokenSnapshot>(async (session, scope, token) =>
        {
            if (!string.Equals(scope, choice.NamespaceKey, StringComparison.Ordinal)) return new(null, SourceChanged: true);
            var response = await session.RequestAsync("account/usage/read", new { threadId = choice.Id }, token).ConfigureAwait(false);
            return new(CliThreadUsageParser.Usage(response, choice.Id, DateTimeOffset.UtcNow));
        }, cancellationToken);
    }
    private async Task<CliThreadResult<T>> WithSession<T>(Func<IAccountRpcSession, string, CancellationToken, Task<CliThreadResult<T>>> operation, CancellationToken cancellationToken) where T : class
    {
        IAccountRpcSession? session = null;
        CliThreadResult<T> result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = createSession();
            var initialized = await session.RequestAsync("initialize", new { clientInfo = new { name = "codex_usage_manual_threads", version = "0.1.0" } }, cancellationToken).ConfigureAwait(false);
            var scope = CliThreadUsageParser.Namespace(initialized);
            await session.NotifyInitializedAsync(cancellationToken).ConfigureAwait(false);
            result = await operation(session, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountRpcException e) { result = new(null, e.Status); }
        catch (JsonException) { result = new(null, AccountQuotaStatus.ProtocolError); }
        catch (IOException) { result = new(null, AccountQuotaStatus.StartupFailed); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { result = new(null, AccountQuotaStatus.Timeout); }
        finally { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
        return session?.CleanupSucceeded == false ? new(null, AccountQuotaStatus.CleanupFailed) : result;
    }
}

public static class CliThreadUsageParser
{
    private static void Need(bool valid) { if (!valid) throw new AccountRpcException(AccountQuotaStatus.ProtocolError); }
    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    private static string RequiredId(JsonElement value)
    {
        Need(value.ValueKind == JsonValueKind.String);
        var id = value.GetString()!;
        Need(id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        return id;
    }
    public static string Namespace(JsonElement initialized)
    {
        AccountQuotaParser.ValidateObject(initialized);
        var home = Field(initialized, "codexHome");
        Need(home.ValueKind == JsonValueKind.String && home.GetString() is { Length: > 0 and <= 4096 });
        // Hash only an official initialize value; never open or persist this path.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.GetString()!)));
    }
    public static CliThreadCatalog Catalog(JsonElement response, string scope, DateTimeOffset received)
    {
        AccountQuotaParser.ValidateObject(response);
        var rows = Field(response, "data");
        Need(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() <= CliThreadUsageClient.PageSize);
        var choices = new List<CliThreadChoice>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            Need(row.ValueKind == JsonValueKind.Object);
            var id = RequiredId(Field(row, "id"));
            Need(ids.Add(id));
            var name = Field(row, "name");
            Need(Missing(name) || name.ValueKind == JsonValueKind.String);
            // User-set titles are inert UI text; previews, paths, routing and readStates aren't retained.
            var label = name.ValueKind == JsonValueKind.String ? string.Concat((name.GetString() ?? "").EnumerateRunes().Where(r => !Rune.IsControl(r)).Take(96).Select(r => r.ToString())).Trim() : "";
            if (label.Length == 0) label = "会话 " + id[..Math.Min(id.Length, 8)];
            DateTimeOffset? created = null;
            var timestamp = Field(row, "createdAt");
            if (!Missing(timestamp))
            {
                Need(timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out _));
                try { created = DateTimeOffset.FromUnixTimeSeconds(timestamp.GetInt64()); }
                catch (ArgumentOutOfRangeException) { throw new AccountRpcException(AccountQuotaStatus.ProtocolError); }
            }
            choices.Add(new(id, label, created, scope));
        }
        var next = Field(response, "nextCursor");
        Need(Missing(next) || next.ValueKind == JsonValueKind.String && next.GetString() is { Length: > 0 and <= 4096 });
        return new(choices.AsReadOnly(), Missing(next) ? null : next.GetString(), received);
    }
    public static CliThreadTokenSnapshot? Usage(JsonElement response, string requestedId, DateTimeOffset received)
    {
        AccountQuotaParser.ValidateObject(response);
        var usage = Field(response, "threadUsage");
        if (Missing(usage)) return null;
        Need(usage.ValueKind == JsonValueKind.Object && RequiredId(Field(usage, "threadId")) == requestedId);
        var groups = Field(usage, "groups");
        Need(groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() <= 64);
        var result = new List<CliThreadTokenGroup>();
        foreach (var group in groups.EnumerateArray())
        {
            Need(group.ValueKind == JsonValueKind.Object);
            var input = Number(Field(group, "inputTokens"));
            var cached = Number(Field(group, "cachedInputTokens"));
            var net = Number(Field(group, "netNewInputTokens"));
            var output = Number(Field(group, "outputTokens"));
            var total = Number(Field(group, "totalTokens"));
            Need(!(cached > input) && !(net > input));
            if (input is { } a && cached is { } b && net is { } c) Need((decimal)b + c == a);
            if (input is { } i && output is { } o && total is { } t) Need((decimal)i + o == t);
            result.Add(new(input, cached, net, output, total));
        }
        return result.Any(g => g.Input is not null || g.CachedInput is not null || g.NetNewInput is not null || g.Output is not null || g.Total is not null)
            ? new(result.AsReadOnly(), received) : null;
    }
    private static long? Number(JsonElement value)
    {
        if (Missing(value)) return null;
        Need(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _) && value.GetInt64() >= 0);
        return value.GetInt64();
    }
}
