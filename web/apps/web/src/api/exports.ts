// Hand-written client of the export centre (slice S1.16b; routes in src/Platform/Erp.Kernel.Api/Itg/ExportEndpoints.cs). The server decides
// who may export what and who may download which file; the screen shows its answers as confirmed, never assumed (spec 13 STA-06).
import { problemOf, requestJson, send } from './client';

export const EXPORT_STATUSES = ['queued', 'running', 'done', 'failed', 'expired', 'awaiting_approval', 'rejected'] as const;
export type ExportStatus = (typeof EXPORT_STATUSES)[number];

export interface ExportView {
  id: string;
  source: string;
  format: 'csv' | 'xlsx';
  status: ExportStatus;
  fileName: string | null;
  rowCount: number | null;
  sizeBytes: number | null;
  reason: string | null;
  sensitiveClasses: string[];
  companyId: string;
  createdBy: string;
  createdByName: string | null;
  createdAt: string;
  finishedAt: string | null;
  expiresAt: string | null;
  errorKey: string | null;
  heldRequestId: string | null;
  mine: boolean;
  canDownload: boolean;
}

export interface ExportPage { items: ExportView[]; next: string | null }

export const EXPORT_POLL_MS = 3000;
export const isExportRunning = (status: ExportStatus) => status === 'queued' || status === 'running';

const base = '/api/itg/exports';

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
  const text = params.toString();
  return text ? `?${text}` : '';
}

export const exportsApi = {
  list: (filters: { all?: boolean; status?: string; cursor?: string | null }, signal?: AbortSignal) =>
    requestJson<ExportPage>('GET', `${base}${query({ all: filters.all, status: filters.status, cursor: filters.cursor })}`, signal ? { signal } : {}),
  /** Saves the file the browser keeps (never shown in the page); the name is the server's. */
  download: async (view: ExportView): Promise<void> => {
    const response = await send('GET', `${base}/${encodeURIComponent(view.id)}/download`);
    if (!response.ok) throw await problemOf(response);
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = view.fileName ?? `export.${view.format}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  },
};
