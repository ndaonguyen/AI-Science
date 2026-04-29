import { useState } from 'react';
import { api } from '../api/client';
import type { RagResponse } from '../types';
import { BugCard } from './BugCard';

export function AskTab() {
  const [question, setQuestion] = useState('');
  const [response, setResponse] = useState<RagResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleAsk() {
    if (!question.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const result = await api.ask(question, 5);
      setResponse(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Request failed');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div>
      <div className="field">
        <label className="label">Ask your bug memory</label>
        <textarea
          value={question}
          onChange={e => setQuestion(e.target.value)}
          placeholder="e.g. How did we fix the duplicate key error in content-media-service?"
          onKeyDown={e => {
            if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) handleAsk();
          }}
        />
        <div className="hint">Cmd/Ctrl + Enter to ask</div>
      </div>
      <div className="row end">
        <button className="primary" onClick={handleAsk} disabled={loading || !question.trim()}>
          {loading ? 'Thinking...' : 'Ask'}
        </button>
      </div>

      {error && <div className="status error" style={{ marginTop: 16 }}>{error}</div>}

      {response && (
        <div style={{ marginTop: 24 }}>
          <div className="answer-card">
            <h3>Answer</h3>
            <div className="answer-text">{response.answer}</div>
          </div>

          {response.citations.length > 0 && (
            <>
              <div className="section-label" style={{ marginBottom: 8 }}>
                Sources ({response.citations.length})
              </div>
              {response.citations.map(c => (
                <BugCard key={c.entry.id} bug={c.entry} score={c.score} />
              ))}
            </>
          )}
        </div>
      )}
    </div>
  );
}
