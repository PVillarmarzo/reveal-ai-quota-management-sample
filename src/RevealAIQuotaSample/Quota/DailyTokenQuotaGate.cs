using Microsoft.Extensions.Options;
using Reveal.Sdk.AI;

namespace RevealAIQuotaSample.Quota;

/// <summary>
/// Per-user daily token quota for Reveal AI.
/// <para>
/// <see cref="CheckAsync"/> runs before every provider call. It reserves an estimate for the call
/// (EstimatedInputTokensPerCall + the profile's MaxOutputTokens) and denies the call when the
/// user's committed tokens for the UTC day plus that reservation would exceed the limit.
/// </para>
/// <para>
/// <see cref="SettleAsync"/> runs after the call, keyed by the same CallId, and replaces the
/// reservation with the tokens the provider reported. It is idempotent.
/// </para>
/// Registered as a singleton by AddQuotaGate, so it must be thread-safe (the store is).
/// </summary>
public sealed class DailyTokenQuotaGate : IAIQuotaGate
{
    /// <summary>The user id the SDK uses when the user context has none.</summary>
    public const string AnonymousUserId = "anonymous";

    private readonly QuotaStore _store;
    private readonly IOptionsMonitor<QuotaOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<DailyTokenQuotaGate> _logger;

    public DailyTokenQuotaGate(
        QuotaStore store,
        IOptionsMonitor<QuotaOptions> options,
        IConfiguration configuration,
        TimeProvider time,
        ILogger<DailyTokenQuotaGate> logger)
    {
        _store = store;
        _options = options;
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    public async ValueTask<AIGateDecision> CheckAsync(AICallContext context, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var userId = ResolveUserId(context);
        if (userId is null)
        {
            // Without an identity every caller would share the "anonymous" budget.
            return AIGateDecision.Deny(AIDenialReason.NotEntitled, "Sign in to use AI features.");
        }

        var reservation = (long)options.EstimatedInputTokensPerCall + GetOutputReservation(context.Profile, options);
        var now = _time.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);

        var result = await _store.TryReserveAsync(
            context.CallId, userId, day, reservation, options.DailyTokenLimit,
            new CallInfo(context.Intent, context.Profile, context.Model), cancellationToken);

        if (result.Allowed)
        {
            return AIGateDecision.Allow();
        }

        var nextReset = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        _logger.LogInformation(
            "AI call denied for user {UserId}: {Committed} of {Limit} tokens committed today, call needs {Reservation}.",
            userId, result.CommittedTokens, options.DailyTokenLimit, reservation);

        return AIGateDecision.Deny(
            AIDenialReason.QuotaExceeded,
            $"Daily AI token limit reached ({options.DailyTokenLimit:N0} tokens). It resets at 00:00 UTC.",
            nextReset - now);
    }

    public async ValueTask SettleAsync(AICallContext context, AIUsageRecord actual, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var userId = ResolveUserId(context) ?? AnonymousUserId;

        var inputTokens = Normalize(actual.InputTokens, options);
        var outputTokens = Normalize(actual.OutputTokens, options);

        var result = await _store.SettleAsync(
            context.CallId, userId, DateOnly.FromDateTime(actual.Timestamp.UtcDateTime),
            inputTokens, outputTokens, actual.Succeeded, actual.ErrorCode,
            options.MissingUsagePolicy, new CallInfo(context.Intent, context.Profile, actual.Model ?? context.Model),
            cancellationToken);

        if (result.UsageMissing && !result.AlreadySettled)
        {
            _logger.LogWarning(
                "AI call {CallId} for user {UserId} settled without complete token usage (input={InputTokens}, output={OutputTokens}, succeeded={Succeeded}, error={ErrorCode}); charged {Charged} tokens per policy {Policy}.",
                context.CallId, userId, actual.InputTokens?.ToString() ?? "n/a", actual.OutputTokens?.ToString() ?? "n/a",
                actual.Succeeded, actual.ErrorCode, result.ChargedTokens, options.MissingUsagePolicy);
        }
    }

    /// <summary>Applies <see cref="QuotaOptions.TreatZeroTokenCountsAsMissing"/>.</summary>
    private static int? Normalize(int? tokens, QuotaOptions options)
        => options.TreatZeroTokenCountsAsMissing && tokens == 0 ? null : tokens;

    /// <summary>The account a call is charged to, or null when the caller is anonymous.</summary>
    private static string? ResolveUserId(AICallContext context)
    {
        var userId = context.User?.UserId;
        if (context.IsSystemCall)
        {
            // Background SDK work, only seen here when RevealAI:Gate:IncludeSystemCalls is true.
            return string.IsNullOrEmpty(userId) ? "system" : userId;
        }

        return string.IsNullOrEmpty(userId) || userId == AnonymousUserId ? null : userId;
    }

    private long GetOutputReservation(string profile, QuotaOptions options)
        => _configuration.GetValue<int?>($"RevealAI:Profiles:{profile}:MaxOutputTokens")
           ?? options.DefaultOutputTokenReservation;
}
