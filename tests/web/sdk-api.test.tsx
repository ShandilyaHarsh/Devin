import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, newIdempotencyKey, setCsrfToken } from '@platform/sdk';
import { json, pathOf, stubFetch } from './helpers';

const KEY_PATTERN = /^[A-Za-z0-9-]{8,128}$/;

describe('api client', () => {
  beforeEach(() => {
    setCsrfToken('csrf-token-abc');
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('POST sends JSON, X-CSRF-Token, a fresh valid Idempotency-Key and same-origin credentials', async () => {
    const calls = stubFetch(() => json({ ok: true }));
    const result = await api.post<{ ok: boolean }>('/api/apps/x/things', { a: 1 });

    expect(result).toEqual({ ok: true });
    expect(calls).toHaveLength(1);
    const call = calls[0]!;
    expect(call.method).toBe('POST');
    expect(pathOf(call.url)).toBe('/api/apps/x/things');
    expect(call.headers.get('X-CSRF-Token')).toBe('csrf-token-abc');
    expect(call.headers.get('Content-Type')).toMatch(/application\/json/);
    expect(call.headers.get('Idempotency-Key')).toMatch(KEY_PATTERN);
    expect(JSON.parse(call.body ?? 'null')).toEqual({ a: 1 });
    expect(call.credentials).toBe('same-origin');
  });

  it('generates a different Idempotency-Key per call', async () => {
    const calls = stubFetch(() => json({}));
    await api.post('/api/apps/x/a', {});
    await api.post('/api/apps/x/a', {});
    const k1 = calls[0]!.headers.get('Idempotency-Key');
    const k2 = calls[1]!.headers.get('Idempotency-Key');
    expect(k1).toBeTruthy();
    expect(k2).toBeTruthy();
    expect(k1).not.toBe(k2);
  });

  it('reuses a caller-provided Idempotency-Key (retry safety)', async () => {
    const calls = stubFetch(() => json({}));
    const key = 'retry-key-12345678';
    await api.post('/api/apps/x/a', { n: 1 }, { idempotencyKey: key });
    await api.post('/api/apps/x/a', { n: 1 }, { idempotencyKey: key });
    expect(calls.map((c) => c.headers.get('Idempotency-Key'))).toEqual([key, key]);
  });

  it('PUT and DELETE also carry CSRF + Idempotency-Key', async () => {
    const calls = stubFetch(() => json({}));
    await api.put('/api/apps/x/a', { b: 2 });
    await api.del('/api/apps/x/a');
    for (const call of calls) {
      expect(call.headers.get('X-CSRF-Token')).toBe('csrf-token-abc');
      expect(call.headers.get('Idempotency-Key')).toMatch(KEY_PATTERN);
    }
    expect(calls.map((c) => c.method)).toEqual(['PUT', 'DELETE']);
  });

  it('GET sends neither X-CSRF-Token nor Idempotency-Key', async () => {
    const calls = stubFetch(() => json([1, 2]));
    const data = await api.get<number[]>('/api/platform/catalog');
    expect(data).toEqual([1, 2]);
    const call = calls[0]!;
    expect(call.method).toBe('GET');
    expect(call.headers.has('X-CSRF-Token')).toBe(false);
    expect(call.headers.has('Idempotency-Key')).toBe(false);
  });

  it('maps 403 to ApiError with status and problem', async () => {
    stubFetch(() =>
      json({ title: 'Forbidden', status: 403, reason: 'maker-checker' }, { status: 403, headers: { 'Content-Type': 'application/problem+json' } }),
    );
    const error = await api.post('/api/apps/x/a', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(403);
    expect(apiError.problem?.reason).toBe('maker-checker');
  });

  it('maps GET 401/500 to ApiError too (never resolves on non-2xx)', async () => {
    stubFetch((c) => (pathOf(c.url) === '/a' ? json({ title: 'Unauthorized', status: 401 }, { status: 401 }) : json({ title: 'x', status: 500 }, { status: 500 })));
    await expect(api.get('/a')).rejects.toMatchObject({ status: 401 });
    await expect(api.get('/b')).rejects.toMatchObject({ status: 500 });
  });

  it('postWithMeta exposes Idempotency-Replayed', async () => {
    let n = 0;
    stubFetch(() => (n++ === 0 ? json({ id: 1 }) : json({ id: 1 }, { headers: { 'Idempotency-Replayed': 'true' } })));
    const first = await api.postWithMeta<{ id: number }>('/api/apps/x/a', {}, { idempotencyKey: 'same-key-123456' });
    const second = await api.postWithMeta<{ id: number }>('/api/apps/x/a', {}, { idempotencyKey: 'same-key-123456' });
    expect(first).toEqual({ data: { id: 1 }, replayed: false });
    expect(second).toEqual({ data: { id: 1 }, replayed: true });
  });

  it('newIdempotencyKey returns unique keys matching the server format', () => {
    const keys = new Set(Array.from({ length: 50 }, () => newIdempotencyKey()));
    expect(keys.size).toBe(50);
    for (const key of keys) {
      expect(key).toMatch(KEY_PATTERN);
    }
  });

  it('does not persist the CSRF token in web storage', async () => {
    stubFetch(() => json({}));
    setCsrfToken('stored-nowhere-token');
    await api.post('/api/apps/x/a', {});
    expect(JSON.stringify({ ...globalThis.localStorage })).not.toContain('stored-nowhere-token');
    expect(JSON.stringify({ ...globalThis.sessionStorage })).not.toContain('stored-nowhere-token');
  });
});
