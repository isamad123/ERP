// Hand-written client of the pay set-up (slice S1.34a; contract "Pay set-up API"): generating the periods of a tax year, changing a
// period's dates before it opens, the payroll calendar and the readiness checklist. Pay groups and their periods are read (and pay
// groups written) through the generated routes by the engine. The server checks every request again; the screens show its
// answers as confirmed, never assumed (spec 13 STA-06). No amount of a pay group is part of these answers (GHR-06).
import { requestJson } from './client';

const enc = encodeURIComponent;

export type PeriodStatus = 'future' | 'open' | 'processing' | 'closed';
export type ReadinessStatus = 'passed' | 'failed' | 'not_available';

export interface GroupOption { id: string; code: string; nameI18n: Record<string, string>; frequency: 'monthly' | 'semi_monthly' | 'bi_weekly' | 'weekly'; isActive: boolean }

export interface CalendarPeriod {
  id: string;
  companyId: string;
  payGroupId: string;
  payGroupCode: string | null;
  periodNo: number;
  taxYear: number;
  startDate: string;
  endDate: string;
  cutOffDate: string;
  paymentDate: string;
  status: PeriodStatus;
  daysToCutOff: number;
  daysToPayment: number;
  rowVersion: number;
}

export interface ReadinessEmployee { employmentId: string; employeeNo: string }
export interface ReadinessItem { code: string; labelKey: string; slice: string; requirement: string; status: ReadinessStatus; count: number; employees: ReadinessEmployee[] }
export interface Readiness { periodId: string; payGroupId: string; ready: boolean; summary: { passed: number; failed: number; notAvailable: number }; items: ReadinessItem[] }

export const WEEKDAYS = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'] as const;
export type Weekday = (typeof WEEKDAYS)[number];

export const payApi = {
  groups: () => requestJson<{ items: GroupOption[] }>('GET', '/api/hrm/pay-groups?limit=200'),
  calendar: (payGroupId: string | null, taxYear: number | null) => {
    const params = new URLSearchParams();
    if (payGroupId) params.set('payGroupId', payGroupId);
    if (taxYear) params.set('taxYear', String(taxYear));
    const query = params.toString();
    return requestJson<{ items: CalendarPeriod[]; more: boolean }>('GET', `/api/hrm/pay-calendar${query ? `?${query}` : ''}`);
  },
  generate: (payGroupId: string, body: { taxYear: number; weekStartsOn?: Weekday }) =>
    requestJson<{ payGroupId: string; taxYear: number; count: number }>('POST', `/api/hrm/pay-groups/${enc(payGroupId)}/periods/generate`, { body }),
  change: (periodId: string, body: { cutOffDate?: string; paymentDate?: string; rowVersion: number }) =>
    requestJson<{ id: string; rowVersion: number }>('PUT', `/api/hrm/pay-periods/${enc(periodId)}`, { body }),
  readiness: (periodId: string) => requestJson<Readiness>('GET', `/api/hrm/pay-periods/${enc(periodId)}/readiness`),
};
