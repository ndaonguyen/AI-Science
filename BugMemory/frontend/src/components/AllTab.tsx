import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { BugMemory } from '../types';
import { BugCard } from './BugCard';

interface Props {
  refreshKey: number;
  onEdit: (bug: BugMemory) => void;
}

export function AllTab({ refreshKey, onEdit }: Props) {
  const [bugs, setBugs] = useState<BugMemory[]>([]);
  const [filter, setFilter] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const data = await api.list();
      setBugs(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load');
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [refreshKey]);

  async function handleDelete(id: string) {
    const bug = bugs.find(b => b.id === id);
    if (!bug) return;
    if (!confirm(`Delete "${bug.title}"?`)) return;
    try {
      await api.remove(id);
      setBugs(bs => bs.filter(b => b.id !== id));
    } catch (e) {
      alert(e instanceof Error ? e.message : 'Delete failed');
    }
  }

  const filtered = filter.trim()
    ? bugs.filter(b => {
        const haystack = [b.title, b.tags.join(' '), b.context, b.rootCause, b.solution].join(' ').toLowerCase();
        return haystack.includes(filter.toLowerCase());
      })
    : bugs;

  return (
    <div>
      <div className="row" style={{ marginBottom: 16 }}>
        <input
          value={filter}
          onChange={e => setFilter(e.target.value)}
          placeholder="Filter by tag or text..."
          style={{ flex: 1 }}
        />
        <button onClick={load}>Refresh</button>
      </div>

      {loading && <div className="empty">Loading...</div>}
      {error && <div className="status error">{error}</div>}
      {!loading && !error && filtered.length === 0 && (
        <div className="empty">
          {bugs.length === 0 ? 'No bugs saved yet.' : 'No bugs match the filter.'}
        </div>
      )}
      {filtered.map(bug => (
        <BugCard key={bug.id} bug={bug} onEdit={onEdit} onDelete={handleDelete} />
      ))}
    </div>
  );
}
