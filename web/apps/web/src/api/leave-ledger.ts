// Hand-written client of the HR leave ledger, recompute and year-end screens (slice S1.33b; FR-HRM-LVE-10, -11, -14, FR-XM-05, FR-XM-06). Reads of other years come from the generated
// reads (@erp/generated/api/hrm: leaveLedger, leaveYearEndRuns); everything that posts, reverses, confirms or rolls back goes through these endpoints, which check the rules again.
// Texts in answers (`error`, `explanation`) are server keys, shown through `useText().server(key)`.
import { leaveLedger, leaveYearEndRuns, type LeaveLedger, type LeaveYearEndRun } from '@erp/generated/api/hrm';
import { requestJson } from './client';
import type { BalanceLine } from './leave';
import type { JobRun } from './ops';

const enc = encodeURIComponent;

/** Entries a person may post by hand (server `LeaveLedgerStore.ManualKinds`). */
export const MANUAL_KINDS = ['opening', 'grant', 'adjustment'] as const;
export type ManualKind = (typeof MANUAL_KINDS)[number];
/** Entries a person may reverse (server `LeaveLedgerStore.ReversibleKinds`); anything else is undone by its own process. */
export const REVERSIBLE_KINDS = ['opening', 'grant', 'accrual', 'adjustment'];

export interface BalancesAnswer { employmentId: string; companyId: string; employeeNo: string; asOf: string; balances: BalanceLine[] }

export interface PostBody { leaveTypeId: string; kind: ManualKind; quantity: number; entryDate: string; expiresOn?: string; reason: string; settlesPeriod?: string }
export interface ReverseBody { entryDate?: string; reason: string }

export type DifferenceKind = 'missing' | 'differs' | 'unexpected' | 'year_moved' | 'below_allowed';
export interface RecomputeDifference {
  leaveTypeId: string;
  leaveTypeCode: string;
  leaveYear: number;
  /** `Y2026` or `M2026-03`; null for a difference of the whole year. */
  period: string | null;
  kind: DifferenceKind;
  expected: number | null;
  posted: number | null;
  date: string | null;
  entryId: string | null;
  /** A server key. */
  explanation: string;
  entries: { id: string; entryDate: string; kind: string; quantity: number; reversedById: string | null }[];
}
export interface RecomputeAnswer {
  employmentId: string;
  employeeNo: string;
  asOf: string;
  horizonFrom: string | null;
  checked: number;
  changed: boolean;
  differences: RecomputeDifference[];
}

export interface YearEndLot { lotId: string; quantity: number; expiresOn: string }
export interface YearEndLine {
  employmentId: string; employeeNo: string; leaveTypeId: string; code: string; unit: string; yearStart: string; yearEnd: string;
  closing: number; carryForward: number; encash: number; lapse: number; carryExpiresOn: string | null; status: 'ok' | 'no_policy' | 'separated'; lots?: YearEndLot[] | null;
}
export interface YearEndSummary {
  leaveYear: number;
  preparedAt?: string;
  types: { leaveTypeId: string; code: string; yearStart: string; yearEnd: string }[];
  totals: { lines: number; carryForward: number; encash: number; lapse: number; noPolicy: number; separated: number };
  lines: YearEndLine[];
  confirmedAt?: string;
  confirmedBy?: string;
  posted?: number;
}
export type YearEndStatus = LeaveYearEndRun['status'];
export interface YearEndAnswer { id: string; status: YearEndStatus | 'pending'; rowVersion?: number; requestId?: string }

const PAGE = 200;
const MAX_PAGES = 25;

export const leaveLedgerApi = {
  /** The entries of the leave year containing `asOf` (today when left out), per leave type, with the balance the server worked out. */
  balances: (employmentId: string, asOf?: string, leaveTypeId?: string, signal?: AbortSignal) => {
    const q = [asOf ? `asOf=${enc(asOf)}` : '', leaveTypeId ? `leaveTypeId=${enc(leaveTypeId)}` : ''].filter(Boolean).join('&');
    return requestJson<BalancesAnswer>('GET', `/api/hrm/employments/${enc(employmentId)}/leave-balances${q ? `?${q}` : ''}`, signal ? { signal } : {});
  },
  /** Every entry of every leave year (the generated read), to learn which years and types have entries. */
  allEntries: async (employmentId: string, leaveTypeId?: string): Promise<LeaveLedger[]> => {
    const rows: LeaveLedger[] = [];
    let after: string | undefined;
    for (let page = 0; page < MAX_PAGES; page += 1) {
      const answer = await leaveLedger.list({ employmentId, limit: PAGE, sort: 'leaveYear', ...(leaveTypeId ? { leaveTypeId } : {}), ...(after ? { after } : {}) });
      rows.push(...answer.items);
      if (!answer.next) break;
      after = answer.next;
    }
    return rows;
  },
  post: (employmentId: string, body: PostBody) => requestJson<{ id: string }>('POST', `/api/hrm/employments/${enc(employmentId)}/leave-ledger`, { body }),
  reverse: (entryId: string, body: ReverseBody) => requestJson<{ id: string }>('POST', `/api/hrm/leave-ledger/${enc(entryId)}/reverse`, { body }),
  recompute: (employmentId: string, leaveTypeId?: string) =>
    requestJson<RecomputeAnswer>('GET', `/api/hrm/employments/${enc(employmentId)}/leave-recompute${leaveTypeId ? `?leaveTypeId=${enc(leaveTypeId)}` : ''}`),

  yearEndRuns: async (companyId: string): Promise<LeaveYearEndRun[]> =>
    (await leaveYearEndRuns.list({ companyId, limit: 100, sort: '-leaveYear' })).items,
  yearEndRun: (id: string) => leaveYearEndRuns.get(id),
  confirm: (id: string, rowVersion: number, reason?: string) => requestJson<YearEndAnswer>('POST', `/api/hrm/leave-year-end-runs/${enc(id)}/confirm`, { body: { rowVersion, ...(reason ? { reason } : {}) } }),
  rollback: (id: string, rowVersion: number, reason: string) => requestJson<YearEndAnswer>('POST', `/api/hrm/leave-year-end-runs/${enc(id)}/rollback`, { body: { rowVersion, reason } }),
  /** Starts the preview job for a company and leave year (the job centre makes the run; the preview appears when it has succeeded). */
  startPreview: (definitionId: string, companyId: string, leaveYear: number) =>
    requestJson<JobRun>('POST', `/api/jobs/definitions/${enc(definitionId)}/run`, { body: { companyId, parameters: { leave_year: leaveYear } } }),
};

/** The summary of a year-end run as the job wrote it; missing parts read as empty. */
export function summaryOf(run: Pick<LeaveYearEndRun, 'summary' | 'leaveYear'>): YearEndSummary {
  const s = (run.summary ?? {}) as Partial<YearEndSummary>;
  const totals = s.totals ?? { lines: 0, carryForward: 0, encash: 0, lapse: 0, noPolicy: 0, separated: 0 };
  return { ...s, leaveYear: s.leaveYear ?? run.leaveYear, types: s.types ?? [], totals, lines: s.lines ?? [] };
}
