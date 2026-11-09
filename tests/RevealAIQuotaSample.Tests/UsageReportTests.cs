using RevealAIQuotaSample.Quota;
using Xunit;

namespace RevealAIQuotaSample.Tests;

/// <summary>Per-request aggregation rules (<see cref="UsageReport.Build"/>), without a database.</summary>
public sealed class UsageReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Calls_with_the_same_OperationId_form_one_request()
    {
        var ledger = new[]
        {
            Settled("c1", "op1", 0, "intent-classifier", input: 500, output: 10),
            Settled("c2", "op1", 1, "generation-build-report", input: 8_000, output: 900, stage: "Plan", attempt: 1),
            Settled("c3", "op1", 2, "generation-build-report", input: 6_000, output: 700, stage: "DefineWidgets", attempt: 2),
            Settled("c4", "op2", 10, "intent-classifier", input: 400, output: 10),
            Settled("c5", "op2", 11, "chat", input: 1_000, output: 190),
        };

        var report = UsageReport.Build("alice", "2026-11-04", ledger, [], recentLimit: 10);

        Assert.Equal(2, report.Requests);
        Assert.Equal(5, report.ProviderCalls);
        Assert.Equal(17_710, report.ReportedTotalTokens);
        Assert.Equal(17_710 / 2.0, report.AverageReportedTokensPerRequest);
        Assert.Equal(17_710 / 5.0, report.AverageReportedTokensPerProviderCall);

        var newest = report.RecentRequests[0];
        Assert.Equal("op2", newest.OperationId);
        var first = report.RecentRequests[1];
        Assert.Equal(3, first.ProviderCalls);
        Assert.Equal(16_110, first.ReportedTotalTokens);
        Assert.Equal(["c1", "c2", "c3"], first.Calls.Select(c => c.CallId));
        Assert.Equal("DefineWidgets", first.Calls[2].Stage);
        Assert.Equal(2, first.Calls[2].Attempt);
    }

    [Fact]
    public void Reasoning_and_cached_tokens_are_reported_but_never_added_to_the_total()
    {
        var ledger = new[]
        {
            Settled("c1", "op1", 0, "chat", input: 1_000, output: 300, reasoning: 200, cached: 768),
            Settled("c2", "op1", 1, "chat", input: 500, output: 50),
        };

        var request = UsageReport.Build("alice", "d", ledger, [], 10).RecentRequests.Single();

        Assert.Equal(1_850, request.ReportedTotalTokens);   // 1,000 + 300 + 500 + 50
        Assert.Equal(1_850, request.ChargedTokens);
        Assert.Equal(200, request.ReasoningTokens);
        Assert.Equal(768, request.CachedInputTokens);
    }

    [Fact]
    public void Reasoning_and_cached_tokens_stay_null_when_no_call_reported_them()
    {
        var request = UsageReport.Build("alice", "d", [Settled("c1", "op1", 0, "chat", 100, 10)], [], 10).RecentRequests.Single();

        Assert.Null(request.ReasoningTokens);
        Assert.Null(request.CachedInputTokens);
    }

    [Fact]
    public void An_auxiliary_operation_counts_toward_the_request_named_by_ParentOperationId()
    {
        var ledger = new[] { Settled("c1", "chat-op", 0, "intent-classifier", 500, 10) };
        var records = new[]
        {
            Record("r1", "ranking-op", 1, "datasource-ranker", 2_000, 40, parent: "chat-op", system: true),
        };

        var report = UsageReport.Build("alice", "d", ledger, records, 10);

        var request = Assert.Single(report.RecentRequests);
        Assert.Equal("chat-op", request.OperationId);
        Assert.Equal(2, request.ProviderCalls);
        Assert.Equal(2_550, request.ReportedTotalTokens);
        Assert.Equal(510, request.ChargedTokens);                 // the system call was not gated
        Assert.Equal(["ranking-op"], request.AuxiliaryOperationIds);
        Assert.Equal(UsageReport.NotGated, request.Calls[1].Status);
        Assert.Equal(1, report.Requests);
    }

    [Fact]
    public void Denied_calls_are_not_provider_calls()
    {
        var ledger = new[] { Settled("c1", "op1", 0, "intent-classifier", 500, 10) };
        var records = new[]
        {
            Record("d1", "op1", 1, "generation-build-report", null, null, error: "denied"),
            Record("d2", "op2", 5, "intent-classifier", null, null, error: "denied"), // a request denied up front
        };

        var report = UsageReport.Build("alice", "d", ledger, records, 10);

        Assert.Equal(1, report.Requests);          // op2 never reached the provider
        Assert.Equal(1, report.ProviderCalls);
        Assert.Equal(510, report.AverageReportedTokensPerRequest);
        var op1 = report.RecentRequests.Single(r => r.OperationId == "op1");
        Assert.Equal(1, op1.DeniedCalls);
        Assert.Equal(1, op1.ProviderCalls);
        Assert.Equal(2, report.RecentRequests.Count); // still listed, with 0 provider calls
    }

    [Fact]
    public void Reservations_are_not_usage()
    {
        var ledger = new[]
        {
            Settled("c1", "op1", 0, "intent-classifier", 500, 10),
            InFlight("c2", "op1", 1, reserved: 20_096),
        };

        var request = UsageReport.Build("alice", "d", ledger, [], 10).RecentRequests.Single();

        Assert.Equal(510, request.ReportedTotalTokens);
        Assert.Equal(510, request.ChargedTokens);
        Assert.Equal(20_096, request.PendingReservedTokens);
        Assert.Null(request.Calls[1].ChargedTokens);
    }

    [Fact]
    public void Missing_usage_is_charged_by_policy_but_not_reported()
    {
        var ledger = new[] { Settled("c1", "op1", 0, "chat", null, null, charged: 20_096, usageMissing: true, succeeded: false, error: "timeout") };

        var report = UsageReport.Build("alice", "d", ledger, [], 10);
        var request = report.RecentRequests.Single();

        Assert.Equal(0, request.ReportedTotalTokens);
        Assert.Equal(20_096, request.ChargedTokens);
        Assert.Equal(1, request.CallsWithMissingUsage);
        Assert.Equal(1, report.CallsWithMissingUsage);
        Assert.Equal(20_096, report.AverageChargedTokensPerRequest);
    }

    [Fact]
    public void The_ledger_wins_for_accounting_and_the_usage_record_fills_in_details()
    {
        // The gate treated the provider's 0 output as missing; the sink kept the raw record.
        var ledger = new[] { Settled("c1", "op1", 0, "chat", 1_200, null, charged: 20_096, usageMissing: true) };
        var records = new[] { Record("c1", "op1", 0, "chat", 1_200, 0, stage: "SingleShot", attempt: 1) };

        var call = UsageReport.Build("alice", "d", ledger, records, 10).RecentRequests.Single().Calls.Single();

        Assert.Null(call.OutputTokens);
        Assert.Equal(20_096, call.ChargedTokens);
        Assert.Equal("SingleShot", call.Stage);
        Assert.Equal(1, call.Attempt);
        Assert.Equal(UsageReport.Settled, call.Status);
    }

    [Fact]
    public void Rows_from_before_the_upgrade_are_counted_but_not_grouped()
    {
        var ledger = new[]
        {
            Settled("old", null, 0, "chat", 300, 30),
            Settled("c1", "op1", 1, "chat", 100, 10),
        };

        var report = UsageReport.Build("alice", "d", ledger, [], 10);

        Assert.Equal(1, report.CallsWithoutOperationId);
        Assert.Equal(1, report.Requests);
        Assert.Equal(110, report.ReportedTotalTokens);
    }

    [Fact]
    public void The_recent_list_is_limited_but_the_averages_cover_the_whole_day()
    {
        var ledger = Enumerable.Range(0, 30).Select(i => Settled($"c{i}", $"op{i}", i, "chat", 100, 10)).ToArray();

        var report = UsageReport.Build("alice", "d", ledger, [], recentLimit: 5);

        Assert.Equal(5, report.RecentRequests.Count);
        Assert.Equal("op29", report.RecentRequests[0].OperationId);
        Assert.Equal(30, report.Requests);
        Assert.Equal(110, report.AverageReportedTokensPerRequest);
    }

    [Fact]
    public void An_empty_day_has_no_averages()
    {
        var report = UsageReport.Build("alice", "d", [], [], 10);

        Assert.Equal(0, report.Requests);
        Assert.Null(report.AverageReportedTokensPerRequest);
        Assert.Null(report.AverageReportedTokensPerProviderCall);
        Assert.Empty(report.RecentRequests);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static string At(int seconds) => T0.AddSeconds(seconds).ToString("O");

    private static CallRecord Settled(
        string callId, string? operationId, int second, string intent, int? input, int? output,
        string? stage = null, int? attempt = null, int? reasoning = null, int? cached = null,
        long? charged = null, bool usageMissing = false, bool succeeded = true, string? error = null)
        => new(callId, "2026-11-04", "settled", 20_096, input, output,
            charged ?? (long)(input ?? 0) + (output ?? 0), usageMissing, succeeded, error,
            intent, "default", "test-model", At(second), At(second + 1),
            OperationId: operationId, Provider: "openai", Stage: stage, Attempt: attempt,
            ReasoningTokens: reasoning, CachedInputTokens: cached, UserId: "alice");

    private static CallRecord InFlight(string callId, string operationId, int second, long reserved)
        => new(callId, "2026-11-04", "reserved", reserved, null, null, 0, false, null, null,
            "generation-build-report", "default", "test-model", At(second), null,
            OperationId: operationId, Provider: "openai", UserId: "alice");

    private static UsageRow Record(
        string callId, string operationId, int second, string intent, int? input, int? output,
        string? parent = null, bool system = false, string? error = null, string? stage = null, int? attempt = null)
        => new(callId, operationId, parent, system ? "system" : "alice", At(second), system,
            intent, "openai", "test-model", stage, attempt, input, output, null, null, 10,
            Succeeded: error is null, ErrorCode: error);
}
