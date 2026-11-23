// Hand-written client of the numbering screens (slice S1.15c Build item 9; contract "Numbering API", S1.08). The schemes are read
// through the generated client (`numberingSchemes.list`); changes go through these purpose-built endpoints. No number is ever
// handed out through the API. The server checks every request again; the screens show its answers as confirmed, never assumed.
import { requestJson } from './client';

const enc = encodeURIComponent;

export type ResetRule = 'never' | 'yearly' | 'monthly' | 'fiscal_yearly';
export const RESET_RULES: readonly ResetRule[] = ['never', 'yearly', 'monthly', 'fiscal_yearly'];

export interface NumberedType {
  code: string;
  module: string;
  assign: 'create' | 'post';
  gapFreeAllowed: boolean;
  overrideAllowed: boolean;
  tenantWide: boolean;
  hasBranch: boolean;
  defaultPattern: string;
  defaultReset: ResetRule;
  hasActiveScheme: boolean | null;
}

export interface Scheme {
  id: string;
  companyId: string | null;
  branchId: string | null;
  documentType: string;
  pattern: string;
  resetRule: ResetRule;
  gapFree: boolean;
  startValue: number;
  status: 'active' | 'retired';
  inUse: boolean;
  rowVersion: number;
}

export interface SchemeWrite { companyId?: string | null; branchId?: string | null; documentType?: string; pattern: string; resetRule: ResetRule; gapFree: boolean; startValue: number }

export interface IssuedRange { period: string; firstNumber: string; lastNumber: string; count: number | null; gapFree: boolean }
export interface VoidLine { id: string; number: string; reason: string; documentId: string | null; schemeId: string; voidedAt: string; voidedBy: string }
export interface VoidReport { schemeId: string; documentType: string; pattern: string; gapFree: boolean; from: string | null; to: string | null; issued: IssuedRange[]; voids: VoidLine[] }

export const numberingApi = {
  types: (companyId?: string | null) => requestJson<NumberedType[]>('GET', `/api/cfg/numbering/document-types${companyId ? `?companyId=${enc(companyId)}` : ''}`),
  create: (body: SchemeWrite) => requestJson<Scheme>('POST', '/api/cfg/numbering/schemes', { body }),
  edit: (id: string, body: Omit<SchemeWrite, 'companyId' | 'documentType'> & { rowVersion: number }) => requestJson<Scheme>('PUT', `/api/cfg/numbering/schemes/${enc(id)}`, { body }),
  retire: (id: string, rowVersion: number) => requestJson<Scheme>('POST', `/api/cfg/numbering/schemes/${enc(id)}/retire`, { body: { rowVersion } }),
  voidReport: (schemeId: string, from?: string, to?: string) => {
    const params = new URLSearchParams({ schemeId });
    if (from) params.set('from', from);
    if (to) params.set('to', to);
    return requestJson<VoidReport>('GET', `/api/cfg/numbering/void-report?${params.toString()}`);
  },
};

/** A scheme as the generated read endpoint lists it (whether it has issued a number is not part of it; the void report says). */
export type SchemeRow = Omit<Scheme, 'inUse'>;
export interface BranchRow { id: string; code: string; nameI18n: Record<string, string> }

/** Every scheme the person may see (a few pages at most: a customer has tens of schemes). */
export async function allSchemes(): Promise<SchemeRow[]> {
  const rows: SchemeRow[] = [];
  let after: string | null = null;
  for (let page = 0; page < 20; page += 1) {
    const answer: { items: SchemeRow[]; next: string | null } = await requestJson('GET', `/api/cfg/numbering-schemes?limit=200&sort=documentType${after ? `&after=${enc(after)}` : ''}`);
    rows.push(...answer.items);
    after = answer.next;
    if (!after) break;
  }
  return rows;
}

export const branchesOf = async (companyId: string): Promise<BranchRow[]> =>
  (await requestJson<{ items: BranchRow[] }>('GET', `/api/org/branches?companyId=${enc(companyId)}&isActive=true&limit=200&sort=code`)).items;
