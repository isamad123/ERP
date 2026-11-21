// Hand-written clients of the shell's server data (slice S1.12b): who is signed in (contract "Session context API", S1.12
// ruling Q9), what the customer's licence allows (contract "Licence API"), the notification centre (contract
// "Notification API") and the inbox counts of the home page (contract "Workflow API").
import type { Digits, Grouping } from '@erp/i18n';
import { requestJson } from './client';

export interface PermissionGrant {
  code: string;
  /** Null: every company. */
  companies: string[] | null;
}

export interface SessionCompany {
  id: string;
  code: string;
  name: Record<string, string>;
}

export interface Me {
  userId: string;
  displayName: string;
  language: string;
  timeZone: string;
  defaultCompanyId: string | null;
  companies: SessionCompany[];
  permissions: PermissionGrant[];
  mfa: { required: boolean; methods: string[]; enrolBy: string | null };
  numbers: { digits: Digits; grouping: Grouping };
}

export type LicenceState = 'active' | 'warning' | 'grace' | 'read_only' | 'locked';

export interface LimitUse {
  key: string;
  max: number | null;
  used: number | null;
  percent: number | null;
}

export interface Capabilities {
  modules: string[];
  features: string[];
  flags: string[];
  state: LicenceState;
  nextState: LicenceState | null;
  nextStateOn: string | null;
  warnings: LimitUse[];
}

export interface NotificationItem {
  id: string;
  eventCode: string;
  eventGroup: string | null;
  title: string;
  body: string;
  link: string | null;
  createdAt: string;
  readAt: string | null;
}

export interface NotificationPage {
  items: NotificationItem[];
  total: number;
  unread: number;
}

export interface NotificationQuery {
  unread?: boolean;
  search?: string;
  group?: string;
  limit: number;
  offset?: number;
}

export interface InboxCounts {
  total: number;
  overdue: number;
  changeApprovals: number;
}

function query(values: Record<string, string | number | boolean | undefined | null>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

export const kernel = {
  me: (companyId?: string | null) => requestJson<Me>('GET', `/api/kernel/me${query({ companyId })}`),
  capabilities: () => requestJson<Capabilities>('GET', '/api/kernel/capabilities'),
  inboxCounts: () => requestJson<InboxCounts>('GET', '/api/wf/inbox/counts'),
};

export const notifications = {
  list: (q: NotificationQuery) => requestJson<NotificationPage>('GET', `/api/ntf/centre/items${query({ ...q, unread: q.unread ? true : undefined })}`),
  unreadCount: () => requestJson<{ unread: number }>('GET', '/api/ntf/centre/unread-count'),
  markRead: (id: string) => requestJson<undefined>('POST', `/api/ntf/centre/items/${encodeURIComponent(id)}/read`),
  markUnread: (id: string) => requestJson<undefined>('POST', `/api/ntf/centre/items/${encodeURIComponent(id)}/unread`),
  readAll: () => requestJson<{ marked: number }>('POST', '/api/ntf/centre/items/read-all'),
};
