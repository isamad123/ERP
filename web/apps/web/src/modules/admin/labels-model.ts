// The label catalogue of the labels editor (slice S1.15b Build item 5; FR-ADM-STU-25, -05, FR-KRN-MET-22): every text a customer
// may rename, English and Urdu side by side, with where it appears. Two sources: the engine's table, field and option labels
// (generated English, hand-kept Urdu) and the web's own texts that have no placeholders (menu names, titles, field labels). A text
// with placeholders keeps the product's wording (an override can never break a message), so it is not offered here.
import { LABELS } from '@erp/engine';
import en from '@erp/i18n/messages/en.json';
import ur from '@erp/i18n/messages/ur.json';
import configEn from '@erp/i18n/messages/config.en.json';
import configUr from '@erp/i18n/messages/config.ur.json';
import type { LabelOverride } from '../../api/config';
import type { ScreenDeclaration } from '../../navigation/screens';

export interface CatalogueEntry {
  key: string;
  en: string;
  ur: string;
  /** `engine`: a table, field or option label; `message`: a web text. */
  kind: 'engine' | 'message';
  /** The table an engine label belongs to (`org.org_units`), else null. */
  table: string | null;
}

/** Columns every table has and no screen shows as a field: left out of the catalogue. */
const SYSTEM_COLUMNS = new Set(['id', 'created_at', 'created_by', 'updated_at', 'updated_by', 'row_version', 'is_deleted', 'deleted_at', 'deleted_by', 'custom']);

const ENGINE_KEY = /^(entities|fields|options)\.([a-z0-9_]+\.[a-z0-9_]+)(?:\.([a-z0-9_]+))?/;

/** The table an engine label key is about, and its column when it is a field or option label. */
export function tableOfKey(key: string): { table: string; column: string | null } | null {
  const match = ENGINE_KEY.exec(key);
  if (!match) return null;
  const [, kind, table = '', rest] = match;
  if (kind === 'entities') return { table, column: null };
  return { table, column: rest ?? null };
}

const plain = (text: string | undefined): text is string => typeof text === 'string' && text.length > 0 && !/[{}<>]/.test(text);

/** Every renameable label, once, sorted by key. Built on first use (about three thousand entries). */
let built: CatalogueEntry[] | null = null;
export function catalogue(): CatalogueEntry[] {
  if (built) return built;
  const entries = new Map<string, CatalogueEntry>();
  for (const [key, text] of Object.entries(LABELS.en)) {
    const where = tableOfKey(key);
    if (where?.column && SYSTEM_COLUMNS.has(where.column)) continue;
    if (!plain(text)) continue;
    entries.set(key, { key, en: text, ur: LABELS.ur[key] ?? '', kind: 'engine', table: where?.table ?? null });
  }
  const messages: [Record<string, string>, Record<string, string>][] = [[en, ur], [configEn, configUr]];
  for (const [english, urdu] of messages) {
    for (const [key, text] of Object.entries(english)) {
      if (key.startsWith('_') || !plain(text) || entries.has(key)) continue;
      const other = (urdu as Record<string, string>)[key] ?? '';
      entries.set(key, { key, en: text, ur: /[{}<>]/.test(other) ? '' : other, kind: 'message', table: null });
    }
  }
  built = [...entries.values()].sort((a, b) => a.key.localeCompare(b.key));
  return built;
}

/** The text of a key in each language as the layers stack: product, then the product pack, the customer, then the company. */
export interface LayerView {
  /** The text without the customer's change (the product's, or the pack's). */
  base: { en: string; ur: string };
  /** The text in force at the chosen layer. */
  effective: { en: string; ur: string };
  /** The change made at the chosen layer itself, per language (absent: none). */
  own: { en?: string; ur?: string };
  /** Whether the customer's layer and the chosen company's layer changed this label. */
  changedBy: { customer: boolean; company: boolean };
}

export type Overrides = ReadonlyMap<string, readonly LabelOverride[]>;

export function overridesByKey(items: readonly LabelOverride[]): Map<string, LabelOverride[]> {
  const map = new Map<string, LabelOverride[]>();
  for (const item of items) map.set(item.key, [...(map.get(item.key) ?? []), item]);
  return map;
}

/** How a key looks at the customer layer (`companyId` null) or at one company's layer. */
export function viewOf(entry: CatalogueEntry, overrides: Overrides, companyId: string | null): LayerView {
  const rows = overrides.get(entry.key) ?? [];
  const pick = (layer: LabelOverride['layer'], language: 'en' | 'ur', scope: string | null) =>
    rows.find((r) => r.layer === layer && r.language === language && (r.scopeId ?? null) === scope)?.text;
  const base = { en: pick('pack', 'en', null) ?? entry.en, ur: pick('pack', 'ur', null) ?? entry.ur };
  const tenant = { en: pick('tenant', 'en', null), ur: pick('tenant', 'ur', null) };
  const company = { en: companyId ? pick('company', 'en', companyId) : undefined, ur: companyId ? pick('company', 'ur', companyId) : undefined };
  const below = { en: companyId ? tenant.en ?? base.en : base.en, ur: companyId ? tenant.ur ?? base.ur : base.ur };
  const own = companyId ? company : tenant;
  return {
    base: below,
    effective: { en: own.en ?? below.en, ur: own.ur ?? below.ur },
    own: { ...(own.en !== undefined ? { en: own.en } : {}), ...(own.ur !== undefined ? { ur: own.ur } : {}) },
    changedBy: { customer: tenant.en !== undefined || tenant.ur !== undefined, company: company.en !== undefined || company.ur !== undefined },
  };
}

/** Text search over key and both languages, case-insensitive. */
export function matchesEntry(entry: CatalogueEntry, view: LayerView, search: string): boolean {
  const q = search.trim().toLowerCase();
  if (!q) return true;
  return entry.key.toLowerCase().includes(q) || view.effective.en.toLowerCase().includes(q) || entry.en.toLowerCase().includes(q)
    || view.effective.ur.includes(search.trim()) || entry.ur.includes(search.trim());
}

/** The screens that show a table's labels (their menu names). */
export function screensOf(table: string, screens: readonly ScreenDeclaration[]): ScreenDeclaration[] {
  return screens.filter((s) => s.tables?.includes(table));
}

/** Tables the menu screens declare, for the "screen or table" filter. */
export function declaredTables(screens: readonly ScreenDeclaration[]): string[] {
  return [...new Set(screens.flatMap((s) => s.tables ?? []))].sort();
}
