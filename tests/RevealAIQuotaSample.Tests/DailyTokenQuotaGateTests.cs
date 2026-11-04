using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reveal.Sdk;
using Reveal.Sdk.AI;
using RevealAIQuotaSample.Quota;
using Xunit;

namespace RevealAIQuotaSample.Tests;

public sealed class DailyTokenQuotaGateTests : IDisposable
{
    // Each call reserves 1,000 input + 500 output = 1,500 tokens.
    private const int InputEstimate = 1_000;
    private const int MaxOutput = 500;
    private const long Reservation = InputEstimate + MaxOutput;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"quota-test-{Guid.NewGuid():n}.db");
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Allows_calls_until_reservations_reach_the_limit()
    {
        var gate = CreateGate(dailyLimit: 3_000);

        Assert.True((await gate.CheckAsync(Context("alice"), default)).Allowed);
        Assert.True((await gate.CheckAsync(Context("alice"), default)).Allowed);

        var denied = await gate.CheckAsync(Context("alice"), default);
        Assert.False(denied.Allowed);
        Assert.Equal(AIDenialReason.QuotaExceeded, denied.Reason);
        Assert.Equal(TimeSpan.FromHours(14), denied.RetryAfter); // until 00:00 UTC
    }

    [Fact]
    public async Task Settle_replaces_the_reservation_with_actual_tokens()
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 10_000);
        var context = Context("alice");

        await gate.CheckAsync(context, default);
        Assert.Equal(Reservation, (await store.GetUsageAsync("alice", Today, default)).PendingReservedTokens);

        await gate.SettleAsync(context, Usage(context, input: 300, output: 120), default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(0, usage.PendingReservedTokens);
        Assert.Equal(300, usage.InputTokens);
        Assert.Equal(120, usage.OutputTokens);
        Assert.Equal(420, usage.ChargedTokens);
    }

    [Fact]
    public async Task Settle_is_idempotent_per_call_id()
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 10_000);
        var context = Context("alice");

        await gate.CheckAsync(context, default);
        await gate.SettleAsync(context, Usage(context, input: 300, output: 120), default);
        await gate.SettleAsync(context, Usage(context, input: 300, output: 120), default);
        await gate.SettleAsync(context, Usage(context, input: 9_999, output: 9_999), default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(420, usage.ChargedTokens);
        Assert.Equal(1, usage.Calls);
    }

    [Fact]
    public async Task Repeated_check_for_the_same_call_id_reserves_once()
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 10_000);
        var context = Context("alice");

        await gate.CheckAsync(context, default);
        await gate.CheckAsync(context, default);

        Assert.Equal(Reservation, (await store.GetUsageAsync("alice", Today, default)).PendingReservedTokens);
    }

    [Theory]
    [InlineData(MissingUsagePolicy.ChargeReservation, Reservation)]
    [InlineData(MissingUsagePolicy.ChargeNothing, 0)]
    public async Task Missing_usage_is_charged_per_policy(MissingUsagePolicy policy, long expectedCharge)
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 10_000, policy: policy);
        var context = Context("alice");

        await gate.CheckAsync(context, default);
        await gate.SettleAsync(context, Usage(context, input: null, output: null, succeeded: false, errorCode: "timeout"), default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(expectedCharge, usage.ChargedTokens);
        Assert.Equal(1, usage.CallsWithMissingUsage);
        Assert.Equal(0, usage.PendingReservedTokens);
    }

    [Fact]
    public async Task Concurrent_checks_never_reserve_more_than_the_limit()
    {
        // Room for exactly 10 reservations of 1,500 tokens.
        var (gate, store) = CreateGateAndStore(dailyLimit: 15_000);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(async () => await gate.CheckAsync(Context("alice"), default))));

        Assert.Equal(10, decisions.Count(d => d.Allowed));
        Assert.Equal(40, decisions.Count(d => !d.Allowed));
        Assert.Equal(15_000, (await store.GetUsageAsync("alice", Today, default)).PendingReservedTokens);
    }

    [Fact]
    public async Task Concurrent_settles_of_the_same_call_charge_once()
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 10_000);
        var context = Context("alice");
        await gate.CheckAsync(context, default);

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await gate.SettleAsync(context, Usage(context, 100, 50), default))));

        Assert.Equal(150, (await store.GetUsageAsync("alice", Today, default)).ChargedTokens);
    }

    [Fact]
    public async Task Users_have_separate_budgets()
    {
        var gate = CreateGate(dailyLimit: 1_500);

        Assert.True((await gate.CheckAsync(Context("alice"), default)).Allowed);
        Assert.False((await gate.CheckAsync(Context("alice"), default)).Allowed);
        Assert.True((await gate.CheckAsync(Context("bob"), default)).Allowed);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("")]
    public async Task Anonymous_users_are_denied(string userId)
    {
        var decision = await CreateGate(dailyLimit: 10_000).CheckAsync(Context(userId), default);

        Assert.False(decision.Allowed);
        Assert.Equal(AIDenialReason.NotEntitled, decision.Reason);
    }

    [Fact]
    public async Task Budget_resets_on_the_next_utc_day()
    {
        var gate = CreateGate(dailyLimit: 1_500);
        Assert.True((await gate.CheckAsync(Context("alice"), default)).Allowed);
        Assert.False((await gate.CheckAsync(Context("alice"), default)).Allowed);

        _time.Now = _time.Now.AddDays(1);

        Assert.True((await gate.CheckAsync(Context("alice"), default)).Allowed);
    }

    [Fact]
    public async Task Usage_persists_across_restarts_and_open_reservations_are_settled()
    {
        var (gate, _) = CreateGateAndStore(dailyLimit: 10_000);
        var settled = Context("alice");
        await gate.CheckAsync(settled, default);
        await gate.SettleAsync(settled, Usage(settled, 200, 100), default);
        await gate.CheckAsync(Context("alice"), default); // still reserved when the "process" stops

        var restarted = new QuotaStore(_dbPath, MissingUsagePolicy.ChargeReservation, _time);

        var usage = await restarted.GetUsageAsync("alice", Today, default);
        Assert.Equal(300 + Reservation, usage.ChargedTokens);
        Assert.Equal(0, usage.PendingReservedTokens);
        Assert.Equal(2, usage.Calls);
    }

    [Fact]
    public async Task Reset_clears_settled_usage_but_keeps_in_flight_reservations()
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 10_000);
        var done = Context("alice");
        await gate.CheckAsync(done, default);
        await gate.SettleAsync(done, Usage(done, 200, 100), default);
        var inFlight = Context("alice");
        await gate.CheckAsync(inFlight, default);

        await store.ResetAsync("alice", Today, default);

        var usage = await store.GetUsageAsync("alice", Today, default);
        Assert.Equal(0, usage.ChargedTokens);
        Assert.Equal(Reservation, usage.PendingReservedTokens);
    }

    [Fact]
    public async Task Output_reservation_follows_the_profile_max_output_tokens()
    {
        var (gate, store) = CreateGateAndStore(dailyLimit: 100_000, profileMaxOutput: 2_000);

        await gate.CheckAsync(Context("alice"), default);

        Assert.Equal(InputEstimate + 2_000, (await store.GetUsageAsync("alice", Today, default)).PendingReservedTokens);
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.Now.UtcDateTime);

    private DailyTokenQuotaGate CreateGate(long dailyLimit) => CreateGateAndStore(dailyLimit).Gate;

    private (DailyTokenQuotaGate Gate, QuotaStore Store) CreateGateAndStore(
        long dailyLimit, MissingUsagePolicy policy = MissingUsagePolicy.ChargeReservation, int profileMaxOutput = MaxOutput)
    {
        var options = new QuotaOptions
        {
            DailyTokenLimit = dailyLimit,
            EstimatedInputTokensPerCall = InputEstimate,
            MissingUsagePolicy = policy,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RevealAI:Profiles:default:MaxOutputTokens"] = profileMaxOutput.ToString(),
            })
            .Build();
        var store = new QuotaStore(_dbPath, policy, _time);
        var gate = new DailyTokenQuotaGate(store, new StaticOptions(options), configuration, _time,
            NullLogger<DailyTokenQuotaGate>.Instance);
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

    private AIUsageRecord Usage(AICallContext context, int? input, int? output, bool succeeded = true, string? errorCode = null) => new()
    {
        OperationId = context.OperationId,
        CallId = context.CallId,
        Timestamp = _time.Now,
        UserId = context.User.UserId,
        Intent = context.Intent,
        Profile = context.Profile,
        Provider = context.Provider,
        Model = context.Model,
        InputTokens = input,
        OutputTokens = output,
        Duration = TimeSpan.FromMilliseconds(10),
        Succeeded = succeeded,
        ErrorCode = errorCode,
    };

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StaticOptions(QuotaOptions value) : IOptionsMonitor<QuotaOptions>
    {
        public QuotaOptions CurrentValue => value;
        public QuotaOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<QuotaOptions, string?> listener) => null;
    }
}
