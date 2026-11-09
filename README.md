# Reveal AI – Quota Management Sample

A small **ASP.NET Core (.NET 8)** sample showing how to use `IAIQuotaGate` to
limit AI token usage per user, and how to report usage **per user request** with
the usage fields added in Reveal AI SDK 1.6. It includes a local demo page
(Alice and Bob), SQLite usage tracking, and automated tests.

**Packages:** `Reveal.Sdk.AI.AspNetCore` and `Reveal.Sdk.AI.OpenAI` **1.6.358**, with
`Reveal.Sdk.AspNetCore` **2.3.0** (the minimum that SDK 1.6.358 requires), from nuget.org.

## Quick start

You need **.NET 8**, your **own Reveal license with AI support**, and an **OpenAI
API key** authorized to make model requests. The sample includes neither credential.
It also downloads a public sample Excel file when the datasource is used.

1. Clone the repository (or extract the ZIP) and open a terminal **in the project folder**.
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
- Ask a question in **AI Chat**, such as *Show total clicks by date*
  Requests go through Reveal AI; the browser does not call OpenAI directly.
- Watch the **token budget** and **Usage per request** panels update. One chat
  message can make several LLM calls; the panel shows each request's total and
  number of provider calls, and expands into one row per call (intent, stage,
  attempt, tokens, charge).
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
| `src/RevealAIQuotaSample/Quota/DailyTokenQuotaGate.cs` | **`CheckAsync`** checks/reserves budget *before* each LLM call. **`SettleAsync`** records reported usage *after* the call, with the 1.6 usage fields. |
| `src/RevealAIQuotaSample/Quota/QuotaStore.cs` | SQLite persistence, atomic reservations and idempotent settlement by `CallId`. Replace with your own storage if needed. |
| `src/RevealAIQuotaSample/Quota/UsageLogSink.cs` | **`IAIUsageSink`**: logs and stores every usage record (including denied and system calls). Reporting only; **not** authoritative billing. |
| `src/RevealAIQuotaSample/Quota/UsageReport.cs` | Groups calls into user requests and computes the per-request and per-call averages (`GET /quota/me/requests`). |
| `src/RevealAIQuotaSample/Identity/` | Demo `X-User-Id` identity versus authenticated user identity. |
| `src/RevealAIQuotaSample/appsettings.json` | Provider, model, `MaxOutputTokens` and quota settings. |

**The main file to read is `DailyTokenQuotaGate.cs`.** Everything else supports
running the example or recording its usage.

## Usage per request

One chat request does **not** always make the same number of provider calls. A typical
dashboard request makes an intent-classifier call and then one or more generation calls;
other requests make fewer or more. Don't divide totals by a fixed number. Group instead:

| Field | Meaning |
| --- | --- |
| `OperationId` | Shared by every provider call of one top-level operation (one chat request). |
| `CallId` | Unique per provider call. The quota ledger is keyed by it. |
| `ParentOperationId` | Set on a call made through a separate operation on behalf of another one (for example datasource ranking during a dashboard generation, when several datasources are candidates). It names the request that caused it. |
| `Stage`, `Attempt` | The step of a multi-step feature (for example `Plan`, `SingleShot`) and the try within it (1, then 2+ for repairs). `Stage` is null outside such a feature, such as the classifier call; `Attempt` is also null when a stage doesn't number its calls. |
| `ReasoningTokens`, `CachedInputTokens` | Breakdowns reported by the provider, already **included** in `OutputTokens` and `InputTokens` for OpenAI. Shown, never added to totals. |

How the sample computes the figures (`UsageReport.cs`, for one user and one UTC day):

- **Tokens per request** = sum of `InputTokens + OutputTokens` of every call whose
  `ParentOperationId ?? OperationId` is that request.
- **Provider calls per request** = calls that reached the provider (denied calls are listed but not counted).
- **Average per request** = total tokens ÷ requests. **Average per provider call** = total tokens ÷ provider calls.
- **Reported** tokens (what the provider said), **charged** tokens (what the quota counted,
  including the missing-usage policy) and **reservations** (estimates held while a call runs)
  are kept apart. Reservations are never counted as usage. When some calls reported no usage,
  the reported averages are a lower bound; the demo says so.

Which fields you get depends on the provider and the feature: reasoning and cached counts
appear only when the provider reports them; `Stage`/`Attempt` appear only on multi-step
features (dashboard generation); `ParentOperationId` appears only on auxiliary operations.
`SettleAsync` only sees calls that went through the gate, so system calls (such as ranking)
reach your storage through an `IAIUsageSink` unless `RevealAI:Gate:IncludeSystemCalls` is true.

A PostgreSQL version of the same calculation, for a table with one row per `AIUsageRecord`:

```sql
WITH calls AS (
  SELECT COALESCE(parent_operation_id, operation_id) AS request_id, call_id,
         COALESCE(input_tokens, 0) + COALESCE(output_tokens, 0) AS tokens
  FROM ai_usage
  WHERE error_code IS DISTINCT FROM 'denied'                  -- denied calls never reached the provider
    AND NOT (is_system_call AND parent_operation_id IS NULL)  -- background work, not a user request
),
requests AS (
  SELECT request_id, SUM(tokens) AS tokens, COUNT(DISTINCT call_id) AS provider_calls
  FROM calls GROUP BY request_id
)
SELECT COUNT(*)                                  AS requests,
       AVG(tokens)                               AS avg_tokens_per_request,
       SUM(tokens)::numeric / SUM(provider_calls) AS avg_tokens_per_provider_call
FROM requests;
```

**Upgrading from 1.5.213:** the SQLite database is upgraded in place at startup (new columns
are added, existing rows are kept; rows from before the upgrade have no `OperationId` and are
left out of per-request figures). In SDK 1.6 dashboards are always generated by the multi-step
generator, so a request can make more provider calls than in 1.5, and the
`UseDashboardGeneratorV2` setting is ignored.

## Tests

From the project folder:

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
  With SDK 1.6 a response without usage reports `null` counts, but a partial
  usage block can still report a missing count as `0`, so the sample also treats
  zero counts as missing (`TreatZeroTokenCountsAsMissing`).
- **Other SDK calls:** background/system calls bypass the quota gate by default.
  `IAIUsageSink` is best-effort reporting (records can be dropped under load);
  only `SettleAsync` updates balances.
- **A `CallId` can include retries:** the SDK may resend a call inside the same
  `CallId` (for example after the endpoint rejects a request parameter), and the
  OpenAI client has its own transport retries. Usage is reported once per `CallId`.
- **Credentials:** use your own license and key. Never bundle credentials,
  generated databases or logs when sharing this sample.

Public SDK documentation: [Quotas and Billing](https://github.com/RevealBi/Documentation/blob/master/docs/ai/quota-gate.md)
& [Usage Tracking](https://github.com/RevealBi/Documentation/blob/master/docs/ai/usage-tracking.md).
