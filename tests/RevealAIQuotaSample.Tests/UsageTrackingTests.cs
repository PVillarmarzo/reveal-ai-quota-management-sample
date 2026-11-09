using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reveal.Sdk;
using Reveal.Sdk.AI;
using RevealAIQuotaSample.Quota;
using Xunit;

namespace RevealAIQuotaSample.Tests;

/// <summary>SDK 1.6 usage fields through the gate, the sink and the SQLite store, and the schema upgrade.</summary>
public sealed class UsageTrackingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"quota-usage-{Guid.NewGuid():n}.db");
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Day = new(2026, 11, 4);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Settle_stores_the_new_fields_without_changing_the_charge()
    {
        var (gate, store) = Create();
        var context = Context("alice", "op1");

        await gate.CheckAsync(context, default);
        await gate.SettleAsync(context, Usage(context, 1_000, 300) with
        {
            Stage = "DefineWidgets",
            Attempt = 2,
            ReasoningTokens = 200,
            CachedInputTokens = 768,
        }, default);

        var call = Assert.Single(await store.GetRecentCallsAsync("alice", 10, default));
        Assert.Equal("op1", call.OperationId);
        Assert.Equal("openai", call.Provider);
        Assert.Equal("DefineWidgets", call.Stage);
        Assert.Equal(2, call.Attempt);
        Assert.Equal(200, call.ReasoningTokens);
        Assert.Equal(768, call.CachedInputTokens);
        Assert.Null(call.ParentOperationId);
        Assert.Equal(1_300, call.ChargedTokens);   // input + output only
        Assert.Equal(1_300, (await store.GetUsageAsync("alice", Day, default)).ChargedTokens);
    }

    [Fact]
    public async Task Sink_stores_each_record_once_with_nullable_fields_kept_null()
    {
        var store = Create().Store;
        var sink = new UsageLogSink(store, NullLogger<UsageLogSink>.Instance);
        var record = Usage(Context("alice", "op1"), null, null, succeeded: false, errorCode: "denied");

        await sink.RecordAsync(record, default);
        await sink.RecordAsync(record, default);

        var (_, rows) = await store.GetReportRowsAsync("alice", Day, default);
        var row = Assert.Single(rows);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.ReasoningTokens);
        Assert.Null(row.Stage);
        Assert.Null(row.Attempt);
        Assert.Equal("denied", row.ErrorCode);
    }

    [Fact]
    public async Task One_chat_request_is_reported_as_one_operation_with_its_calls()
    {
        var (gate, store) = Create();
        var sink = new UsageLogSink(store, NullLogger<UsageLogSink>.Instance);

        // What the SDK does for one chat turn: every provider call shares the router's OperationId.
        var classifier = Context("alice", "chat-op", intent: "intent-classifier");
        var generation = Context("alice", "chat-op", intent: "generation-build-report");
        foreach (var (context, usage) in new[]
        {
            (classifier, Usage(classifier, 500, 10)),
            (generation, Usage(generation, 15_000, 900) with { Stage = "SingleShot", Attempt = 1 }),
        })
        {
            await gate.CheckAsync(context, default);
            await sink.RecordAsync(usage, default);   // the SDK hands the record to sinks first...
            await gate.SettleAsync(context, usage, default); // ...then settles the gate
        }

        // Datasource ranking runs through a separate (system) router during the generation: it has its own
        // OperationId, the chat's OperationId as ParentOperationId, and it bypasses the gate by default.
        await sink.RecordAsync(new AIUsageRecord
        {
            OperationId = "ranking-op",
            ParentOperationId = "chat-op",
            CallId = Guid.NewGuid().ToString("n"),
            Timestamp = _time.GetUtcNow(),
            UserId = "system",
            IsSystemCall = true,
            Intent = "datasource-ranker",
            Profile = "default",
            Provider = "openai",
            Model = "test-model",
            InputTokens = 2_000,
            OutputTokens = 40,
            Duration = TimeSpan.FromMilliseconds(5),
            Succeeded = true,
        }, default);

        var (ledger, records) = await store.GetReportRowsAsync("alice", Day, default);
        var report = UsageReport.Build("alice", "2026-11-04", ledger, records, 10);

        var request = Assert.Single(report.RecentRequests);
        Assert.Equal("chat-op", request.OperationId);
        Assert.Equal(3, request.ProviderCalls);
        Assert.Equal(18_450, request.ReportedTotalTokens);
        Assert.Equal(16_410, request.ChargedTokens);       // the ungated system call is not charged to Alice
        Assert.Equal(["ranking-op"], request.AuxiliaryOperationIds);
        Assert.Equal(18_450, report.AverageReportedTokensPerRequest);
        Assert.Equal(6_150, report.AverageReportedTokensPerProviderCall);
        Assert.Contains(request.Calls, c => c.Stage == "SingleShot" && c.Attempt == 1);
    }

    [Fact]
    public async Task Reset_also_removes_the_users_usage_records_and_their_auxiliary_records()
    {
        var (gate, store) = Create();
        var sink = new UsageLogSink(store, NullLogger<UsageLogSink>.Instance);
        var context = Context("alice", "chat-op");
        var usage = Usage(context, 100, 10);
        await gate.CheckAsync(context, default);
        await sink.RecordAsync(usage, default);
        await gate.SettleAsync(context, usage, default);
        await sink.RecordAsync(Usage(Context("system", "ranking-op"), 50, 5) with { ParentOperationId = "chat-op", IsSystemCall = true }, default);
        await sink.RecordAsync(Usage(Context("bob", "bob-op"), 70, 7), default);

        await store.ResetAsync("alice", Day, default);

        var (aliceLedger, aliceRecords) = await store.GetReportRowsAsync("alice", Day, default);
        Assert.Empty(aliceLedger);
        Assert.Empty(aliceRecords);
        Assert.Single((await store.GetReportRowsAsync("bob", Day, default)).Records);
    }

    [Fact]
    public async Task A_database_from_the_previous_version_is_upgraded_in_place()
    {
        CreateVersion15Database(_dbPath);

        var store = new QuotaStore(_dbPath, MissingUsagePolicy.ChargeReservation, _time);
        _ = new QuotaStore(_dbPath, MissingUsagePolicy.ChargeReservation, _time); // a second start changes nothing

        var usage = await store.GetUsageAsync("alice", Day, default);
        Assert.Equal(1, usage.Calls);
        Assert.Equal(15_695, usage.ChargedTokens);

        var old = Assert.Single(await store.GetRecentCallsAsync("alice", 10, default));
        Assert.Equal("legacy-call", old.CallId);
        Assert.Null(old.OperationId);
        Assert.Null(old.Stage);

        // New calls work next to the old row, and the report keeps the old row apart.
        var gate = new DailyTokenQuotaGate(store, new StaticOptions(new QuotaOptions { DailyTokenLimit = 100_000, EstimatedInputTokensPerCall = 1_000 }),
            Configuration(), _time, NullLogger<DailyTokenQuotaGate>.Instance);
        var context = Context("alice", "op1");
        await gate.CheckAsync(context, default);
        await gate.SettleAsync(context, Usage(context, 100, 10), default);

        var (ledger, records) = await store.GetReportRowsAsync("alice", Day, default);
        var report = UsageReport.Build("alice", "2026-11-04", ledger, records, 10);
        Assert.Equal(1, report.CallsWithoutOperationId);
        Assert.Equal(1, report.Requests);
        Assert.Equal(15_805, (await store.GetUsageAsync("alice", Day, default)).ChargedTokens);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static void CreateVersion15Database(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ai_calls (
                call_id TEXT PRIMARY KEY, user_id TEXT NOT NULL, day_utc TEXT NOT NULL, status TEXT NOT NULL,
                reserved_tokens INTEGER NOT NULL, input_tokens INTEGER NULL, output_tokens INTEGER NULL,
                charged_tokens INTEGER NOT NULL DEFAULT 0, usage_missing INTEGER NOT NULL DEFAULT 0,
                succeeded INTEGER NULL, error_code TEXT NULL, intent TEXT NULL, profile TEXT NULL, model TEXT NULL,
                created_utc TEXT NOT NULL, settled_utc TEXT NULL);
            CREATE INDEX ix_ai_calls_user_day ON ai_calls (user_id, day_utc);
            INSERT INTO ai_calls VALUES ('legacy-call', 'alice', '2026-11-04', 'settled', 20096, 15274, 421, 15695, 0, 1,
                NULL, 'chat', 'default', 'gpt-4.1-mini', '2026-11-04T09:00:00.0000000+00:00', '2026-11-04T09:00:05.0000000+00:00');
            """;
        command.ExecuteNonQuery();
    }

    private (DailyTokenQuotaGate Gate, QuotaStore Store) Create()
    {
        var store = new QuotaStore(_dbPath, MissingUsagePolicy.ChargeReservation, _time);
        var options = new QuotaOptions { DailyTokenLimit = 1_000_000, EstimatedInputTokensPerCall = 1_000 };
        var gate = new DailyTokenQuotaGate(store, new StaticOptions(options), Configuration(), _time, NullLogger<DailyTokenQuotaGate>.Instance);
        return (gate, store);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["RevealAI:Profiles:default:MaxOutputTokens"] = "500" })
        .Build();

    private static AICallContext Context(string userId, string operationId, string intent = "chat") => new()
    {
        OperationId = operationId,
        CallId = Guid.NewGuid().ToString("n"),
        User = new RVUserContext(userId),
        Intent = intent,
        Profile = "default",
        Provider = "openai",
        Model = "test-model",
    };

    private AIUsageRecord Usage(AICallContext context, int? input, int? output, bool succeeded = true, string? errorCode = null) => new()
    {
        OperationId = context.OperationId,
        CallId = context.CallId,
        Timestamp = _time.GetUtcNow(),
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
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticOptions(QuotaOptions value) : IOptionsMonitor<QuotaOptions>
    {
        public QuotaOptions CurrentValue => value;
        public QuotaOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<QuotaOptions, string?> listener) => null;
    }
}
