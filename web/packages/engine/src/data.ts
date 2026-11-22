// How the engine reaches the server (slice S1.12c): the application gives it one `send` function (its API client: web
// header, access token in memory, single refresh, busy retry; S1.12a) through EngineProvider. The engine reads and writes
// JSON itself, so decimal fields stay exact both ways, and turns problem answers into field errors.
import { createContext, createElement, useContext, type ReactNode } from 'react';
import type { FieldProblem, FormValues } from './validation';
import { fromJson, toJson } from './validation';
import type { TableMetadata } from './metadata';

/** A problem answer (RFC 9457) as the application's client reports it. */
export interface Problem {
  status: number;
  /** The server's localisation key (`title`), or a web key such as `error.network`. */
  key: string;
  fieldErrors: Readonly<Record<string, readonly string[]>>;
  reference: string | null;
  /** The database rule (constraint) that refused a save, when the server names it. */
  detail: string | null;
}

export function isProblem(value: unknown): value is Problem {
  return typeof value === 'object' && value !== null && 'status' in value && 'key' in value && 'fieldErrors' in value;
}

export interface EngineApi {
  /** Sends a request with an optional JSON body (already serialized); resolves the answer's text, rejects with a Problem. */
  send: (method: string, path: string, body?: string, signal?: AbortSignal) => Promise<string>;
}

const EngineContext = createContext<EngineApi | null>(null);

export function EngineProvider({ api, children }: { api: EngineApi; children: ReactNode }) {
  return createElement(EngineContext.Provider, { value: api }, children);
}

export function useEngineApi(): EngineApi {
  const api = useContext(EngineContext);
  if (!api) throw new Error('EngineProvider is missing');
  return api;
}

export interface Page<Row> {
  items: Row[];
  next: string | null;
}

export type Row = FormValues & { id: string; rowVersion: number };

function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

/** The generated endpoints of one table, read and written with exact decimals. */
export function tableClient(api: EngineApi, metadata: TableMetadata, decimals: ReadonlySet<string>) {
  const route = metadata.route;
  const parse = <T,>(text: string): T => fromJson<T>(text, decimals);
  return {
    list: async (params: Record<string, string | number | boolean | null | undefined>, signal?: AbortSignal) => parse<Page<Row>>(await api.send('GET', route + query(params), undefined, signal)),
    get: async (id: string, signal?: AbortSignal) => parse<Row>(await api.send('GET', `${route}/${encodeURIComponent(id)}`, undefined, signal)),
    create: async (body: FormValues) => parse<Row>(await api.send('POST', route, toJson(body, decimals))),
    update: async (id: string, body: FormValues, rowVersion: number) => parse<Row>(await api.send('PUT', `${route}/${encodeURIComponent(id)}`, toJson({ ...body, rowVersion }, decimals))),
    remove: async (id: string, rowVersion: number) => { await api.send('DELETE', `${route}/${encodeURIComponent(id)}${query({ rowVersion })}`); },
  };
}

export { query as queryString };

/** Field errors of a refused save: the server's field map, else the database rule named in `detail`. */
export function fieldProblemsOf(problem: Problem, metadata: TableMetadata): Record<string, FieldProblem> {
  const problems: Record<string, FieldProblem> = {};
  for (const [field, keys] of Object.entries(problem.fieldErrors)) {
    // A server key with its own text (e.g. errors.access.field_not_editable) is shown in the person's language.
    problems[field] = keys[0] === 'errors.data.required_or_invalid' ? { key: 'engine.error.required' } : { key: 'engine.error.server', values: { text: keys[0] ?? '' } };
  }
  const field = problem.detail ? metadata.constraints?.[problem.detail] : undefined;
  if (field && !problems[field]) {
    problems[field] = problem.key === 'errors.data.duplicate' ? { key: 'engine.error.duplicate' } : { key: 'engine.error.rule' };
  }
  return problems;
}
