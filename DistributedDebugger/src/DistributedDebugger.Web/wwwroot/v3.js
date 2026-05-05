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
  // Most recently clicked row key — used as the pivot for shift-click range
  // selection. Null when the table is fresh or after clear-all. Updated by
  // every plain (non-shift) click so 'extend the selection from here'
  // always means 'from the last thing I touched'.
  lastSelectedKey: null,
  // Array<EvidenceItem> in insertion order. Each item: { id, kind, title, content }.
  // Sent with the Analyze request alongside logs so the LLM can reason
  // about Mongo / OpenSearch / Kafka / Note alongside the log evidence.
  evidence: [],
  // What kind of evidence the form is currently editing. Null when the
  // form is closed. Set when a 'Add' button is clicked.
  evidenceFormKind: null,
  // The most recent analysis returned by /analyze, plus the inputs that
  // produced it. Stashed so the 'Suggest queries' follow-up can send the
  // analysis back to the server without re-typing the bug context. Null
  // until first analyze completes; reset when a new analyze starts so an
  // in-progress request can't poison stale state.
  lastAnalysis: null,
  lastAnalysisContext: null,
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
  // Suggest-queries follow-up. The button is hidden until an analysis
  // exists; this handler is wired once at init and remains live.
  document.getElementById('suggestQueriesBtn').addEventListener('click', onSuggestQueries);

  // 'Clear selection' link in the Logs card header. Only deselects
  // rows; the gathered log set is unaffected. Keeps the user in flow when
  // they want to start a fresh extension pivot without losing the logs
  // they've already gathered.
  document.getElementById('clearSelectionLink').addEventListener('click', e => {
    e.preventDefault();
    onClearSelection();
  });
  document.getElementById('selectAllLink').addEventListener('click', e => {
    e.preventDefault();
    selectAll();
  });
  // Range-select control: two number inputs + Select button. The button
  // is disabled until both inputs parse to positive integers — keeps
  // accidental clicks on an empty form from doing anything weird.
  const $rangeFrom = document.getElementById('rangeFrom');
  const $rangeTo   = document.getElementById('rangeTo');
  const $rangeBtn  = document.getElementById('rangeSelectBtn');
  function refreshRangeBtnEnabled() {
    const f = parseInt($rangeFrom.value, 10);
    const t = parseInt($rangeTo.value, 10);
    $rangeBtn.disabled = !(Number.isFinite(f) && f >= 1 && Number.isFinite(t) && t >= 1);
  }
  $rangeFrom.addEventListener('input', refreshRangeBtnEnabled);
  $rangeTo.addEventListener('input',   refreshRangeBtnEnabled);
  // Enter in either input fires the button — saves a tab + click. Only when
  // the button is enabled, of course (otherwise we'd run with NaN inputs).
  function rangeEnterHandler(ev) {
    if (ev.key === 'Enter' && !$rangeBtn.disabled) {
      ev.preventDefault();
      $rangeBtn.click();
    }
  }
  $rangeFrom.addEventListener('keydown', rangeEnterHandler);
  $rangeTo.addEventListener('keydown',   rangeEnterHandler);
  $rangeBtn.addEventListener('click', () => {
    const f = parseInt($rangeFrom.value, 10);
    const t = parseInt($rangeTo.value, 10);
    selectByIndexRange(f, t);
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
  const map = { '2m': 2, '3m': 3, '5m': 5, '10m': 10, '15m': 15, '1h': 60, '6h': 360, '24h': 1440 };
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
    const res = await fetch('/api/v3/logs/filter', {
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
      const res = await fetch('/api/v3/logs/extend', {
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

  // Send the user-selected subset when a selection exists; otherwise
  // send the whole gathered table. Selection has two roles in this UI —
  // 'pivots for Extend' AND 'rows to analyse' — and selection-narrowing
  // for Analyze is opt-in: if you don't pick anything, you get the
  // original whole-table behaviour. analyzeTargets() encapsulates the
  // decision so the button label and the request payload can't drift.
  const logsToAnalyze = analyzeTargets();

  $analyzeBtn.disabled = true;
  setStatus(`Analyzing ${logsToAnalyze.length} log${logsToAnalyze.length === 1 ? '' : 's'}…`, 'info');
  $analysisCard.classList.remove('hidden');
  $analysisBody.innerHTML = '<p class="hint">Thinking…</p>';

  try {
    const res = await fetch('/api/v3/logs/analyze', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        description,
        ticketId: $('ticketId').value || null,
        logs: logsToAnalyze,
        // Strip the local id field — the server doesn't need or expect it.
        evidence: state.evidence.map(({ kind, title, command, content }) =>
          ({ kind, title, command, content })),
        // Per-request memory toggle. The server has its own env-var default
        // (DD_MEMORY_DISABLED), but the checkbox always wins. Sending the
        // flag explicitly even when checked makes the wiring obvious in
        // network logs.
        useMemory: $('useMemory').checked,
      }),
    });
    const data = await safeJson(res);
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
    renderAnalysis(data);
    // Stash everything needed to fire a follow-up 'Suggest queries' call.
    // We snapshot the description / ticket / evidence at THIS analyze's
    // moment — if the user changes them after analyze but before clicking
    // Suggest, the suggestion still corresponds to the analysis the user
    // sees on screen.
    state.lastAnalysis = data;
    state.lastAnalysisContext = {
      description,
      ticketId: $('ticketId').value || null,
      evidence: state.evidence.map(({ kind, title, command, content }) =>
        ({ kind, title, command, content })),
    };
    // Reveal the Suggest-queries area now that there's an analysis to
    // suggest against. Reset the prior status / list so a previous
    // suggestion's output doesn't linger over a fresh analysis.
    document.getElementById('suggestQueriesArea').classList.remove('hidden');
    document.getElementById('suggestQueriesList').innerHTML = '';
    document.getElementById('suggestQueriesStatus').textContent = '';
    document.getElementById('suggestQueriesBtn').disabled = false;
    document.getElementById('suggestQueriesBtn').textContent = 'Suggest next queries';
    // The schemasIncluded array names which reference schemas the analyzer
    // prepended to the prompt. Surfacing them here is a quick sanity check
    // that the wiring is firing — if you ever see 'schemas: (none)' the
    // backend either failed to load the markdown files or the request
    // bypassed the bundle.
    const schemas = (data.schemasIncluded ?? []).join(', ') || '(none)';
    // V3-specific: rag bookkeeping. Renders as either
    //   "RAG: kept 25 of 240 (top-K, threshold 100)"
    // when retrieval fired, or
    //   "RAG: skipped (47 ≤ threshold 100)"
    // when the gathered set was small enough to send everything. Lets you
    // see at a glance whether the LLM actually saw the full set.
    let ragLine = '';
    if (data.rag) {
      ragLine = data.rag.used
        ? `RAG: kept ${data.rag.keptCount} of ${data.rag.fromCount} (top-K, threshold ${data.rag.threshold})`
        : `RAG: skipped (${data.rag.fromCount} ≤ threshold ${data.rag.threshold})`;
    }
    // V3-specific: memory bookkeeping. Three possible states:
    //   "memory: 2 related (of 47)"     — read fired, found matches
    //   "memory: 0 related (of 47)"     — read fired, no matches above threshold
    //   "memory: off"                    — checkbox unchecked or env disabled
    //   "memory: unavailable (...)"      — vec0 missing or other init failure
    let memLine = '';
    if (data.memory) {
      if (!data.memory.enabled) {
        memLine = 'memory: off';
      } else if (data.memory.used) {
        memLine = `memory: ${data.memory.retrievedCount} related (of ${data.memory.corpusSize})`;
      } else {
        memLine = `memory: unavailable (${data.memory.skipReason ?? 'unknown'})`;
      }
    }
    $analysisCost.textContent = [ragLine, memLine, `schemas: ${schemas}`,
        `tokens: ${data.inputTokens} in / ${data.outputTokens} out`]
      .filter(Boolean).join(' · ');
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
  state.lastSelectedKey = null;
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
  // Reset pivot — it might point at a row we're about to deselect anyway,
  // and an unset pivot is the cleaner state after 'start over'.
  state.lastSelectedKey = null;
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
    $logTbody.innerHTML = `<tr class="empty"><td colspan="5">No logs yet — filter to begin.</td></tr>`;
    updateActionState();
    return;
  }
  // Build via DocumentFragment to avoid layout thrash for big result sets.
  // Index is 1-based so row 1 is the first log — matches how a human counts
  // ("the first log") not how a programmer counts ("logs[0]"). Stays stable
  // across renders because state.logs is sort-stable on timestamp.
  const frag = document.createDocumentFragment();
  let i = 1;
  for (const [key, log] of state.logs) {
    const tr = document.createElement('tr');
    tr.dataset.key = key;
    if (state.selected.has(key)) tr.classList.add('selected');
    tr.innerHTML =
      `<td class="col-idx">${i}</td>` +
      `<td class="col-pick">${state.selected.has(key) ? '●' : '○'}</td>` +
      `<td class="col-ts mono">${formatTs(log.timestamp)}</td>` +
      `<td class="col-svc mono">${escapeHtml(log.service)}</td>` +
      `<td class="col-msg mono">${escapeHtml(truncate(extractDisplayMessage(log.message), 480))}</td>`;
    // Shift-click → range select from lastSelectedKey to this row. Plain
    // click → toggle just this row (and update the pivot).
    tr.addEventListener('click', (ev) => {
      if (ev.shiftKey && state.lastSelectedKey && state.lastSelectedKey !== key) {
        ev.preventDefault();
        // Stop the browser from selecting text between the click points —
        // shift-click on text content does that by default and it's noisy.
        window.getSelection()?.removeAllRanges();
        rangeSelect(state.lastSelectedKey, key);
      } else {
        toggleSelected(key);
      }
    });
    frag.appendChild(tr);
    i++;
  }
  $logTbody.innerHTML = '';
  $logTbody.appendChild(frag);
  updateActionState();
}

function toggleSelected(key) {
  if (state.selected.has(key)) state.selected.delete(key);
  else state.selected.add(key);
  // Pivot moves with every plain click so the next shift-click extends from
  // wherever the user just touched. Standard file-manager behaviour.
  state.lastSelectedKey = key;
  // Targeted update — avoids re-rendering the whole table on every click.
  const tr = $logTbody.querySelector(`tr[data-key="${cssEscape(key)}"]`);
  if (tr) {
    tr.classList.toggle('selected', state.selected.has(key));
    tr.querySelector('.col-pick').textContent = state.selected.has(key) ? '●' : '○';
  }
  refreshSelectedHeader();
  updateActionState();
}

/// Range select between two row keys (inclusive). Both endpoints AND every
/// row between them in the current chronological order get marked selected
/// (NOT toggled — shift-click means 'add this range to my selection', which
/// matches how every spreadsheet and email client does it). The pivot moves
/// to the new endpoint so a follow-up shift-click extends from there.
function rangeSelect(fromKey, toKey) {
  // Walk state.logs (already sort-stable) and find the index of each
  // endpoint. Whichever is earlier is the start; whichever is later is the
  // end. Single pass.
  const keys = [...state.logs.keys()];
  const i = keys.indexOf(fromKey);
  const j = keys.indexOf(toKey);
  if (i < 0 || j < 0) {
    // Pivot row was cleared between clicks; fall back to single toggle.
    toggleSelected(toKey);
    return;
  }
  const [lo, hi] = i <= j ? [i, j] : [j, i];
  for (let n = lo; n <= hi; n++) {
    const key = keys[n];
    if (!state.selected.has(key)) {
      state.selected.add(key);
      const tr = $logTbody.querySelector(`tr[data-key="${cssEscape(key)}"]`);
      if (tr) {
        tr.classList.add('selected');
        tr.querySelector('.col-pick').textContent = '●';
      }
    }
  }
  state.lastSelectedKey = toKey;
  refreshSelectedHeader();
  updateActionState();
}

/// Select rows by 1-based index range (inclusive on both ends). The from
/// and to numbers correspond to the '#' column in the table — what a human
/// would call 'log 5' through 'log 12'. The actual row keys are derived
/// from state.logs (the Map preserves insertion order, which is the same
/// order renderTable iterates), so the index-to-key translation is just
/// the position in [...state.logs.keys()].
///
/// Edge handling:
///   - Reversed range (to < from): swapped silently. 'Select 20 to 5'
///     and 'Select 5 to 20' do the same thing.
///   - Out-of-range: clamped to [1, state.logs.size]. Asking for
///     '1 to 9999' with only 240 logs selects 1-240. Friendlier than
///     erroring.
///   - Both endpoints out-of-range with no overlap (e.g. 'from=500
///     to=600' on a 240-row table): nothing gets selected, status hint
///     explains why.
///   - Empty table: button is hidden via refreshSelectedHeader so this
///     shouldn't fire, but defensive guard anyway.
///
/// Behaves additively — adds to whatever's already selected, doesn't
/// replace. Same model as Select-all and shift-click. If the user wants
/// a clean range, they Clear-selection first.
function selectByIndexRange(fromIdx, toIdx) {
  const total = state.logs.size;
  if (total === 0) return;

  // Swap so lo <= hi.
  let lo = Math.min(fromIdx, toIdx);
  let hi = Math.max(fromIdx, toIdx);

  // Clamp to the visible range. A request for '500-600' on a 240-row
  // table becomes [240, 240] which the no-overlap check below catches.
  lo = Math.max(1, Math.min(lo, total));
  hi = Math.max(1, Math.min(hi, total));

  if (hi < lo || lo > total) {
    setStatus(`Range ${fromIdx}-${toIdx} is outside the table (1-${total}).`, 'error');
    return;
  }

  // Translate 1-based index → key. state.logs is the same iteration order
  // renderTable uses, so index 1 is the first key, etc.
  const keys = [...state.logs.keys()];
  const fromKey = keys[lo - 1];
  const toKey   = keys[hi - 1];

  // Reuse the existing range-select machinery; it already knows how to
  // walk between two keys, mark them all selected, and update the pivot
  // and DOM. Single source of truth for 'select a contiguous range'.
  rangeSelect(fromKey, toKey);

  setStatus(`Selected logs ${lo}-${hi}.`, 'info');
}

/// Select every row currently in state.logs. Triggered by the 'Select all'
/// link in the Logs card header. No-op if everything is already selected
/// (the link auto-hides in that case via refreshSelectedHeader).
function selectAll() {
  let changed = false;
  for (const key of state.logs.keys()) {
    if (!state.selected.has(key)) {
      state.selected.add(key);
      const tr = $logTbody.querySelector(`tr[data-key="${cssEscape(key)}"]`);
      if (tr) {
        tr.classList.add('selected');
        tr.querySelector('.col-pick').textContent = '●';
      }
      changed = true;
    }
  }
  if (changed) {
    // Pivot to the LAST log so a shift-click after select-all extends
    // backward from the bottom — which matches what a user expects after
    // 'select all then de-select these last few'.
    const keys = [...state.logs.keys()];
    state.lastSelectedKey = keys[keys.length - 1];
    refreshSelectedHeader();
    updateActionState();
    setStatus(`Selected all ${state.logs.size} logs.`, 'info');
  }
}

/// Update the 'Selected (N)' heading and toggle the Clear-selection link
/// visibility based on the current selected set. Centralised because three
/// different code paths (toggle, clear-selection, clear-all) need to keep
/// these in sync, and a stale heading was easy to introduce.
function refreshSelectedHeader() {
  const heading = document.getElementById('selectedHeading');
  if (heading) heading.textContent = `Selected (${state.selected.size})`;
  const clearLink = document.getElementById('clearSelectionLink');
  if (clearLink) clearLink.classList.toggle('hidden', state.selected.size === 0);
  // Hide 'Select all' once everything is already selected — clicking it
  // would be a no-op and the visual clutter isn't worth it. Also hide when
  // there are no logs to select (initial empty state).
  const allLink = document.getElementById('selectAllLink');
  if (allLink) {
    const allSelected = state.logs.size > 0 && state.selected.size === state.logs.size;
    allLink.classList.toggle('hidden', state.logs.size === 0 || allSelected);
  }
  // Range-select inputs only make sense with logs in the table. Toggling
  // visibility on the whole group keeps the header clean during the empty
  // state — same pattern as the links above.
  const rangeGroup = document.getElementById('rangeSelectGroup');
  if (rangeGroup) rangeGroup.classList.toggle('hidden', state.logs.size === 0);
}

function updateActionState() {
  $extendBtn.disabled = state.selected.size === 0;
  $analyzeBtn.disabled = state.logs.size === 0;
  // Button label reflects what onAnalyze will actually send. Mirror the
  // logic in analyzeTargets() so the user can tell at a glance whether
  // selection is going to narrow the analysis or not.
  //   - empty table          → "Analyze gathered logs" (disabled state)
  //   - selection present    → "Analyze N selected"  (selection narrows input)
  //   - no selection         → "Analyze all M logs"  (whole table goes in)
  if (state.logs.size === 0) {
    $analyzeBtn.textContent = 'Analyze gathered logs';
  } else if (state.selected.size > 0) {
    const n = state.selected.size;
    $analyzeBtn.textContent = `Analyze ${n} selected`;
  } else {
    const m = state.logs.size;
    $analyzeBtn.textContent = `Analyze all ${m} log${m === 1 ? '' : 's'}`;
  }
}

/// What the Analyze action should actually send to the server.
/// Selection wins when present — that's the user explicitly narrowing.
/// When nothing is selected we fall back to the whole gathered table,
/// which preserves the original 'gather then analyze the lot' workflow.
/// Selected-but-orphaned keys (the row was removed by Clear or filter
/// before Analyze fired) are silently skipped — same defensive pattern
/// as the rest of the file.
function analyzeTargets() {
  if (state.selected.size === 0) {
    return Array.from(state.logs.values());
  }
  const out = [];
  for (const key of state.selected) {
    const log = state.logs.get(key);
    if (log) out.push(log);
  }
  // Sort chronologically so the LLM reads events in time order regardless
  // of the order the user clicked rows. Matches what state.logs does
  // already for the unfiltered case.
  out.sort((a, b) => new Date(a.timestamp) - new Date(b.timestamp));
  return out;
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

// ---- suggest queries ----

/// Click handler for the 'Suggest next queries' button. Sends the most
/// recent analysis (stashed at /analyze success time) plus the bug
/// description and evidence to /api/v3/logs/suggest-queries, then renders
/// the response as a stack of suggestion cards.
///
/// Errors render inline rather than as a global toast — the analysis is
/// still visible and useful, so the user shouldn't lose their place.
async function onSuggestQueries() {
  if (!state.lastAnalysis || !state.lastAnalysisContext) {
    // Defensive: button should be hidden when this is the case, but if
    // something went wrong with the reveal logic, fail loudly here rather
    // than firing a malformed request.
    setStatus('No analysis to suggest queries for. Run Analyze first.', 'error');
    return;
  }

  const $btn    = document.getElementById('suggestQueriesBtn');
  const $status = document.getElementById('suggestQueriesStatus');
  const $list   = document.getElementById('suggestQueriesList');

  $btn.disabled = true;
  $btn.textContent = 'Suggesting…';
  $status.textContent = '';
  $list.innerHTML = '';

  const a = state.lastAnalysis;
  try {
    const res = await fetch('/api/v3/logs/suggest-queries', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ...state.lastAnalysisContext,
        analysis: {
          summary:            a.summary,
          suspicious:         a.suspicious ?? [],
          hypothesis:         a.hypothesis,
          suggestedFollowups: a.suggestedFollowups ?? [],
        },
      }),
    });
    const data = await safeJson(res);
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);

    renderSuggestedQueries(data.suggestions ?? []);

    const n = (data.suggestions ?? []).length;
    $status.textContent = n === 0
      ? 'No queries suggested.'
      : `${n} suggestion${n === 1 ? '' : 's'} · tokens: ${data.inputTokens} in / ${data.outputTokens} out`;
    $btn.textContent = 'Suggest again';
    $btn.disabled = false;
  } catch (err) {
    $status.textContent = `Failed: ${err.message}`;
    $btn.textContent = 'Suggest next queries';
    $btn.disabled = false;
  }
}

/// Render a list of QuerySuggestion objects into the suggestQueriesList
/// container. Each suggestion gets a card with: a coloured system tag,
/// the executable query in a mono code block, a Copy button, and a
/// rationale line. The Copy button uses the Clipboard API (available
/// everywhere on http://localhost) and flips to a 'Copied!' state for a
/// second so the user gets visible feedback.
function renderSuggestedQueries(suggestions) {
  const $list = document.getElementById('suggestQueriesList');
  if (suggestions.length === 0) {
    $list.innerHTML = '';
    return;
  }
  // Build via DOM rather than innerHTML so the Copy buttons can carry
  // closures over their own query string — simpler than data-* attributes
  // plus delegated event handling for a small, fixed-size list.
  $list.innerHTML = '';
  for (const s of suggestions) {
    const card = document.createElement('div');
    card.className = 'suggested-query';

    const header = document.createElement('div');
    header.className = 'sq-header';

    const tag = document.createElement('span');
    tag.className = `sq-system ${s.system}`;
    tag.textContent = s.system;
    header.appendChild(tag);

    const copy = document.createElement('button');
    copy.type = 'button';
    copy.className = 'sq-copy';
    copy.textContent = 'Copy';
    copy.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(s.query);
        copy.textContent = 'Copied!';
        copy.classList.add('copied');
        setTimeout(() => {
          copy.textContent = 'Copy';
          copy.classList.remove('copied');
        }, 1200);
      } catch {
        // Clipboard API can fail when the page isn't focused or in some
        // browser contexts. Fall back to selecting the query so the user
        // can manually copy.
        const range = document.createRange();
        range.selectNodeContents(card.querySelector('.sq-query'));
        const sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
      }
    });
    header.appendChild(copy);

    card.appendChild(header);

    const pre = document.createElement('pre');
    pre.className = 'sq-query';
    pre.textContent = s.query;
    card.appendChild(pre);

    if (s.rationale) {
      const r = document.createElement('div');
      r.className = 'sq-rationale';
      r.textContent = s.rationale;
      card.appendChild(r);
    }

    $list.appendChild(card);
  }
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
