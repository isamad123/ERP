// Hand-written client of the operations screens (slice S1.16c): job centre (/api/jobs), message log (/api/ntf/deliveries), system
// health (/api/jobs/system-health) and the licence page (/api/cfg/licence). The server checks every request again.
import { requestJson } from './client';
import type { Names } from './workflow';

export type Query = Record<string, string | number | null | undefined>;

function query(values: Query): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
  const text = params.toString();
  return text ? `?${text}` : '';
}

/** A job's schedule exactly as the server stores it: one of the five shapes, optionally working days only. */
export interface Schedule {
  every_minutes?: number;
  hourly?: { minute: number };
  daily?: { at: string };
  weekly?: { days: string[]; at: string };
  monthly?: { day: number | 'last'; at: string };
  working_days_only?: boolean;
}

export interface JobDefinition {
  id: string; code: string; module: string; name: Names; description: Names; schedule: Schedule | null; isEnabled: boolean; priority: number;
  manual: boolean; scheduleEditable: boolean; licensed: boolean; runsIn: string[]; nextRunAt: string | null; lastRunAt: string | null; lastStatus: string | null; rowVersion: number;
}

export const RUN_STATUSES = ['queued', 'running', 'succeeded', 'failed', 'cancelled'] as const;
export type RunStatus = (typeof RUN_STATUSES)[number];

export interface JobRun {
  id: string; definitionId: string; code: string; status: RunStatus; companyId: string | null; progressPct: number | null; attempt: number;
  scheduledFor: string | null; startedAt: string | null; finishedAt: string | null; nextAttemptAt: string | null; cancelRequestedAt: string | null;
  error: string | null; requestedBy: string | null; actorUserId: string | null; parameters: Record<string, unknown> | null; createdAt: string;
}
export interface JobLog { at: string; level: string; message: string }
export interface JobRunDetail { run: JobRun; logs: JobLog[] }
export interface EventBacklog { schema: string; waiting: number; failing: number; oldestAt: string | null }
export interface JobHealth {
  queued: number; running: number; failedLast24Hours: number; oldestQueuedAt: string | null; lastWorkerActivityAt: string | null; workerSeenRecently: boolean; events: EventBacklog[];
}
export interface SystemHealth {
  jobs: JobHealth; filesNotScanned: number; oldestUnscannedAt: string | null; storageUsedBytes: number; databaseSizeBytes: number;
  lastBackupAt: string | null; lastRestoreTestAt: string | null; at: string;
}

export interface Delivery {
  id: string; createdAt: string; channel: string; status: 'queued' | 'sent' | 'failed' | 'dead'; recipient: string; userId: string | null; eventCode: string | null;
  companyId: string | null; subject: string; attempts: number; nextAttemptAt: string | null; error: string | null; providerRef: string | null; updatedAt: string; resendable: boolean;
}
export interface DeliveryDetail { message: Delivery; body: string; link: string | null; language: string; resendOf: string | null; bodyWithheld: boolean }
export interface DeliverySummary { queued: number; retrying: number; sent24h: number; failed24h: number; dead24h: number; oldestQueuedAt: string | null }
export interface DeliveryDay { day: string; sent: number; failed: number; dead: number; queued: number }

export interface LimitUse { key: string; max: number | null; used: number | null; percent: number | null }
export interface Licence {
  edition: string | null; version: number | null; deployment: string; modules: string[]; features: string[]; packs: string[]; addons: string[]; limits: LimitUse[];
  effectiveFrom: string | null; expiresAt: string | null; maintenanceExpiresAt: string | null; supportTier: string | null; state: string; nextState: string | null; nextStateOn: string | null; scheduledFrom: string | null;
}
export interface UsageLine { meter: string; value: number; limitKey: string | null; limit: number | null; percent: number | null }
export interface UsageStatement { period: string; lines: UsageLine[]; highestPercent: number | null }

export const PAGE_SIZE = 25;
const jobs = '/api/jobs';
const id = (value: string) => encodeURIComponent(value);

export const opsApi = {
  definitions: () => requestJson<JobDefinition[]>('GET', `${jobs}/definitions`),
  changeSchedule: (definitionId: string, body: { schedule: Schedule | null; isEnabled: boolean; rowVersion: number }) =>
    requestJson<JobDefinition>('PUT', `${jobs}/definitions/${id(definitionId)}/schedule`, { body }),
  runNow: (definitionId: string) => requestJson<JobRun>('POST', `${jobs}/definitions/${id(definitionId)}/run`, { body: {} }),
  runs: (q: Query, signal?: AbortSignal) => requestJson<JobRun[]>('GET', `${jobs}/runs${query(q)}`, signal ? { signal } : {}),
  run: (runId: string) => requestJson<JobRunDetail>('GET', `${jobs}/runs/${id(runId)}`),
  rerun: (runId: string) => requestJson<JobRun>('POST', `${jobs}/runs/${id(runId)}/rerun`, {}),
  cancel: (runId: string) => requestJson<JobRun>('POST', `${jobs}/runs/${id(runId)}/cancel`, {}),
  systemHealth: () => requestJson<SystemHealth>('GET', `${jobs}/system-health`),
  deliveries: (q: Query, signal?: AbortSignal) => requestJson<Delivery[]>('GET', `/api/ntf/deliveries${query(q)}`, signal ? { signal } : {}),
  delivery: (deliveryId: string) => requestJson<DeliveryDetail>('GET', `/api/ntf/deliveries/${id(deliveryId)}`),
  resend: (deliveryId: string) => requestJson<Delivery>('POST', `/api/ntf/deliveries/${id(deliveryId)}/resend`, {}),
  deliverySummary: () => requestJson<DeliverySummary>('GET', '/api/ntf/deliveries/summary'),
  deliveryDaily: (days = 14) => requestJson<DeliveryDay[]>('GET', `/api/ntf/deliveries/daily${query({ days })}`),
  licence: () => requestJson<Licence>('GET', '/api/cfg/licence'),
  usage: (from: string, to: string) => requestJson<UsageStatement[]>('GET', `/api/cfg/licence/usage${query({ from, to })}`),
};
