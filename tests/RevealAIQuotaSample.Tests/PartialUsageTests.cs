using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reveal.Sdk;
using Reveal.Sdk.AI;
using RevealAIQuotaSample.Quota;
using Xunit;

namespace RevealAIQuotaSample.Tests;

/// <summary>Accounting when the provider reports both, one or neither token count.</summary>
public sealed class PartialUsageTests : IDisposable
{
    private const long Reservation = 1_500; // 1,000 input estimate + 500 MaxOutputTokens

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"quota-partial-{Guid.NewGuid():n}.db");
    private readonly TimeProvider _time = TimeProvider.System;

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    // input, output, policy, expected charge, expected "usage missing" flag
    public static TheoryData<int?, int?, MissingUsagePolicy, long, bool> Cases => new()
    {
        // Complete usage: always exactly input + output, whatever the policy.
        { 300, 120, MissingUsagePolicy.ChargeReservation, 420, false },
        { 300, 120, MissingUsagePolicy.ChargeNothing, 420, false },
        { 5_000, 900, MissingUsagePolicy.ChargeReservation, 5_900, false },

        // Only input reported.
        { 300, null, MissingUsagePolicy.ChargeReservation, Reservation, true },
        { 300, null, MissingUsagePolicy.ChargeNothing, 300, true },
        { 30_000, null, MissingUsagePolicy.ChargeReservation, 30_000, true }, // never discounted below what was reported

        // Only output reported.
        { null, 120, MissingUsagePolicy.ChargeReservation, Reservation, true },
        { null, 120, MissingUsagePolicy.ChargeNothing, 120, true },
        { null, 9_000, MissingUsagePolicy.ChargeReservation, 9_000, true },

        // Nothing reported.
        { null, null, MissingUsagePolicy.ChargeReservation, Reservation, true },
        { null, null, MissingUsagePolicy.ChargeNothing, 0, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ComputeCharge_follows_the_documented_rule(int? input, int? output, MissingUsagePolicy policy, long expected, bool _)
        => Assert.Equal(expected, QuotaStore.ComputeCharge(Reservation, input, output, policy));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Settled_calls_are_charged_and_flagged_per_rule(
        int? input, int? output, MissingUsagePolicy policy, long expected, bool missing)
    {
        var (gate, store) = Create(policy);
        var context = Context("alice");

        await gate.CheckAsync(context, default);
        await gate.SettleAsync(context, Usage(context, input, output), default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(expected, usage.ChargedTokens);
        Assert.Equal(input ?? 0, usage.InputTokens);
        Assert.Equal(output ?? 0, usage.OutputTokens);
        Assert.Equal(missing ? 1 : 0, usage.CallsWithMissingUsage);
        Assert.Equal(0, usage.PendingReservedTokens);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Repeated_settlement_with_partial_usage_charges_once(
        int? input, int? output, MissingUsagePolicy policy, long expected, bool _)
    {
        var (gate, store) = Create(policy);
        var context = Context("alice");
        await gate.CheckAsync(context, default);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => await gate.SettleAsync(context, Usage(context, input, output), default))));
        // A later settlement with different numbers is ignored too.
        await gate.SettleAsync(context, Usage(context, 99_999, 99_999), default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(expected, usage.ChargedTokens);
        Assert.Equal(1, usage.Calls);
    }

    [Fact]
    public void ComputeCharge_treats_explicit_zeros_as_reported()
        => Assert.Equal(0, QuotaStore.ComputeCharge(Reservation, 0, 0, MissingUsagePolicy.ChargeReservation));

    // The built-in OpenAI provider reports an omitted count as 0, so by default the gate treats
    // a 0 as "not reported" before the store applies the rule above.
    [Theory]
    [InlineData(1_200, 0, true, Reservation, true)]
    [InlineData(0, 80, true, Reservation, true)]
    [InlineData(0, 0, true, Reservation, true)]
    [InlineData(1_200, 0, false, 1_200, false)]
    [InlineData(0, 0, false, 0, false)]
    [InlineData(1_200, 80, true, 1_280, false)]
    public async Task Zero_counts_follow_the_TreatZeroTokenCountsAsMissing_option(
        int input, int output, bool treatZeroAsMissing, long expected, bool missing)
    {
        var (gate, store) = Create(MissingUsagePolicy.ChargeReservation, treatZeroAsMissing);
        var context = Context("alice");

        await gate.CheckAsync(context, default);
        await gate.SettleAsync(context, Usage(context, input, output), default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(expected, usage.ChargedTokens);
        Assert.Equal(missing ? 1 : 0, usage.CallsWithMissingUsage);
    }

    [Theory]
    [InlineData(300, null, 300)]
    [InlineData(null, 120, 120)]
    [InlineData(null, null, 0)]
    public async Task Settlement_without_a_reservation_charges_only_reported_tokens(int? input, int? output, long expected)
    {
        var (gate, store) = Create(MissingUsagePolicy.ChargeReservation);
        var context = Context("alice"); // never checked: no reservation exists

        await gate.SettleAsync(context, Usage(context, input, output), default);

        Assert.Equal(expected, (await store.GetUsageAsync("alice", Today, default)).ChargedTokens);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private (DailyTokenQuotaGate, QuotaStore) Create(MissingUsagePolicy policy, bool treatZeroAsMissing = true)
    {
        var options = new QuotaOptions
        {
            DailyTokenLimit = 1_000_000,
            EstimatedInputTokensPerCall = 1_000,
            MissingUsagePolicy = policy,
            TreatZeroTokenCountsAsMissing = treatZeroAsMissing,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RevealAI:Profiles:default:MaxOutputTokens"] = "500" })
            .Build();
        var store = new QuotaStore(_dbPath, policy, _time);
        var gate = new DailyTokenQuotaGate(store, new StaticOptions(options), configuration, _time, NullLogger<DailyTokenQuotaGate>.Instance);
        return (gate, store);
    }

    private static AICallContext Context(string userId) => new()
    {
        OperationId = Guid.NewGuid().ToString("n"),
        CallId = Guid.NewGuid().ToString("n"),
        User = new RVUserContext(userId),
        Intent = "chat",
        Profile = "default",
        Provider = "openai",
        Model = "test-model",
    };

    private static AIUsageRecord Usage(AICallContext context, int? input, int? output) => new()
    {
        OperationId = context.OperationId,
        CallId = context.CallId,
        Timestamp = DateTimeOffset.UtcNow,
        UserId = context.User.UserId,
        Intent = context.Intent,
        Profile = context.Profile,
        Provider = context.Provider,
        Model = context.Model,
        InputTokens = input,
        OutputTokens = output,
        Duration = TimeSpan.FromMilliseconds(5),
        Succeeded = true,
    };

    private sealed class StaticOptions(QuotaOptions value) : IOptionsMonitor<QuotaOptions>
    {
        public QuotaOptions CurrentValue => value;
        public QuotaOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<QuotaOptions, string?> listener) => null;
    }
}
