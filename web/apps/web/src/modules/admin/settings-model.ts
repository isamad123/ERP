// The settings centre's pure rules (slice S1.15a; FR-ADM-SET-01 to -04, FR-KRN-SET-03): which group a setting belongs to, what
// it is called, how a value is shown and how an edited value is turned back into the typed JSON value the server expects.
// The server checks every value again; these checks only spare a round trip and put the problem next to the field.
import { formatInteger, webMessages, type MessageKey, type NumberPreferences } from '@erp/i18n';
import { nameOf } from '../../api/workflow';
import type { SettingItem, SettingLayer, SettingValidation } from '../../api/settings';

/** The groups of the menu, in order (ruling Q1: "Sign-in and security" holds the iam.* keys and the number formats, Q4). */
export const GROUPS = ['signin', 'branding', 'organization', 'workflow', 'documents', 'notifications', 'jobs', 'licence', 'numbering', 'hr', 'other'] as const;
export type Group = (typeof GROUPS)[number];

/** The login logo is set by uploading a file on the sign-in page screen; its raw file id is not shown as a setting. */
export const HIDDEN_KEYS: readonly string[] = ['cfg.branding.logo_file_id'];

export function groupOf(s: SettingItem): Group {
  const area = s.area ?? '';
  if (area.startsWith('security') || area === 'localisation') return 'signin';
  if (area === 'branding') return 'branding';
  if (area.startsWith('organization')) return 'organization';
  if (area === 'workflow') return 'workflow';
  if (area === 'documents') return 'documents';
  if (area === 'notifications') return 'notifications';
  if (area === 'jobs') return 'jobs';
  if (area === 'licence') return 'licence';
  if (area === 'numbering') return 'numbering';
  if (area.startsWith('hr')) return 'hr';
  return 'other';
}

export const titleOf = (s: SettingItem, locale: string): string => nameOf(s.name ?? s.description, locale);

/** Search over the key, the name and the description in both languages ("idle" finds "Idle timeout"). */
export function matches(s: SettingItem, text: string): boolean {
  const needle = text.trim().toLowerCase();
  if (needle === '') return true;
  const haystack = [s.key, ...Object.values(s.name ?? {}), ...Object.values(s.description ?? {}), ...Object.values(s.help ?? {})].join('\n').toLowerCase();
  return needle.split(/\s+/).every((word) => haystack.includes(word));
}

const pick = <T>(v: SettingValidation, snake: keyof SettingValidation, camel: keyof SettingValidation): T | null =>
  ((v?.[snake] ?? v?.[camel] ?? null) as T | null);

export interface Rules { min: number | null; max: number | null; maxLength: number | null; choices: string[] | null; minItems: number | null; maxItems: number | null; itemType: string | null; format: string | null }

export function rulesOf(s: SettingItem): Rules {
  const v = s.validation ?? {};
  return {
    min: pick<number>(v, 'min', 'min'), max: pick<number>(v, 'max', 'max'), maxLength: pick<number>(v, 'max_length', 'maxLength'),
    choices: pick<string[]>(v, 'choices', 'choices'), minItems: pick<number>(v, 'min_items', 'min_items'), maxItems: pick<number>(v, 'max_items', 'max_items'),
    itemType: pick<string>(v, 'item_type', 'itemType'), format: pick<string>(v, 'format', 'format'),
  };
}

/** What is typed into the editor: a switch (boolean), a set of ticked choices (list), or text. */
export type Draft = string | boolean | string[];

export function toDraft(s: SettingItem, value: unknown): Draft {
  switch (s.type) {
    case 'bool': return value === true;
    case 'list': {
      const items = Array.isArray(value) ? value.map(String) : [];
      return rulesOf(s).choices ? items : items.join(', ');
    }
    case 'json': return value === undefined || value === null ? '' : JSON.stringify(value, null, 2);
    default: return value === undefined || value === null ? '' : String(value);
  }
}

export type Parsed = { ok: true; value: unknown } | { ok: false; problem: 'required' | 'whole' | 'decimal' | 'min' | 'max' | 'length' | 'choice' | 'items' | 'json'; limit?: number };

export function fromDraft(s: SettingItem, draft: Draft): Parsed {
  const rules = rulesOf(s);
  switch (s.type) {
    case 'bool': return { ok: true, value: draft === true };
    case 'int':
    case 'decimal': {
      const text = String(draft).trim();
      if (text === '') return { ok: false, problem: 'required' };
      if (s.type === 'int' ? !/^-?\d+$/.test(text) : !/^-?\d+(\.\d+)?$/.test(text)) return { ok: false, problem: s.type === 'int' ? 'whole' : 'decimal' };
      const n = Number(text);
      if (rules.min !== null && n < rules.min) return { ok: false, problem: 'min', limit: rules.min };
      if (rules.max !== null && n > rules.max) return { ok: false, problem: 'max', limit: rules.max };
      return { ok: true, value: n };
    }
    case 'choice': {
      const text = String(draft);
      if (text === '') return { ok: false, problem: 'required' };
      return rules.choices && !rules.choices.includes(text) ? { ok: false, problem: 'choice' } : { ok: true, value: text };
    }
    case 'list': {
      const items = Array.isArray(draft) ? draft : String(draft).split(/[,\n]/).map((x) => x.trim()).filter((x) => x !== '');
      if (rules.minItems !== null && items.length < rules.minItems) return { ok: false, problem: 'items', limit: rules.minItems };
      if (rules.maxItems !== null && items.length > rules.maxItems) return { ok: false, problem: 'items', limit: rules.maxItems };
      if (rules.itemType === 'int') {
        if (!items.every((x) => /^-?\d+$/.test(x))) return { ok: false, problem: 'whole' };
        return { ok: true, value: items.map(Number) };
      }
      return { ok: true, value: items };
    }
    case 'json': {
      try {
        return { ok: true, value: JSON.parse(String(draft)) as unknown };
      } catch {
        return { ok: false, problem: 'json' };
      }
    }
    default: {
      const text = String(draft);
      if (rules.maxLength !== null && text.length > rules.maxLength) return { ok: false, problem: 'length', limit: rules.maxLength };
      return { ok: true, value: text };
    }
  }
}

export const sameValue = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);

/** The layers a person may write with this screen: the customer and, for a company, its own value. */
export const EDITABLE_LAYERS: readonly SettingLayer[] = ['tenant', 'company'];

/** A short plain text of a value for a list row; empty text and empty lists are "not set" for the caller to word. */
export function shortValue(s: SettingItem, value: unknown, numbers: NumberPreferences, words: { yes: string; no: string; notSet: string }): string {
  if (s.type === 'bool') return value === true ? words.yes : words.no;
  if ((s.type === 'int') && typeof value === 'number') return formatInteger(value, numbers);
  if (Array.isArray(value)) return value.length === 0 ? words.notSet : value.map(String).join(', ');
  if (value === null || value === undefined || value === '') return words.notSet;
  if (typeof value === 'object') return JSON.stringify(value);
  const text = String(value);
  return text.length > 60 ? `${text.slice(0, 57)}…` : text;
}

/** Whether the web catalogue has a text for the key: a missing text would throw, so optional texts are looked up first. */
export const hasText = (key: string): key is MessageKey => Object.prototype.hasOwnProperty.call(webMessages('en'), key);
