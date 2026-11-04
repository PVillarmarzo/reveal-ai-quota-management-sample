using Reveal.Sdk.AI;

namespace RevealAIQuotaSample.Quota;

/// <summary>
/// Observability only: writes one log line per AI call (including denied and system calls).
/// The SDK delivers usage records to sinks on a best-effort basis and may drop records under
/// load, so a sink must never be the source of truth for quotas or billing. Accounting happens
/// in <see cref="DailyTokenQuotaGate.SettleAsync"/>.
/// </summary>
public sealed class UsageLogSink : IAIUsageSink
{
    private readonly ILogger<UsageLogSink> _logger;

    public UsageLogSink(ILogger<UsageLogSink> logger) => _logger = logger;

    public ValueTask RecordAsync(AIUsageRecord record, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "AI usage: user={UserId} system={IsSystemCall} intent={Intent} profile={Profile} model={Model} " +
            "input={InputTokens} output={OutputTokens} succeeded={Succeeded} error={ErrorCode} durationMs={DurationMs}",
            record.UserId, record.IsSystemCall, record.Intent, record.Profile, record.Model,
            record.InputTokens?.ToString() ?? "n/a", record.OutputTokens?.ToString() ?? "n/a",
            record.Succeeded, record.ErrorCode, (long)record.Duration.TotalMilliseconds);
        return ValueTask.CompletedTask;
    }
}
