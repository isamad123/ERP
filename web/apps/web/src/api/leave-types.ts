// Hand-written client of leave types (slice S1.33b Build item 6; FR-HRM-LVE-01). Reads are the generated list `/api/hrm/leave-types`; create and change go through the
// purpose-built endpoints, which check the permission, the company's data scope and every rule again. Texts in refusals are server keys (`useText().server(key)`).
import { requestJson } from './client';
import type { Names } from './workflow';

const enc = encodeURIComponent;

export const COLOUR_TOKENS = ['primary', 'success', 'warning', 'danger', 'info', 'neutral'] as const;
export type ColourToken = (typeof COLOUR_TOKENS)[number];
export const COUNTINGS = ['calendar_days', 'working_days', 'hours'] as const;
export type Counting = (typeof COUNTINGS)[number];
export const UNITS = ['day', 'half_day', 'hour'] as const;
export type LeaveUnit = (typeof UNITS)[number];

/** A row of the generated list. */
export interface LeaveTypeRow {
  id: string;
  companyId: string;
  code: string;
  nameI18n: Names;
  isPaid: boolean;
  counting: Counting;
  unit: LeaveUnit;
  requiresDocument: boolean;
  isCompOff: boolean;
  isUnpaidDeduction: boolean;
  source: string;
  colorToken?: string | null;
  isActive: boolean;
  rowVersion: number;
}

/** The body of a create or a change (server `LeaveTypeRequest`). An empty `colorToken` clears the colour. */
export interface LeaveTypeBody {
  companyId?: string;
  code?: string;
  nameI18n?: Names;
  isPaid?: boolean;
  counting?: Counting;
  unit?: LeaveUnit;
  requiresDocument?: boolean;
  isCompOff?: boolean;
  isUnpaidDeduction?: boolean;
  isActive?: boolean;
  colorToken?: string;
  rowVersion?: number;
}

export const leaveTypesApi = {
  list: async (companyId: string, signal?: AbortSignal): Promise<LeaveTypeRow[]> => {
    const rows: LeaveTypeRow[] = [];
    let after: string | null = null;
    for (let page = 0; page < 10; page += 1) {
      const answer: { items: LeaveTypeRow[]; next?: string | null } = await requestJson('GET', `/api/hrm/leave-types?companyId=${enc(companyId)}&limit=200&sort=code${after ? `&after=${enc(after)}` : ''}`, { signal });
      rows.push(...answer.items);
      after = answer.next ?? null;
      if (!after) break;
    }
    return rows;
  },
  create: (body: LeaveTypeBody) => requestJson<LeaveTypeRow>('POST', '/api/hrm/leave-types', { body }),
  update: (id: string, body: LeaveTypeBody) => requestJson<LeaveTypeRow>('PUT', `/api/hrm/leave-types/${enc(id)}`, { body }),
};
