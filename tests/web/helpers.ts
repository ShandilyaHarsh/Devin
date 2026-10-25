import { vi } from 'vitest';

export interface Call {
  url: string;
  method: string;
  headers: Headers;
  body: string | null;
  credentials: RequestCredentials | undefined;
}

export type Route = (call: Call) => Response | undefined;

export function json(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init.headers as Record<string, string> | undefined) },
  });
}

/** Stub global fetch; records every call normalised to {url, method, headers, body}. */
export function stubFetch(route: Route): Call[] {
  const calls: Call[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const call: Call = {
      url,
      method: (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase(),
      headers: new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined)),
      body: typeof init?.body === 'string' ? init.body : null,
      credentials: init?.credentials,
    };
    calls.push(call);
    const response = route(call);
    if (!response) {
      return json({ title: 'not stubbed: ' + url, status: 404 }, { status: 404 });
    }
    return response;
  });
  vi.stubGlobal('fetch', fetchMock);
  return calls;
}

export function pathOf(url: string): string {
  return new URL(url, 'https://localhost').pathname;
}
