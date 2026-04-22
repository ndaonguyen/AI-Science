// Vanilla JS — no build step. The page supports two modes:
//
//   - autonomous: user describes a bug, agent runs end-to-end until finished
//   - guided:     user describes a bug + picks services/env, agent does ONE
//                 turn at a time, pauses with a summary, user clicks the
//                 next-step button to drive the next turn
//
// Under the hood both modes use SSE for live event streaming and the same
// paste-panel flow for request_* tools. The mode just decides which API
// endpoints we hit.

const state = {
  mode: 'guided',       // 'guided' | 'autonomous'
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
const $modeBadge = document.getElementById('modeBadge');
const $guidedFields = document.getElementById('guided-fields');

const $turnSummary = document.getElementById('turn-summary');
const $hypothesis = document.getElementById('hypothesis-text');
const $findings = document.getElementById('findings');
const $nextButtons = document.querySelector('.next-buttons');
const $moreLogsPanel = document.getElementById('more-logs-panel');
const $runningIndicator = document.getElementById('running-indicator');

// ---- wiring ----
document.querySelectorAll('input[name=mode]').forEach(r =>
  r.addEventListener('change', onModeChange));

document.getElementById('startBtn').addEventListener('click', startInvestigation);
document.getElementById('newBtn').addEventListener('click', resetToForm);

document.getElementById('submitPasteBtn').addEventListener('click', () =>
  submitPaste('paste', $pasteText.value));
document.getElementById('emptyBtn').addEventListener('click', () =>
  submitPaste('empty', null));
document.getElementById('skipBtn').addEventListener('click', () =>
  submitPaste('skip', null));

// Guided next-step buttons. Delegated click handler on the container so we
// don't have to wire each button individually.
$nextButtons.addEventListener('click', e => {
  const btn = e.target.closest('button[data-action]');
  if (!btn) return;
  const action = btn.dataset.action;

  if (action === 'more_logs') {
    $moreLogsPanel.classList.remove('hidden');
    document.getElementById('dig-errors-panel').classList.add('hidden');
    return;
  }
  if (action === 'dig_errors') {
    document.getElementById('dig-errors-panel').classList.remove('hidden');
    $moreLogsPanel.classList.add('hidden');
    // Clear any previous selection so user must pick a row
    document.getElementById('dig-ts-iso').value = '';
    document.getElementById('dig-ts-display').textContent = '— click a log row below —';
    document.getElementById('dig-env-val').value = '';
    document.getElementById('dig-env-display').textContent = '—';
    document.getElementById('dig-errors-panel').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    return;
  }
  runStep(action);
});

document.getElementById('more-logs-submit').addEventListener('click', () => {
  const services = Array.from($moreLogsPanel.querySelectorAll('input[type=checkbox]:checked'))
    .map(c => c.value);
  const env = document.getElementById('more-logs-env').value;
  const filterText = document.getElementById('more-logs-filter').value.trim();
  if (services.length === 0) {
    alert('Pick at least one service to search.');
    return;
  }
  const { startTime, endTime } = resolveTimeRange('more-logs-preset', 'mlStartDate', 'mlStartTime', 'mlEndDate', 'mlEndTime');
  $moreLogsPanel.classList.add('hidden');
  runStep('more_logs', { services, environment: env, startTime, endTime, filterText });
});

document.getElementById('more-logs-cancel').addEventListener('click', () => {
  $moreLogsPanel.classList.add('hidden');
});

document.getElementById('previewLogsBtn').addEventListener('click', previewLogs);

document.getElementById('rawLogsClose').addEventListener('click', () => {
  document.getElementById('rawLogsSection').classList.add('hidden');
});

document.getElementById('run-raw-logs-close').addEventListener('click', () => {
  document.getElementById('run-raw-logs-section').classList.add('hidden');
});

document.getElementById('more-logs-preview').addEventListener('click', async () => {
  const services = Array.from($moreLogsPanel.querySelectorAll('input[type=checkbox]:checked'))
    .map(c => c.value);
  const env = document.getElementById('more-logs-env').value;
  const filter = document.getElementById('more-logs-filter').value.trim();
  const { startTime, endTime } = resolveTimeRange('more-logs-preset','mlStartDate','mlStartTime','mlEndDate','mlEndTime');
  if (services.length === 0) { alert('Pick at least one service.'); return; }
  for (const svc of services) {
    await showRawLogs(svc, env, startTime, endTime, filter);
  }
});

document.getElementById('dig-cancel').addEventListener('click', () => {
  document.getElementById('dig-errors-panel').classList.add('hidden');
});

document.getElementById('dig-preview').addEventListener('click', async () => {
  const p = getDigParams({ forPreview: true });
  if (!p) return;
  const { services, env, startTime, endTime, filterText } = p;
  if (!services.length) { alert('Pick at least one service.'); return; }
  for (const svc of services) {
    await showRawLogs(svc, env, startTime, endTime, filterText);
  }
});

document.getElementById('dig-analyze').addEventListener('click', () => {
  const p = getDigParams({ forPreview: false });
  if (!p) return;
  const { services, env, startTime, endTime } = p;
  if (!services.length) { alert('Pick at least one service.'); return; }
  document.getElementById('dig-errors-panel').classList.add('hidden');
  runStep('dig_errors', { services, environment: env, startTime, endTime });
});

function getDigParams({ forPreview = false } = {}) {
  const isoTs = document.getElementById('dig-ts-iso').value;
  const env   = document.getElementById('dig-env-val').value;
  const windowMin = parseInt(document.getElementById('dig-window').value, 10) || 2;

  if (!isoTs) { alert('Please click a log row first to select a timestamp.'); return null; }

  const center = new Date(isoTs);
  const startTime = new Date(center - windowMin * 60_000).toISOString();
  const endTime   = new Date(center + windowMin * 60_000).toISOString();

  const services = Array.from(
    document.querySelectorAll('#dig-services input[type=checkbox]:checked')
  ).map(c => c.value);

  // Preview uses keyword filter so the table is manageable.
  // Analyze sends NO filter — AI gets all logs and finds suspicious ones itself.
  let filterText = '';
  if (forPreview) {
    const keywords = Array.from(
      document.querySelectorAll('#dig-keywords input[type=checkbox]:checked')
    ).map(c => c.value);
    filterText = keywords.map(k => k.includes(' ') ? `?"${k}"` : `?${k}`).join(' ');
  }

  return { services, env, startTime, endTime, filterText };
}

// ---- CloudWatch-style datetime picker ----
setupDateRangePicker({
  presetId:    'timePreset',
  panelId:     'customRangePanel',
  summaryId:   'customRangeSummary',
  startDateId: 'startDate',
  startTimeId: 'startTimeInput',
  endDateId:   'endDate',
  endTimeId:   'endTimeInput',
  applyId:     'applyCustomRange',
  cancelId:    'cancelCustomRange',
});

setupDateRangePicker({
  presetId:    'more-logs-preset',
  panelId:     'moreLogsCustomPanel',
  summaryId:   'moreLogsRangeSummary',
  startDateId: 'mlStartDate',
  startTimeId: 'mlStartTime',
  endDateId:   'mlEndDate',
  endTimeId:   'mlEndTime',
  applyId:     'applyMoreLogsRange',
  cancelId:    'cancelMoreLogsRange',
});

function setupDateRangePicker({ presetId, panelId, summaryId, startDateId, startTimeId, endDateId, endTimeId, applyId, cancelId }) {
  const $preset  = document.getElementById(presetId);
  const $panel   = document.getElementById(panelId);
  const $summary = document.getElementById(summaryId);

  $preset.addEventListener('change', () => {
    if ($preset.value === 'custom') {
      // Seed inputs with sensible defaults: last 1 hour in UTC
      const now = new Date();
      const ago = new Date(now - 3600_000);
      document.getElementById(startDateId).value = toUtcDate(ago);
      document.getElementById(startTimeId).value = toUtcTime(ago);
      document.getElementById(endDateId).value   = toUtcDate(now);
      document.getElementById(endTimeId).value   = toUtcTime(now);
      $panel.classList.remove('hidden');
      $summary.classList.add('hidden');
    } else {
      $panel.classList.add('hidden');
      $summary.classList.add('hidden');
    }
  });

  document.getElementById(applyId).addEventListener('click', () => {
    const sd = document.getElementById(startDateId).value;
    const st = document.getElementById(startTimeId).value;
    const ed = document.getElementById(endDateId).value;
    const et = document.getElementById(endTimeId).value;
    if (!sd || !ed) { alert('Please fill in both start and end dates.'); return; }
    $panel.classList.add('hidden');
    $summary.textContent = `${sd} ${st} → ${ed} ${et} UTC`;
    $summary.classList.remove('hidden');
  });

  document.getElementById(cancelId).addEventListener('click', () => {
    $panel.classList.add('hidden');
    // Revert preset back to last non-custom value
    $preset.value = '1h';
    $summary.classList.add('hidden');
  });
}

onModeChange();

// ---- mode switching ----
function onModeChange() {
  const selected = document.querySelector('input[name=mode]:checked')?.value || 'guided';
  state.mode = selected;
  // Services/env only matter in guided mode (autonomous lets the agent pick).
  $guidedFields.style.display = selected === 'guided' ? '' : 'none';
}

// ---- start investigation ----
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

  const endpoint = state.mode === 'guided' ? '/api/guided/start' : '/api/investigate';
  const res = await fetch(endpoint, {
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
  $modeBadge.textContent = state.mode;

  $form.classList.add('hidden');
  $run.classList.remove('hidden');
  $events.innerHTML = '';
  $report.classList.add('hidden');
  $report.innerHTML = '';
  $turnSummary.classList.add('hidden');

  subscribe(sessionId);

  // In guided mode, immediately run the first step (search_logs on the
  // selected services). Feels more natural than making the user click
  // "search logs" on an empty page right after starting.
  if (state.mode === 'guided') {
    const services = Array.from(document.querySelectorAll('input[name=service]:checked'))
      .map(c => c.value);
    const environment = document.getElementById('environment').value;
    const filterText  = document.getElementById('filterText').value.trim();
    if (services.length === 0) {
      alert('Pick at least one service to search.');
      return;
    }
    const { startTime, endTime } = resolveTimeRange('timePreset', 'startDate', 'startTimeInput', 'endDate', 'endTimeInput');

    // If the user typed a filter, show raw preview first, then let AI run.
    if (filterText && !document.getElementById('mock').checked) {
      for (const svc of services) {
        await showRawLogs(svc, environment, startTime, endTime, filterText);
      }
    }

    runStep('search_logs', { services, environment, startTime, endTime, filterText });
  }
}

function subscribe(sessionId) {
  closeStream();

  const streamUrl = state.mode === 'guided'
    ? `/api/guided/stream/${sessionId}`
    : `/api/stream/${sessionId}`;

  const es = new EventSource(streamUrl);
  state.eventSource = es;

  es.addEventListener('model_call', e => append('model-call', 'model →', JSON.parse(e.data)));
  es.addEventListener('model_response', e => appendModelResponse(JSON.parse(e.data)));
  es.addEventListener('tool_call', e => appendToolCall(JSON.parse(e.data)));
  es.addEventListener('tool_result', e => appendToolResult(JSON.parse(e.data)));
  es.addEventListener('hypothesis', e => appendHypothesis(JSON.parse(e.data)));
  es.addEventListener('paste_request', e => showPastePanel(JSON.parse(e.data)));
  es.addEventListener('paste_received', () => hidePastePanel());
  es.addEventListener('completed', e => showReport(JSON.parse(e.data)));
  es.addEventListener('error', e => {
    if (e.data) appendError(JSON.parse(e.data));
  });

  // Guided-mode-specific events
  es.addEventListener('turn_started', e => {
    const d = JSON.parse(e.data);
    $turnSummary.classList.add('hidden');
    $runningIndicator.classList.remove('hidden');
    appendLine('model-call', '▶ turn', d.action || '', null);
  });
  es.addEventListener('turn_summary', e => {
    $runningIndicator.classList.add('hidden');
    showTurnSummary(JSON.parse(e.data));
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

// ---- guided-mode turn ----
async function runStep(action, context) {
  if (!state.sessionId || state.mode !== 'guided') return;

  disableNextButtons(true);
  $turnSummary.classList.add('hidden');
  $runningIndicator.classList.remove('hidden');

  const body = { action, context: context || null };
  const res = await fetch(`/api/guided/step/${state.sessionId}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  disableNextButtons(false);
  $runningIndicator.classList.add('hidden');

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    alert('Turn failed: ' + (err.error || res.statusText));
  }
  // Actual summary rendering is driven by the SSE `turn_summary` event, not
  // this response — SSE arrives first in practice.
}

function disableNextButtons(disabled) {
  $nextButtons.querySelectorAll('button').forEach(b => b.disabled = disabled);
}

function showTurnSummary(data) {
  $hypothesis.textContent = data.hypothesis || '(none)';
  $findings.innerHTML = '';
  (data.findings || []).forEach(f => {
    const li = document.createElement('li');
    li.textContent = f;
    $findings.appendChild(li);
  });

  // Highlight the agent's suggested next step so the user can see what it
  // recommends without losing the option to pick something else.
  $nextButtons.querySelectorAll('button').forEach(b => {
    b.classList.toggle('suggested', b.dataset.action === data.suggestedNext);
  });

  $turnSummary.classList.remove('hidden');
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

function hidePastePanel() { $paste.classList.add('hidden'); }

async function submitPaste(mode, text) {
  if (!state.sessionId) return;
  hidePastePanel();
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
  $turnSummary.classList.add('hidden');
  $report.classList.remove('hidden');
  const h = document.createElement('h2');
  h.textContent = 'Final report';
  $report.innerHTML = '';
  $report.appendChild(h);
  const pre = document.createElement('pre');
  pre.textContent = data.markdown || '(no report)';
  $report.appendChild(pre);
}

// ---- raw log preview ----
async function previewLogs() {
  const services = Array.from(document.querySelectorAll('input[name=service]:checked'))
    .map(c => c.value);
  const environment = document.getElementById('environment').value;
  const filterText  = document.getElementById('filterText').value.trim();
  const { startTime, endTime } = resolveTimeRange('timePreset', 'startDate', 'startTimeInput', 'endDate', 'endTimeInput');

  if (services.length === 0) { alert('Pick at least one service.'); return; }

  for (const svc of services) {
    await showRawLogs(svc, environment, startTime, endTime, filterText);
  }
}

async function showRawLogs(service, environment, startTime, endTime, filterText) {
  // Use the in-investigation container if a session is active, else the form-section one.
  const inRun = !!state.sessionId;
  const sectionId = inRun ? 'run-raw-logs-section' : 'rawLogsSection';
  const titleId   = inRun ? 'run-raw-logs-title'   : 'rawLogsTitle';
  const tableId   = inRun ? 'run-raw-logs-table'   : 'rawLogsTable';

  const $section = document.getElementById(sectionId);
  const $table   = document.getElementById(tableId);
  const $title   = document.getElementById(titleId);

  $section.classList.remove('hidden');
  $title.textContent = '';   // clear header — individual groups have their own label

  // Create a new group div and append it (preserving previous results)
  const $group = document.createElement('div');
  $group.className = 'raw-logs-group';
  $group.innerHTML = `<div class="raw-logs-group-header">
    <span class="raw-logs-group-title">Loading ${escape(service)} …</span>
    <button type="button" class="raw-logs-group-close secondary small" title="Remove this result">✕</button>
  </div>
  <div class="raw-logs-group-body"><div class="raw-logs-loading">Fetching from CloudWatch…</div></div>`;
  $table.appendChild($group);
  $group.scrollIntoView({ behavior: 'smooth', block: 'nearest' });

  // Wire the ✕ button to remove just this group
  $group.querySelector('.raw-logs-group-close').addEventListener('click', () => $group.remove());

  const $groupTitle = $group.querySelector('.raw-logs-group-title');
  const $groupBody  = $group.querySelector('.raw-logs-group-body');

  try {
    const res = await fetch('/api/logs/raw', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ service, environment, startTime, endTime, filterText }),
    });
    const data = await res.json();

    if (!res.ok) {
      $groupBody.innerHTML = `<div class="raw-logs-error">Error: ${escape(data.detail || data.error || 'unknown')}</div>`;
      $groupTitle.textContent = `${service} — error`;
      return;
    }

    const { logGroup, count, events } = data;
    $groupTitle.textContent = `${logGroup}  ·  ${count} event${count !== 1 ? 's' : ''}` +
                              (filterText ? `  ·  filter: "${filterText}"` : '');

    if (!events || events.length === 0) {
      $groupBody.innerHTML = '<div class="raw-logs-empty">No matching log events in this time window.</div>';
      return;
    }

    // Build clickable rows
    const rows = events.map(ev => {
      const msg = filterText
        ? escape(ev.message).replace(new RegExp(escapeRegex(filterText), 'gi'), m => `<mark>${m}</mark>`)
        : escape(ev.message);
      return `<div class="raw-log-row" data-ts="${escape(ev.timestamp)}" data-env="${escape(environment)}" title="Click to dig into errors around this timestamp">
        <span class="raw-log-ts">${escape(ev.timestamp)}</span>
        <span class="raw-log-msg">${msg}</span>
        <span class="raw-log-dig-hint">🔍</span>
      </div>`;
    }).join('');

    $groupBody.innerHTML = rows;

    // Wire row clicks — only available in run mode
    if (inRun) {
      $groupBody.querySelectorAll('.raw-log-row').forEach(row => {
        row.addEventListener('click', () => {
          // Clear selected state across ALL groups
          document.querySelectorAll(`#${tableId} .raw-log-row`).forEach(r => r.classList.remove('selected'));
          row.classList.add('selected');
          openDigPanelFromRow(row.dataset.ts, row.dataset.env);
        });
      });
    }
  } catch (err) {
    $groupBody.innerHTML = `<div class="raw-logs-error">Network error: ${escape(err.message)}</div>`;
    $groupTitle.textContent = `${service} — network error`;
  }
}

/**
 * Populate and open the dig panel from a clicked raw log row.
 * Timestamp comes from the row's data-ts attribute ("2026-04-22 09:09:31.851 UTC").
 */
function openDigPanelFromRow(rawTs, environment) {
  // Parse "2026-04-22 09:09:31.851 UTC" → ISO string
  const isoTs = rawTs.replace(' UTC', 'Z').replace(' ', 'T');
  const display = rawTs;

  document.getElementById('dig-ts-iso').value     = isoTs;
  document.getElementById('dig-ts-display').textContent = display;
  document.getElementById('dig-env-val').value    = environment;
  document.getElementById('dig-env-display').textContent = environment;

  // Auto-check services that match the environment (pre-tick common ones)
  document.getElementById('dig-errors-panel').classList.remove('hidden');
  $moreLogsPanel.classList.add('hidden');

  // Scroll dig panel into view
  document.getElementById('dig-errors-panel').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
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
function escapeRegex(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Convert a preset select + optional custom date/time inputs into ISO UTC strings. */
function resolveTimeRange(presetId, startDateId, startTimeId, endDateId, endTimeId) {
  const preset = document.getElementById(presetId)?.value || '1h';
  if (preset === 'custom') {
    const sd = document.getElementById(startDateId)?.value;
    const st = document.getElementById(startTimeId)?.value || '00:00';
    const ed = document.getElementById(endDateId)?.value;
    const et = document.getElementById(endTimeId)?.value || '23:59';
    return {
      startTime: sd ? new Date(`${sd}T${st}:00Z`).toISOString() : null,
      endTime:   ed ? new Date(`${ed}T${et}:00Z`).toISOString() : null,
    };
  }
  const minutes = { '30m': 30, '1h': 60, '3h': 180, '6h': 360, '12h': 720, '24h': 1440 }[preset] || 60;
  const end = new Date();
  const start = new Date(end - minutes * 60_000);
  return { startTime: start.toISOString(), endTime: end.toISOString() };
}

/** Zero-pad to 2 digits */
const pad = n => String(n).padStart(2, '0');

/** Format a Date as YYYY-MM-DD in UTC */
function toUtcDate(d) {
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth()+1)}-${pad(d.getUTCDate())}`;
}

/** Format a Date as HH:MM in UTC */
function toUtcTime(d) {
  return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

