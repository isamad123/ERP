// Hand-written client of the API client service (slice S1.27c; routes in src/Platform/Erp.Kernel.Api/Access/ApiClientEndpoints.cs; FR-ADM-SEC-02).
// The secret of a new or rotated key is in the answer once; nothing here keeps it after the screen closes.
import { requestJson } from './client';

const base = '/api/iam/api-clients';
const enc = encodeURIComponent;

export interface ApiClientView {
  id: string;
  clientId: string;
  name: string;
  scopes: string[];
  ipAllow: string[];
  rateLimitPerMinute: number;
  expiresAt: string | null;
  lastUsedAt: string | null;
  status: 'active' | 'revoked' | 'expired';
  previousSecretValidUntil: string | null;
  revokedAt: string | null;
  createdAt: string;
}

export interface ApiClientScopeOption { code: string; module: string; descriptionKey: string }

/** What a create or a rotation answers: the key, shown once. While `heldRequestId` is set the key opens nothing until a second person approves. */
export interface ApiClientSecret {
  id: string | null;
  clientId: string;
  secret: string;
  apiKey: string;
  heldRequestId: string | null;
  validUntil: string | null;
}

export interface ApiClientUsageDay { date: string; calls: number; errors4xx: number; errors5xx: number; rateLimited: number; p95Ms: number; maxMs: number }
export interface ApiClientUsage { clientId: string; lastUsedAt: string | null; days: ApiClientUsageDay[] }

export interface ApiClientCreate { name: string; scopes: string[]; ipAllow: string[]; rateLimitPerMinute: number; expiresAt: string | null }

export const apiClients = {
  list: (signal?: AbortSignal) => requestJson<ApiClientView[]>('GET', base, signal ? { signal } : {}),
  scopeOptions: (signal?: AbortSignal) => requestJson<ApiClientScopeOption[]>('GET', `${base}/scope-options`, signal ? { signal } : {}),
  create: (body: ApiClientCreate) => requestJson<ApiClientSecret>('POST', base, { body }),
  rotate: (id: string) => requestJson<ApiClientSecret>('POST', `${base}/${enc(id)}/rotate`, { body: {} }),
  revoke: (id: string) => requestJson<ApiClientView>('POST', `${base}/${enc(id)}/revoke`, { body: {} }),
  usage: (id: string, days: number, signal?: AbortSignal) => requestJson<ApiClientUsage>('GET', `${base}/${enc(id)}/usage?days=${days}`, signal ? { signal } : {}),
};
