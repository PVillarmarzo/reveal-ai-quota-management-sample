// Reveal AI Quota Demo (development only).
// Talks only to this server: the Reveal AI chat endpoint and the sample's quota endpoints.
// Every piece of user or model text is inserted with textContent / text nodes, never as HTML.
"use strict";

const DATASOURCE_ID = "SampleExcel";
const USERS = { alice: "Alice", bob: "Bob" };

const state = {
  user: "alice",
  busy: false,
  transcripts: { alice: [], bob: [] }, // in-memory, per user, for this page only
};

const $ = (id) => document.getElementById(id);
const fmt = new Intl.NumberFormat();
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
}

// Renders **bold** segments as <strong> using text nodes only; everything else stays plain text.
function richText(text) {
  const fragment = document.createDocumentFragment();
  const parts = String(text ?? "").split(/\*\*(.+?)\*\*/g);
  parts.forEach((part, i) => {
    fragment.appendChild(i % 2 === 1 ? el("strong", null, part) : document.createTextNode(part));
  });
  return fragment;
}

function headers(extra) {
  return Object.assign({ "X-User-Id": state.user }, extra || {});
}

async function readJson(response) {
  try { return await response.json(); } catch { return null; }
}

// ---- Usage panel ---------------------------------------------------------------------------

async function refreshUsage() {
  const user = state.user;
  try {
    const [meResponse, callsResponse] = await Promise.all([
      fetch("/quota/me", { headers: headers(), cache: "no-store" }),
      fetch(`/dev/quota/${encodeURIComponent(user)}/calls?limit=12`, { headers: headers(), cache: "no-store" }),
    ]);
    if (user !== state.user) return; // the user switched while loading
    if (!meResponse.ok) {
      renderUsageError(meResponse.status === 401
        ? "This server does not recognize the demo user. The demo identity only works in Development, from this machine."
        : `Could not load usage (HTTP ${meResponse.status}).`);
      return;
    }
    renderUsage(await meResponse.json());
    renderCalls(callsResponse.ok ? await callsResponse.json() : []);
  } catch {
    renderUsageError("Could not reach the server.");
  }
}

function renderUsage(u) {
  const limit = u.dailyTokenLimit || 0;
  const charged = u.chargedTokens || 0;
  const reserved = u.pendingReservedTokens || 0;
  const chargedPct = limit ? Math.min(100, (charged / limit) * 100) : 0;
  const reservedPct = limit ? Math.min(100 - chargedPct, (reserved / limit) * 100) : 0;
  const usedPct = limit ? ((charged + reserved) / limit) * 100 : 0;

  $("meter-charged").style.width = `${chargedPct}%`;
  $("meter-reserved").style.width = `${reservedPct}%`;
  const meter = $("meter-charged").parentElement;
  meter.classList.toggle("warn", usedPct >= 75 && usedPct < 100);
  meter.classList.toggle("full", usedPct >= 100);

  $("usage-percent").textContent = `${usedPct.toFixed(usedPct < 10 ? 1 : 0)}%`;
  $("usage-day").textContent = `${u.dayUtc} (UTC)`;
  $("stat-limit").textContent = fmt.format(limit);
  $("stat-remaining").textContent = fmt.format(u.remainingTokens);
  $("stat-input").textContent = fmt.format(u.inputTokens);
  $("stat-output").textContent = fmt.format(u.outputTokens);
  $("stat-charged").textContent = fmt.format(charged);
  $("stat-reserved").textContent = fmt.format(reserved);

  const resets = new Date(u.resetsAtUtc);
  const notes = [`${fmt.format(u.calls)} AI call(s) today. Resets ${resets.toLocaleString()}.`];
  if (u.callsWithMissingUsage > 0) {
    notes.push(`${u.callsWithMissingUsage} call(s) had missing or partial token counts and were charged by the configured policy.`);
  }
  $("usage-note").textContent = notes.join(" ");
}

function renderUsageError(message) {
  for (const id of ["stat-limit", "stat-remaining", "stat-input", "stat-output", "stat-charged", "stat-reserved"]) {
    $(id).textContent = "-";
  }
  $("usage-percent").textContent = "-";
  $("usage-note").textContent = message;
  renderCalls([]);
}

function renderCalls(calls) {
  const body = $("calls-body");
  body.replaceChildren();
  $("calls-empty").hidden = calls.length > 0;
  for (const call of calls) {
    const row = document.createElement("tr");
    row.appendChild(el("td", null, timeFmt.format(new Date(call.createdUtc))));
    row.appendChild(el("td", null, call.intent ?? "-"));
    row.appendChild(el("td", null, call.model ?? "-"));
    row.appendChild(el("td", "num", call.inputTokens === null ? "n/a" : fmt.format(call.inputTokens)));
    row.appendChild(el("td", "num", call.outputTokens === null ? "n/a" : fmt.format(call.outputTokens)));
    row.appendChild(el("td", "num", call.status === "reserved" ? `(${fmt.format(call.reservedTokens)})` : fmt.format(call.chargedTokens)));
    const result = el("td");
    result.appendChild(resultPill(call));
    row.appendChild(result);
    body.appendChild(row);
  }
}

function resultPill(call) {
  if (call.status === "reserved") return el("span", "pill warn", "in flight");
  if (call.succeeded === false) return el("span", "pill bad", call.errorCode || "failed");
  if (call.usageMissing) return el("span", "pill warn", "usage missing");
  return el("span", "pill ok", "ok");
}

// ---- Chat ----------------------------------------------------------------------------------

function addMessage(entry, remember = true) {
  if (remember) state.transcripts[state.user].push(entry);
  $("empty-state").hidden = true;
  const box = el("div", `msg ${entry.kind}`);

  if (entry.title) box.appendChild(el("span", "title", entry.title));
  box.appendChild(entry.kind === "ai" ? richText(entry.text) : document.createTextNode(entry.text ?? ""));

  if (entry.dashboard) box.appendChild(dashboardSummary(entry.dashboard));
  if (entry.meta) box.appendChild(el("span", "meta", entry.meta));
  if (entry.details) {
    const details = el("details");
    details.appendChild(el("summary", null, "Details"));
    details.appendChild(el("div", null, entry.details));
    box.appendChild(details);
  }

  $("messages").appendChild(box);
  box.scrollIntoView({ block: "end", behavior: "smooth" });
  return box;
}

function dashboardSummary(summary) {
  const box = el("div", "dashboard-summary");
  if (!summary.widgets.length) {
    box.appendChild(el("strong", null, `Dashboard returned: ${summary.title || "Untitled"}`));
    box.appendChild(el("div", "muted small", "It contains no visualizations."));
    return box;
  }
  box.appendChild(el("strong", null, `Dashboard generated: ${summary.title || "Untitled"}`));
  if (summary.outcome) box.appendChild(el("span", "muted small", `  (outcome: ${summary.outcome})`));
  if (summary.widgets.length) {
    const list = el("ul");
    for (const title of summary.widgets) list.appendChild(el("li", null, title));
    box.appendChild(list);
  }
  box.appendChild(el("div", "muted small",
    "The full dashboard JSON is in the response; render it with a RevealView in your own app."));
  return box;
}

// Reads the title and visualization titles from the RDASH JSON the SDK returned.
function summarizeDashboard(json, outcome) {
  if (!json) return null;
  try {
    const dashboard = JSON.parse(json);
    const widgets = Array.isArray(dashboard.Widgets) ? dashboard.Widgets : [];
    return {
      title: typeof dashboard.Title === "string" ? dashboard.Title : "",
      widgets: widgets.map((w, i) => (typeof w.Title === "string" && w.Title) ? w.Title : `Visualization ${i + 1}`),
      outcome: typeof outcome === "string" ? outcome : null,
    };
  } catch {
    return null;
  }
}

function usageMeta(usage) {
  if (!usage) return "Reveal AI did not report token usage for this request.";
  const input = usage.inputTokens ?? null;
  const output = usage.outputTokens ?? null;
  const part = (v) => (v === null ? "n/a" : fmt.format(v));
  return `This request: ${part(input)} input + ${part(output)} output tokens, as reported by Reveal AI.`;
}

async function sendMessage(text) {
  if (state.busy) return;
  const user = state.user;
  setBusy(true);
  addMessage({ kind: "user", text });
  const pending = addMessage({ kind: "ai pending", text: "Reveal AI is working" }, false);
  pending.classList.add("dots");

  let entry;
  try {
    const response = await fetch("/api/reveal/ai/chat", {
      method: "POST",
      headers: headers({ "Content-Type": "application/json" }),
      body: JSON.stringify({ message: text, datasourceId: DATASOURCE_ID }),
    });
    const body = await readJson(response);
    entry = toEntry(response.status, body);
  } catch {
    entry = { kind: "error", title: "Network error", text: "The server could not be reached. Is the sample still running?" };
  }

  pending.remove();
  if (user === state.user) {
    addMessage(entry);
  } else {
    state.transcripts[user].push(entry);
  }
  setBusy(false);
  await refreshUsage();
}

function toEntry(status, body) {
  if (status === 200 && body) {
    return {
      kind: "ai",
      text: body.explanation || "Reveal AI returned no explanation text.",
      dashboard: summarizeDashboard(body.dashboard, body.dashboardGenerationOutcome),
      meta: usageMeta(body.usage),
    };
  }

  const detail = body && (body.detail || body.error || body.title);
  if (status === 429) {
    const retry = body && Number.isFinite(body.retryAfterSeconds) ? body.retryAfterSeconds : null;
    const when = retry !== null ? ` Try again in about ${formatDuration(retry)}.` : "";
    return {
      kind: "quota",
      title: "Daily token limit reached",
      text: `${detail || "The quota gate denied this request."}${when}`,
      meta: "The gate blocked the call before it reached the AI provider. Use \"Reset today's usage\" to continue the demo.",
    };
  }
  if (status === 403) {
    return {
      kind: "error",
      title: "Not allowed",
      text: detail || "This user is not allowed to use AI features.",
      meta: "The demo identity is only accepted in Development, from this machine.",
    };
  }
  if (status === 400 || status === 500) {
    return {
      kind: "error",
      title: "Reveal AI could not process the request",
      text: "The request failed. Check the server console for the full error.",
      details: typeof detail === "string" ? detail.slice(0, 400) : undefined,
    };
  }
  return { kind: "error", title: `Unexpected response (HTTP ${status})`, text: "Check the server console for details." };
}

function formatDuration(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.ceil((seconds % 3600) / 60);
  return h > 0 ? `${h} h ${m} min` : `${m} min`;
}

function setBusy(busy) {
  state.busy = busy;
  $("send-btn").disabled = busy;
  $("prompt").disabled = busy;
  $("new-chat-btn").disabled = busy;
  $("reset-btn").disabled = busy;
}

function renderTranscript() {
  const messages = $("messages");
  messages.querySelectorAll(".msg").forEach((m) => m.remove());
  const transcript = state.transcripts[state.user];
  $("empty-state").hidden = transcript.length > 0;
  transcript.forEach((entry) => addMessage(entry, false));
}

// ---- Actions -------------------------------------------------------------------------------

function selectUser(user) {
  if (!(user in USERS) || state.busy) return;
  state.user = user;
  document.querySelectorAll(".user-option").forEach((b) =>
    b.setAttribute("aria-checked", String(b.dataset.user === user)));
  renderTranscript();
  refreshUsage();
}

async function resetUsage() {
  if (!confirm(`Reset today's AI token usage for ${USERS[state.user]}? (development only)`)) return;
  setBusy(true);
  try {
    const response = await fetch(`/dev/quota/${encodeURIComponent(state.user)}/reset`, {
      method: "POST",
      headers: headers({ "X-Demo-Request": "1" }),
    });
    if (!response.ok) {
      addMessage({ kind: "error", title: "Reset failed", text: `The server answered HTTP ${response.status}.` }, false);
    }
  } finally {
    setBusy(false);
    await refreshUsage();
  }
}

async function newConversation() {
  setBusy(true);
  try {
    // Clears this user's server-side chat history in Reveal AI.
    await fetch("/api/reveal/ai/chat/session", { method: "DELETE", headers: headers() });
  } catch {
    // The local transcript is cleared either way.
  } finally {
    state.transcripts[state.user] = [];
    renderTranscript();
    setBusy(false);
  }
}

document.addEventListener("DOMContentLoaded", () => {
  document.querySelectorAll(".user-option").forEach((b) =>
    b.addEventListener("click", () => selectUser(b.dataset.user)));
  document.querySelectorAll(".chip").forEach((chip) =>
    chip.addEventListener("click", () => { $("prompt").value = chip.textContent; $("prompt").focus(); }));

  $("composer").addEventListener("submit", (e) => {
    e.preventDefault();
    const text = $("prompt").value.trim();
    if (!text) return;
    $("prompt").value = "";
    sendMessage(text);
  });
  $("prompt").addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      $("composer").requestSubmit();
    }
  });
  $("refresh-btn").addEventListener("click", refreshUsage);
  $("reset-btn").addEventListener("click", resetUsage);
  $("new-chat-btn").addEventListener("click", newConversation);

  refreshUsage();
});
