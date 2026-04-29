import type {
  BugMemory,
  SearchResult,
  RagResponse,
  ExtractionResult,
} from '../types';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`${res.status}: ${text || res.statusText}`);
  }
  if (res.status === 204) return undefined as T;
  return res.json();
}

export const api = {
  list: () => request<BugMemory[]>('/api/bugs'),

  get: (id: string) => request<BugMemory>(`/api/bugs/${id}`),

  create: (body: Omit<BugMemory, 'id' | 'createdAt' | 'updatedAt'>) =>
    request<BugMemory>('/api/bugs', { method: 'POST', body: JSON.stringify(body) }),

  update: (id: string, body: Omit<BugMemory, 'id' | 'createdAt' | 'updatedAt'>) =>
    request<BugMemory>(`/api/bugs/${id}`, { method: 'PUT', body: JSON.stringify(body) }),

  remove: (id: string) =>
    request<void>(`/api/bugs/${id}`, { method: 'DELETE' }),

  search: (query: string, topK = 5) =>
    request<SearchResult[]>('/api/search', {
      method: 'POST',
      body: JSON.stringify({ query, topK }),
    }),

  ask: (question: string, topK = 5) =>
    request<RagResponse>('/api/ask', {
      method: 'POST',
      body: JSON.stringify({ question, topK }),
    }),

  extract: (sourceText: string) =>
    request<ExtractionResult>('/api/extract', {
      method: 'POST',
      body: JSON.stringify({ sourceText }),
    }),
};
