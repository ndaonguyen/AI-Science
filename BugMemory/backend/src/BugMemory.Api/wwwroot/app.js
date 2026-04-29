// Bug Memory — vanilla JS frontend.
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
    editing: null,              // BugMemory | null — non-null = editing existing
    bugs: [],                   // BugMemory[] — populated when All tab is opened
    bugsLoaded: false,          // false until first All-tab fetch completes
    bugsLoading: false,
    bugsError: null,
    askLoading: false,
    askResponse: null,          // RagResponse | null
    askError: null,
    importOpen: false,
    importStatus: { type: 'idle', msg: '' },
    saveStatus: { type: 'idle', msg: '' },
    filter: '',
  };

  // ================== api ==================
  // Plain fetch wrapper. Same shape as the old TS client. Throws on non-2xx
  // with the response body in the message — backend's PR #46 surfaces
  // OpenAI/Qdrant error bodies, so we want to display those verbatim.

  const api = {
    list:    ()              => req('/api/bugs'),
    get:     (id)            => req(`/api/bugs/${id}`),
    create:  (body)          => req('/api/bugs',     { method: 'POST',   body: JSON.stringify(body) }),
    update:  (id, body)      => req(`/api/bugs/${id}`, { method: 'PUT',  body: JSON.stringify(body) }),
    remove:  (id)            => req(`/api/bugs/${id}`, { method: 'DELETE' }),
    search:  (query, topK=5) => req('/api/search',   { method: 'POST', body: JSON.stringify({ query, topK }) }),
    ask:     (question, topK=5) => req('/api/ask',   { method: 'POST', body: JSON.stringify({ question, topK }) }),
    extract: (sourceText)    => req('/api/extract',  { method: 'POST', body: JSON.stringify({ sourceText }) }),
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

  // ================== tab switching ==================

  function switchTab(name) {
    // Match the React App.tsx behaviour: leaving the Add tab while
    // editing-an-existing-bug clears the editing state. Keeps the edit
    // session bounded to that tab.
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
    // button or a save (which clears the cache via handleSaved).
    if (name === 'all' && !state.bugsLoaded && !state.bugsLoading) {
      loadAllBugs();
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
          .map(c => renderBugCardHtml(c.entry, { score: c.score }))
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

  function setEditing(bug) {
    state.editing = bug;
    if (bug) {
      $('fieldTitle').value     = bug.title || '';
      $('fieldTags').value      = (bug.tags || []).join(', ');
      $('fieldContext').value   = bug.context || '';
      $('fieldRootCause').value = bug.rootCause || '';
      $('fieldSolution').value  = bug.solution || '';
    }
    renderAddTab();
  }

  function clearForm() {
    $('fieldTitle').value     = '';
    $('fieldTags').value      = '';
    $('fieldContext').value   = '';
    $('fieldRootCause').value = '';
    $('fieldSolution').value  = '';
  }

  function renderAddTab() {
    // Tab label flips between 'Add bug' and 'Edit bug'
    $('tab-add-label').textContent = state.editing ? 'Edit bug' : 'Add bug';

    // Save button label
    $('saveButton').textContent = state.editing ? 'Update bug' : 'Save bug';

    // Cancel button only appears while editing
    $('cancelEdit').style.display = state.editing ? '' : 'none';

    // Save button disabled during save
    $('saveButton').disabled = state.saveStatus.type === 'loading';

    // Status messages
    setStatus($('saveStatus'),   state.saveStatus.type,   state.saveStatus.msg);
    setStatus($('importStatus'), state.importStatus.type, state.importStatus.msg);

    // Import panel toggle
    $('importBody').style.display = state.importOpen ? '' : 'none';
    $('importToggle').textContent = state.importOpen ? 'Hide' : 'Show';
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
      $('fieldTitle').value     = result.title     || '';
      $('fieldTags').value      = (result.tags || []).join(', ');
      $('fieldContext').value   = result.context   || '';
      $('fieldRootCause').value = result.rootCause || '';
      $('fieldSolution').value  = result.solution  || '';

      // Brief flash on the form to draw the eye to the new content.
      // Same animation as the React app — class + setTimeout.
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
    const title    = $('fieldTitle').value.trim();
    const tagsRaw  = $('fieldTags').value;
    const context  = $('fieldContext').value.trim();
    const rootCause= $('fieldRootCause').value.trim();
    const solution = $('fieldSolution').value.trim();

    if (!title) {
      state.saveStatus = { type: 'error', msg: 'Title is required' };
      renderAddTab();
      return;
    }
    if (!context && !rootCause && !solution) {
      state.saveStatus = { type: 'error', msg: 'Fill at least one of: context, root cause, solution' };
      renderAddTab();
      return;
    }

    const body = {
      title,
      tags: tagsRaw.split(',').map(t => t.trim()).filter(Boolean),
      context, rootCause, solution,
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
      // If the user is currently ON the All tab somehow (rare — saving
      // happens from Add — but possible after switching mid-save),
      // refetch immediately so they see the new state.
      clearForm();
      $('importText').value = '';
      state.importStatus = { type: 'idle', msg: '' };
      state.editing = null;
      state.bugsLoaded = false;
      if (state.tab === 'all') {
        loadAllBugs();
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

  async function loadAllBugs() {
    state.bugsLoading = true;
    state.bugsError = null;
    renderAllTab();
    try {
      state.bugs = await api.list();
      state.bugsLoaded = true;
    } catch (e) {
      state.bugsError = e.message || 'Failed to load';
    } finally {
      state.bugsLoading = false;
      renderAllTab();
    }
  }

  async function handleDelete(id) {
    const bug = state.bugs.find(b => b.id === id);
    if (!bug) return;
    if (!confirm(`Delete "${bug.title}"?`)) return;
    try {
      await api.remove(id);
      state.bugs = state.bugs.filter(b => b.id !== id);
      renderAllTab();
    } catch (e) {
      alert(e.message || 'Delete failed');
    }
  }

  function handleEdit(id) {
    const bug = state.bugs.find(b => b.id === id);
    if (!bug) return;
    setEditing(bug);
    switchTab('add');
  }

  function renderAllTab() {
    const container = $('allList');

    if (state.bugsLoading) {
      container.innerHTML = '<div class="empty">Loading...</div>';
      return;
    }
    if (state.bugsError) {
      container.innerHTML = `<div class="status error">${escape(state.bugsError)}</div>`;
      return;
    }

    const f = state.filter.trim().toLowerCase();
    const filtered = f
      ? state.bugs.filter(b => {
          const haystack = [b.title, (b.tags || []).join(' '), b.context, b.rootCause, b.solution]
            .join(' ').toLowerCase();
          return haystack.includes(f);
        })
      : state.bugs;

    if (filtered.length === 0) {
      const msg = state.bugs.length === 0 ? 'No bugs saved yet.' : 'No bugs match the filter.';
      container.innerHTML = `<div class="empty">${msg}</div>`;
      return;
    }

    container.innerHTML = filtered
      .map(bug => renderBugCardHtml(bug, { withActions: true }))
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

  // ================== BugCard renderer (returns HTML) ==================
  // Used by both the Ask tab (citation cards, no actions) and the All tab
  // (with Edit/Delete buttons). The `score` option adds a "X% match" pill
  // for citation contexts.

  function renderBugCardHtml(bug, opts) {
    opts = opts || {};
    const tags = (bug.tags || []).map(t => `<span class="tag">${escape(t)}</span>`).join('');
    const scoreTag = (opts.score != null)
      ? `<span class="tag score">${Math.round(opts.score * 100)}% match</span>`
      : '';

    const sections = [];
    if (bug.context)   sections.push(section('Context',    bug.context));
    if (bug.rootCause) sections.push(section('Root cause', bug.rootCause));
    if (bug.solution)  sections.push(section('Solution',   bug.solution));

    const actions = opts.withActions
      ? `<div class="actions">
           <button class="small" data-action="edit"   data-id="${escape(bug.id)}">Edit</button>
           <button class="small danger" data-action="delete" data-id="${escape(bug.id)}">Delete</button>
         </div>`
      : '';

    return `
      <div class="card">
        <div class="card-header">
          <div style="flex: 1; min-width: 0;">
            <h3 class="card-title">${escape(bug.title)}</h3>
            <div class="tags">${tags}${scoreTag}</div>
          </div>
          <span class="card-meta">${escape(formatDate(bug.updatedAt))}</span>
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

    // All — filter & refresh
    $('filterText').addEventListener('input', (e) => {
      state.filter = e.target.value;
      renderAllTab();
    });
    $('refreshList').addEventListener('click', loadAllBugs);

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
