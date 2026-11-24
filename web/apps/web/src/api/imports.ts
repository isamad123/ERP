// Hand-written client of the import centre (slice S1.16a; contract in docs/handoff/2026-11-23-S1.16a.md). The server checks every
// request again; the screens show its answers as confirmed, never assumed (spec 13 STA-06). Files are sent as multipart forms.
import { problemOf, requestJson, send } from './client';
import type { Names } from './workflow';

export type ImportColumnType = 'text' | 'number' | 'date' | 'bool' | 'option';
export type ImportFormat = 'csv' | 'xlsx';
export type ImportMode = 'create' | 'create_or_update';
export const BATCH_STATUSES = ['uploaded', 'validating', 'validated', 'committing', 'committed', 'failed', 'rolled_back'] as const;
export type BatchStatus = (typeof BATCH_STATUSES)[number];

export interface ImportColumn {
  name: string;
  field: string;
  type: ImportColumnType;
  required: boolean;
  label: Names;
  lookup: string | null;
  options: string[] | null;
  example: string | null;
}

export interface ImportType {
  code: string;
  module: string;
  name: Names;
  permission: string;
  canUpdate: boolean;
  canRun: boolean;
  columns: ImportColumn[];
}

export interface MappingColumn { field: string; source: string; transform: string[]; default: string | null }
export interface TemplateMapping { delimiter: ',' | ';' | null; sheet: string | null; headerRow: number; columns: MappingColumn[] }

export interface ImportTemplate {
  id: string;
  code: string;
  name: Names;
  entityType: string;
  format: ImportFormat;
  mode: ImportMode;
  isDefault: boolean;
  version: number;
  isActive: boolean;
  rowVersion: number;
  mapping: TemplateMapping;
}

export interface TemplateWrite { code: string; name: Names; entityType: string; format: ImportFormat; mode: ImportMode; mapping: TemplateMapping }
export interface TemplateUpdate { name: Names; mode: ImportMode; mapping: TemplateMapping; rowVersion: number }

export interface ImportBatch {
  id: string;
  templateId: string;
  templateName: Names;
  entityType: string;
  fileName: string | null;
  status: BatchStatus;
  mode: ImportMode;
  totalRows: number | null;
  okRows: number | null;
  errorRows: number | null;
  createdRows: number | null;
  updatedRows: number | null;
  progressPct: number | null;
  jobRunId: string | null;
  createdAt: string;
  createdBy: string | null;
  validatedAt: string | null;
  committedAt: string | null;
  rolledBackAt: string | null;
  canCommit: boolean;
  canRollback: boolean;
  rollbackBlockedKey: string | null;
}

export interface BatchError { rowNo: number; field: string | null; message: string; phase: 'validate' | 'commit' }
export interface Page<T> { items: T[]; next: string | null }
export interface TestResult { totalRows: number; errorRows: number; errors: { rowNo: number; field: string | null; message: string }[]; sample: { rowNo: number; values: Record<string, unknown> }[] }

const id = (value: string) => encodeURIComponent(value);
const base = '/api/itg/imports';

function query(values: Record<string, string | number | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
  const text = params.toString();
  return text ? `?${text}` : '';
}

/** Saves a downloaded answer as a file the browser keeps (never shown in the page). */
async function save(response: Response, fileName: string): Promise<void> {
  if (!response.ok) throw await problemOf(response);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

async function upload<T>(path: string, form: FormData): Promise<T> {
  const response = await send('POST', path, { form });
  if (!response.ok) throw await problemOf(response);
  return (await response.json()) as T;
}

export const importsApi = {
  types: () => requestJson<ImportType[]>('GET', `${base}/types`),
  templates: (type?: string) => requestJson<ImportTemplate[]>('GET', `${base}/templates${query({ type })}`),
  createTemplate: (body: TemplateWrite) => requestJson<ImportTemplate>('POST', `${base}/templates`, { body }),
  updateTemplate: (templateId: string, body: TemplateUpdate) => requestJson<ImportTemplate>('PUT', `${base}/templates/${id(templateId)}`, { body }),
  downloadTemplate: async (template: ImportTemplate, format: ImportFormat) =>
    save(await send('GET', `${base}/templates/${id(template.id)}/file${query({ format })}`), `${template.code}.${format}`),
  testTemplate: (templateId: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return upload<TestResult>(`${base}/templates/${id(templateId)}/test`, form);
  },
  upload: (file: File, templateId: string) => {
    const form = new FormData();
    form.append('file', file);
    form.append('templateId', templateId);
    return upload<ImportBatch>(`${base}/batches`, form);
  },
  batches: (filters: { type?: string | undefined; status?: string | undefined }, signal?: AbortSignal) =>
    requestJson<Page<ImportBatch>>('GET', `${base}/batches${query(filters)}`, signal ? { signal } : {}),
  batch: (batchId: string) => requestJson<ImportBatch>('GET', `${base}/batches/${id(batchId)}`),
  validate: (batchId: string) => requestJson<ImportBatch>('POST', `${base}/batches/${id(batchId)}/validate`, { body: {} }),
  commit: (batchId: string) => requestJson<ImportBatch>('POST', `${base}/batches/${id(batchId)}/commit`, { body: {} }),
  rollback: (batchId: string, reason: string) => requestJson<ImportBatch>('POST', `${base}/batches/${id(batchId)}/rollback`, { body: { reason } }),
  errors: (batchId: string, limit = 100, cursor?: string | null) => requestJson<Page<BatchError>>('GET', `${base}/batches/${id(batchId)}/errors${query({ limit, cursor })}`),
  downloadErrors: async (batchId: string) => save(await send('GET', `${base}/batches/${id(batchId)}/errors.csv`), 'import-errors.csv'),
};

/** Running batches are watched every two seconds until they stop (live progress, FR-KRN-INT-04). */
export const RUNNING: readonly BatchStatus[] = ['validating', 'committing'];
export const POLL_MS = 2000;
export const isRunning = (status: string | undefined) => RUNNING.includes(status as BatchStatus);
