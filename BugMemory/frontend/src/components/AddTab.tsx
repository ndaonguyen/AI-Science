import { useState } from 'react';
import { api } from '../api/client';
import type { BugMemory, BugMemoryFormFields } from '../types';

interface Props {
  editing: BugMemory | null;
  onSaved: () => void;
  onCancelEdit: () => void;
}

const emptyFields: BugMemoryFormFields = {
  title: '',
  tags: '',
  context: '',
  rootCause: '',
  solution: '',
};

export function AddTab({ editing, onSaved, onCancelEdit }: Props) {
  const [fields, setFields] = useState<BugMemoryFormFields>(
    editing
      ? {
          title: editing.title,
          tags: editing.tags.join(', '),
          context: editing.context,
          rootCause: editing.rootCause,
          solution: editing.solution,
        }
      : emptyFields
  );
  const [importText, setImportText] = useState('');
  const [importOpen, setImportOpen] = useState(false);
  const [importStatus, setImportStatus] = useState<{ type: 'idle' | 'loading' | 'error' | 'success'; msg: string }>({
    type: 'idle',
    msg: '',
  });
  const [saveStatus, setSaveStatus] = useState<{ type: 'idle' | 'loading' | 'error' | 'success'; msg: string }>({
    type: 'idle',
    msg: '',
  });
  const [flashing, setFlashing] = useState(false);

  function update<K extends keyof BugMemoryFormFields>(key: K, value: BugMemoryFormFields[K]) {
    setFields(f => ({ ...f, [key]: value }));
  }

  async function handleExtract() {
    if (importText.trim().length < 30) {
      setImportStatus({ type: 'error', msg: 'Paste a longer thread or chat to extract from' });
      return;
    }
    const hasExisting = Object.values(fields).some(v => v.trim());
    if (hasExisting && !confirm('Form has content. Overwrite with extracted fields?')) {
      return;
    }
    setImportStatus({ type: 'loading', msg: 'Extracting with AI...' });
    try {
      const result = await api.extract(importText);
      setFields({
        title: result.title,
        tags: result.tags.join(', '),
        context: result.context,
        rootCause: result.rootCause,
        solution: result.solution,
      });
      setFlashing(true);
      setTimeout(() => setFlashing(false), 1400);
      setImportStatus({ type: 'success', msg: 'Extracted — review and save' });
    } catch (e) {
      setImportStatus({ type: 'error', msg: e instanceof Error ? e.message : 'Extraction failed' });
    }
  }

  async function handleSave() {
    if (!fields.title.trim()) {
      setSaveStatus({ type: 'error', msg: 'Title is required' });
      return;
    }
    if (!fields.context.trim() && !fields.rootCause.trim() && !fields.solution.trim()) {
      setSaveStatus({ type: 'error', msg: 'Fill at least one of: context, root cause, solution' });
      return;
    }
    const body = {
      title: fields.title.trim(),
      tags: fields.tags.split(',').map(t => t.trim()).filter(Boolean),
      context: fields.context.trim(),
      rootCause: fields.rootCause.trim(),
      solution: fields.solution.trim(),
    };
    setSaveStatus({ type: 'loading', msg: 'Saving...' });
    try {
      if (editing) {
        await api.update(editing.id, body);
      } else {
        await api.create(body);
      }
      setSaveStatus({ type: 'success', msg: editing ? 'Updated' : 'Saved' });
      setFields(emptyFields);
      setImportText('');
      setImportStatus({ type: 'idle', msg: '' });
      onSaved();
      setTimeout(() => setSaveStatus({ type: 'idle', msg: '' }), 2000);
    } catch (e) {
      setSaveStatus({ type: 'error', msg: e instanceof Error ? e.message : 'Save failed' });
    }
  }

  return (
    <div>
      <div className="import-panel">
        <div className="row between" style={{ alignItems: 'flex-start' }}>
          <div>
            <h3>Import from chat or Slack thread</h3>
            <div className="hint">Paste raw text — AI will extract title, tags, context, root cause, and solution.</div>
          </div>
          <button className="small" onClick={() => setImportOpen(o => !o)}>
            {importOpen ? 'Hide' : 'Show'}
          </button>
        </div>
        {importOpen && (
          <>
            <textarea
              value={importText}
              onChange={e => setImportText(e.target.value)}
              placeholder="Paste a Slack thread, error log, or chat conversation..."
              style={{ minHeight: 140, marginTop: 10 }}
            />
            <div className="row between" style={{ marginTop: 8 }}>
              <span className={`status ${importStatus.type === 'idle' ? '' : importStatus.type}`}>
                {importStatus.msg}
              </span>
              <div className="row">
                <button className="small" onClick={() => { setImportText(''); setImportStatus({ type: 'idle', msg: '' }); }}>
                  Clear
                </button>
                <button onClick={handleExtract} disabled={importStatus.type === 'loading'}>
                  Extract fields
                </button>
              </div>
            </div>
          </>
        )}
      </div>

      <div className="divider" />

      <div className={flashing ? 'flash' : ''}>
        <div className="field">
          <label className="label">Title</label>
          <input
            value={fields.title}
            onChange={e => update('title', e.target.value)}
            placeholder="Short summary of the bug"
          />
        </div>
        <div className="field">
          <label className="label">Tags <span style={{ color: 'var(--text-faint)', fontWeight: 400 }}>(comma-separated)</span></label>
          <input
            value={fields.tags}
            onChange={e => update('tags', e.target.value)}
            placeholder="content-media-service, kafka, opensearch"
          />
        </div>
        <div className="field">
          <label className="label">Context</label>
          <textarea
            value={fields.context}
            onChange={e => update('context', e.target.value)}
            placeholder="What were you doing? Which service, environment, conditions?"
          />
        </div>
        <div className="field">
          <label className="label">Root cause</label>
          <textarea
            value={fields.rootCause}
            onChange={e => update('rootCause', e.target.value)}
            placeholder="Why did this bug happen?"
          />
        </div>
        <div className="field">
          <label className="label">Solution</label>
          <textarea
            value={fields.solution}
            onChange={e => update('solution', e.target.value)}
            placeholder="How was it fixed? Code change, config, workaround?"
          />
        </div>
      </div>

      <div className="row end" style={{ gap: 8, marginTop: 8 }}>
        <span className={`status ${saveStatus.type === 'idle' ? '' : saveStatus.type}`} style={{ marginRight: 'auto' }}>
          {saveStatus.msg}
        </span>
        {editing && <button onClick={onCancelEdit}>Cancel</button>}
        <button onClick={() => setFields(emptyFields)}>Clear</button>
        <button className="primary" onClick={handleSave} disabled={saveStatus.type === 'loading'}>
          {editing ? 'Update bug' : 'Save bug'}
        </button>
      </div>
    </div>
  );
}
