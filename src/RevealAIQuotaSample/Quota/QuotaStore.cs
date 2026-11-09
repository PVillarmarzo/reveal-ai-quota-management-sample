using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Reveal.Sdk.AI;

namespace RevealAIQuotaSample.Quota;

/// <summary>Descriptive fields stored with each call when it is reserved (for inspection only).</summary>
public sealed record CallInfo(
    string? Intent,
    string? Profile,
    string? Model,
    string? OperationId = null,
    string? Provider = null,
    bool IsSystemCall = false);

/// <summary>
/// The SDK 1.6 usage details stored with each call when it settles. Informational only: none
/// of them changes the charge. Reasoning and cached input tokens are breakdowns that the
/// provider already counts in the output and input tokens, so they are never added to the total.
/// </summary>
public sealed record UsageBreakdown(
    string? ParentOperationId = null,
    string? Stage = null,
    int? Attempt = null,
    int? ReasoningTokens = null,
    int? CachedInputTokens = null,
    long? DurationMs = null)
{
    public static readonly UsageBreakdown None = new();

    public static UsageBreakdown From(AIUsageRecord record) => new(
        record.ParentOperationId, record.Stage, record.Attempt,
        record.ReasoningTokens, record.CachedInputTokens, (long)record.Duration.TotalMilliseconds);
}

public sealed record ReserveResult(bool Allowed, long CommittedTokens);

public sealed record SettleResult(bool AlreadySettled, long ChargedTokens, bool UsageMissing);

/// <summary>One user's consumption for one UTC day.</summary>
public sealed record UserQuotaUsage(
    string UserId,
    string DayUtc,
    long InputTokens,
    long OutputTokens,
    long ChargedTokens,
    long PendingReservedTokens,
    int Calls,
    int CallsWithMissingUsage)
{
    /// <summary>Tokens counted against the limit: settled charges plus in-flight reservations.</summary>
    public long CommittedTokens => ChargedTokens + PendingReservedTokens;
}

/// <summary>One row of the quota ledger (<c>ai_calls</c>): a provider call the gate checked or settled.</summary>
public sealed record CallRecord(
    string CallId,
    string DayUtc,
    string Status,
    long ReservedTokens,
    int? InputTokens,
    int? OutputTokens,
    long ChargedTokens,
    bool UsageMissing,
    bool? Succeeded,
    string? ErrorCode,
    string? Intent,
    string? Profile,
    string? Model,
    string CreatedUtc,
    string? SettledUtc,
    string? OperationId = null,
    string? ParentOperationId = null,
    string? Provider = null,
    string? Stage = null,
    int? Attempt = null,
    int? ReasoningTokens = null,
    int? CachedInputTokens = null,
    bool IsSystemCall = false,
    string? UserId = null);

/// <summary>
/// Per-user, per-UTC-day token accounting in SQLite.
/// <para>
/// <c>ai_calls</c> is the quota ledger: one row per provider call, keyed by the SDK's CallId.
/// A call is first "reserved" (CheckAsync) and then "settled" (SettleAsync). Every
/// read-modify-write runs under an in-process lock and inside an immediate SQLite transaction,
/// so concurrent calls cannot both spend the same remaining budget.
/// </para>
/// <para>
/// <c>ai_usage_records</c> keeps every <see cref="AIUsageRecord"/> the usage sink receives,
/// including denied calls and system calls the gate never sees. It is for reporting only.
/// </para>
/// </summary>
public sealed class QuotaStore
{
    private const string Reserved = "reserved";
    private const string Settled = "settled";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time;

    public QuotaStore(IOptions<QuotaOptions> options, IHostEnvironment environment, TimeProvider time)
        : this(ResolvePath(options.Value.DatabasePath, environment.ContentRootPath), options.Value.MissingUsagePolicy, time)
    {
    }

    /// <summary>Creates the store on <paramref name="databasePath"/>, adding any missing columns
    /// to an existing database. Reservations left unsettled by a previous process (crash, kill)
    /// are settled now with <paramref name="abandonedPolicy"/>.</summary>
    public QuotaStore(string databasePath, MissingUsagePolicy abandonedPolicy, TimeProvider time)
    {
        _time = time;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        Initialize(abandonedPolicy);
    }

    public static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reserves <paramref name="reservationTokens"/> for a call if the user's committed tokens
    /// for the day plus the reservation stay within <paramref name="dailyLimit"/>.
    /// </summary>
    public async Task<ReserveResult> TryReserveAsync(
        string callId, string userId, DateOnly day, long reservationTokens, long dailyLimit,
        CallInfo info, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(); // BEGIN IMMEDIATE

            var committed = await GetCommittedAsync(connection, transaction, userId, day);
            if (await GetStatusAsync(connection, transaction, callId) is not null)
            {
                // Already reserved (or settled) under this CallId: never reserve twice.
                transaction.Commit();
                return new ReserveResult(true, committed);
            }

            if (committed + reservationTokens > dailyLimit)
            {
                transaction.Commit();
                return new ReserveResult(false, committed);
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ai_calls (call_id, user_id, day_utc, status, reserved_tokens, intent, profile, model,
                    operation_id, provider, is_system_call, created_utc)
                VALUES ($callId, $userId, $day, 'reserved', $reserved, $intent, $profile, $model,
                    $operationId, $provider, $system, $now)
                """;
            insert.Parameters.AddWithValue("$callId", callId);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$day", FormatDay(day));
            insert.Parameters.AddWithValue("$reserved", reservationTokens);
            AddCallInfoParameters(insert, info);
            insert.Parameters.AddWithValue("$now", Now());
            await insert.ExecuteNonQueryAsync(cancellationToken);

            transaction.Commit();
            return new ReserveResult(true, committed + reservationTokens);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Settles a call with its actual usage, replacing its reservation. Idempotent on
    /// <paramref name="callId"/>: settling an already settled call changes nothing.
    /// </summary>
    public async Task<SettleResult> SettleAsync(
        string callId, string userId, DateOnly dayIfNotReserved, int? inputTokens, int? outputTokens,
        bool succeeded, string? errorCode, MissingUsagePolicy missingUsagePolicy, CallInfo info,
        UsageBreakdown breakdown, CancellationToken cancellationToken)
    {
        // Settlement is accounting for tokens that may already be spent: don't let a cancelled
        // request skip it.
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            var (status, reservedTokens) = await GetCallAsync(connection, transaction, callId);
            if (status == Settled)
            {
                transaction.Commit();
                return new SettleResult(true, 0, false);
            }

            var usageMissing = !IsUsageComplete(inputTokens, outputTokens);
            var charge = ComputeCharge(reservedTokens, inputTokens, outputTokens, missingUsagePolicy);

            if (status == Reserved)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE ai_calls SET
                        status = 'settled',
                        input_tokens = $input,
                        output_tokens = $output,
                        charged_tokens = $charge,
                        usage_missing = $missing,
                        succeeded = $succeeded,
                        error_code = $error,
                        model = COALESCE($model, model),
                        operation_id = COALESCE(operation_id, $operationId),
                        provider = COALESCE(provider, $provider),
                        parent_operation_id = $parentOperationId,
                        stage = $stage,
                        attempt = $attempt,
                        reasoning_tokens = $reasoning,
                        cached_input_tokens = $cached,
                        duration_ms = $durationMs,
                        settled_utc = $now
                    WHERE call_id = $callId AND status = 'reserved'
                    """;
                AddSettleParameters(update, callId, inputTokens, outputTokens, usageMissing, charge, succeeded, errorCode, info, breakdown);
                await update.ExecuteNonQueryAsync(CancellationToken.None);
                transaction.Commit();
                return new SettleResult(false, charge, usageMissing);
            }

            // Settled without a reservation: the gate did not check this call (for example a
            // system call while IncludeSystemCalls was being toggled). Record what was reported;
            // with no reservation, ComputeCharge charges only the reported tokens.
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ai_calls (call_id, user_id, day_utc, status, reserved_tokens, input_tokens, output_tokens,
                    charged_tokens, usage_missing, succeeded, error_code, intent, profile, model,
                    operation_id, provider, is_system_call, parent_operation_id, stage, attempt,
                    reasoning_tokens, cached_input_tokens, duration_ms, created_utc, settled_utc)
                VALUES ($callId, $userId, $day, 'settled', 0, $input, $output,
                    $charge, $missing, $succeeded, $error, $intent, $profile, $model,
                    $operationId, $provider, $system, $parentOperationId, $stage, $attempt,
                    $reasoning, $cached, $durationMs, $now, $now)
                """;
            AddSettleParameters(insert, callId, inputTokens, outputTokens, usageMissing, charge, succeeded, errorCode, info, breakdown);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$day", FormatDay(dayIfNotReserved));
            insert.Parameters.AddWithValue("$intent", (object?)info.Intent ?? DBNull.Value);
            insert.Parameters.AddWithValue("$profile", (object?)info.Profile ?? DBNull.Value);
            insert.Parameters.AddWithValue("$system", info.IsSystemCall ? 1 : 0);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
            transaction.Commit();
            return new SettleResult(false, charge, usageMissing);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Stores a usage record from the usage sink. One row per CallId; a repeated record is
    /// ignored. Token counts are stored exactly as reported (null stays null).
    /// </summary>
    public async Task RecordUsageAsync(AIUsageRecord record, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO ai_usage_records (call_id, operation_id, parent_operation_id, user_id, day_utc,
                timestamp_utc, is_system_call, is_user_connection, intent, profile, provider, model, stage, attempt,
                input_tokens, output_tokens, reasoning_tokens, cached_input_tokens, duration_ms, finish_reason,
                succeeded, error_code)
            VALUES ($callId, $operationId, $parentOperationId, $userId, $day,
                $timestamp, $system, $userConnection, $intent, $profile, $provider, $model, $stage, $attempt,
                $input, $output, $reasoning, $cached, $durationMs, $finishReason,
                $succeeded, $error)
            """;
        var timestamp = record.Timestamp.ToUniversalTime();
        command.Parameters.AddWithValue("$callId", record.CallId);
        command.Parameters.AddWithValue("$operationId", record.OperationId);
        command.Parameters.AddWithValue("$parentOperationId", (object?)record.ParentOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$userId", record.UserId);
        command.Parameters.AddWithValue("$day", FormatDay(DateOnly.FromDateTime(timestamp.UtcDateTime)));
        command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$system", record.IsSystemCall ? 1 : 0);
        command.Parameters.AddWithValue("$userConnection", record.IsUserConnection ? 1 : 0);
        command.Parameters.AddWithValue("$intent", record.Intent);
        command.Parameters.AddWithValue("$profile", record.Profile);
        command.Parameters.AddWithValue("$provider", record.Provider);
        command.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$stage", (object?)record.Stage ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempt", (object?)record.Attempt ?? DBNull.Value);
        command.Parameters.AddWithValue("$input", (object?)record.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)record.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoning", (object?)record.ReasoningTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$cached", (object?)record.CachedInputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", (long)record.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$finishReason", (object?)record.FinishReason?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$succeeded", record.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)record.ErrorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserQuotaUsage> GetUsageAsync(string userId, DateOnly day, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = UsageSelect + " WHERE user_id = $userId AND day_utc = $day GROUP BY user_id";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$day", FormatDay(day));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadUsage(reader, FormatDay(day))
            : new UserQuotaUsage(userId, FormatDay(day), 0, 0, 0, 0, 0, 0);
    }

    public async Task<IReadOnlyList<UserQuotaUsage>> GetAllUsageAsync(DateOnly day, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = UsageSelect + " WHERE day_utc = $day GROUP BY user_id ORDER BY user_id";
        command.Parameters.AddWithValue("$day", FormatDay(day));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UserQuotaUsage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadUsage(reader, FormatDay(day)));
        }
        return result;
    }

    public async Task<IReadOnlyList<CallRecord>> GetRecentCallsAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = CallSelect + " WHERE user_id = $userId ORDER BY created_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadCallsAsync(command, cancellationToken);
    }

    /// <summary>
    /// The rows behind one user's per-request report for one UTC day: the user's ledger rows,
    /// the user's usage records, and the usage records of auxiliary operations whose
    /// ParentOperationId is one of the user's operations (for example datasource ranking, which
    /// runs as a system call during a dashboard generation).
    /// </summary>
    public async Task<(IReadOnlyList<CallRecord> Ledger, IReadOnlyList<UsageRow> Records)> GetReportRowsAsync(
        string userId, DateOnly day, CancellationToken cancellationToken)
    {
        using var connection = Open();

        using var ledgerCommand = connection.CreateCommand();
        ledgerCommand.CommandText = CallSelect + " WHERE user_id = $userId AND day_utc = $day";
        ledgerCommand.Parameters.AddWithValue("$userId", userId);
        ledgerCommand.Parameters.AddWithValue("$day", FormatDay(day));
        var ledger = await ReadCallsAsync(ledgerCommand, cancellationToken);

        using var recordCommand = connection.CreateCommand();
        recordCommand.CommandText = """
            SELECT call_id, operation_id, parent_operation_id, user_id, timestamp_utc, is_system_call, intent,
                   provider, model, stage, attempt, input_tokens, output_tokens, reasoning_tokens,
                   cached_input_tokens, duration_ms, succeeded, error_code
            FROM ai_usage_records
            WHERE (user_id = $userId AND day_utc = $day)
               OR parent_operation_id IN (
                    SELECT operation_id FROM ai_calls WHERE user_id = $userId AND day_utc = $day AND operation_id IS NOT NULL
                    UNION
                    SELECT operation_id FROM ai_usage_records WHERE user_id = $userId AND day_utc = $day)
            """;
        recordCommand.Parameters.AddWithValue("$userId", userId);
        recordCommand.Parameters.AddWithValue("$day", FormatDay(day));
        var records = new List<UsageRow>();
        using (var reader = await recordCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new UsageRow(
                    CallId: reader.GetString(0),
                    OperationId: reader.GetString(1),
                    ParentOperationId: NullableString(reader, 2),
                    UserId: reader.GetString(3),
                    TimestampUtc: reader.GetString(4),
                    IsSystemCall: reader.GetInt64(5) == 1,
                    Intent: NullableString(reader, 6),
                    Provider: NullableString(reader, 7),
                    Model: NullableString(reader, 8),
                    Stage: NullableString(reader, 9),
                    Attempt: NullableInt(reader, 10),
                    InputTokens: NullableInt(reader, 11),
                    OutputTokens: NullableInt(reader, 12),
                    ReasoningTokens: NullableInt(reader, 13),
                    CachedInputTokens: NullableInt(reader, 14),
                    DurationMs: reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    Succeeded: reader.GetInt64(16) == 1,
                    ErrorCode: NullableString(reader, 17)));
            }
        }

        return (ledger, records);
    }

    /// <summary>Deletes a user's settled calls for the day, and the day's usage records for the
    /// user's operations. In-flight reservations are kept so their settlement still finds them.</summary>
    public async Task<int> ResetAsync(string userId, DateOnly day, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var records = connection.CreateCommand())
            {
                records.Transaction = transaction;
                records.CommandText = """
                    DELETE FROM ai_usage_records
                    WHERE (user_id = $userId AND day_utc = $day)
                       OR parent_operation_id IN (
                            SELECT operation_id FROM ai_calls WHERE user_id = $userId AND day_utc = $day AND status = 'settled' AND operation_id IS NOT NULL
                            UNION
                            SELECT operation_id FROM ai_usage_records WHERE user_id = $userId AND day_utc = $day)
                    """;
                records.Parameters.AddWithValue("$userId", userId);
                records.Parameters.AddWithValue("$day", FormatDay(day));
                await records.ExecuteNonQueryAsync(cancellationToken);
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ai_calls WHERE user_id = $userId AND day_utc = $day AND status = 'settled'";
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$day", FormatDay(day));
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            return deleted;
        }
        finally
        {
            _lock.Release();
        }
    }

    private const string UsageSelect = """
        SELECT user_id,
               COALESCE(SUM(CASE WHEN status = 'settled' THEN COALESCE(input_tokens, 0) ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN status = 'settled' THEN COALESCE(output_tokens, 0) ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN status = 'settled' THEN charged_tokens ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN status = 'reserved' THEN reserved_tokens ELSE 0 END), 0),
               COUNT(*),
               COALESCE(SUM(usage_missing), 0)
        FROM ai_calls
        """;

    private const string CallSelect = """
        SELECT call_id, day_utc, status, reserved_tokens, input_tokens, output_tokens, charged_tokens, usage_missing,
               succeeded, error_code, intent, profile, model, created_utc, settled_utc,
               operation_id, parent_operation_id, provider, stage, attempt, reasoning_tokens, cached_input_tokens,
               is_system_call, user_id
        FROM ai_calls
        """;

    private static async Task<IReadOnlyList<CallRecord>> ReadCallsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CallRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CallRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                NullableInt(reader, 4),
                NullableInt(reader, 5),
                reader.GetInt64(6), reader.GetInt64(7) == 1,
                reader.IsDBNull(8) ? null : reader.GetInt64(8) == 1,
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                reader.GetString(13),
                NullableString(reader, 14),
                OperationId: NullableString(reader, 15),
                ParentOperationId: NullableString(reader, 16),
                Provider: NullableString(reader, 17),
                Stage: NullableString(reader, 18),
                Attempt: NullableInt(reader, 19),
                ReasoningTokens: NullableInt(reader, 20),
                CachedInputTokens: NullableInt(reader, 21),
                IsSystemCall: reader.GetInt64(22) == 1,
                UserId: reader.GetString(23)));
        }
        return result;
    }

    private static UserQuotaUsage ReadUsage(SqliteDataReader reader, string day) => new(
        reader.GetString(0), day, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
        reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6));

    private static string? NullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    // Columns added for SDK 1.6 usage details. They are all nullable (or have a default), so a
    // database created by an earlier version of the sample keeps its rows and only gains columns.
    private static readonly (string Name, string Definition)[] LedgerColumnsAddedIn16 =
    {
        ("operation_id", "TEXT NULL"),
        ("parent_operation_id", "TEXT NULL"),
        ("provider", "TEXT NULL"),
        ("is_system_call", "INTEGER NOT NULL DEFAULT 0"),
        ("stage", "TEXT NULL"),
        ("attempt", "INTEGER NULL"),
        ("reasoning_tokens", "INTEGER NULL"),
        ("cached_input_tokens", "INTEGER NULL"),
        ("duration_ms", "INTEGER NULL"),
    };

    private void Initialize(MissingUsagePolicy abandonedPolicy)
    {
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS ai_calls (
                    call_id         TEXT PRIMARY KEY,
                    user_id         TEXT NOT NULL,
                    day_utc         TEXT NOT NULL,
                    status          TEXT NOT NULL,
                    reserved_tokens INTEGER NOT NULL,
                    input_tokens    INTEGER NULL,
                    output_tokens   INTEGER NULL,
                    charged_tokens  INTEGER NOT NULL DEFAULT 0,
                    usage_missing   INTEGER NOT NULL DEFAULT 0,
                    succeeded       INTEGER NULL,
                    error_code      TEXT NULL,
                    intent          TEXT NULL,
                    profile         TEXT NULL,
                    model           TEXT NULL,
                    created_utc     TEXT NOT NULL,
                    settled_utc     TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_ai_calls_user_day ON ai_calls (user_id, day_utc);

                CREATE TABLE IF NOT EXISTS ai_usage_records (
                    call_id             TEXT PRIMARY KEY,
                    operation_id        TEXT NOT NULL,
                    parent_operation_id TEXT NULL,
                    user_id             TEXT NOT NULL,
                    day_utc             TEXT NOT NULL,
                    timestamp_utc       TEXT NOT NULL,
                    is_system_call      INTEGER NOT NULL,
                    is_user_connection  INTEGER NOT NULL,
                    intent              TEXT NULL,
                    profile             TEXT NULL,
                    provider            TEXT NULL,
                    model               TEXT NULL,
                    stage               TEXT NULL,
                    attempt             INTEGER NULL,
                    input_tokens        INTEGER NULL,
                    output_tokens       INTEGER NULL,
                    reasoning_tokens    INTEGER NULL,
                    cached_input_tokens INTEGER NULL,
                    duration_ms         INTEGER NULL,
                    finish_reason       TEXT NULL,
                    succeeded           INTEGER NOT NULL,
                    error_code          TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_ai_usage_records_user_day ON ai_usage_records (user_id, day_utc);
                CREATE INDEX IF NOT EXISTS ix_ai_usage_records_operation ON ai_usage_records (operation_id);
                CREATE INDEX IF NOT EXISTS ix_ai_usage_records_parent ON ai_usage_records (parent_operation_id);
                """;
            create.ExecuteNonQuery();
        }

        // Upgrade a database created by the 1.5 version of the sample: add the new columns, keep every row.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(ai_calls);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }
        foreach (var (name, definition) in LedgerColumnsAddedIn16)
        {
            if (existing.Contains(name))
            {
                continue;
            }
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE ai_calls ADD COLUMN {name} {definition};";
            alter.ExecuteNonQuery();
        }
        using (var index = connection.CreateCommand())
        {
            index.Transaction = transaction;
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_ai_calls_operation ON ai_calls (operation_id);";
            index.ExecuteNonQuery();
        }

        // A reservation still open at startup belongs to a process that stopped before the
        // call settled. Its tokens may have been spent, so settle it with the configured policy.
        using (var abandon = connection.CreateCommand())
        {
            abandon.Transaction = transaction;
            abandon.CommandText = """
                UPDATE ai_calls SET
                    status = 'settled',
                    charged_tokens = CASE WHEN $policy = 'ChargeReservation' THEN reserved_tokens ELSE 0 END,
                    usage_missing = 1,
                    error_code = 'abandoned',
                    settled_utc = $now
                WHERE status = 'reserved'
                """;
            abandon.Parameters.AddWithValue("$policy", abandonedPolicy.ToString());
            abandon.Parameters.AddWithValue("$now", Now());
            abandon.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>True when the provider reported both input and output token counts.</summary>
    public static bool IsUsageComplete(int? inputTokens, int? outputTokens)
        => inputTokens is not null && outputTokens is not null;

    /// <summary>
    /// The tokens to charge for a settled call. One rule covers complete, partial and missing usage:
    /// <list type="bullet">
    /// <item>Both counts reported: charge exactly input + output.</item>
    /// <item>One or both counts missing, <see cref="MissingUsagePolicy.ChargeReservation"/>: charge
    /// the larger of the reported tokens and the call's reservation. The unknown part is covered
    /// by the reservation, and a reported count larger than the reservation is never discounted.</item>
    /// <item>One or both counts missing, <see cref="MissingUsagePolicy.ChargeNothing"/>: charge only
    /// the reported tokens (zero when nothing was reported).</item>
    /// </list>
    /// Reasoning and cached input tokens are not added: they are already part of the output and
    /// input counts. The result is a single number written once per CallId, so nothing is charged twice.
    /// </summary>
    public static long ComputeCharge(long reservedTokens, int? inputTokens, int? outputTokens, MissingUsagePolicy policy)
    {
        var reported = (long)(inputTokens ?? 0) + (outputTokens ?? 0);
        if (IsUsageComplete(inputTokens, outputTokens))
        {
            return reported;
        }

        return policy == MissingUsagePolicy.ChargeReservation
            ? Math.Max(reported, reservedTokens)
            : reported;
    }

    private static void AddCallInfoParameters(SqliteCommand command, CallInfo info)
    {
        command.Parameters.AddWithValue("$intent", (object?)info.Intent ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile", (object?)info.Profile ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)info.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$operationId", (object?)info.OperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)info.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$system", info.IsSystemCall ? 1 : 0);
    }

    private void AddSettleParameters(
        SqliteCommand command, string callId, int? inputTokens, int? outputTokens, bool usageMissing,
        long charge, bool succeeded, string? errorCode, CallInfo info, UsageBreakdown breakdown)
    {
        command.Parameters.AddWithValue("$callId", callId);
        command.Parameters.AddWithValue("$input", (object?)inputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)outputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$charge", charge);
        command.Parameters.AddWithValue("$missing", usageMissing ? 1 : 0);
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)info.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$operationId", (object?)info.OperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)info.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentOperationId", (object?)breakdown.ParentOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$stage", (object?)breakdown.Stage ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempt", (object?)breakdown.Attempt ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoning", (object?)breakdown.ReasoningTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$cached", (object?)breakdown.CachedInputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", (object?)breakdown.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
    }

    private static async Task<string?> GetStatusAsync(SqliteConnection connection, SqliteTransaction transaction, string callId)
        => (await GetCallAsync(connection, transaction, callId)).Status;

    private static async Task<(string? Status, long ReservedTokens)> GetCallAsync(SqliteConnection connection, SqliteTransaction transaction, string callId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status, reserved_tokens FROM ai_calls WHERE call_id = $callId";
        command.Parameters.AddWithValue("$callId", callId);
        using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetString(0), reader.GetInt64(1)) : (null, 0);
    }

    private static async Task<long> GetCommittedAsync(SqliteConnection connection, SqliteTransaction transaction, string userId, DateOnly day)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN status = 'settled' THEN charged_tokens ELSE reserved_tokens END), 0)
            FROM ai_calls WHERE user_id = $userId AND day_utc = $day
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$day", FormatDay(day));
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static string ResolvePath(string path, string contentRoot)
        => Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);
}
