// Hand-written client of the first-run wizard and readiness checklist (slice S1.18b; contract "Readiness API", S1.18a). The server decides every
// status; the screens show its answers as confirmed. Every change goes through these endpoints, so the audit log records it as the person's own.
import { requestJson } from './client';

const enc = encodeURIComponent;

export type SetupStatus = 'pending' | 'done' | 'skipped';

export interface SetupTask {
  moduleId: string;
  taskKey: string;
  kind: 'step' | 'item';
  titleKey: string;
  required: boolean;
  level: 'customer' | 'company';
  link: string | null;
  status: SetupStatus;
  manual: boolean;
  messageKey: string | null;
  messageArguments: Record<string, string> | null;
  note: string | null;
  completedBy: string | null;
  completedAt: string | null;
  order: number;
}

export interface ModuleReadiness {
  moduleId: string;
  nameKey: string;
  requiredTotal: number;
  requiredDone: number;
  optionalTotal: number;
  optionalDone: number;
  isLive: boolean;
  liveAt: string | null;
  liveNote: string | null;
  goLiveActions: string[];
  items: SetupTask[];
}

export interface ReadinessOverview { companyId: string | null; steps: SetupTask[]; modules: ModuleReadiness[] }
export interface GoLiveResult { moduleId: string; companyId: string; liveAt: string; overridden: boolean; openRequired: number }

const task = (module: string, key: string, action: 'complete' | 'skip' | 'reopen') => `/api/cfg/readiness/${enc(module)}/tasks/${enc(key)}/${action}`;

export const setupApi = {
  overview: (companyId: string | null) => requestJson<ReadinessOverview>('GET', `/api/cfg/readiness${companyId ? `?companyId=${enc(companyId)}` : ''}`),
  complete: (t: SetupTask, companyId: string | null) => requestJson<SetupTask>('POST', task(t.moduleId, t.taskKey, 'complete'), { body: { companyId: t.level === 'company' ? companyId : null } }),
  skip: (t: SetupTask, companyId: string | null, note: string) => requestJson<SetupTask>('POST', task(t.moduleId, t.taskKey, 'skip'), { body: { companyId: t.level === 'company' ? companyId : null, note: note.trim() || null } }),
  reopen: (t: SetupTask, companyId: string | null) => requestJson<SetupTask>('POST', task(t.moduleId, t.taskKey, 'reopen'), { body: { companyId: t.level === 'company' ? companyId : null } }),
  goLive: (moduleId: string, companyId: string, overrideReason: string) =>
    requestJson<GoLiveResult>('POST', `/api/cfg/readiness/${enc(moduleId)}/go-live`, { body: { companyId, overrideReason: overrideReason.trim() || null } }),
};

/** The wizard is "finished" when no step is waiting: every step is done or skipped. */
export const stepsOpen = (steps: SetupTask[]) => steps.filter((s) => s.status === 'pending').length;

/** Nothing has been touched yet: the first sign-in of a new customer's administrator (ruling Q1). */
export const untouched = (steps: SetupTask[]) => steps.length > 0 && steps.every((s) => s.status === 'pending');
