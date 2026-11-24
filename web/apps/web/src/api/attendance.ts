// Hand-written client of attendance capture (slice S1.27a; contract "HR attendance capture API"). The person of a punch is the signed-in
// user's own employment, chosen by the server; the screens only say which direction. Every answer is shown as confirmed by the server.
import { requestJson } from './client';

export interface MyDayEvent { id: string; eventAt: string; direction: string; source: string; isDuplicate: boolean }
export interface MyDay {
  employments: { employmentId: string; companyId: string; employeeNo: string }[];
  employmentId: string | null;
  companyId: string | null;
  needsCompany: boolean;
  today: string | null;
  serverTime: string;
  next: 'in' | 'out' | null;
  events: MyDayEvent[];
}
export interface PunchResult { eventId: string; status: 'stored' | 'already_received'; eventAt: string; direction: string; source: string; isDuplicate: boolean }
export interface KioskResult { punch: PunchResult; employeeNo: string; firstName: string }
export interface QrCode { code: string; companyId: string; locationId: string; windowSeconds: number; expiresAt: string; serverTime: string }

/** A client-made key: a retried request punches once. */
export const newKey = (): string => crypto.randomUUID();

export const attendanceApi = {
  myDay: (companyId?: string | null, signal?: AbortSignal) =>
    requestJson<MyDay>('GET', '/api/hrm/attendance/my-day' + (companyId ? `?companyId=${encodeURIComponent(companyId)}` : ''), { signal }),
  punch: (direction: 'in' | 'out', idempotencyKey: string, companyId?: string | null) =>
    requestJson<PunchResult>('POST', '/api/hrm/attendance/punch', { body: { direction, idempotencyKey, companyId: companyId ?? undefined } }),
  qrPunch: (code: string, direction: 'in' | 'out', idempotencyKey: string) =>
    requestJson<PunchResult>('POST', '/api/hrm/attendance/qr-punch', { body: { code, direction, idempotencyKey } }),
  kioskPunch: (companyId: string, badgeNo: string, direction: 'in' | 'out', idempotencyKey: string) =>
    requestJson<KioskResult>('POST', '/api/hrm/attendance/kiosk/punch', { body: { companyId, badgeNo, direction, idempotencyKey } }),
  qrCode: (companyId: string, locationId: string, signal?: AbortSignal) =>
    requestJson<QrCode>('GET', `/api/hrm/attendance/qr-code?companyId=${encodeURIComponent(companyId)}&locationId=${encodeURIComponent(locationId)}`, { signal }),
  locations: (companyId: string, signal?: AbortSignal) =>
    requestJson<{ items: { id: string; code: string; nameI18n: Record<string, string> }[] }>('GET', `/api/org/locations?companyId=${encodeURIComponent(companyId)}&limit=200`, { signal }),
};
