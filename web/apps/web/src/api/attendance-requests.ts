// Hand-written client of the attendance request services (slice S1.30a; FR-HRM-ATT-11, ATT-14, GHR-09) and the exception queue. The lists of the
// requests themselves are the generated reads (@erp/generated/api/hrm); a request is decided and withdrawn through the shared approval endpoints.
import { problemOf, requestJson, send } from './client';

export interface ExceptionRow {
  id: string; companyId: string; employmentId: string; employeeNo: string | null; workDate: string; kind: string; status: string; attendanceDayId: string | null; createdAt: string; ageDays: number; rowVersion: number;
}
export interface UnmappedItem { id: string; companyId: string; deviceId: string; deviceUserNo: string; punchedAt: string; direction: string; kind: 'unmapped' }
export interface ExceptionQueue { status: string; items: ExceptionRow[]; more: boolean; unmapped: { count: number; items: UnmappedItem[] } | null }
export interface ExceptionFilter { companyId?: string; status?: string; kind?: string; minAgeDays?: string; unitId?: string; from?: string; to?: string }

export interface RegularizationBody {
  employmentId?: string; exceptionId?: string; workDate?: string; requestedInTime?: string; requestedInDayOffset?: number; requestedOutTime?: string; requestedOutDayOffset?: number; reason: string;
}
export interface DutyBody { employmentId?: string; kind: string; startDate: string; endDate: string; startTime?: string; endTime?: string; purpose: string }
export interface Requested { id: string; employmentId: string; status: string; instanceId: string; onBehalf: boolean; rowVersion: number }

const query = (values: Record<string, string | undefined>) => {
  const q = Object.entries(values).filter(([, v]) => v !== undefined && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
  return q ? `?${q}` : '';
};

export const attendanceRequestsApi = {
  exceptions: (filter: ExceptionFilter, signal?: AbortSignal) => requestJson<ExceptionQueue>('GET', '/api/hrm/attendance/exceptions' + query({ ...filter, limit: '200' }), { signal }),
  regularize: (body: RegularizationBody) => requestJson<Requested>('POST', '/api/hrm/attendance/regularization-requests', { body }),
  duty: (body: DutyBody) => requestJson<Requested>('POST', '/api/hrm/attendance/duty-requests', { body }),
  withdraw: (instanceId: string, reason: string) => requestJson<unknown>('POST', `/api/wf/instances/${instanceId}/withdraw`, { body: { reason } }),
};

// Proof files of a request (S1.30b, R-161; FR-KRN-DOC-01): the file belongs to the request as its owner, through the kernel's attachment service.
export const PROOF_OWNER = { correction: 'hrm.regularization_requests', duty: 'hrm.duty_requests' } as const;
export type ProofOwner = (typeof PROOF_OWNER)[keyof typeof PROOF_OWNER];
export interface ProofFile { id: string; fileName: string; contentType?: string; sizeBytes?: number }
export const PROOF_ACCEPT = 'image/png,image/jpeg,application/pdf';

export const proofFilesApi = {
  list: async (ownerType: ProofOwner, ownerId: string, signal?: AbortSignal): Promise<ProofFile[]> => {
    const answer = await requestJson<unknown>('GET', `/api/doc/attachments?ownerType=${encodeURIComponent(ownerType)}&ownerId=${encodeURIComponent(ownerId)}`, { signal });
    return Array.isArray(answer) ? (answer as ProofFile[]) : [];
  },
  upload: async (ownerType: ProofOwner, ownerId: string, file: File): Promise<ProofFile> => {
    const form = new FormData();
    form.set('OwnerType', ownerType);
    form.set('OwnerId', ownerId);
    form.set('File', file, file.name);
    const response = await send('POST', '/api/doc/attachments', { form });
    if (!response.ok) throw await problemOf(response);
    return (await response.json()) as ProofFile;
  },
  remove: (id: string) => requestJson<unknown>('DELETE', `/api/doc/attachments/${id}`),
  /** The file as a blob, fetched through the signed-in client (no token in an address). */
  content: async (id: string): Promise<Blob> => {
    const response = await send('GET', `/api/doc/attachments/${id}/content`);
    if (!response.ok) throw await problemOf(response);
    return await response.blob();
  },
};
