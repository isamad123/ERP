// Hand-written client of the pack manager (slice S1.23b; contract "Pack manager API"; FR-ADM-PKG-01, FR-KRN-CFG-02, FR-HRM-POL-06). The server decides what
// is installed, changed by the customer and in conflict; the screen shows its answers. Installs, upgrades and "take the pack value" go through these
// endpoints only, so the change log records them as the person's own, and each may answer "waiting for approval" (202).
import { requestJson } from './client';

const enc = encodeURIComponent;

export type Texts = Record<string, string>;
export interface PackContentCount { type: string; count: number }
export interface PackUpdate { version: string; releasedAt: string; releaseNotes: Texts; compatible: boolean; licensed: boolean }

export interface InstalledPack {
  packId: string;
  kind: string;
  country: string | null;
  title: Texts;
  version: string;
  previousVersion: string | null;
  installedAt: string;
  upgradedAt: string | null;
  compatibleFrom: string;
  compatibleTo: string | null;
  compatible: boolean;
  releaseNotes: Texts;
  contents: PackContentCount[];
  overridden: number;
  conflicts: number;
  retired: number;
  updates: PackUpdate[];
}

export interface AvailablePack {
  packId: string;
  kind: string;
  country: string | null;
  title: Texts;
  version: string;
  releasedAt: string;
  releaseNotes: Texts;
  licensed: boolean;
  compatible: boolean;
  contents: PackContentCount[];
}

export interface PendingPackChange { requestId: string; action: 'install' | 'upgrade' | 'take_value'; packId: string; version: string | null; itemId: string | null; requestedAt: string; mine: boolean }

export interface PackManagerView { productVersion: string; installed: InstalledPack[]; available: AvailablePack[]; pending: PendingPackChange[]; approvalRequired: boolean }

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export interface PackItem {
  id: string;
  type: string;
  key: string;
  companyId: string | null;
  companyCode: string | null;
  version: string;
  changed: boolean;
  packValue: Record<string, JsonValue> | null;
  customerValue: Record<string, JsonValue> | null;
  conflictVersion: string | null;
  conflictValue: Record<string, JsonValue> | null;
  retiredIn: string | null;
}

export interface PackItems { packId: string; version: string; total: number; truncated: boolean; items: PackItem[] }

/** An action's answer: applied (with the server's report) or waiting for a second person. */
export interface PackActionAnswer { status: 'applied' | 'pending'; requestId?: string; result?: { added?: number; updated?: number; kept?: number; retired?: number } }

export const packsApi = {
  overview: () => requestJson<PackManagerView>('GET', '/api/cfg/packs'),
  items: (packId: string, changedOnly: boolean) => requestJson<PackItems>('GET', `/api/cfg/packs/${enc(packId)}/items${changedOnly ? '?changed=true' : ''}`),
  install: (packId: string, version: string | null, reason: string) =>
    requestJson<PackActionAnswer>('POST', '/api/cfg/packs/install', { body: { packId, version, reason: reason.trim() || null } }),
  upgrade: (packId: string, version: string, reason: string) =>
    requestJson<PackActionAnswer>('POST', `/api/cfg/packs/${enc(packId)}/upgrade`, { body: { version, reason: reason.trim() || null } }),
  takePackValue: (itemId: string, reason: string) =>
    requestJson<PackActionAnswer>('POST', `/api/cfg/packs/items/${enc(itemId)}/take-pack-value`, { body: { reason: reason.trim() || null } }),
};

/** A name in the person's language, else English, else the first one given. */
export const textOf = (texts: Texts | null | undefined, locale: string): string =>
  texts ? texts[locale] ?? texts.en ?? Object.values(texts)[0] ?? '' : '';

/**
 * A pack object's value in plain words for the diff view: names per language become the person's language, other values are shown as they are
 * stored. Field names are the pack format's own (pack data, not screen text).
 */
export function describeValue(value: Record<string, JsonValue> | null, locale: string): string {
  if (!value) return '';
  return Object.entries(value).map(([field, v]) => `${field}: ${show(v, locale)}`).join(' · ');
}

function show(v: JsonValue, locale: string): string {
  if (v === null) return '—';
  if (typeof v !== 'object') return String(v);
  if (Array.isArray(v)) return v.map((x) => show(x, locale)).join(', ');
  const keys = Object.keys(v);
  if (keys.length > 0 && keys.every((k) => k === 'en' || k === 'ur')) return textOf(v as Texts, locale);
  return JSON.stringify(v);
}
