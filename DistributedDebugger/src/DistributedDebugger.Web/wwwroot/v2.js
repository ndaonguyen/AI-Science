// Log Investigator V2 — deterministic flow.
//
// State model: the browser owns the accumulated set of logs. Every fetch
// (filter or extend) merges its results into that set, deduplicating by
// EventId where present. The server never tracks state — each call is a
// fresh request. This makes the whole flow reload-safe (refresh the page
// and you start clean) and trivial to reason about.

const state = {
  // Map<eventId-or-fallbackKey, LogRecord> — keeps insertion order
  logs: new Map(),
  // Set<key> of rows the user has clicked
  selected: new Set(),
  // Array<EvidenceItem> in insertion order. Each item: { id, kind, title, content }.
  // Sent with the Analyze request alongside logs so the LLM can reason
  // about Mongo / OpenSearch / Kafka / Note alongside the log evidence.
  evidence: [],
  // What kind of evidence the form is currently editing. Null when the
  // form is closed. Set when a 'Add' button is clicked.
  evidenceFormKind: null,
};

// Service list mirrors the V1 ServiceLogGroupResolver.KnownServices. Keep
// in sync if you add services there.
const KNOWN_SERVICES = [
  'authoring-service',
  'content-media-service',
  'content-search-service',
  'ai-content-authoring',
  'authentication',
  'class-management',
  'core-entities-content',
  'core-entities-authoring',
  'graphql-gateway-fusion',
  'learning-pathways-backend',
  'queues-app',
  'web-app',
];

// ---- DOM ----
const $ = id => document.getElementById(id);
const $services       = $('services');
const $environment    = $('environment');
const $filterText     = $('filterText');
const $startTime      = $('startTime');
const $endTime        = $('endTime');
const $filterBtn      = $('filterBtn');
const $extendCustom   = $('extendCustom');
const $extendBtn      = $('extendBtn');
const $analyzeBtn     = $('analyzeBtn');
const $clearBtn       = $('clearBtn');
const $logTbody       = $('logTbody');
const $logCount       = $('logCount');
const $enforcedFilter = $('enforcedFilter');
const $status         = $('status');
const $analysisCard   = $('analysisCard');
const $analysisBody   = $('analysisBody');
const $analysisCost   = $('analysisCost');

const $evidenceCount        = $('evidenceCount');
const $evidenceList         = $('evidenceList');
const $evidenceForm         = $('evidenceForm');
const $evidenceFormHeading  = $('evidenceFormHeading');
const $evidenceFormTitle    = $('evidenceFormTitle');
const $evidenceFormCommand  = $('evidenceFormCommand');
const $evidenceFormCommandLabel = $('evidenceFormCommandLabel');
const $evidenceFormCommandText  = $('evidenceFormCommandText');
const $evidenceFormContent  = $('evidenceFormContent');
const $evidenceFormSave     = $('evidenceFormSave');
const $evidenceFormCancel   = $('evidenceFormCancel');

// ---- init ----
(function init() {
  // Build services checkboxes (default: authoring-service checked, like V1)
  for (const svc of KNOWN_SERVICES) {
    const id = `svc-${svc}`;
    const wrap = document.createElement('label');
    wrap.className = 'checkbox-item';
    wrap.innerHTML =
      `<input type="checkbox" name="service" value="${svc}" id="${id}"`
      + (svc === 'authoring-service' ? ' checked' : '') + `>`
      + `<span>${svc}</span>`;
    $services.appendChild(wrap);
  }

  // Default time range = last 1 hour
  setRangeFromPreset('1h');

  document.querySelectorAll('.quick-ranges .chip').forEach(b =>
    b.addEventListener('click', () => setRangeFromPreset(b.dataset.range)));

  // Extend-window chips: clicking a chip clears the custom input and
  // marks the chip as active. Typing in the custom input clears any
  // active chip. resolveExtendMinutes() picks whichever the user used
  // most recently. Default state: ±1 active, custom empty.
  document.querySelectorAll('.extend-presets .chip').forEach(b =>
    b.addEventListener('click', () => {
      document.querySelectorAll('.extend-presets .chip').forEach(c => c.classList.remove('active'));
      b.classList.add('active');
      $extendCustom.value = '';
    }));
  $extendCustom.addEventListener('input', () => {
    if ($extendCustom.value !== '') {
      document.querySelectorAll('.extend-presets .chip').forEach(c => c.classList.remove('active'));
    }
  });

  $filterBtn.addEventListener('click', onFilter);
  $extendBtn.addEventListener('click', onExtend);
  $analyzeBtn.addEventListener('click', onAnalyze);
  $clearBtn.addEventListener('click', onClear);

  // 'Clear selection' link in the Logs card header. Only deselects
  // rows; the gathered log set is unaffected. Keeps the user in flow when
  // they want to start a fresh extension pivot without losing the logs
  // they've already gathered.
  document.getElementById('clearSelectionLink').addEventListener('click', e => {
    e.preventDefault();
    onClearSelection();
  });

  // Evidence-add buttons. Each one sets the active kind on state and opens
  // the form with the appropriate heading. We don't have separate forms
  // per kind because the inputs are identical — only the label changes.
  document.querySelectorAll('[data-evidence-kind]').forEach(b =>
    b.addEventListener('click', () => openEvidenceForm(b.dataset.evidenceKind)));
  $evidenceFormSave.addEventListener('click', saveEvidenceFromForm);
  $evidenceFormCancel.addEventListener('click', closeEvidenceForm);
})();

// ---- helpers ----
function setRangeFromPreset(p) {
  const now = new Date();
  const map = { '15m': 15, '1h': 60, '6h': 360, '24h': 1440 };
  const minutes = map[p] ?? 60;
  const start = new Date(now.getTime() - minutes * 60_000);
  // datetime-local needs YYYY-MM-DDTHH:MM:SS in *local* time, but we want
  // UTC. Browsers don't expose UTC datetime-local natively, so we render
  // local time and treat it as UTC at submit time. Document this in the
  // UI's helper text someday — for now the field labels say "(UTC)".
  $startTime.value = toLocalInput(start);
  $endTime.value = toLocalInput(now);
}
function toLocalInput(d) {
  const pad = n => String(n).padStart(2, '0');
  // We treat the picker value as UTC at submit time, so emit UTC components.
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}` +
         `T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
}
function fromLocalInput(v) {
  // Treat value as UTC. Browsers parse as local; we strip and re-create.
  // Empty value → null (server defaults to last hour).
  if (!v) return null;
  return v + 'Z';
}
function selectedServices() {
  return Array.from(document.querySelectorAll('input[name=service]:checked')).map(c => c.value);
}

/// Pick the active extend-window value: custom input wins when it's a
/// non-negative number; otherwise the active preset chip's data-mins.
/// Falls back to ±1 if somehow nothing is set, so Extend can never send
/// NaN to the server.
function resolveExtendMinutes() {
  const customRaw = $extendCustom.value.trim();
  if (customRaw !== '') {
    const n = Number(customRaw);
    if (Number.isFinite(n) && n >= 0) return n;
  }
  const active = document.querySelector('.extend-presets .chip.active');
  if (active?.dataset.mins !== undefined) {
    const n = Number(active.dataset.mins);
    if (Number.isFinite(n) && n >= 0) return n;
  }
  return 1;
}
function setStatus(text, kind) {
  if (!text) { $status.classList.add('hidden'); return; }
  $status.textContent = text;
  $status.className = `status ${kind || ''}`;
  $status.classList.remove('hidden');
}
function recordKey(log) {
  // Prefer EventId (CloudWatch's stable per-event id); fall back to a
  // composite key for cases where it's missing (shouldn't happen with
  // FilterLogEvents, but be defensive).
  return log.eventId || `${log.timestamp}|${log.service}|${log.message?.slice(0, 80)}`;
}

// Parse a fetch Response into JSON, gracefully handling cases where the
// server returned non-JSON (or an empty body) — e.g. an unhandled 500. The
// browser's default Response.json() throws "Unexpected end of JSON input",
// which is much less actionable than "HTTP 500: <body text>".
async function safeJson(res) {
  const text = await res.text();
  if (!text) {
    throw new Error(`HTTP ${res.status} with empty body — check the server console for the stack trace`);
  }
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`HTTP ${res.status}: ${text.slice(0, 300)}`);
  }
}

// ---- actions ----
async function onFilter() {
  const services = selectedServices();
  if (services.length === 0) { setStatus('Pick at least one service.', 'error'); return; }

  $filterBtn.disabled = true;
  setStatus('Fetching logs…', 'info');
  try {
    const body = {
      services,
      environment: $environment.value,
      filterText: $filterText.value,
      startTime: fromLocalInput($startTime.value),
      endTime:   fromLocalInput($endTime.value),
      limit: 500,
    };
    const res = await fetch('/api/v2/logs/filter', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await safeJson(res);
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);

    mergeLogs(data.logs || []);
    $enforcedFilter.textContent = data.appliedFilter
      ? `filterPattern: ${data.appliedFilter}` : '(no filter)';
    setStatus(`Fetched ${data.logs?.length ?? 0} logs.`, 'success');
  } catch (err) {
    setStatus(`Filter failed: ${err.message}`, 'error');
  } finally {
    $filterBtn.disabled = false;
  }
}

async function onExtend() {
  if (state.selected.size === 0) {
    setStatus('Select at least one row first (click a row).', 'error');
    return;
  }
  $extendBtn.disabled = true;
  setStatus('Extending around selected rows…', 'info');

  const services = selectedServices();
  const windowMinutes = resolveExtendMinutes();
  let added = 0;
  try {
    // One extend call per selected row. Fast in practice; CloudWatch
    // caps each call to a few hundred ms.
    for (const key of state.selected) {
      const pivot = state.logs.get(key);
      if (!pivot) continue;
      const res = await fetch('/api/v2/logs/extend', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          services,
          environment: $environment.value,
          around: pivot.timestamp,
          windowMinutes,
          limit: 500,
        }),
      });
      const data = await safeJson(res);
      if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
      added += mergeLogs(data.logs || []);
    }
    setStatus(`Added ${added} new logs.`, 'success');
  } catch (err) {
    setStatus(`Extend failed: ${err.message}`, 'error');
  } finally {
    $extendBtn.disabled = false;
    updateActionState();
  }
}

async function onAnalyze() {
  const description = $('description').value.trim();
  if (!description) { setStatus('Add a bug description first.', 'error'); return; }
  if (state.logs.size === 0) { setStatus('Fetch some logs first.', 'error'); return; }

  // Send ALL gathered logs to the analyzer. Selection is for Extend
  // (pivot rows), not for narrowing analysis — the user gathers a
  // curated set via filter+extend, then analyzes that whole set.
  const logsToAnalyze = Array.from(state.logs.values());

  $analyzeBtn.disabled = true;
  setStatus(`Analyzing ${logsToAnalyze.length} log${logsToAnalyze.length === 1 ? '' : 's'}…`, 'info');
  $analysisCard.classList.remove('hidden');
  $analysisBody.innerHTML = '<p class="hint">Thinking…</p>';

  try {
    const res = await fetch('/api/v2/logs/analyze', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        description,
        ticketId: $('ticketId').value || null,
        logs: logsToAnalyze,
        // Strip the local id field — the server doesn't need or expect it.
        evidence: state.evidence.map(({ kind, title, command, content }) =>
          ({ kind, title, command, content })),
      }),
    });
    const data = await safeJson(res);
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
    renderAnalysis(data);
    $analysisCost.textContent = `tokens: ${data.inputTokens} in / ${data.outputTokens} out`;
    setStatus('Analysis complete.', 'success');
  } catch (err) {
    setStatus(`Analyze failed: ${err.message}`, 'error');
    $analysisBody.innerHTML = `<p class="error">${err.message}</p>`;
  } finally {
    $analyzeBtn.disabled = false;
  }
}

function onClear() {
  state.logs.clear();
  state.selected.clear();
  renderTable();
  refreshSelectedHeader();
  $analysisCard.classList.add('hidden');
  setStatus('Cleared.', 'info');
}

// ---- evidence ----

const EVIDENCE_LABELS = {
  mongo:      'Mongo document',
  opensearch: 'OpenSearch result',
  kafka:      'Kafka event',
  note:       'Note',
};

const EVIDENCE_PLACEHOLDERS = {
  mongo:      'e.g. ComponentBlockModel — _id 67abcd',
  opensearch: 'e.g. activities index — query {match_all}',
  kafka:      'e.g. content-indexed topic — partition 3 offset 12345',
  note:       'e.g. observed in production around 09:09 UTC',
};

// Per-kind labels and placeholders for the Command field. The Command
// captures HOW the user got the result (the query / shell command run),
// which lets the LLM reason about what was being asked, not just what
// came back. Notes have no command — it's free-form text.
const EVIDENCE_COMMAND_LABELS = {
  mongo:      'Mongo shell command',
  opensearch: 'OpenSearch query DSL',
  kafka:      'Kafka consumer command',
};
const EVIDENCE_COMMAND_PLACEHOLDERS = {
  mongo:      "e.g. db.activities.findOne({_id: ObjectId('67abcd...')})",
  opensearch: 'e.g. GET /activities/_search\n{ "query": { "term": { "_id": "act-789" } } }',
  kafka:      'e.g. kafka-console-consumer --topic content-indexed --partition 3 --offset 12345 --max-messages 1',
};

function openEvidenceForm(kind) {
  state.evidenceFormKind = kind;
  $evidenceFormHeading.textContent = `Add ${EVIDENCE_LABELS[kind] ?? 'evidence'}`;
  $evidenceFormTitle.value = '';
  $evidenceFormTitle.placeholder = EVIDENCE_PLACEHOLDERS[kind] ?? '';
  $evidenceFormCommand.value = '';

  // Notes are free-form (no query that produced them) so the Command
  // field is hidden entirely. For the three real kinds, label and
  // placeholder swap based on what makes sense for that ecosystem.
  if (kind === 'note') {
    $evidenceFormCommandLabel.classList.add('hidden');
  } else {
    $evidenceFormCommandLabel.classList.remove('hidden');
    $evidenceFormCommandText.textContent = EVIDENCE_COMMAND_LABELS[kind] ?? 'Command';
    $evidenceFormCommand.placeholder = EVIDENCE_COMMAND_PLACEHOLDERS[kind] ?? '';
  }

  $evidenceFormContent.value = '';
  $evidenceForm.classList.remove('hidden');
  // Auto-focus title so the user can start typing immediately.
  $evidenceFormTitle.focus();
}

function closeEvidenceForm() {
  state.evidenceFormKind = null;
  $evidenceForm.classList.add('hidden');
}

function saveEvidenceFromForm() {
  const kind = state.evidenceFormKind ?? 'note';
  const content = $evidenceFormContent.value.trim();
  const title = $evidenceFormTitle.value.trim();
  const command = kind === 'note' ? '' : $evidenceFormCommand.value.trim();
  if (!content) {
    setStatus('Evidence content is required.', 'error');
    return;
  }
  state.evidence.push({
    // Random ID — collision-free enough for an in-memory list a human is
    // managing by hand. Used as the dataset key for delete-button wiring.
    id: `ev-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
    kind,
    title,
    command,
    content,
  });
  closeEvidenceForm();
  renderEvidence();
  setStatus(`Added ${EVIDENCE_LABELS[kind] ?? 'evidence'}.`, 'success');
}

function deleteEvidence(id) {
  state.evidence = state.evidence.filter(e => e.id !== id);
  renderEvidence();
}

function renderEvidence() {
  $evidenceCount.textContent = state.evidence.length;
  $evidenceList.innerHTML = '';
  if (state.evidence.length === 0) return;

  const frag = document.createDocumentFragment();
  for (const item of state.evidence) {
    const card = document.createElement('div');
    card.className = `evidence-card evidence-${item.kind}`;
    const commandHtml = item.command
      ? `<pre class="evidence-command mono">$ ${escapeHtml(truncate(item.command, 600))}</pre>`
      : '';
    card.innerHTML =
      `<div class="evidence-card-header">` +
        `<span class="evidence-kind">${escapeHtml(EVIDENCE_LABELS[item.kind] ?? item.kind)}</span>` +
        (item.title ? `<span class="evidence-title">${escapeHtml(item.title)}</span>` : '') +
        `<button type="button" class="evidence-delete" data-id="${escapeHtml(item.id)}" title="Remove this evidence">✕</button>` +
      `</div>` +
      commandHtml +
      `<pre class="evidence-content mono">${escapeHtml(truncate(item.content, 1500))}</pre>`;
    frag.appendChild(card);
  }
  $evidenceList.appendChild(frag);

  // Wire delete buttons after the DOM update so the data-id matches.
  $evidenceList.querySelectorAll('.evidence-delete').forEach(b =>
    b.addEventListener('click', () => deleteEvidence(b.dataset.id)));
}

function onClearSelection() {
  if (state.selected.size === 0) return;
  // Remove the visual selected state from every row without re-rendering
  // the whole table. `Set.clear` first so toggleSelected-style targeted
  // updates can read state.selected as already-empty.
  const previouslySelected = Array.from(state.selected);
  state.selected.clear();
  for (const key of previouslySelected) {
    const tr = $logTbody.querySelector(`tr[data-key="${cssEscape(key)}"]`);
    if (tr) {
      tr.classList.remove('selected');
      const pick = tr.querySelector('.col-pick');
      if (pick) pick.textContent = '○';
    }
  }
  refreshSelectedHeader();
  updateActionState();
  setStatus('Selection cleared.', 'info');
}

// ---- log table ----
function mergeLogs(arr) {
  let added = 0;
  for (const log of arr) {
    const key = recordKey(log);
    if (!state.logs.has(key)) {
      state.logs.set(key, log);
      added++;
    }
  }
  // Re-sort by timestamp on every merge — keeps the visible table stable
  // regardless of fetch order across multiple filter / extend calls.
  const sorted = [...state.logs.entries()].sort((a, b) =>
    new Date(a[1].timestamp) - new Date(b[1].timestamp));
  state.logs = new Map(sorted);
  renderTable();
  return added;
}

function renderTable() {
  $logCount.textContent = state.logs.size;
  if (state.logs.size === 0) {
    $logTbody.innerHTML = `<tr class="empty"><td colspan="4">No logs yet — filter to begin.</td></tr>`;
    updateActionState();
    return;
  }
  // Build via DocumentFragment to avoid layout thrash for big result sets.
  const frag = document.createDocumentFragment();
  for (const [key, log] of state.logs) {
    const tr = document.createElement('tr');
    tr.dataset.key = key;
    if (state.selected.has(key)) tr.classList.add('selected');
    tr.innerHTML =
      `<td class="col-pick">${state.selected.has(key) ? '●' : '○'}</td>` +
      `<td class="col-ts mono">${formatTs(log.timestamp)}</td>` +
      `<td class="col-svc mono">${escapeHtml(log.service)}</td>` +
      `<td class="col-msg mono">${escapeHtml(truncate(extractDisplayMessage(log.message), 480))}</td>`;
    tr.addEventListener('click', () => toggleSelected(key));
    frag.appendChild(tr);
  }
  $logTbody.innerHTML = '';
  $logTbody.appendChild(frag);
  updateActionState();
}

function toggleSelected(key) {
  if (state.selected.has(key)) state.selected.delete(key);
  else state.selected.add(key);
  // Targeted update — avoids re-rendering the whole table on every click.
  const tr = $logTbody.querySelector(`tr[data-key="${cssEscape(key)}"]`);
  if (tr) {
    tr.classList.toggle('selected', state.selected.has(key));
    tr.querySelector('.col-pick').textContent = state.selected.has(key) ? '●' : '○';
  }
  refreshSelectedHeader();
  updateActionState();
}

/// Update the 'Selected (N)' heading and toggle the Clear-selection link
/// visibility based on the current selected set. Centralised because three
/// different code paths (toggle, clear-selection, clear-all) need to keep
/// these in sync, and a stale heading was easy to introduce.
function refreshSelectedHeader() {
  const heading = document.getElementById('selectedHeading');
  if (heading) heading.textContent = `Selected (${state.selected.size})`;
  const link = document.getElementById('clearSelectionLink');
  if (link) link.classList.toggle('hidden', state.selected.size === 0);
}

function updateActionState() {
  $extendBtn.disabled = state.selected.size === 0;
  $analyzeBtn.disabled = state.logs.size === 0;
  // Button label simply reflects how many logs will be analyzed.
  $analyzeBtn.textContent = state.logs.size === 0
    ? 'Analyze gathered logs'
    : `Analyze ${state.logs.size} log${state.logs.size === 1 ? '' : 's'}`;
}

// ---- analysis render ----
function renderAnalysis(a) {
  const html = [];
  html.push(`<h3>Summary</h3><p>${escapeHtml(a.summary || '(empty)')}</p>`);
  if (a.suspicious?.length) {
    html.push('<h3>Suspicious lines</h3><ul class="suspicious">');
    for (const s of a.suspicious) html.push(`<li class="mono">${escapeHtml(s)}</li>`);
    html.push('</ul>');
  }
  html.push(`<h3>Hypothesis</h3><p>${escapeHtml(a.hypothesis || '(unclear)')}</p>`);
  if (a.suggestedFollowups?.length) {
    html.push('<h3>Suggested followups</h3><ul>');
    for (const f of a.suggestedFollowups) html.push(`<li>${escapeHtml(f)}</li>`);
    html.push('</ul>');
  }
  $analysisBody.innerHTML = html.join('');
}

// ---- formatting ----
function formatTs(iso) {
  // Server returns ISO strings. Show with millisecond precision so the
  // ordering with same-second logs is unambiguous.
  const d = new Date(iso);
  const pad = (n, w = 2) => String(n).padStart(w, '0');
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ` +
         `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}.${pad(d.getUTCMilliseconds(), 3)}`;
}
function truncate(s, n) {
  if (!s) return '';
  return s.length > n ? s.slice(0, n) + '…' : s;
}

/// EP services wrap every log line in a JSON envelope like:
///   {"container_id":"…","log":"  Unexpected Execution Error at /…",
///    "container_name":"authoring-service","ecs_cluster":"core", …}
/// Showing the whole envelope makes the table unreadable — the actual
/// log message is buried after a wall of metadata. Try to extract the
/// inner `log` field; on any parse failure, fall back to the raw message
/// so we never silently lose information.
function extractDisplayMessage(raw) {
  if (!raw) return '';
  const trimmed = raw.trim();
  if (trimmed[0] !== '{') return raw;
  try {
    const obj = JSON.parse(trimmed);
    if (typeof obj?.log === 'string' && obj.log.length > 0) return obj.log.trim();
    if (typeof obj?.message === 'string' && obj.message.length > 0) return obj.message.trim();
    return raw;
  } catch {
    return raw;
  }
}
function escapeHtml(s) {
  return String(s ?? '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}
function cssEscape(s) {
  // Polyfill-ish: CSS.escape isn't always available; for our keys (eventIds
  // or composite strings) the safe move is to escape backslashes and quotes.
  if (window.CSS && window.CSS.escape) return CSS.escape(s);
  return String(s).replace(/[\\"]/g, '\\$&');
}
