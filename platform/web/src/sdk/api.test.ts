import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, setCsrfToken } from './api';

function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' }, ...init });
}

describe('api', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
    setCsrfToken('csrf-123');
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function headersOf(call: number): Record<string, string> {
    return fetchMock.mock.calls[call]?.[1]?.headers as Record<string, string>;
  }

  it('sends CSRF token, JSON content type and the given idempotency key on POST', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ ok: true }));
    const result = await api.post<{ ok: boolean }>('/api/x', { a: 1 }, { idempotencyKey: 'key-12345678' });
    expect(result).toEqual({ ok: true });
    const init = fetchMock.mock.calls[0]?.[1];
    expect(init?.method).toBe('POST');
    expect(init?.credentials).toBe('same-origin');
    expect(init?.body).toBe('{"a":1}');
    expect(headersOf(0)).toMatchObject({
      'Content-Type': 'application/json',
      'X-CSRF-Token': 'csrf-123',
      'Idempotency-Key': 'key-12345678',
    });
  });

  it('generates a fresh idempotency key per mutation', async () => {
    fetchMock.mockImplementation(async () => jsonResponse({}));
    await api.post('/api/x', {});
    await api.put('/api/x', {});
    const k1 = headersOf(0)['Idempotency-Key'];
    const k2 = headersOf(1)['Idempotency-Key'];
    expect(k1).toMatch(/^[A-Za-z0-9-]{8,128}$/);
    expect(k2).toMatch(/^[A-Za-z0-9-]{8,128}$/);
    expect(k1).not.toBe(k2);
  });

  it('GET sends neither CSRF nor idempotency headers', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse([]));
    await api.get('/api/x');
    expect(headersOf(0)).not.toHaveProperty('X-CSRF-Token');
    expect(headersOf(0)).not.toHaveProperty('Idempotency-Key');
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBe('GET');
  });

  it('postWithMeta reports replayed responses', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: 1 }, { headers: { 'Idempotency-Replayed': 'true' } }));
    await expect(api.postWithMeta('/api/x', {})).resolves.toEqual({ data: { id: 1 }, replayed: true });
  });

  it('throws ApiError with the problem JSON on non-2xx', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: 'Forbidden', reason: 'maker-checker' }, { status: 403 }));
    const err = await api.post('/api/x', {}).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(403);
    expect((err as ApiError).problem.reason).toBe('maker-checker');
  });

  it('refuses cross-origin paths', async () => {
    await expect(api.get('https://evil.example.test/x')).rejects.toThrow();
    await expect(api.get('//evil.example.test/x')).rejects.toThrow();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
