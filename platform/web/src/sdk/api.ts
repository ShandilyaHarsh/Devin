import type { ProblemDetails } from './types';

let csrfToken = '';

/** Sets the CSRF token sent on every mutating request (taken from GET /api/session). Kept in memory only. */
export function setCsrfToken(token: string): void {
  csrfToken = token;
}

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

export interface MutationOptions {
  idempotencyKey?: string;
}

export interface WithMeta<T> {
  data: T;
  replayed: boolean;
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    const text = await response.text();
    if (text) {
      const parsed: unknown = JSON.parse(text);
      if (parsed && typeof parsed === 'object') {
        return { status: response.status, ...(parsed as ProblemDetails) };
      }
    }
  } catch {
    // Non-JSON error bodies are ignored; never surface raw server output.
  }
  return { status: response.status, title: response.statusText || 'Request failed' };
}

async function request<T>(
  method: Method,
  path: string,
  body: unknown,
  options: MutationOptions | undefined,
): Promise<WithMeta<T>> {
  if (!path.startsWith('/') || path.startsWith('//')) {
    throw new Error('api paths must be same-origin absolute paths');
  }
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') {
    headers['Content-Type'] = 'application/json';
    headers['X-CSRF-Token'] = csrfToken;
    headers['Idempotency-Key'] = options?.idempotencyKey ?? newIdempotencyKey();
  }
  const init: RequestInit = { method, headers, credentials: 'same-origin', cache: 'no-store' };
  if (method !== 'GET' && body !== undefined) {
    init.body = JSON.stringify(body);
  }
  const response = await fetch(path, init);
  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response));
  }
  const replayed = response.headers.get('Idempotency-Replayed') === 'true';
  if (response.status === 204) {
    return { data: undefined as T, replayed };
  }
  const text = await response.text();
  return { data: (text ? JSON.parse(text) : undefined) as T, replayed };
}

export const api = {
  async get<T>(path: string): Promise<T> {
    return (await request<T>('GET', path, undefined, undefined)).data;
  },
  async post<T>(path: string, body?: unknown, options?: MutationOptions): Promise<T> {
    return (await request<T>('POST', path, body, options)).data;
  },
  postWithMeta<T>(path: string, body?: unknown, options?: MutationOptions): Promise<WithMeta<T>> {
    return request<T>('POST', path, body, options);
  },
  async put<T>(path: string, body?: unknown, options?: MutationOptions): Promise<T> {
    return (await request<T>('PUT', path, body, options)).data;
  },
  async del<T>(path: string, options?: MutationOptions): Promise<T> {
    return (await request<T>('DELETE', path, undefined, options)).data;
  },
};
