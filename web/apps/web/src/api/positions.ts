// Hand-written client of the positions, the strength report and the org chart by position (slice S1.22b; contract "Positions API").
// Positions are written through these routes (the generated writes are closed: a change from a date adds a row); grades and headcount
// budgets are made by the engine. The server checks every request again (permissions, data scope); the screens show its answers as
// confirmed, never assumed (spec 13 STA-06). Exports are downloaded through the authenticated client, never through a link.
import { problemOf, requestJson, send } from './client';

const enc = encodeURIComponent;

export type PositionStatus = 'planned' | 'open' | 'filled' | 'frozen' | 'closed';
export type PlanningModel = 'position' | 'headcount';

export interface PositionRow {
  id: string;
  companyId: string;
  positionCode: string;
  title: string;
  orgUnitId: string;
  gradeId: string | null;
  reportsToPositionId: string | null;
  locationId: string | null;
  budgetedFte: string;
  status: string;
  validFrom: string;
  validTo: string | null;
  rowVersion: number;
}

export interface PositionHolder { employmentId: string; employeeNo: string; fte: number; isActing: boolean }
export interface PositionHolders { position: PositionRow; asOf: string; status: PositionStatus; budgetedFte: number; filledFte: number; vacantFte: number; overFte: number; holders: PositionHolder[] }

export interface PositionWrite {
  companyId?: string;
  positionCode?: string;
  title?: string;
  orgUnitId?: string;
  gradeId?: string | null;
  reportsToPositionId?: string | null;
  locationId?: string | null;
  budgetedFte?: number;
  validFrom?: string;
  effectiveDate?: string;
  status?: 'open' | 'frozen';
  clear?: string[];
  rowVersion?: number;
}

export interface StrengthRow {
  unitId: string | null; unitCode: string | null; unitName: Record<string, string> | null;
  gradeId: string | null; gradeCode: string | null;
  locationId: string | null; locationCode: string | null; locationName: Record<string, string> | null;
  sanctioned: number; filled: number; vacant: number; over: number; planned: number; frozen: number; positions: number;
}
export interface StrengthTotals { sanctioned: number; filled: number; vacant: number; over: number; planned: number; frozen: number }
export interface StrengthReport { companyId: string; asOf: string; model: PlanningModel; groupBy: string[]; rows: StrengthRow[]; totals: StrengthTotals }
export interface StrengthFilter { companyId: string; asOf: string; groupBy: string[]; unitId?: string | null; gradeId?: string | null; locationId?: string | null }

export interface DrillPosition {
  id: string; code: string; title: string; unitId: string; gradeId: string | null; locationId: string | null;
  budgetedFte: number; filledFte: number; vacantFte: number; overFte: number; status: PositionStatus;
  holders: { employmentId: string; employeeNo: string; fte: number; isActing: boolean; name: string | null }[];
}
export interface DrillPerson { employmentId: string; employeeNo: string; fte: number; gradeId: string | null; title: string; name: string | null }
export interface StrengthDrill { asOf: string; model: PlanningModel; positions: DrillPosition[]; people: DrillPerson[] }

export interface ChartHolder { employmentId: string; employeeNo: string; name: string | null; nameLocal: string | null; photoFileId: string | null; fte: number; isActing: boolean }
export interface ChartNode {
  id: string; parentId: string | null; code: string; title: string; unitId: string | null; unitCode: string | null; gradeId: string | null; locationId: string | null;
  budgetedFte: number; filledFte: number; vacant: boolean; status: string; holders: ChartHolder[];
}
export interface OrgChart { companyId: string; asOf: string; model: PlanningModel; truncated: boolean; total: number; nodes: ChartNode[] }
export interface ChartFilter { companyId: string; asOf: string; unitId?: string | null; locationId?: string | null }
export interface ExportLabels { title: string; asOf: string; vacant: string }

const query = (values: Record<string, string | null | undefined>) => {
  const parts = Object.entries(values).filter(([, v]) => v !== null && v !== undefined && v !== '').map(([k, v]) => `${k}=${enc(String(v))}`);
  return parts.length > 0 ? `?${parts.join('&')}` : '';
};

const strengthQuery = (f: StrengthFilter) => query({ companyId: f.companyId, asOf: f.asOf, groupBy: f.groupBy.join(','), unitId: f.unitId, gradeId: f.gradeId, locationId: f.locationId });

/** Saves the file the server answered (an export), through a temporary link. */
async function download(response: Response, fallbackName: string): Promise<void> {
  if (!response.ok) throw await problemOf(response);
  const name = /filename="?([^";]+)"?/.exec(response.headers.get('content-disposition') ?? '')?.[1] ?? fallbackName;
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
}

export const positionsApi = {
  list: (companyId: string, asOf: string | null, search: string | null = null) =>
    requestJson<{ items: PositionRow[]; next: string | null }>('GET', `/api/hrm/positions${query({ companyId, asOf, search, limit: '200', sort: 'code' })}`),
  history: (companyId: string, code: string) => requestJson<{ items: PositionRow[] }>('GET', `/api/hrm/positions${query({ companyId, search: code, limit: '100' })}`),
  holders: (id: string, asOf: string | null) => requestJson<PositionHolders>('GET', `/api/hrm/positions/${id}/holders${query({ asOf })}`),
  create: (body: PositionWrite) => requestJson<PositionRow>('POST', '/api/hrm/positions', { body }),
  change: (id: string, body: PositionWrite) => requestJson<PositionRow>('POST', `/api/hrm/positions/${id}/changes`, { body }),
  close: (id: string, body: { effectiveDate: string; rowVersion: number }) => requestJson<PositionRow>('POST', `/api/hrm/positions/${id}/close`, { body }),
  grades: (companyId: string) => requestJson<{ items: { id: string; code: string; nameI18n: Record<string, string>; rank: number }[] }>('GET', `/api/hrm/grades${query({ companyId, limit: '200', sort: 'code' })}`),
};

export const strengthApi = {
  report: (f: StrengthFilter, signal?: AbortSignal) => requestJson<StrengthReport>('GET', `/api/hrm/strength-report${strengthQuery(f)}`, signal ? { signal } : {}),
  drill: (f: StrengthFilter, signal?: AbortSignal) => requestJson<StrengthDrill>('GET', `/api/hrm/strength-report/drill${strengthQuery(f)}`, signal ? { signal } : {}),
  exportCsv: async (f: StrengthFilter) => { await download(await send('GET', `/api/hrm/strength-report/export${strengthQuery(f)}`), 'strength.csv'); },
};

export const chartApi = {
  chart: (f: ChartFilter, signal?: AbortSignal) => requestJson<OrgChart>('GET', `/api/hrm/org-chart${query({ companyId: f.companyId, asOf: f.asOf, unitId: f.unitId, locationId: f.locationId })}`, signal ? { signal } : {}),
  exportFile: async (f: ChartFilter, format: 'pdf' | 'csv', language: 'en' | 'ur', labels: ExportLabels) => {
    await download(await send('POST', '/api/hrm/org-chart/export', { body: { companyId: f.companyId, asOf: f.asOf, unitId: f.unitId ?? null, locationId: f.locationId ?? null, format, language, labels } }), `org-chart.${format}`);
  },
};
