// Hand-written clients of the label overrides, option lists and custom field definitions (slice S1.15b; contracts "Label API",
// "Option list API" and "Custom field API", CR-071). The server checks every request again; the screens show its answers as
// confirmed, never assumed (spec 13 STA-06).
import { requestJson } from './client';
import type { Names } from './workflow';

const enc = encodeURIComponent;

function query(values: Record<string, string | undefined | null>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) if (value) params.set(name, value);
  const text = params.toString();
  return text ? `?${text}` : '';
}

// ---- labels (FR-ADM-STU-25, -05, FR-KRN-MET-22) ----

export type LabelLayer = 'pack' | 'tenant' | 'company';
export type LabelLanguage = 'en' | 'ur';
export interface LabelOverride { id: string; key: string; language: LabelLanguage; layer: LabelLayer; scopeId: string | null; text: string; rowVersion: number }
export interface LabelWrite { key: string; layer: 'tenant' | 'company'; companyId?: string; texts: { en?: string | null; ur?: string | null } }

export const labelsApi = {
  list: () => requestJson<{ items: LabelOverride[] }>('GET', '/api/cfg/label-overrides'),
  save: (body: LabelWrite) => requestJson<{ items: LabelOverride[] }>('PUT', '/api/cfg/label-overrides', { body }),
};

// ---- option lists (FR-KRN-MET-01, Q7) ----

export interface OptionListSummary { id: string; code: string; name: Names; isSystem: boolean; itemCount: number; rowVersion: number }
export interface OptionItem { id: string; code: string; label: Names; seq: number; isActive: boolean; rowVersion: number }
export interface OptionListDetail { id: string; code: string; name: Names; isSystem: boolean; rowVersion: number; items: OptionItem[] }

export const optionListsApi = {
  list: () => requestJson<{ items: OptionListSummary[] }>('GET', '/api/cfg/option-lists'),
  get: (id: string) => requestJson<OptionListDetail>('GET', `/api/cfg/option-lists/${enc(id)}`),
  create: (body: { code: string; name: Names }) => requestJson<OptionListDetail>('POST', '/api/cfg/option-lists', { body }),
  rename: (id: string, name: Names, rowVersion: number) => requestJson<OptionListDetail>('PUT', `/api/cfg/option-lists/${enc(id)}`, { body: { name, rowVersion } }),
  addItem: (id: string, body: { code: string; label: Names; isActive?: boolean }) => requestJson<OptionListDetail>('POST', `/api/cfg/option-lists/${enc(id)}/items`, { body }),
  changeItem: (id: string, itemId: string, body: { label: Names; isActive: boolean; rowVersion: number }) =>
    requestJson<OptionListDetail>('PUT', `/api/cfg/option-lists/${enc(id)}/items/${enc(itemId)}`, { body }),
  order: (id: string, itemIds: string[]) => requestJson<OptionListDetail>('POST', `/api/cfg/option-lists/${enc(id)}/items/order`, { body: { itemIds } }),
};

// ---- custom fields (FR-KRN-MET-01, -02, -05) ----

export const CUSTOM_TYPES = ['text', 'long_text', 'number', 'decimal', 'date', 'bool', 'choice', 'multi_choice', 'user', 'employee', 'file'] as const;
export type CustomType = (typeof CUSTOM_TYPES)[number];

export interface CustomValidation { minLength?: number | null; maxLength?: number | null; pattern?: string | null; min?: number | string | null; max?: number | string | null; minDate?: string | null; maxDate?: string | null; maxItems?: number | null }

export interface CustomEntity { entityType: string; labelKey: string; companyScoped: boolean }
export interface CustomDef {
  id: string;
  entityType: string;
  fieldKey: string;
  dataType: CustomType;
  label: Names;
  help: Names | null;
  validation: CustomValidation;
  defaultValue: unknown;
  isRequired: boolean;
  isUnique: boolean;
  optionListId: string | null;
  section: string | null;
  seq: number;
  status: 'active' | 'retired';
  version: number;
  rowVersion: number;
}
export type CustomDefCreate = Omit<CustomDef, 'id' | 'status' | 'version' | 'rowVersion'>;
export type CustomDefChange = Pick<CustomDef, 'label' | 'help' | 'validation' | 'defaultValue' | 'isRequired' | 'isUnique' | 'section' | 'seq'> & { rowVersion: number };

export const customFieldsApi = {
  entities: () => requestJson<{ items: CustomEntity[] }>('GET', '/api/cfg/custom-field-entities'),
  list: (entityType: string) => requestJson<{ items: CustomDef[] }>('GET', `/api/cfg/custom-fields${query({ entityType })}`),
  create: (body: CustomDefCreate) => requestJson<CustomDef>('POST', '/api/cfg/custom-fields', { body }),
  update: (id: string, body: CustomDefChange) => requestJson<CustomDef>('PUT', `/api/cfg/custom-fields/${enc(id)}`, { body }),
  retire: (id: string, rowVersion: number) => requestJson<CustomDef>('POST', `/api/cfg/custom-fields/${enc(id)}/retire`, { body: { rowVersion } }),
  restore: (id: string, rowVersion: number) => requestJson<CustomDef>('POST', `/api/cfg/custom-fields/${enc(id)}/restore`, { body: { rowVersion } }),
};
