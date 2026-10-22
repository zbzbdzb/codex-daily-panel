using System.Globalization;
using System.Text.Json;

namespace CodexUsage.Core;

public enum AccountQuotaStatus
{
    Ready, NoData, LoginRequired, UnsupportedAccount, CliNotFound, StartupFailed,
    PermissionDenied, Timeout, NetworkError, RateLimited, UnsupportedProtocol, ProtocolError, ServiceError, CleanupFailed
}

// Account quotas deliberately have no session identity, token count or cost projection.
public sealed record AccountQuotaWindow(double UsedPercent, long? WindowMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
public sealed record AccountCredits(bool? HasCredits, bool? Unlimited, decimal? Balance);
public sealed record AccountQuotaBucket(string Id, string? Plan, AccountQuotaWindow? Primary,
    AccountQuotaWindow? Secondary, AccountCredits? Credits);
public sealed record AccountQuotaSnapshot(DateTimeOffset ObservedAt, string? Plan,
    IReadOnlyList<AccountQuotaBucket> Buckets, long? AvailableResetCount);
public sealed record AccountQuotaResult(AccountQuotaStatus Status, DateTimeOffset AttemptedAt, AccountQuotaSnapshot? Snapshot = null);

public sealed class AccountRpcException(AccountQuotaStatus status) : Exception("Account quota request failed.")
{
    public AccountQuotaStatus Status { get; } = status;
}

public interface IAccountRpcSession : IAsyncDisposable
{
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken);
    Task NotifyInitializedAsync(CancellationToken cancellationToken);
    bool CleanupSucceeded { get; }
}

public sealed class AccountQuotaClient(Func<IAccountRpcSession> createSession)
{
    public async Task<AccountQuotaResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        var attempted = DateTimeOffset.UtcNow;
        IAccountRpcSession? session = null;
        AccountQuotaResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = createSession();
            await session.RequestAsync("initialize", new { clientInfo = new { name = "codex_usage_monitor", version = "0.1.0" } }, cancellationToken).ConfigureAwait(false);
            await session.NotifyInitializedAsync(cancellationToken).ConfigureAwait(false);
            var account = await session.RequestAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
            var identity = AccountQuotaParser.ReadAccount(account);
            if (identity.Status is { } unavailable) result = new(unavailable, attempted);
            else
            {
                var limits = await session.RequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
                var snapshot = AccountQuotaParser.ReadLimits(limits, identity.Plan, DateTimeOffset.UtcNow);
                var hasData = snapshot.Buckets.Any(b => b.Primary is not null || b.Secondary is not null || b.Credits is not null)
                    || snapshot.AvailableResetCount is not null;
                result = new(hasData ? AccountQuotaStatus.Ready : AccountQuotaStatus.NoData, attempted, snapshot);
            }
        }
        catch (AccountRpcException e) { result = new(e.Status, attempted); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { result = new(AccountQuotaStatus.Timeout, attempted); }
        catch (JsonException) { result = new(AccountQuotaStatus.ProtocolError, attempted); }
        catch (IOException) { result = new(AccountQuotaStatus.StartupFailed, attempted); }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        return session?.CleanupSucceeded == false ? new(AccountQuotaStatus.CleanupFailed, attempted) : result;
    }
}

public static class AccountQuotaParser
{
    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    private static void Need(bool valid) { if (!valid) throw new AccountRpcException(AccountQuotaStatus.ProtocolError); }
    public static void ValidateObject(JsonElement value)
    {
        Need(value.ValueKind == JsonValueKind.Object);
        ValidateDuplicates(value);
    }
    private static void ValidateDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { Need(names.Add(field.Name)); ValidateDuplicates(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) ValidateDuplicates(element);
    }
    public static (AccountQuotaStatus? Status, string? Plan) ReadAccount(JsonElement result)
    {
        ValidateObject(result);
        var account = Field(result, "account");
        Need(result.TryGetProperty("account", out _));
        if (Missing(account))
            return (Field(result, "requiresOpenaiAuth").ValueKind == JsonValueKind.True ? AccountQuotaStatus.LoginRequired : AccountQuotaStatus.UnsupportedAccount, null);
        Need(account.ValueKind == JsonValueKind.Object);
        var type = Field(account, "type");
        Need(type.ValueKind == JsonValueKind.String);
        return type.GetString() == "chatgpt" ? (null, Plan(Field(account, "planType"))) : (AccountQuotaStatus.UnsupportedAccount, null);
    }
    public static AccountQuotaSnapshot ReadLimits(JsonElement result, string? plan, DateTimeOffset observed)
    {
        ValidateObject(result);
        var buckets = new List<AccountQuotaBucket>();
        var multiple = Field(result, "rateLimitsByLimitId");
        if (!Missing(multiple))
        {
            Need(multiple.ValueKind == JsonValueKind.Object);
            foreach (var field in multiple.EnumerateObject())
            {
                Need(buckets.Count < 16);
                // Bucket labels are identifiers, never account IDs or free-form server text.
                var id = SafeId(field.Name) ? field.Name : $"额度 {buckets.Count + 1}";
                buckets.Add(Bucket(field.Value, id));
            }
        }
        if (buckets.Count == 0)
        {
            var single = Field(result, "rateLimits");
            Need(result.TryGetProperty("rateLimits", out _) || !Missing(multiple));
            if (!Missing(single)) buckets.Add(Bucket(single, "codex"));
        }
        var resets = Field(result, "rateLimitResetCredits");
        Need(Missing(resets) || resets.ValueKind == JsonValueKind.Object);
        var count = Missing(resets) ? null : Integer(Field(resets, "availableCount"));
        return new(observed, plan, buckets.AsReadOnly(), count);
    }
    private static bool SafeId(string value) => value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static AccountQuotaBucket Bucket(JsonElement value, string id)
    {
        Need(value.ValueKind == JsonValueKind.Object);
        return new(id, Plan(Field(value, "planType")), Window(Field(value, "primary")), Window(Field(value, "secondary")), Credits(Field(value, "credits")));
    }
    private static string? Plan(JsonElement value)
    {
        if (Missing(value)) return null;
        Need(value.ValueKind == JsonValueKind.String);
        // Unknown plan variants don't require identity or credential decoding.
        return value.GetString() switch
        {
            "free" => "Free", "plus" => "Plus", "pro" => "Pro", "team" => "Team", "business" => "Business",
            "enterprise" => "Enterprise", "edu" => "Edu", _ => "其他套餐"
        };
    }
    private static AccountQuotaWindow? Window(JsonElement value)
    {
        if (Missing(value)) return null;
        Need(value.ValueKind == JsonValueKind.Object);
        var used = Field(value, "usedPercent");
        Need(used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out _) && double.IsFinite(used.GetDouble()) && used.GetDouble() >= 0);
        var duration = Integer(Field(value, "windowDurationMins"));
        Need(duration is null or > 0);
        var reset = Integer(Field(value, "resetsAt"));
        DateTimeOffset? timestamp = null;
        if (reset is { } seconds)
        {
            try { timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { throw new AccountRpcException(AccountQuotaStatus.ProtocolError); }
        }
        return new(used.GetDouble(), duration, timestamp);
    }
    private static long? Integer(JsonElement value)
    {
        if (Missing(value)) return null;
        Need(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _) && value.GetInt64() >= 0);
        return value.GetInt64();
    }
    private static bool? Boolean(JsonElement value)
    {
        if (Missing(value)) return null;
        Need(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return value.GetBoolean();
    }
    private static AccountCredits? Credits(JsonElement value)
    {
        if (Missing(value)) return null;
        Need(value.ValueKind == JsonValueKind.Object);
        var balance = Field(value, "balance");
        decimal? amount = null;
        if (!Missing(balance))
        {
            decimal number = 0;
            var valid = balance.ValueKind == JsonValueKind.Number ? balance.TryGetDecimal(out number)
                : balance.ValueKind == JsonValueKind.String && balance.GetString() is { Length: <= 64 } text
                    && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
            Need(valid && number >= 0);
            amount = number;
        }
        var has = Boolean(Field(value, "hasCredits"));
        var unlimited = Boolean(Field(value, "unlimited"));
        return amount is null && has is null && unlimited is null ? null : new(has, unlimited, amount);
    }
}
