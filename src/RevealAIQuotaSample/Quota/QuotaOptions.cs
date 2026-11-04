namespace RevealAIQuotaSample.Quota;

/// <summary>
/// What to charge when a call finishes without complete token counts: the provider reported
/// neither count, or only one of input/output. Calls with both counts are always charged exactly.
/// </summary>
public enum MissingUsagePolicy
{
    /// <summary>Charge the larger of the reported tokens and the call's reservation
    /// (conservative; the default).</summary>
    ChargeReservation,

    /// <summary>Charge only the tokens that were reported (permissive; the call is still
    /// recorded and flagged as missing usage).</summary>
    ChargeNothing,
}

/// <summary>Settings bound from the "Quota" configuration section.</summary>
public sealed class QuotaOptions
{
    public const string SectionName = "Quota";

    /// <summary>Tokens (input + output) each user may spend per UTC day.</summary>
    public long DailyTokenLimit { get; set; } = 100_000;

    /// <summary>
    /// Input tokens reserved for every call before it runs. The quota gate does not see the
    /// prompt, so this is an estimate: set it from the input sizes you observe in /quota/me.
    /// </summary>
    public int EstimatedInputTokensPerCall { get; set; } = 16_000;

    /// <summary>
    /// Output tokens reserved per call when the profile has no MaxOutputTokens. When the profile
    /// sets MaxOutputTokens, that value is reserved instead.
    /// </summary>
    public int DefaultOutputTokenReservation { get; set; } = 4_096;

    /// <summary>What to charge when usage is missing or partial (or the call failed).</summary>
    public MissingUsagePolicy MissingUsagePolicy { get; set; } = MissingUsagePolicy.ChargeReservation;

    /// <summary>
    /// Treat a token count of 0 as "not reported". Some providers, including the built-in
    /// OpenAI provider, report a count the API omitted as 0, which would silently undercount.
    /// A real call always sends a prompt and a successful answer has output, so a 0 is far more
    /// likely to mean "unknown". Set to false for a provider that reports genuine zeros.
    /// </summary>
    public bool TreatZeroTokenCountsAsMissing { get; set; } = true;

    /// <summary>SQLite database file, relative to the content root unless absolute.</summary>
    public string DatabasePath { get; set; } = "App_Data/quota.db";

    /// <summary>
    /// Maps the /dev/quota endpoints (list all users, reset a user). They are only ever mapped in
    /// the Development environment, and only when this is true.
    /// </summary>
    public bool EnableDevelopmentEndpoints { get; set; }
}
