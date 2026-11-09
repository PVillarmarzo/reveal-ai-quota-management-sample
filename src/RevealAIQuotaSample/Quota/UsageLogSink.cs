using Reveal.Sdk.AI;

namespace RevealAIQuotaSample.Quota;

/// <summary>
/// Observability only: logs one line per AI call and stores the full <see cref="AIUsageRecord"/>
/// in the <c>ai_usage_records</c> table, including denied calls and system calls (such as
/// datasource ranking) that the quota gate never sees. The per-request report uses these rows to
/// attribute auxiliary operations to the request that started them (<c>ParentOperationId</c>).
/// <para>
/// The SDK delivers usage records to sinks on a best-effort basis and may drop records under
/// load, so a sink must never be the source of truth for quotas or billing. Accounting happens
/// in <see cref="DailyTokenQuotaGate.SettleAsync"/>. A sink that writes to a remote database
/// should queue the record and return quickly.
/// </para>
/// </summary>
public sealed class UsageLogSink : IAIUsageSink
{
    private readonly QuotaStore _store;
    private readonly ILogger<UsageLogSink> _logger;

    public UsageLogSink(QuotaStore store, ILogger<UsageLogSink> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async ValueTask RecordAsync(AIUsageRecord record, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "AI usage: user={UserId} system={IsSystemCall} op={OperationId} parentOp={ParentOperationId} call={CallId} " +
            "intent={Intent} stage={Stage} attempt={Attempt} provider={Provider} model={Model} " +
            "input={InputTokens} output={OutputTokens} reasoning={ReasoningTokens} cachedInput={CachedInputTokens} " +
            "succeeded={Succeeded} error={ErrorCode} durationMs={DurationMs}",
            record.UserId, record.IsSystemCall, record.OperationId, record.ParentOperationId ?? "-", record.CallId,
            record.Intent, record.Stage ?? "-", record.Attempt?.ToString() ?? "-", record.Provider, record.Model,
            Count(record.InputTokens), Count(record.OutputTokens), Count(record.ReasoningTokens), Count(record.CachedInputTokens),
            record.Succeeded, record.ErrorCode, (long)record.Duration.TotalMilliseconds);

        await _store.RecordUsageAsync(record, cancellationToken);
    }

    private static string Count(int? tokens) => tokens?.ToString() ?? "n/a";
}
