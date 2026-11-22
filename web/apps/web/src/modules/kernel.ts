// Screens of the kernel's web module (slice S1.12b). The route table also fixes the deep links of the notification
// catalogue (S1.11b1: /settings/licence, /admin/jobs/runs, /admin/jobs/health, /admin/audit, /admin/documents/quarantine,
// /documents/{id}); screens of later slices open "not available yet" until their slice adds the component.
import { lazy } from 'react';
import { Activity, BarChart3, Bell, Building2, CalendarDays, ClipboardCheck, Download, EyeOff, FileText, Filter, House, KeyRound, Landmark, MapPin, ScrollText, ShieldAlert, ShieldCheck, UserCog, Users, UsersRound } from 'lucide-react';
import type { ScreenDeclaration } from '../navigation/screens';
import { HomePage } from '../pages/home';

const NotificationsPage = lazy(() => import('../pages/notifications'));
// S1.12c: the first list and form screen (the engine is loaded with it, not with the shell).
const BranchesScreen = lazy(() => import('./kernel/branches'));
// S1.12d: the approval inbox (workflow tasks and approvals of changes), loaded with its route.
const ApprovalsScreen = lazy(() => import('./kernel/approvals'));
// S1.14a: companies, locations and holiday calendars (the units and the org chart follow in S1.14b).
const CompaniesScreen = lazy(() => import('./organization/companies'));
const LocationsScreen = lazy(() => import('./organization/locations'));
const CalendarsScreen = lazy(() => import('./organization/calendars'));
// S1.13a: the administrator's user console.
const UsersScreen = lazy(() => import('./admin/users'));
// S1.13b: roles, groups, data scopes, field security, delegations, access reports and audit search (ruling Q1).
const RolesScreen = lazy(() => import('./admin/roles'));
const GroupsScreen = lazy(() => import('./admin/groups'));
const ScopesScreen = lazy(() => import('./admin/scopes'));
const FieldsScreen = lazy(() => import('./admin/fields'));
const DelegationsScreen = lazy(() => import('./admin/delegations'));
const ReportsScreen = lazy(() => import('./admin/reports'));
const AuditScreen = lazy(() => import('./admin/audit'));

export const kernelScreens: ScreenDeclaration[] = [
  { id: 'home', path: '/', labelKey: 'nav.home', icon: House, group: 'home', allowedWhenLocked: false, component: HomePage },
  { id: 'notifications', path: '/notifications', labelKey: 'nav.notifications', icon: Bell, group: 'home', permission: 'ntf.in_app_items.read', component: NotificationsPage },
  { id: 'approvals', path: '/approvals/*', labelKey: 'nav.approvals', icon: ClipboardCheck, group: 'approvals', permission: 'wf.workflow_tasks.read', component: ApprovalsScreen },
  // S1.14a (ruling Q1: menu group Administration; each entry needs the permission to read what it shows).
  { id: 'companies', path: '/organization/companies/*', labelKey: 'nav.companies', icon: Landmark, group: 'administration', permission: 'org.companies.read', component: CompaniesScreen, tables: ['org.companies'] },
  // S1.12c (FR-KRN-ORG-02): branches; `tables` lists what the screen shows, for the Urdu label check (Build item 15).
  { id: 'branches', path: '/organization/branches/*', labelKey: 'nav.branches', icon: Building2, group: 'administration', permission: 'org.branches.read', component: BranchesScreen, tables: ['org.branches'] },
  { id: 'locations', path: '/organization/locations/*', labelKey: 'nav.locations', icon: MapPin, group: 'administration', permission: 'org.locations.read', component: LocationsScreen, tables: ['org.locations'] },
  { id: 'holiday-calendars', path: '/organization/holiday-calendars/*', labelKey: 'nav.holidayCalendars', icon: CalendarDays, group: 'administration', permission: 'ref.calendars.read', component: CalendarsScreen, tables: ['ref.calendars', 'ref.holidays'] },
  // S1.13a (FR-ADM-USR-01): the user console; the menu entry needs the permission to read users (ruling Q1).
  { id: 'users', path: '/admin/users/*', labelKey: 'nav.users', icon: Users, group: 'administration', permission: 'iam.users.read', component: UsersScreen },
  // S1.13b (FR-ADM-ROL-01 to -11, FR-ADM-USR-06, FR-KRN-AUD-04): each entry needs its own permission.
  { id: 'roles', path: '/admin/roles/*', labelKey: 'nav.roles', icon: ShieldCheck, group: 'administration', permission: 'iam.roles.read', component: RolesScreen },
  { id: 'groups', path: '/admin/groups/*', labelKey: 'nav.groups', icon: UsersRound, group: 'administration', permission: 'iam.user_groups.read', component: GroupsScreen },
  { id: 'data-scopes', path: '/admin/data-scopes/*', labelKey: 'nav.dataScopes', icon: Filter, group: 'administration', permission: 'iam.data_scopes.read', component: ScopesScreen },
  { id: 'field-security', path: '/admin/field-security/*', labelKey: 'nav.fieldSecurity', icon: EyeOff, group: 'administration', permission: 'iam.roles.read', component: FieldsScreen },
  { id: 'delegations', path: '/admin/delegations/*', labelKey: 'nav.delegations', icon: UserCog, group: 'administration', permission: 'iam.users.read', component: DelegationsScreen },
  { id: 'access-reports', path: '/admin/access-reports/*', labelKey: 'nav.accessReports', icon: BarChart3, group: 'administration', permission: 'iam.roles.read', component: ReportsScreen },
  { id: 'licence', path: '/settings/licence', labelKey: 'nav.licence', icon: KeyRound, group: 'administration', permission: 'cfg.licence.read', allowedWhenLocked: true, comesWith: 'S1.16' },
  { id: 'jobs', path: '/admin/jobs/*', labelKey: 'nav.jobs', icon: Activity, group: 'administration', permission: 'job.runs.read', comesWith: 'S1.16' },
  { id: 'audit', path: '/admin/audit/*', labelKey: 'nav.audit', icon: ScrollText, group: 'administration', permission: 'aud.changes.view', component: AuditScreen },
  { id: 'exports', path: '/admin/exports', labelKey: 'nav.exports', icon: Download, permission: 'aud.export', allowedWhenLocked: true, comesWith: 'S1.16' },
  { id: 'quarantine', path: '/admin/documents/quarantine', labelKey: 'nav.quarantine', icon: ShieldAlert, comesWith: 'S1.16' },
  // A file follows its record (S1.11c1 ruling Q23): the server decides when the screen asks for it.
  { id: 'document', path: '/documents/:id', labelKey: 'nav.document', icon: FileText, comesWith: 'S1.24' },
];
