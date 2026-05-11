// Memory — vanilla JS frontend.
//
// Structure: one global `state` object, render functions per tab, an api
// object for the fetch wrapper. No framework. Mirrors the DistributedDebugger
// v3.js pattern so the two projects feel consistent.
//
// State changes flow through small mutator helpers (setEditing, setTab,
// setAskResponse, etc.) which always end by calling the relevant render
// function. There's no virtual-DOM diffing — each render rewrites that
// section's innerHTML from current state, which is cheap at this scale
// (a few dozen DOM nodes max).

(function() {
  'use strict';

  // ================== state ==================

  const state = {
    tab: 'ask',                 // 'ask' | 'add' | 'all'
    editing: null,              // Entry | null — non-null = editing existing
    formKind: 'Bug',            // 'Bug' | 'Feature' — selected kind on Add form
    entries: [],                // Entry[] — populated when All tab is opened
    entriesLoaded: false,       // false until first All-tab fetch completes
    entriesLoading: false,
    entriesError: null,
    listKind: 'all',            // 'all' | 'Bug' | 'Feature' — All-tab kind filter
    askLoading: false,
    askResponse: null,          // RagResponse | null
    askError: null,
    importOpen: false,
    importStatus: { type: 'idle', msg: '' },
    reviewStatus: { type: 'idle', msg: '' },
    review: null,               // ContextReviewDto | null
    saveStatus: { type: 'idle', msg: '' },
    filter: '',
  };

  // ================== api ==================
  // Plain fetch wrapper. Throws on non-2xx with the response body in the
  // message — backend surfaces OpenAI/Qdrant error bodies, so we want to
  // display those verbatim. The URL still says /api/bugs even though it
  // serves both kinds; the rename was deferred to keep the diff bounded.

  const api = {
    list:    (kind)          => req(kind && kind !== 'all' ? `/api/bugs?kind=${kind}` : '/api/bugs'),
    get:     (id)            => req(`/api/bugs/${id}`),
    create:  (body)          => req('/api/bugs',     { method: 'POST',   body: JSON.stringify(body) }),
    update:  (id, body)      => req(`/api/bugs/${id}`, { method: 'PUT',  body: JSON.stringify(body) }),
    remove:  (id)            => req(`/api/bugs/${id}`, { method: 'DELETE' }),
    search:  (query, topK=5) => req('/api/search',   { method: 'POST', body: JSON.stringify({ query, topK }) }),
    ask:     (question, topK=5) => req('/api/ask',   { method: 'POST', body: JSON.stringify({ question, topK }) }),
    extract: (sourceText)    => req('/api/extract',  { method: 'POST', body: JSON.stringify({ sourceText }) }),
    review:  (body)          => req('/api/review',   { method: 'POST', body: JSON.stringify(body) }),
    findAnswer: (body)       => req('/api/review/answer', { method: 'POST', body: JSON.stringify(body) }),
  };

  async function req(path, init) {
    const opts = init ? { ...init } : {};
    opts.headers = { 'Content-Type': 'application/json', ...(opts.headers || {}) };
    const res = await fetch(path, opts);
    if (!res.ok) {
      let text;
      try { text = await res.text(); } catch { text = ''; }
      throw new Error(`${res.status}: ${text || res.statusText}`);
    }
    if (res.status === 204) return undefined;
    return res.json();
  }

  // ================== utilities ==================

  // tiny helpers used by the renderers
  const $ = (id) => document.getElementById(id);
  const escape = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  function formatDate(iso) {
    return new Date(iso).toLocaleDateString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }

  function setStatus(el, type, msg) {
    // type: 'idle' | 'loading' | 'error' | 'success'
    el.className = 'status' + (type === 'idle' ? '' : ' ' + type);
    el.textContent = msg || '';
  }

  // Field labels flip with kind. 'Why' / 'Decision' for features so the
  // form reads as feature rationale, not a bug postmortem.
  function labelsFor(kind) {
    return kind === 'Feature'
      ? { rootCause: 'Why',        solution: 'Decision' }
      : { rootCause: 'Root cause', solution: 'Solution' };
  }

  // ================== tab switching ==================

  function switchTab(name) {
    // Leaving the Add tab while editing-an-existing-entry clears the
    // editing state. Keeps the edit session bounded to that tab.
    if (state.tab === 'add' && name !== 'add' && state.editing) {
      state.editing = null;
      renderAddTab();
    }
    state.tab = name;

    // Update which tab button is .active
    document.querySelectorAll('.tab').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.tab === name);
    });
    // Show only the matching panel
    document.querySelectorAll('.tab-panel').forEach(panel => {
      panel.style.display = panel.dataset.panel === name ? '' : 'none';
    });

    // First time the user opens All, fetch the list. Subsequent visits
    // reuse the cached list — refreshing requires the explicit Refresh
    // button or a save (which clears the cache).
    if (name === 'all' && !state.entriesLoaded && !state.entriesLoading) {
      loadAllEntries();
    }
  }

  // ================== Ask tab ==================

  async function handleAsk() {
    const q = $('askQuestion').value.trim();
    if (!q) return;
    state.askLoading = true;
    state.askError = null;
    renderAskTab();
    try {
      state.askResponse = await api.ask(q, 5);
    } catch (e) {
      state.askError = e.message || 'Request failed';
      state.askResponse = null;
    } finally {
      state.askLoading = false;
      renderAskTab();
    }
  }

  function renderAskTab() {
    const btn = $('askButton');
    const q = $('askQuestion').value.trim();
    btn.disabled = state.askLoading || !q;
    btn.textContent = state.askLoading ? 'Thinking...' : 'Ask';

    const errEl = $('askError');
    if (state.askError) {
      errEl.style.display = '';
      errEl.textContent = state.askError;
    } else {
      errEl.style.display = 'none';
    }

    const resultEl = $('askResult');
    if (state.askResponse) {
      resultEl.style.display = '';
      $('askAnswerText').textContent = state.askResponse.answer;
      const cites = state.askResponse.citations || [];
      const label = $('askSourcesLabel');
      const sourcesEl = $('askSources');
      if (cites.length > 0) {
        label.style.display = '';
        label.textContent = `Sources (${cites.length})`;
        sourcesEl.innerHTML = cites
          .map(c => renderEntryCardHtml(c.entry, { score: c.score }))
          .join('');
      } else {
        label.style.display = 'none';
        sourcesEl.innerHTML = '';
      }
    } else {
      resultEl.style.display = 'none';
    }
  }

  // ================== Add tab ==================

  function setFormKind(kind) {
    state.formKind = kind;
    document.querySelectorAll('.kind-option[data-kind]').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.kind === kind);
    });
    // Show/hide feature-only fields
    document.querySelectorAll('.feature-only').forEach(el => {
      el.style.display = kind === 'Feature' ? '' : 'none';
    });
    // Flip labels
    const labels = labelsFor(kind);
    $('labelRootCause').textContent = labels.rootCause;
    $('labelSolution').textContent  = labels.solution;
    // Adapt placeholders so the form reads like the chosen kind
    if (kind === 'Feature') {
      $('fieldRootCause').placeholder = 'Why was this needed? What problem did it solve?';
      $('fieldSolution').placeholder  = 'What was decided / built?';
    } else {
      $('fieldRootCause').placeholder = 'Why did this bug happen?';
      $('fieldSolution').placeholder  = 'How was it fixed? Code change, config, workaround?';
    }
  }

  function setEditing(entry) {
    state.editing = entry;
    if (entry) {
      const kind = entry.kind || 'Bug';
      setFormKind(kind);
      $('fieldTitle').value     = entry.title || '';
      $('fieldTags').value      = (entry.tags || []).join(', ');
      $('fieldServices').value  = (entry.affectedServices || []).join(', ');
      $('fieldContext').value   = entry.context || '';
      $('fieldRootCause').value = entry.rootCause || '';
      $('fieldSolution').value  = entry.solution || '';
      $('fieldLinks').value     = (entry.links || []).join('\n');
    }
    renderAddTab();
  }

  function clearForm() {
    setFormKind('Bug');
    $('fieldTitle').value     = '';
    $('fieldTags').value      = '';
    $('fieldServices').value  = '';
    $('fieldContext').value   = '';
    $('fieldRootCause').value = '';
    $('fieldSolution').value  = '';
    $('fieldLinks').value     = '';
    state.review = null;
    state.reviewStatus = { type: 'idle', msg: '' };
  }

  function renderAddTab() {
    // Tab label flips between 'Add' and 'Edit'
    $('tab-add-label').textContent = state.editing ? 'Edit' : 'Add';

    // Save button label
    $('saveButton').textContent = state.editing ? 'Update' : 'Save';

    // Cancel button only appears while editing
    $('cancelEdit').style.display = state.editing ? '' : 'none';

    // Save button disabled during save
    $('saveButton').disabled = state.saveStatus.type === 'loading';

    // Status messages
    setStatus($('saveStatus'),   state.saveStatus.type,   state.saveStatus.msg);
    setStatus($('importStatus'), state.importStatus.type, state.importStatus.msg);
    setStatus($('reviewStatus'), state.reviewStatus.type, state.reviewStatus.msg);

    // Import panel toggle
    $('importBody').style.display = state.importOpen ? '' : 'none';
    $('importToggle').textContent = state.importOpen ? 'Hide' : 'Show';

    // Review button + pane
    $('reviewButton').disabled = state.reviewStatus.type === 'loading';
    renderReviewPane();
  }

  function renderReviewPane() {
    const pane = $('reviewPane');
    const review = state.review;
    if (!review) {
      pane.innerHTML = '<div class="review-empty">Click <strong>Review my input</strong> to have the AI check your Context against the affected service\'s local repo.</div>';
      return;
    }
    if (!review.answers) review.answers = {}; // { [index]: { status, answer?, evidence?, error? } }

    const summary = review.summary ? `<div class="review-summary">${escape(review.summary)}</div>` : '';

    let suggestionsHtml;
    if (review.suggestions && review.suggestions.length) {
      suggestionsHtml = '<ul class="review-suggestions">' + review.suggestions.map((s, i) => {
        const ans = review.answers[i];
        let answerBlock = '';
        if (ans) {
          if (ans.status === 'loading') {
            answerBlock = '<div class="answer-block loading">Searching repo...</div>';
          } else if (ans.status === 'error') {
            answerBlock = `<div class="answer-block error">${escape(ans.error || 'Lookup failed')}</div>`;
          } else if (ans.status === 'success') {
            const evidence = (ans.evidence && ans.evidence.length)
              ? `<div class="answer-evidence">Evidence: ${ans.evidence.map(escape).join(', ')}</div>`
              : '';
            answerBlock = `<div class="answer-block">${escape(ans.answer || '(no answer)')}${evidence}</div>`;
          }
        }
        const btnLabel = ans && ans.status === 'success' ? 'Re-find' : 'Find answer';
        const btnDisabled = ans && ans.status === 'loading' ? 'disabled' : '';
        return `<li>${escape(s)} <button class="link-btn" data-find-idx="${i}" ${btnDisabled}>${btnLabel}</button>${answerBlock}</li>`;
      }).join('') + '</ul>';
    } else {
      suggestionsHtml = '<div class="review-empty">No suggestions — looks good.</div>';
    }

    const scanned = (review.scannedRepos && review.scannedRepos.length)
      ? `Scanned: ${review.scannedRepos.map(escape).join(', ')}`
      : 'No local repos scanned — prose-only review';
    const unconfigured = (review.unconfiguredServices && review.unconfiguredServices.length)
      ? `<span class="warn">Unconfigured: ${review.unconfiguredServices.map(escape).join(', ')}</span>`
      : '';

    const rewriteBtn = (review.rewrittenContext && review.rewrittenContext.trim())
      ? `<div class="review-rewrite"><button class="small" id="applyRewrite" type="button">Apply AI rewrite to Context</button></div>`
      : '';

    pane.innerHTML = `${summary}${suggestionsHtml}${rewriteBtn}<div class="review-meta">${scanned}${unconfigured ? ' — ' + unconfigured : ''}</div>`;

    const applyBtn = $('applyRewrite');
    if (applyBtn) {
      applyBtn.addEventListener('click', () => {
        if (state.review && state.review.rewrittenContext) {
          $('fieldContext').value = state.review.rewrittenContext;
        }
      });
    }

    pane.querySelectorAll('[data-find-idx]').forEach(btn => {
      btn.addEventListener('click', () => {
        const idx = parseInt(btn.getAttribute('data-find-idx'), 10);
        handleFindAnswer(idx);
      });
    });
  }

  async function handleFindAnswer(index) {
    const review = state.review;
    if (!review || !review.suggestions || !review.suggestions[index]) return;
    const question = review.suggestions[index];
    const draftContext = $('fieldContext').value.trim();
    const tags = $('fieldTags').value.split(',').map(t => t.trim()).filter(Boolean);
    const affectedServices = $('fieldServices').value.split(',').map(s => s.trim()).filter(Boolean);

    if (!review.answers) review.answers = {};
    review.answers[index] = { status: 'loading' };
    renderReviewPane();

    try {
      const result = await api.findAnswer({ question, draftContext, tags, affectedServices });
      review.answers[index] = {
        status: 'success',
        answer: result.answer,
        evidence: result.evidence || [],
      };
    } catch (e) {
      review.answers[index] = { status: 'error', error: e.message || 'Lookup failed' };
    } finally {
      renderReviewPane();
    }
  }

  async function handleReview() {
    const context = $('fieldContext').value.trim();
    if (context.length < 10) {
      state.reviewStatus = { type: 'error', msg: 'Write a bit of Context first' };
      renderAddTab();
      return;
    }
    const tags = $('fieldTags').value.split(',').map(t => t.trim()).filter(Boolean);
    const affectedServices = $('fieldServices').value.split(',').map(s => s.trim()).filter(Boolean);

    state.reviewStatus = { type: 'loading', msg: 'Reviewing with AI...' };
    renderAddTab();
    try {
      const result = await api.review({ context, tags, affectedServices });
      state.review = result;
      const scannedCount = (result.scannedRepos || []).length;
      state.reviewStatus = scannedCount > 0
        ? { type: 'success', msg: `Reviewed against ${scannedCount} repo(s)` }
        : { type: 'success', msg: 'Reviewed (no local repos scanned)' };
    } catch (e) {
      state.reviewStatus = { type: 'error', msg: e.message || 'Review failed' };
    } finally {
      renderAddTab();
    }
  }

  async function handleExtract() {
    const text = $('importText').value;
    if (text.trim().length < 30) {
      state.importStatus = { type: 'error', msg: 'Paste a longer thread or chat to extract from' };
      renderAddTab();
      return;
    }
    // If form already has content, confirm before clobbering
    const hasExisting = ['fieldTitle','fieldTags','fieldContext','fieldRootCause','fieldSolution']
      .some(id => $(id).value.trim() !== '');
    if (hasExisting && !confirm('Form has content. Overwrite with extracted fields?')) return;

    state.importStatus = { type: 'loading', msg: 'Extracting with AI...' };
    renderAddTab();
    try {
      const result = await api.extract(text);
      // Extract is bug-shaped — switch the form to Bug kind.
      setFormKind('Bug');
      $('fieldTitle').value     = result.title     || '';
      $('fieldTags').value      = (result.tags || []).join(', ');
      $('fieldContext').value   = result.context   || '';
      $('fieldRootCause').value = result.rootCause || '';
      $('fieldSolution').value  = result.solution  || '';

      // Brief flash on the form to draw the eye to the new content.
      const formEl = $('addForm');
      formEl.classList.add('flash');
      setTimeout(() => formEl.classList.remove('flash'), 1400);

      state.importStatus = { type: 'success', msg: 'Extracted — review and save' };
    } catch (e) {
      state.importStatus = { type: 'error', msg: e.message || 'Extraction failed' };
    } finally {
      renderAddTab();
    }
  }

  async function handleSave() {
    const kind     = state.formKind;
    const title    = $('fieldTitle').value.trim();
    const tagsRaw  = $('fieldTags').value;
    const services = $('fieldServices').value;
    const context  = $('fieldContext').value.trim();
    const rootCause= $('fieldRootCause').value.trim();
    const solution = $('fieldSolution').value.trim();
    const linksRaw = $('fieldLinks').value;

    if (!title) {
      state.saveStatus = { type: 'error', msg: 'Title is required' };
      renderAddTab();
      return;
    }
    if (!context && !rootCause && !solution) {
      const labels = labelsFor(kind);
      state.saveStatus = { type: 'error', msg: `Fill at least one of: context, ${labels.rootCause.toLowerCase()}, ${labels.solution.toLowerCase()}` };
      renderAddTab();
      return;
    }

    const body = {
      kind,
      title,
      tags: tagsRaw.split(',').map(t => t.trim()).filter(Boolean),
      context, rootCause, solution,
      affectedServices: services.split(',').map(s => s.trim()).filter(Boolean),
      links: linksRaw.split(/[\n,]/).map(l => l.trim()).filter(Boolean),
    };

    state.saveStatus = { type: 'loading', msg: 'Saving...' };
    renderAddTab();
    try {
      if (state.editing) {
        await api.update(state.editing.id, body);
      } else {
        await api.create(body);
      }
      state.saveStatus = { type: 'success', msg: state.editing ? 'Updated' : 'Saved' };
      // After a successful save: clear form, reset edit state, mark
      // the cached All-tab list as stale so it refetches next visit.
      clearForm();
      $('importText').value = '';
      state.importStatus = { type: 'idle', msg: '' };
      state.editing = null;
      state.entriesLoaded = false;
      if (state.tab === 'all') {
        loadAllEntries();
      }
      renderAddTab();
      setTimeout(() => {
        state.saveStatus = { type: 'idle', msg: '' };
        renderAddTab();
      }, 2000);
    } catch (e) {
      state.saveStatus = { type: 'error', msg: e.message || 'Save failed' };
      renderAddTab();
    }
  }

  // ================== All tab ==================

  async function loadAllEntries() {
    state.entriesLoading = true;
    state.entriesError = null;
    renderAllTab();
    try {
      state.entries = await api.list(state.listKind);
      state.entriesLoaded = true;
    } catch (e) {
      state.entriesError = e.message || 'Failed to load';
    } finally {
      state.entriesLoading = false;
      renderAllTab();
    }
  }

  function setListKind(kind) {
    state.listKind = kind;
    document.querySelectorAll('.kind-option[data-list-kind]').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.listKind === kind);
    });
    state.entriesLoaded = false;
    loadAllEntries();
  }

  async function handleDelete(id) {
    const entry = state.entries.find(e => e.id === id);
    if (!entry) return;
    if (!confirm(`Delete "${entry.title}"?`)) return;
    try {
      await api.remove(id);
      state.entries = state.entries.filter(e => e.id !== id);
      renderAllTab();
    } catch (e) {
      alert(e.message || 'Delete failed');
    }
  }

  function handleEdit(id) {
    const entry = state.entries.find(e => e.id === id);
    if (!entry) return;
    setEditing(entry);
    switchTab('add');
  }

  function renderAllTab() {
    const container = $('allList');

    if (state.entriesLoading) {
      container.innerHTML = '<div class="empty">Loading...</div>';
      return;
    }
    if (state.entriesError) {
      container.innerHTML = `<div class="status error">${escape(state.entriesError)}</div>`;
      return;
    }

    const f = state.filter.trim().toLowerCase();
    const filtered = f
      ? state.entries.filter(e => {
          const haystack = [
            e.title,
            (e.tags || []).join(' '),
            (e.affectedServices || []).join(' '),
            e.context, e.rootCause, e.solution,
          ].join(' ').toLowerCase();
          return haystack.includes(f);
        })
      : state.entries;

    if (filtered.length === 0) {
      const msg = state.entries.length === 0 ? 'Nothing saved yet.' : 'No entries match the filter.';
      container.innerHTML = `<div class="empty">${msg}</div>`;
      return;
    }

    container.innerHTML = filtered
      .map(entry => renderEntryCardHtml(entry, { withActions: true }))
      .join('');

    // Wire up Edit / Delete buttons after render. Using event delegation
    // (one click handler on the container) would also work; per-button
    // wiring is simpler to reason about given the small list size.
    container.querySelectorAll('button[data-action="edit"]').forEach(btn => {
      btn.addEventListener('click', () => handleEdit(btn.dataset.id));
    });
    container.querySelectorAll('button[data-action="delete"]').forEach(btn => {
      btn.addEventListener('click', () => handleDelete(btn.dataset.id));
    });
  }

  // ================== Card renderer (returns HTML) ==================
  // Used by both the Ask tab (citation cards, no actions) and the All tab
  // (with Edit/Delete buttons). The `score` option adds a "X% match" pill
  // for citation contexts.

  function renderEntryCardHtml(entry, opts) {
    opts = opts || {};
    const kind = entry.kind || 'Bug';
    const labels = labelsFor(kind);
    const kindBadge = `<span class="tag kind kind-${kind.toLowerCase()}">${kind}</span>`;
    const tags = (entry.tags || []).map(t => `<span class="tag">${escape(t)}</span>`).join('');
    const scoreTag = (opts.score != null)
      ? `<span class="tag score">${Math.round(opts.score * 100)}% match</span>`
      : '';

    const sections = [];
    const services = entry.affectedServices || [];
    if (services.length > 0) sections.push(section('Affected services', services.join(', ')));
    if (entry.context)   sections.push(section('Context',          entry.context));
    if (entry.rootCause) sections.push(section(labels.rootCause,   entry.rootCause));
    if (entry.solution)  sections.push(section(labels.solution,    entry.solution));
    const links = entry.links || [];
    if (links.length > 0) sections.push(linksSection(links));

    const actions = opts.withActions
      ? `<div class="actions">
           <button class="small" data-action="edit"   data-id="${escape(entry.id)}">Edit</button>
           <button class="small danger" data-action="delete" data-id="${escape(entry.id)}">Delete</button>
         </div>`
      : '';

    return `
      <div class="card">
        <div class="card-header">
          <div style="flex: 1; min-width: 0;">
            <h3 class="card-title">${escape(entry.title)}</h3>
            <div class="tags">${kindBadge}${tags}${scoreTag}</div>
          </div>
          <span class="card-meta">${escape(formatDate(entry.updatedAt))}</span>
        </div>
        ${sections.join('')}
        ${actions}
      </div>`;
  }

  function section(label, body) {
    return `<div class="section">
              <div class="section-label">${escape(label)}</div>
              <div class="section-body">${escape(body)}</div>
            </div>`;
  }

  // Render link list. http(s) values become anchors; everything else
  // (local file paths) renders as plain code-style text — browsers refuse
  // to follow file:// from an http origin, so a clickable file:// link
  // would just look broken.
  function linksSection(links) {
    const items = links.map(l => {
      if (/^https?:\/\//i.test(l)) {
        return `<li><a href="${escape(l)}" target="_blank" rel="noreferrer noopener">${escape(l)}</a></li>`;
      }
      return `<li><code>${escape(l)}</code></li>`;
    }).join('');
    return `<div class="section">
              <div class="section-label">Links</div>
              <ul class="link-list">${items}</ul>
            </div>`;
  }

  // ================== wiring ==================

  function init() {
    // Tab buttons
    document.querySelectorAll('.tab').forEach(btn => {
      btn.addEventListener('click', () => switchTab(btn.dataset.tab));
    });

    // Ask
    $('askButton').addEventListener('click', handleAsk);
    $('askQuestion').addEventListener('input', renderAskTab);
    $('askQuestion').addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) handleAsk();
    });

    // Add — kind toggle
    document.querySelectorAll('.kind-option[data-kind]').forEach(btn => {
      btn.addEventListener('click', () => setFormKind(btn.dataset.kind));
    });

    // Add — buttons
    $('saveButton').addEventListener('click', handleSave);
    $('clearForm').addEventListener('click', () => { clearForm(); renderAddTab(); });
    $('cancelEdit').addEventListener('click', () => { setEditing(null); clearForm(); });

    // Add — import panel
    $('importToggle').addEventListener('click', () => {
      state.importOpen = !state.importOpen;
      renderAddTab();
    });
    $('importExtract').addEventListener('click', handleExtract);
    $('importClear').addEventListener('click', () => {
      $('importText').value = '';
      state.importStatus = { type: 'idle', msg: '' };
      renderAddTab();
    });

    // Add — review pane
    $('reviewButton').addEventListener('click', handleReview);

    // All — kind filter, text filter, refresh
    document.querySelectorAll('.kind-option[data-list-kind]').forEach(btn => {
      btn.addEventListener('click', () => setListKind(btn.dataset.listKind));
    });
    $('filterText').addEventListener('input', (e) => {
      state.filter = e.target.value;
      renderAllTab();
    });
    $('refreshList').addEventListener('click', loadAllEntries);

    // Initial form-kind state
    setFormKind('Bug');

    // Initial render of each tab so they have correct state on first show
    renderAskTab();
    renderAddTab();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
