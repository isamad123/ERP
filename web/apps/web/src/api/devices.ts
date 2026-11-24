// Hand-written client of the attendance device services (slice S1.27b; contract "HR attendance devices API"). Credentials are sent when a device is
// registered or changed and are never returned: the server answers only whether the device has some.
import { requestJson } from './client';

export interface DeviceRow {
  id: string; companyId: string; locationId: string; serialNo: string; model: string | null; provider: string | null; host: string | null; port: number | null; protocol: string | null;
  hasCredentials: boolean; status: 'active' | 'offline' | 'retired'; online: boolean; lastSeenAt: string | null; lastSyncAt: string | null; lastError: string | null;
  syncRequestedAt: string | null; unmappedOpen: number; rowVersion: number;
}
export interface DeviceInput { companyId: string; locationId: string; serialNo: string; model?: string; provider: string; host?: string; port?: number; protocol?: string; credentials?: string }
export interface MapRow {
  id: string; companyId: string; deviceId: string | null; deviceUserNo: string; employmentId: string; employeeNo: string | null; validFrom: string; validTo: string | null;
  consentAt: string | null; removalDueAt: string | null; removalDoneAt: string | null; rowVersion: number;
}
export interface UnmappedRow { id: string; companyId: string; deviceId: string; deviceUserNo: string; punchedAt: string; direction: string; receivedAt: string; status: string; rowVersion: number }
export interface LogRow {
  id: string; employmentId: string; employeeNo: string | null; eventAt: string; receivedAt: string; direction: string; source: string; deviceId: string | null; deviceSerial: string | null; isDuplicate: boolean;
}
export interface LogFilter { companyId?: string; from?: string; to?: string; source?: string; employeeNo?: string; deviceId?: string; includeDuplicates?: boolean }

const query = (values: Record<string, string | boolean | undefined>) => {
  const q = Object.entries(values).filter(([, v]) => v !== undefined && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
  return q ? `?${q}` : '';
};

export const devicesApi = {
  list: (companyId?: string, signal?: AbortSignal) => requestJson<{ devices: DeviceRow[] }>('GET', '/api/hrm/attendance/devices' + query({ companyId }), { signal }),
  create: (body: DeviceInput) => requestJson<DeviceRow>('POST', '/api/hrm/attendance/devices', { body }),
  update: (id: string, body: Partial<DeviceInput> & { status?: 'active' | 'retired'; rowVersion: number }) => requestJson<DeviceRow>('PUT', `/api/hrm/attendance/devices/${id}`, { body }),
  syncNow: (id: string) => requestJson<{ id: string }>('POST', `/api/hrm/attendance/devices/${id}/sync`, {}),
  maps: (companyId?: string, state?: string, signal?: AbortSignal) => requestJson<{ maps: MapRow[] }>('GET', '/api/hrm/attendance/device-maps' + query({ companyId, state }), { signal }),
  map: (body: { companyId: string; deviceId?: string; deviceUserNo: string; employeeNo: string; validFrom?: string; consentDate?: string }) =>
    requestJson<{ map: MapRow; replayedPunches: number }>('POST', '/api/hrm/attendance/device-maps', { body }),
  endMap: (id: string, rowVersion: number, endDate?: string) => requestJson<MapRow>('POST', `/api/hrm/attendance/device-maps/${id}/end`, { body: { rowVersion, endDate } }),
  removalDone: (id: string, rowVersion: number) => requestJson<MapRow>('POST', `/api/hrm/attendance/device-maps/${id}/removal-done`, { body: { rowVersion } }),
  unmapped: (companyId?: string, signal?: AbortSignal) => requestJson<{ punches: UnmappedRow[] }>('GET', '/api/hrm/attendance/unmapped' + query({ companyId }), { signal }),
  discard: (id: string, rowVersion: number) => requestJson<{ id: string }>('POST', `/api/hrm/attendance/unmapped/${id}/discard`, { body: { rowVersion } }),
  log: (filter: LogFilter, after?: string | null, signal?: AbortSignal) =>
    requestJson<{ events: LogRow[]; next: string | null }>('GET', '/api/hrm/attendance/punch-log' + query({ ...filter, after: after ?? undefined, limit: '50' }), { signal }),
};
