import type { BugMemory } from '../types';

interface Props {
  bug: BugMemory;
  score?: number;
  onEdit?: (bug: BugMemory) => void;
  onDelete?: (id: string) => void;
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

export function BugCard({ bug, score, onEdit, onDelete }: Props) {
  return (
    <div className="card">
      <div className="card-header">
        <div style={{ flex: 1, minWidth: 0 }}>
          <h3 className="card-title">{bug.title}</h3>
          <div className="tags">
            {bug.tags.map(t => <span key={t} className="tag">{t}</span>)}
            {score != null && (
              <span className="tag score">{Math.round(score * 100)}% match</span>
            )}
          </div>
        </div>
        <span className="card-meta">{formatDate(bug.updatedAt)}</span>
      </div>

      {bug.context && (
        <div className="section">
          <div className="section-label">Context</div>
          <div className="section-body">{bug.context}</div>
        </div>
      )}
      {bug.rootCause && (
        <div className="section">
          <div className="section-label">Root cause</div>
          <div className="section-body">{bug.rootCause}</div>
        </div>
      )}
      {bug.solution && (
        <div className="section">
          <div className="section-label">Solution</div>
          <div className="section-body">{bug.solution}</div>
        </div>
      )}

      {(onEdit || onDelete) && (
        <div className="actions">
          {onEdit && <button className="small" onClick={() => onEdit(bug)}>Edit</button>}
          {onDelete && (
            <button className="small danger" onClick={() => onDelete(bug.id)}>
              Delete
            </button>
          )}
        </div>
      )}
    </div>
  );
}
