using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace RevealAIQuotaSample.Quota;

/// <summary>Descriptive fields stored with each call (for inspection only).</summary>
public sealed record CallInfo(string? Intent, string? Profile, string? Model);

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
    string? SettledUtc);

/// <summary>
/// Per-user, per-UTC-day token accounting in SQLite. One row per provider call, keyed by the
/// SDK's CallId: a call is first "reserved" (CheckAsync) and then "settled" (SettleAsync).
/// Every read-modify-write runs under an in-process lock and inside an immediate SQLite
/// transaction, so concurrent calls cannot both spend the same remaining budget.
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

    /// <summary>Creates the store on <paramref name="databasePath"/>. Reservations left
    /// unsettled by a previous process (crash, kill) are settled now with <paramref name="abandonedPolicy"/>.</summary>
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
                INSERT INTO ai_calls (call_id, user_id, day_utc, status, reserved_tokens, intent, profile, model, created_utc)
                VALUES ($callId, $userId, $day, 'reserved', $reserved, $intent, $profile, $model, $now)
                """;
            insert.Parameters.AddWithValue("$callId", callId);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$day", FormatDay(day));
            insert.Parameters.AddWithValue("$reserved", reservationTokens);
            insert.Parameters.AddWithValue("$intent", (object?)info.Intent ?? DBNull.Value);
            insert.Parameters.AddWithValue("$profile", (object?)info.Profile ?? DBNull.Value);
            insert.Parameters.AddWithValue("$model", (object?)info.Model ?? DBNull.Value);
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
        CancellationToken cancellationToken)
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
                        settled_utc = $now
                    WHERE call_id = $callId AND status = 'reserved'
                    """;
                AddSettleParameters(update, callId, inputTokens, outputTokens, usageMissing, charge, succeeded, errorCode, info.Model);
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
                    charged_tokens, usage_missing, succeeded, error_code, intent, profile, model, created_utc, settled_utc)
                VALUES ($callId, $userId, $day, 'settled', 0, $input, $output,
                    $charge, $missing, $succeeded, $error, $intent, $profile, $model, $now, $now)
                """;
            AddSettleParameters(insert, callId, inputTokens, outputTokens, usageMissing, charge, succeeded, errorCode, info.Model);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$day", FormatDay(dayIfNotReserved));
            insert.Parameters.AddWithValue("$intent", (object?)info.Intent ?? DBNull.Value);
            insert.Parameters.AddWithValue("$profile", (object?)info.Profile ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
            transaction.Commit();
            return new SettleResult(false, charge, usageMissing);
        }
        finally
        {
            _lock.Release();
        }
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
        command.CommandText = """
            SELECT call_id, day_utc, status, reserved_tokens, input_tokens, output_tokens, charged_tokens, usage_missing,
                   succeeded, error_code, intent, profile, model, created_utc, settled_utc
            FROM ai_calls WHERE user_id = $userId ORDER BY created_utc DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CallRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CallRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt64(6), reader.GetInt64(7) == 1,
                reader.IsDBNull(8) ? null : reader.GetInt64(8) == 1,
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return result;
    }

    /// <summary>Deletes a user's settled calls for the day. In-flight reservations are kept so
    /// their settlement still finds them.</summary>
    public async Task<int> ResetAsync(string userId, DateOnly day, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ai_calls WHERE user_id = $userId AND day_utc = $day AND status = 'settled'";
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$day", FormatDay(day));
            return await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static UserQuotaUsage ReadUsage(SqliteDataReader reader, string day) => new(
        reader.GetString(0), day, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
        reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6));

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
                """;
            create.ExecuteNonQuery();
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
    /// The result is a single number written once per CallId, so nothing is charged twice.
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

    private void AddSettleParameters(
        SqliteCommand command, string callId, int? inputTokens, int? outputTokens, bool usageMissing,
        long charge, bool succeeded, string? errorCode, string? model)
    {
        command.Parameters.AddWithValue("$callId", callId);
        command.Parameters.AddWithValue("$input", (object?)inputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)outputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$charge", charge);
        command.Parameters.AddWithValue("$missing", usageMissing ? 1 : 0);
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
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
