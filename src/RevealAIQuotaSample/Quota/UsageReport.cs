using System.Globalization;

namespace RevealAIQuotaSample.Quota;

/// <summary>One row of <c>ai_usage_records</c>: an <see cref="Reveal.Sdk.AI.AIUsageRecord"/> as the usage sink stored it.</summary>
public sealed record UsageRow(
    string CallId,
    string OperationId,
    string? ParentOperationId,
    string UserId,
    string TimestampUtc,
    bool IsSystemCall,
    string? Intent,
    string? Provider,
    string? Model,
    string? Stage,
    int? Attempt,
    int? InputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    int? CachedInputTokens,
    long? DurationMs,
    bool Succeeded,
    string? ErrorCode);

/// <summary>One provider call (or denied attempt) inside a user request.</summary>
/// <param name="Status">
/// <c>settled</c> or <c>reserved</c> (in flight) for calls the quota gate checked; <c>denied</c> when the
/// gate refused the call; <c>not-gated</c> for calls the gate never saw (system calls such as datasource
/// ranking, unless <c>RevealAI:Gate:IncludeSystemCalls</c> is true).
/// </param>
/// <param name="ReservedTokens">The gate's estimate for the call. Never counted as usage.</param>
/// <param name="ChargedTokens">What the call cost against the user's quota (reported usage, or the missing-usage policy).</param>
public sealed record CallDetail(
    string CallId,
    string OperationId,
    string? ParentOperationId,
    string TimestampUtc,
    string? Intent,
    string? Stage,
    int? Attempt,
    string? Provider,
    string? Model,
    bool IsSystemCall,
    int? InputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    int? CachedInputTokens,
    long? ReservedTokens,
    long? ChargedTokens,
    string Status,
    bool ReachedProvider,
    bool UsageMissing,
    bool? Succeeded,
    string? ErrorCode);

/// <summary>
/// Everything one user request (one top-level <c>OperationId</c>) consumed.
/// <para>
/// Reported tokens are what the provider reported. Charged tokens are what the quota gate counted
/// (reported tokens, or the missing-usage policy when counts were missing). Reservations are
/// estimates and only appear as <see cref="PendingReservedTokens"/> while calls are in flight.
/// </para>
/// </summary>
public sealed record RequestSummary(
    string OperationId,
    string StartedUtc,
    int ProviderCalls,
    int DeniedCalls,
    int CallsWithMissingUsage,
    long ReportedInputTokens,
    long ReportedOutputTokens,
    long ReportedTotalTokens,
    long? ReasoningTokens,
    long? CachedInputTokens,
    long ChargedTokens,
    long PendingReservedTokens,
    IReadOnlyList<string> AuxiliaryOperationIds,
    IReadOnlyList<CallDetail> Calls);

/// <summary>A user's per-request usage for one UTC day.</summary>
/// <param name="Requests">Requests with at least one call that reached the provider.</param>
/// <param name="ProviderCalls">Calls that reached the provider (denied calls are left out).</param>
/// <param name="CallsWithoutOperationId">Ledger rows written before the SDK 1.6 upgrade; counted in the quota, left out of these figures.</param>
public sealed record UsageReport(
    string UserId,
    string DayUtc,
    int Requests,
    int ProviderCalls,
    long ReportedTotalTokens,
    long ChargedTokens,
    double? AverageReportedTokensPerRequest,
    double? AverageReportedTokensPerProviderCall,
    double? AverageChargedTokensPerRequest,
    int CallsWithMissingUsage,
    int CallsWithoutOperationId,
    IReadOnlyList<RequestSummary> RecentRequests)
{
    public const string Settled = "settled";
    public const string InFlight = "reserved";
    public const string Denied = "denied";
    public const string NotGated = "not-gated";

    /// <summary>
    /// Builds the report from the quota ledger and the stored usage records.
    /// <list type="bullet">
    /// <item>Calls are matched by <c>CallId</c>. The ledger wins for token counts and charges (it is
    /// written by <c>SettleAsync</c>, which the SDK always calls); usage records add the calls the
    /// gate never saw (denied calls, system calls) and the details of calls the ledger lacks.</item>
    /// <item>A call belongs to the request <c>ParentOperationId ?? OperationId</c>, so an auxiliary
    /// operation (datasource ranking during a dashboard generation) counts toward the request that
    /// started it.</item>
    /// <item>Reasoning and cached input tokens are summed separately and never added to the total.</item>
    /// </list>
    /// </summary>
    public static UsageReport Build(
        string userId, string dayUtc, IEnumerable<CallRecord> ledger, IEnumerable<UsageRow> records, int recentLimit)
    {
        var calls = new Dictionary<string, CallDetail>(StringComparer.Ordinal);
        var withoutOperation = 0;

        foreach (var row in ledger)
        {
            if (row.OperationId is null)
            {
                withoutOperation++;
                continue;
            }

            var inFlight = row.Status == InFlight;
            calls[row.CallId] = new CallDetail(
                row.CallId, row.OperationId, row.ParentOperationId, row.CreatedUtc,
                row.Intent, row.Stage, row.Attempt, row.Provider, row.Model, row.IsSystemCall,
                row.InputTokens, row.OutputTokens, row.ReasoningTokens, row.CachedInputTokens,
                ReservedTokens: row.ReservedTokens,
                ChargedTokens: inFlight ? null : row.ChargedTokens,
                Status: row.Status,
                ReachedProvider: true,
                UsageMissing: row.UsageMissing,
                Succeeded: row.Succeeded,
                ErrorCode: row.ErrorCode);
        }

        foreach (var record in records)
        {
            if (calls.TryGetValue(record.CallId, out var known))
            {
                // Same call in both places: keep the ledger's accounting, fill in what it lacks.
                calls[record.CallId] = known with
                {
                    TimestampUtc = record.TimestampUtc,
                    ParentOperationId = known.ParentOperationId ?? record.ParentOperationId,
                    Stage = known.Stage ?? record.Stage,
                    Attempt = known.Attempt ?? record.Attempt,
                    Provider = known.Provider ?? record.Provider,
                    Model = known.Model ?? record.Model,
                    ReasoningTokens = known.ReasoningTokens ?? record.ReasoningTokens,
                    CachedInputTokens = known.CachedInputTokens ?? record.CachedInputTokens,
                };
                continue;
            }

            var denied = record.ErrorCode == Denied;
            calls[record.CallId] = new CallDetail(
                record.CallId, record.OperationId, record.ParentOperationId, record.TimestampUtc,
                record.Intent, record.Stage, record.Attempt, record.Provider, record.Model, record.IsSystemCall,
                record.InputTokens, record.OutputTokens, record.ReasoningTokens, record.CachedInputTokens,
                ReservedTokens: null,
                ChargedTokens: null,
                Status: denied ? Denied : NotGated,
                ReachedProvider: !denied,
                UsageMissing: !denied && !QuotaStore.IsUsageComplete(record.InputTokens, record.OutputTokens),
                Succeeded: record.Succeeded,
                ErrorCode: record.ErrorCode);
        }

        var requests = calls.Values
            .GroupBy(c => c.ParentOperationId ?? c.OperationId, StringComparer.Ordinal)
            .Select(g => Summarize(g.Key, g))
            .OrderByDescending(r => ParseTime(r.StartedUtc))
            .ToList();

        var counted = requests.Where(r => r.ProviderCalls > 0).ToList();
        var requestCount = counted.Count;
        var providerCalls = counted.Sum(r => r.ProviderCalls);
        var reportedTotal = counted.Sum(r => r.ReportedTotalTokens);
        var charged = requests.Sum(r => r.ChargedTokens);

        return new UsageReport(
            userId, dayUtc,
            Requests: requestCount,
            ProviderCalls: providerCalls,
            ReportedTotalTokens: reportedTotal,
            ChargedTokens: charged,
            AverageReportedTokensPerRequest: requestCount == 0 ? null : (double)reportedTotal / requestCount,
            AverageReportedTokensPerProviderCall: providerCalls == 0 ? null : (double)reportedTotal / providerCalls,
            AverageChargedTokensPerRequest: requestCount == 0 ? null : (double)charged / requestCount,
            CallsWithMissingUsage: counted.Sum(r => r.CallsWithMissingUsage),
            CallsWithoutOperationId: withoutOperation,
            RecentRequests: requests.Take(Math.Max(0, recentLimit)).ToList());
    }

    private static RequestSummary Summarize(string operationId, IEnumerable<CallDetail> group)
    {
        var calls = group.OrderBy(c => ParseTime(c.TimestampUtc)).ToList();
        var reached = calls.Where(c => c.ReachedProvider).ToList();
        long input = reached.Sum(c => (long)(c.InputTokens ?? 0));
        long output = reached.Sum(c => (long)(c.OutputTokens ?? 0));

        return new RequestSummary(
            operationId,
            StartedUtc: calls[0].TimestampUtc,
            ProviderCalls: reached.Count,
            DeniedCalls: calls.Count(c => c.Status == Denied),
            CallsWithMissingUsage: reached.Count(c => c.UsageMissing),
            ReportedInputTokens: input,
            ReportedOutputTokens: output,
            ReportedTotalTokens: input + output,
            ReasoningTokens: SumOrNull(reached.Select(c => c.ReasoningTokens)),
            CachedInputTokens: SumOrNull(reached.Select(c => c.CachedInputTokens)),
            ChargedTokens: calls.Sum(c => c.ChargedTokens ?? 0),
            PendingReservedTokens: calls.Where(c => c.Status == InFlight).Sum(c => c.ReservedTokens ?? 0),
            AuxiliaryOperationIds: calls.Select(c => c.OperationId)
                .Where(id => id != operationId).Distinct(StringComparer.Ordinal).ToList(),
            Calls: calls);
    }

    private static long? SumOrNull(IEnumerable<int?> values)
    {
        long? sum = null;
        foreach (var value in values)
        {
            if (value is not null)
            {
                sum = (sum ?? 0) + value.Value;
            }
        }
        return sum;
    }

    private static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : DateTimeOffset.MinValue;
}
