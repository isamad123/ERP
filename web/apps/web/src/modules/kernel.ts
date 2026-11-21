// Screens of the kernel's web module (slice S1.12b). The route table also fixes the deep links of the notification
// catalogue (S1.11b1: /settings/licence, /admin/jobs/runs, /admin/jobs/health, /admin/audit, /admin/documents/quarantine,
// /documents/{id}); screens of later slices open "not available yet" until their slice adds the component.
import { lazy } from 'react';
import { Activity, Bell, ClipboardCheck, Download, FileText, House, KeyRound, ScrollText, ShieldAlert } from 'lucide-react';
import type { ScreenDeclaration } from '../navigation/screens';
import { HomePage } from '../pages/home';

const NotificationsPage = lazy(() => import('../pages/notifications'));

export const kernelScreens: ScreenDeclaration[] = [
  { id: 'home', path: '/', labelKey: 'nav.home', icon: House, group: 'home', allowedWhenLocked: false, component: HomePage },
  { id: 'notifications', path: '/notifications', labelKey: 'nav.notifications', icon: Bell, group: 'home', permission: 'ntf.in_app_items.read', component: NotificationsPage },
  { id: 'approvals', path: '/approvals/*', labelKey: 'nav.approvals', icon: ClipboardCheck, group: 'approvals', permission: 'wf.workflow_tasks.read', comesWith: 'S1.12d' },
  { id: 'licence', path: '/settings/licence', labelKey: 'nav.licence', icon: KeyRound, group: 'administration', permission: 'cfg.licence.read', allowedWhenLocked: true, comesWith: 'S1.16' },
  { id: 'jobs', path: '/admin/jobs/*', labelKey: 'nav.jobs', icon: Activity, group: 'administration', permission: 'job.runs.read', comesWith: 'S1.16' },
  { id: 'audit', path: '/admin/audit/*', labelKey: 'nav.audit', icon: ScrollText, group: 'administration', permission: 'aud.changes.view', comesWith: 'S1.16' },
  { id: 'exports', path: '/admin/exports', labelKey: 'nav.exports', icon: Download, permission: 'aud.export', allowedWhenLocked: true, comesWith: 'S1.16' },
  { id: 'quarantine', path: '/admin/documents/quarantine', labelKey: 'nav.quarantine', icon: ShieldAlert, comesWith: 'S1.16' },
  // A file follows its record (S1.11c1 ruling Q23): the server decides when the screen asks for it.
  { id: 'document', path: '/documents/:id', labelKey: 'nav.document', icon: FileText, comesWith: 'S1.24' },
];
