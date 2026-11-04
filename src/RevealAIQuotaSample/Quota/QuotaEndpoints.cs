using Microsoft.Extensions.Options;
using Reveal.Sdk;
using RevealAIQuotaSample.Identity;

namespace RevealAIQuotaSample.Quota;

public static class QuotaEndpoints
{
    /// <summary>
    /// GET /quota/me in every environment: the calling user's usage today, resolved with the
    /// same user context provider the Reveal AI endpoints use.
    /// The /dev/quota endpoints only in Development, only when Quota:EnableDevelopmentEndpoints
    /// is true, and only for local requests (<see cref="LocalRequest.IsLocal"/>).
    /// </summary>
    public static void MapQuotaEndpoints(this WebApplication app)
    {
        app.MapGet("/quota/me", async (
            HttpContext http, IRVUserContextProvider users, QuotaStore store,
            IOptionsMonitor<QuotaOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var userId = users.GetUserContext(http).UserId;
            if (string.IsNullOrEmpty(userId) || userId == DailyTokenQuotaGate.AnonymousUserId)
            {
                return Results.Problem("No user identity for this request.", statusCode: StatusCodes.Status401Unauthorized);
            }

            var usage = await store.GetUsageAsync(userId, Today(time), ct);
            return Results.Ok(ToSummary(usage, options.CurrentValue, time));
        });

        if (!DevelopmentEndpointsEnabled(app))
        {
            return;
        }

        // DEVELOPMENT ONLY: inspection and reset for local testing. Never map these in production;
        // protect real admin endpoints with an authorization policy.
        var dev = app.MapGroup("/dev/quota").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!LocalRequest.IsLocal(http))
            {
                return Results.NotFound();
            }

            if (!HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey(LocalRequest.DemoRequestHeader))
            {
                return Results.Problem($"Send the {LocalRequest.DemoRequestHeader} header with development requests that change data.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return await next(context);
        });

        dev.MapGet("/", async (QuotaStore store, IOptionsMonitor<QuotaOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var all = await store.GetAllUsageAsync(Today(time), ct);
            return Results.Ok(all.Select(u => ToSummary(u, options.CurrentValue, time)));
        });

        dev.MapGet("/{userId}/calls", async (string userId, QuotaStore store, CancellationToken ct, int? limit) =>
            Results.Ok(await store.GetRecentCallsAsync(userId, Math.Clamp(limit ?? 20, 1, 200), ct)));

        dev.MapPost("/{userId}/reset", async (string userId, QuotaStore store, TimeProvider time, CancellationToken ct) =>
        {
            var deleted = await store.ResetAsync(userId, Today(time), ct);
            return Results.Ok(new { userId, dayUtc = QuotaStore.FormatDay(Today(time)), deletedCalls = deleted });
        });
    }

    /// <summary>True only in the Development environment with Quota:EnableDevelopmentEndpoints set.</summary>
    public static bool DevelopmentEndpointsEnabled(WebApplication app)
        => app.Environment.IsDevelopment()
           && app.Services.GetRequiredService<IOptions<QuotaOptions>>().Value.EnableDevelopmentEndpoints;

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static object ToSummary(UserQuotaUsage usage, QuotaOptions options, TimeProvider time) => new
    {
        usage.UserId,
        usage.DayUtc,
        dailyTokenLimit = options.DailyTokenLimit,
        usage.InputTokens,
        usage.OutputTokens,
        chargedTokens = usage.ChargedTokens,
        pendingReservedTokens = usage.PendingReservedTokens,
        remainingTokens = Math.Max(0, options.DailyTokenLimit - usage.CommittedTokens),
        usage.Calls,
        usage.CallsWithMissingUsage,
        resetsAtUtc = new DateTimeOffset(Today(time).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
    };
}
