export interface BugMemory {
  id: string;
  title: string;
  tags: string[];
  context: string;
  rootCause: string;
  solution: string;
  createdAt: string;
  updatedAt: string;
}

export interface SearchResult {
  entry: BugMemory;
  score: number;
}

export interface RagResponse {
  answer: string;
  citations: SearchResult[];
}

export interface ExtractionResult {
  title: string;
  tags: string[];
  context: string;
  rootCause: string;
  solution: string;
}

export interface BugMemoryFormFields {
  title: string;
  tags: string;
  context: string;
  rootCause: string;
  solution: string;
}
