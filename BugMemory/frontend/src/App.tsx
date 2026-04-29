import { useState } from 'react';
import { AskTab } from './components/AskTab';
import { AddTab } from './components/AddTab';
import { AllTab } from './components/AllTab';
import type { BugMemory } from './types';

type Tab = 'ask' | 'add' | 'all';

export default function App() {
  const [tab, setTab] = useState<Tab>('ask');
  const [editing, setEditing] = useState<BugMemory | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  function handleEdit(bug: BugMemory) {
    setEditing(bug);
    setTab('add');
  }

  function handleSaved() {
    setEditing(null);
    setRefreshKey(k => k + 1);
  }

  function handleCancelEdit() {
    setEditing(null);
  }

  function switchTab(next: Tab) {
    if (next !== 'add') setEditing(null);
    setTab(next);
  }

  return (
    <div className="app">
      <header className="header">
        <h1>Bug memory</h1>
        <p>Your personal RAG-powered bug knowledge base</p>
      </header>

      <div className="tabs">
        <button className={`tab ${tab === 'ask' ? 'active' : ''}`} onClick={() => switchTab('ask')}>Ask</button>
        <button className={`tab ${tab === 'add' ? 'active' : ''}`} onClick={() => switchTab('add')}>
          {editing ? 'Edit bug' : 'Add bug'}
        </button>
        <button className={`tab ${tab === 'all' ? 'active' : ''}`} onClick={() => switchTab('all')}>All bugs</button>
      </div>

      {tab === 'ask' && <AskTab />}
      {tab === 'add' && <AddTab editing={editing} onSaved={handleSaved} onCancelEdit={handleCancelEdit} />}
      {tab === 'all' && <AllTab refreshKey={refreshKey} onEdit={handleEdit} />}
    </div>
  );
}
