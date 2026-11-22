// Hand-written clients of the settings centre, the feature flags and the login branding (slice S1.15a; contracts "Settings API",
// "Feature flag API" and the new "Branding API", CR-071). The server checks every request again; the screens show its answers
// as confirmed, never assumed (spec 13 STA-06).
import { requestJson, send, problemOf } from './client';
import { readBranding, type Branding } from './branding';
import type { Names } from './workflow';

export type { Branding };

export type SettingType = 'bool' | 'int' | 'decimal' | 'text' | 'choice' | 'list' | 'json';
export type SettingLayer = 'product' | 'pack' | 'tenant' | 'company' | 'role' | 'user';

export interface SettingValidation {
  min?: number | null;
  max?: number | null;
  choices?: string[] | null;
  pattern?: string | null;
  max_length?: number | null;
  maxLength?: number | null;
  item_type?: string | null;
  itemType?: string | null;
  min_items?: number | null;
  max_items?: number | null;
  format?: string | null;
}

export interface ChangeInfo { at: string; by: string }

export interface SettingItem {
  key: string;
  module: string;
  area: string | null;
  name: Names | null;
  description: Names;
  help: Names | null;
  type: SettingType;
  unit: string | null;
  validation: SettingValidation;
  layers: SettingLayer[];
  requiresApproval: boolean;
  selfService: boolean;
  default: unknown;
  value: unknown;
  source: SettingLayer;
  sourceScopeId: string | null;
  lastChange: ChangeInfo | null;
}

export interface SettingLayerItem { layer: SettingLayer; scopeId: string | null; value: unknown; changed: ChangeInfo | null; ignored: string | null }
export interface SettingDetail { setting: SettingItem; layers: SettingLayerItem[] }
export interface SettingHistoryItem { id: string; key: string; layer: SettingLayer; scopeId: string | null; oldValue: unknown; newValue: unknown; reason: string | null; at: string; by: string }

/** The answer of a change that may wait for a second administrator (200 `done`, 202 `pending`). */
export interface ChangeAnswer { outcome: 'done' | 'pending'; id: string | null; requestId: string | null }

export interface FlagOverride { layer: string; scopeId: string | null; enabled: boolean; changed: ChangeInfo }
export interface FlagItem {
  key: string;
  module: string | null;
  name: Names | null;
  description: Names;
  help: Names | null;
  entitlementKey: string | null;
  licensed: boolean;
  default: boolean;
  enabled: boolean;
  source: string;
  sourceScopeId: string | null;
  overrides: FlagOverride[];
}

export interface BrandingFile { id: string; fileName: string; contentType: string; sizeBytes: number }

export const BRANDING_OWNER_TYPE = 'cfg.branding';
export const BRANDING_OWNER_ID = '00000000-0000-7000-8000-00000000b4a1';
export const BRANDING_KEYS = {
  productName: 'cfg.branding.product_name',
  logo: 'cfg.branding.logo_file_id',
  bannerEn: 'cfg.branding.banner_en',
  bannerUr: 'cfg.branding.banner_ur',
  termsEn: 'cfg.branding.terms_en',
  termsUr: 'cfg.branding.terms_ur',
} as const;

const enc = encodeURIComponent;

function query(values: Record<string, string | undefined | null>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) if (value) params.set(name, value);
  const text = params.toString();
  return text ? `?${text}` : '';
}

export interface Target { layer: SettingLayer; scopeId: string | null }

export const settingsApi = {
  list: (filters: { search?: string | undefined; module?: string | undefined; area?: string | undefined; companyId?: string | undefined }) =>
    requestJson<SettingItem[]>('GET', `/api/cfg/settings${query(filters)}`),
  detail: (key: string, companyId?: string | null) => requestJson<SettingDetail>('GET', `/api/cfg/settings/${enc(key)}${query({ companyId })}`),
  history: (key: string) => requestJson<SettingHistoryItem[]>('GET', `/api/cfg/settings/${enc(key)}/history`),
  set: (key: string, target: Target, value: unknown, reason: string) =>
    requestJson<ChangeAnswer>('PUT', `/api/cfg/settings/${enc(key)}`, { body: { layer: target.layer, scopeId: target.scopeId, value, reason } }),
  reset: (key: string, target: Target, reason: string) =>
    requestJson<ChangeAnswer>('POST', `/api/cfg/settings/${enc(key)}/reset`, { body: { layer: target.layer, scopeId: target.scopeId, reason } }),
  revert: (historyId: string, reason: string) => requestJson<ChangeAnswer>('POST', `/api/cfg/settings/history/${enc(historyId)}/revert`, { body: { reason } }),
  flags: (companyId?: string | null) => requestJson<FlagItem[]>('GET', `/api/cfg/feature-flags${query({ companyId })}`),
  setFlag: (key: string, target: { layer: string; scopeId: string | null }, enabled: boolean, reason: string) =>
    requestJson<ChangeAnswer>('PUT', `/api/cfg/feature-flags/${enc(key)}`, { body: { layer: target.layer, scopeId: target.scopeId, enabled, reason } }),
  resetFlag: (key: string, target: { layer: string; scopeId: string | null }, reason: string) =>
    requestJson<ChangeAnswer>('POST', `/api/cfg/feature-flags/${enc(key)}/reset`, { body: { layer: target.layer, scopeId: target.scopeId, reason } }),
};

export const brandingApi = {
  /** Public: no sign-in needed. */
  get: readBranding,
  file: () => requestJson<BrandingFile[]>('GET', `/api/doc/attachments?ownerType=${enc(BRANDING_OWNER_TYPE)}&ownerId=${BRANDING_OWNER_ID}`),
  upload: async (file: File): Promise<BrandingFile> => {
    const form = new FormData();
    form.set('OwnerType', BRANDING_OWNER_TYPE);
    form.set('OwnerId', BRANDING_OWNER_ID);
    form.set('File', file, file.name);
    const response = await send('POST', '/api/doc/attachments', { form });
    if (!response.ok) throw await problemOf(response);
    return (await response.json()) as BrandingFile;
  },
};
