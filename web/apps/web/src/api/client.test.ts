// API layer (slice S1.12a, Build item 5): problem answers become typed errors with key, field errors and reference;
// the web header is always sent; 401 leads to exactly one refresh; 503 errors.auth.busy is retried once.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProblem, requestJson, setTokenSource } from './client';

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json', ...headers } });

afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

describe('FR-KRN-IAM-01 API client', () => {
  it('turns a problem answer into ApiProblem with key, field errors and correlation id', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(400, { status: 400, title: 'errors.auth.invalid_request', errors: ['errors.password.too_short'] }, { 'X-Correlation-Id': 'abc-123' })));
    const error = await requestJson('POST', '/api/x', { auth: false, body: {} }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiProblem);
    const problem = error as ApiProblem;
    expect(problem.key).toBe('errors.auth.invalid_request');
    expect(problem.errors).toEqual(['errors.password.too_short']);
    expect(problem.reference).toBe('abc-123');
  });

  it('reads field errors given as a map', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(400, { title: 'errors.data.invalid', errors: { code: ['errors.data.required'] } })));
    const problem = (await requestJson('POST', '/api/x', { auth: false }).catch((e: unknown) => e)) as ApiProblem;
    expect(problem.fieldErrors).toEqual({ code: ['errors.data.required'] });
  });

  it('sends the web header and the in-memory token, refreshes once on 401 and retries', async () => {
    let token = 'old';
    const refresh = vi.fn(async () => { token = 'new'; return true; });
    const ended = vi.fn();
    setTokenSource({ accessToken: () => token, refreshAfterUnauthorized: refresh, sessionEnded: ended });
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      const headers = init.headers as Record<string, string>;
      expect(headers['X-Erp-Client']).toBe('web');
      return headers.Authorization === 'Bearer new' ? json(200, { ok: true }) : json(401, { title: 'errors.auth.unauthorized' });
    });
    vi.stubGlobal('fetch', fetchMock);
    await expect(requestJson('GET', '/api/me')).resolves.toEqual({ ok: true });
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(ended).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('after a failed refresh the session ends (sign-in with return address)', async () => {
    const ended = vi.fn();
    setTokenSource({ accessToken: () => 'x', refreshAfterUnauthorized: async () => false, sessionEnded: ended });
    vi.stubGlobal('fetch', vi.fn(async () => json(401, { title: 'x' })));
    await expect(requestJson('GET', '/api/me')).rejects.toBeInstanceOf(ApiProblem);
    expect(ended).toHaveBeenCalledTimes(1);
  });

  it('retries a busy answer once', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json(503, { title: 'errors.auth.busy', retryAfterSeconds: 1 }))
      .mockResolvedValueOnce(json(200, { outcome: 'tokens' }));
    vi.stubGlobal('fetch', fetchMock);
    const pending = requestJson('POST', '/api/iam/auth/sign-in', { auth: false, body: {} });
    await vi.advanceTimersByTimeAsync(1100);
    await expect(pending).resolves.toEqual({ outcome: 'tokens' });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('a network failure is error.network', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('failed'); }));
    const problem = (await requestJson('GET', '/api/x', { auth: false }).catch((e: unknown) => e)) as ApiProblem;
    expect(problem.key).toBe('error.network');
    expect(problem.status).toBe(0);
  });
});
