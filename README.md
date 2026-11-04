# Reveal AI – Quota Management Sample

A small **ASP.NET Core (.NET 8)** sample showing how to use `IAIQuotaGate` to
limit AI token usage per user. It includes a local demo page (Alice and Bob),
SQLite usage tracking, and automated tests.

## Quick start

You need **.NET 8**, your **own Reveal license with AI support**, and an **OpenAI
API key** authorized to make model requests. The sample includes neither credential.
It also downloads a public sample Excel file when the datasource is used.

1. Extract the ZIP and open a terminal **in the extracted project folder**.
2. Set your OpenAI key using .NET user secrets (do not add it to `appsettings.json`):

   ```bash
   dotnet user-secrets set "RevealAI:Providers:openai:ApiKey" "YOUR_OPENAI_API_KEY" --project src/RevealAIQuotaSample
   ```

3. Place your Reveal license in your usual SDK location. If you haven't configured
   one already, Reveal checks `<user home>/.revealbi-sdk/license.key`
   (`%USERPROFILE%\.revealbi-sdk\license.key` on Windows).
4. Run the server:

   ```bash
   dotnet run --project src/RevealAIQuotaSample
   ```

5. Open **http://localhost:5111/demo/** on the same machine.

The launch profile starts in **Development** on localhost. The demo page and
its test-user shortcuts are intentionally unavailable in Production.

## Try the demo

- Switch between **Alice** and **Bob**. Each has an independent daily quota.
- Ask a question in **AI Chat**, such as *Which marketing campaign had the most clicks?*
  Requests go through Reveal AI; the browser does not call OpenAI directly.
- Watch the **token usage** panel and **recent calls** update. One chat message
  can make several LLM calls, so it may create several usage rows.
- To see a **429 quota-exceeded** response sooner, lower `Quota:DailyTokenLimit`
  in `src/RevealAIQuotaSample/appsettings.Development.json`, restart, and try
  a few questions. Use **Reset today's usage** to start over.

The default daily budget is **100,000 tokens per user**. The sample reserves an
estimated **16,000 input + 4,096 output tokens per provider call** before it runs.
These values are configurable; they are not a guarantee of a hard spending cap.

## Where are the hooks?

| File | What to look at |
| --- | --- |
| `src/RevealAIQuotaSample/Program.cs` | Registers `.AddQuotaGate<DailyTokenQuotaGate>()` and `.AddUsageSink<UsageLogSink>()`. |
| `src/RevealAIQuotaSample/Quota/DailyTokenQuotaGate.cs` | **`CheckAsync`** checks/reserves budget *before* each LLM call. **`SettleAsync`** records reported usage *after* the call. |
| `src/RevealAIQuotaSample/Quota/QuotaStore.cs` | SQLite persistence, atomic reservations and idempotent settlement by `CallId`. Replace with your own storage if needed. |
| `src/RevealAIQuotaSample/Quota/UsageLogSink.cs` | Optional logging/observability; **not** authoritative billing. |
| `src/RevealAIQuotaSample/Identity/` | Demo `X-User-Id` identity versus authenticated user identity. |
| `src/RevealAIQuotaSample/appsettings.json` | Provider, model, `MaxOutputTokens` and quota settings. |

**The main file to read is `DailyTokenQuotaGate.cs`.** Everything else supports
running the example or recording its usage.

## Tests

From the extracted project folder:

```bash
dotnet test
```

The test project is in `tests/` and is not needed to run the demo.

## Important notes

- **Demo only:** `X-User-Id`, the demo page and the quota reset endpoints are
  limited to local Development requests. They are **not authentication**.
  For production, configure real ASP.NET Core authentication and a Reveal AI
  authorization policy.
- **Not a billing system:** input-token reservations are estimates, so actual
  usage can exceed a reservation. Multi-server deployments need a shared,
  transactionally consistent quota store; this SQLite implementation targets
  one server process.
- **Incomplete usage:** failed calls or missing token counts follow the
  configurable `MissingUsagePolicy` (default: charge the reservation).
  The sample can also treat zero counts as missing usage.
- **Other SDK calls:** background/system calls bypass the quota gate by default.
  `IAIUsageSink` is best-effort reporting; only `SettleAsync` updates balances.
- **Credentials:** use your own license and key. Never bundle credentials,
  generated databases or logs when sharing this sample.

Public SDK documentation: [Quotas and Billing](https://github.com/RevealBi/Documentation/blob/master/docs/ai/quota-gate.md)
& [Usage Tracking](https://github.com/RevealBi/Documentation/blob/master/docs/ai/usage-tracking.md).
