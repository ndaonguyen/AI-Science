// Vanilla JS — no build step, no bundler. The UI has three states:
//
//   1. Form        — user enters description + options, hits Start.
//   2. Running     — live event feed streams in via SSE. May open a paste panel.
//   3. Complete    — final markdown report renders at the bottom.
//
// The server has three endpoints:
//   POST /api/investigate           → { sessionId }
//   GET  /api/stream/{sessionId}    → text/event-stream
//   POST /api/paste/{sessionId}     → 200 OK
//
// Nothing clever here. One global state object, one EventSource, plain DOM.

const state = {
  sessionId: null,
  eventSource: null,
};

// ---- DOM handles ----
const $form = document.getElementById('form-section');
const $run = document.getElementById('run-section');
const $events = document.getElementById('events');
const $report = document.getElementById('report');
const $paste = document.getElementById('paste-panel');
const $pasteSource = document.getElementById('paste-source');
const $pasteEnv = document.getElementById('paste-env');
const $pasteReason = document.getElementById('paste-reason');
const $pasteQuery = document.getElementById('paste-query');
const $pasteText = document.getElementById('paste-text');
const $sessionIdBadge = document.getElementById('sessionIdBadge');

// ---- form ----
document.getElementById('startBtn').addEventListener('click', startInvestigation);
document.getElementById('newBtn').addEventListener('click', resetToForm);

document.getElementById('submitPasteBtn').addEventListener('click', () =>
  submitPaste('paste', $pasteText.value));
document.getElementById('emptyBtn').addEventListener('click', () =>
  submitPaste('empty', null));
document.getElementById('skipBtn').addEventListener('click', () =>
  submitPaste('skip', null));

async function startInvestigation() {
  const description = document.getElementById('description').value.trim();
  if (!description) {
    alert('Please provide a bug description.');
    return;
  }

  const body = {
    description,
    ticketId: document.getElementById('ticketId').value.trim() || null,
    mock: document.getElementById('mock').checked,
  };

  const res = await fetch('/api/investigate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    alert('Failed to start: ' + (err.error || res.statusText));
    return;
  }

  const { sessionId } = await res.json();
  state.sessionId = sessionId;
  $sessionIdBadge.textContent = sessionId;

  $form.classList.add('hidden');
  $run.classList.remove('hidden');
  $events.innerHTML = '';
  $report.classList.add('hidden');
  $report.innerHTML = '';

  subscribe(sessionId);
}

function subscribe(sessionId) {
  closeStream();

  // EventSource auto-reconnects on drop — handy when the dev server
  // restarts or the laptop sleeps mid-investigation.
  const es = new EventSource(`/api/stream/${sessionId}`);
  state.eventSource = es;

  // One handler per event kind. The server sends `event: <kind>\ndata: <json>`,
  // so we match kinds we care about explicitly; anything unrecognised is silently
  // ignored so the UI doesn't break when we add new event types server-side.
  es.addEventListener('model_call', e => append('model-call', 'model →', JSON.parse(e.data)));
  es.addEventListener('model_response', e => appendModelResponse(JSON.parse(e.data)));
  es.addEventListener('tool_call', e => appendToolCall(JSON.parse(e.data)));
  es.addEventListener('tool_result', e => appendToolResult(JSON.parse(e.data)));
  es.addEventListener('hypothesis', e => appendHypothesis(JSON.parse(e.data)));
  es.addEventListener('paste_request', e => showPastePanel(JSON.parse(e.data)));
  es.addEventListener('paste_received', e => hidePastePanel());
  es.addEventListener('completed', e => showReport(JSON.parse(e.data)));
  es.addEventListener('error', e => {
    // Protect against SSE `error` events from the connection itself (no data).
    if (e.data) appendError(JSON.parse(e.data));
  });
}

function closeStream() {
  if (state.eventSource) {
    state.eventSource.close();
    state.eventSource = null;
  }
}

function resetToForm() {
  closeStream();
  state.sessionId = null;
  $run.classList.add('hidden');
  $form.classList.remove('hidden');
}

// ---- paste panel ----
function showPastePanel(req) {
  $pasteSource.textContent = req.sourceName;
  $pasteEnv.textContent = req.suggestedEnv ? `env: ${req.suggestedEnv}` : '';
  $pasteReason.textContent = req.reason || '';
  $pasteQuery.textContent = req.renderedQuery || '';
  $pasteText.value = '';
  $paste.classList.remove('hidden');
  $pasteText.focus();
}

function hidePastePanel() {
  $paste.classList.add('hidden');
}

async function submitPaste(mode, text) {
  if (!state.sessionId) return;
  hidePastePanel();   // optimistic — the server will echo paste_received anyway
  await fetch(`/api/paste/${state.sessionId}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode, text }),
  });
}

// ---- event feed rendering ----
function appendLine(className, tag, text, iteration) {
  const div = document.createElement('div');
  div.className = 'event ' + className;
  const iter = iteration != null ? `[iter ${iteration}]` : '';
  div.innerHTML = `<span class="iter">${iter}</span><span class="tag">${escape(tag)}</span>${escape(text || '')}`;
  $events.appendChild(div);
  $events.scrollTop = $events.scrollHeight;
  return div;
}
function append(cls, tag, data) {
  return appendLine(cls, tag, '', data.iteration);
}

function appendModelResponse(data) {
  const div = appendLine('model-response', 'model ←',
    data.text ? truncate(data.text, 160) : '(no text)', data.iteration);
  if (data.text && data.text.length > 160) {
    const pre = document.createElement('pre');
    pre.textContent = data.text;
    div.appendChild(pre);
  }
}

function appendToolCall(data) {
  // Compact the tool input so it fits on one line. Full JSON is in the log.
  const argPreview = compactJson(data.input);
  const div = appendLine('tool-call', '🔧 ' + data.toolName, argPreview, data.iteration);
  if (argPreview.length > 200) {
    div.lastChild.remove();
    const pre = document.createElement('pre');
    pre.textContent = JSON.stringify(data.input, null, 2);
    div.appendChild(pre);
  }
}

function appendToolResult(data) {
  const cls = data.isError ? 'tool-result err' : 'tool-result';
  const tag = data.isError ? '✗' : '✓';
  appendLine(cls, tag, truncate(data.output || '', 180), data.iteration);
}

function appendHypothesis(data) {
  appendLine('hypothesis', '💡 ' + (data.hypothesis || ''),
    data.reasoning ? ' — ' + data.reasoning : '', data.iteration);
}

function appendError(data) {
  appendLine('error', '⚠ error', data.message || '(unknown)', data.iteration);
}

// ---- report ----
function showReport(data) {
  hidePastePanel();
  $report.classList.remove('hidden');
  // For now: render markdown as <pre>. A future iteration could wire in
  // marked.js for full rendering. The pre keeps it readable and safe.
  const h = document.createElement('h2');
  h.textContent = 'Final report';
  $report.innerHTML = '';
  $report.appendChild(h);
  const pre = document.createElement('pre');
  pre.textContent = data.markdown || '(no report)';
  $report.appendChild(pre);
}

// ---- utils ----
function truncate(s, n) {
  if (!s) return '';
  return s.length > n ? s.slice(0, n) + '…' : s;
}
function escape(s) {
  return String(s).replace(/[&<>"]/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function compactJson(obj) {
  try {
    const s = JSON.stringify(obj);
    return s.length > 200 ? s.slice(0, 200) + '…' : s;
  } catch { return '(unserializable)'; }
}
