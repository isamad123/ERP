// The API layer (slice S1.12a, Build item 5). One client for the generated client and the hand-written endpoints:
// every request carries `X-Erp-Client: web` (Identity API version 3: the refresh token is an HttpOnly cookie) and the
// access token, which lives in memory only. Problem answers (RFC 9457; the key is in `title`) become ApiProblem with
// the message key, field errors and the correlation id. A 401 leads to one refresh, then to the sign-in screen with a
// return address; a 503 `errors.auth.busy` is retried once after the server's hint.
import { configureApi } from '@erp/generated/api/http';

export const CLIENT_HEADER = 'X-Erp-Client';
export const CLIENT_WEB = 'web';
export const BUSY = 'errors.auth.busy';

export class ApiProblem extends Error {
  constructor(
    readonly status: number,
    /** Localisation key from the server (`title`), or a web key such as `error.network`. */
    readonly key: string,
    /** Error keys of the answer (`errors` as a list), e.g. password rules. */
    readonly errors: readonly string[],
    /** Field errors (`errors` as a map of field to keys). */
    readonly fieldErrors: Readonly<Record<string, readonly string[]>>,
    /** `X-Correlation-Id`, shown as "Reference" for support. */
    readonly reference: string | null,
  ) {
    super(key);
    this.name = 'ApiProblem';
  }
}

/** The pieces of the session the client needs; the session manager provides them. */
export interface TokenSource {
  accessToken(): string | null;
  /** One refresh; resolves true when a new access token is in place. */
  refreshAfterUnauthorized(): Promise<boolean>;
  /** The session cannot continue: go to sign-in and come back to this screen. */
  sessionEnded(): void;
}

let tokens: TokenSource | null = null;

export function setTokenSource(source: TokenSource): void {
  tokens = source;
}

const sleep = (ms: number) => new Promise((resolve) => window.setTimeout(resolve, ms));

/** Reads a problem answer into an ApiProblem (never throws while reading). */
export async function problemOf(response: Response): Promise<ApiProblem> {
  const reference = response.headers.get('X-Correlation-Id');
  let body: { title?: unknown; errors?: unknown } = {};
  try {
    body = (await response.json()) as typeof body;
  } catch {
    // An empty or non-JSON body (404 of a path that does not exist) keeps the generic key.
  }
  const title = typeof body.title === 'string' && body.title.length > 0 ? body.title : `error.http.${response.status}`;
  const errors = Array.isArray(body.errors) ? body.errors.filter((e): e is string => typeof e === 'string') : [];
  const fieldErrors = body.errors && typeof body.errors === 'object' && !Array.isArray(body.errors)
    ? Object.fromEntries(Object.entries(body.errors as Record<string, unknown>).map(([k, v]) => [k, Array.isArray(v) ? v.map(String) : [String(v)]]))
    : {};
  return new ApiProblem(response.status, title, errors, fieldErrors, reference);
}

export interface SendOptions {
  body?: unknown;
  /** Send the access token and refresh once on 401 (false for the anonymous sign-in steps). */
  auth?: boolean;
  signal?: AbortSignal;
}

/** Low-level fetch with the web header, the access token, the busy retry and the single refresh. */
export async function send(method: string, path: string, options: SendOptions = {}): Promise<Response> {
  const auth = options.auth ?? true;
  const attempt = async (): Promise<Response> => {
    const headers: Record<string, string> = { [CLIENT_HEADER]: CLIENT_WEB, Accept: 'application/json' };
    if (options.body !== undefined) headers['Content-Type'] = 'application/json';
    const token = auth ? tokens?.accessToken() : null;
    if (token) headers.Authorization = `Bearer ${token}`;
    try {
      return await fetch(path, {
        method,
        headers,
        body: options.body === undefined ? null : JSON.stringify(options.body),
        credentials: 'same-origin',
        cache: 'no-store',
        signal: options.signal ?? null,
      });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') throw error;
      throw new ApiProblem(0, 'error.network', [], {}, null);
    }
  };

  let response = await attempt();
  if (response.status === 503) {
    const problem = await problemOf(response.clone());
    if (problem.key === BUSY) {
      const hint = Number((await response.clone().json().catch(() => ({})) as { retryAfterSeconds?: number }).retryAfterSeconds ?? 2);
      await sleep(Math.min(Math.max(hint, 1), 5) * 1000);
      response = await attempt();
    }
  }

  if (response.status === 401 && auth && tokens) {
    if (await tokens.refreshAfterUnauthorized()) {
      response = await attempt();
    }
    if (response.status === 401) tokens.sessionEnded();
  }
  return response;
}

/** JSON request; throws ApiProblem for any answer that is not 2xx. */
export async function requestJson<T>(method: string, path: string, options: SendOptions = {}): Promise<T> {
  const response = await send(method, path, options);
  if (!response.ok) throw await problemOf(response);
  if (response.status === 204 || response.status === 202) {
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }
  return (await response.json()) as T;
}

/** The generated client uses the same path: header, token, busy retry and single refresh. */
export function configureGeneratedClient(): void {
  configureApi('', (input, init) => send(init.method ?? 'GET', input, {
    body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined,
    ...(init.signal ? { signal: init.signal } : {}),
  }));
}
