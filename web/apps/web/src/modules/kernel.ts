// Screens of the kernel's web module (slice S1.12b). The route table also fixes the deep links of the notification
// catalogue (S1.11b1: /settings/licence, /admin/jobs/runs, /admin/jobs/health, /admin/audit, /admin/documents/quarantine,
// /documents/{id}); screens of later slices open "not available yet" until their slice adds the component.
import { lazy } from 'react';
import { Activity, BarChart3, Bell, Building2, CalendarDays, ClipboardCheck, Download, EyeOff, FileText, Filter, Flag, House, ImagePlus, KeyRound, Landmark, ListChecks, MapPin, Network, Package, ScrollText, Settings, ShieldAlert, ShieldCheck, Upload, UserCog, Users, UsersRound, Workflow } from 'lucide-react';
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
// S1.14b: organization units, unit types, hierarchies and the org chart.
const UnitsScreen = lazy(() => import('./organization/units'));
const OrgChartScreen = lazy(() => import('./organization/org-chart'));
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
// S1.15a: the settings centre, the feature flags and the sign-in page (login branding); numbering, routes and templates follow in S1.15c and d.
const SettingsScreen = lazy(() => import('./admin/settings'));
const FlagsScreen = lazy(() => import('./admin/flags'));
const BrandingScreen = lazy(() => import('./admin/branding'));
// S1.15b: labels, option lists and custom field definitions.
const LabelsScreen = lazy(() => import('./admin/labels'));
const OptionListsScreen = lazy(() => import('./admin/option-lists'));
const CustomFieldsScreen = lazy(() => import('./admin/custom-fields'));
// S1.15c: numbering schemes and the approval route editor.
// S1.16a: the import centre and the template designer.
const ImportsScreen = lazy(() => import('./admin/imports'));
const NumberingScreen = lazy(() => import('./admin/numbering'));
const RoutesScreen = lazy(() => import('./admin/routes'));
// S1.15d: notification texts and rules, document templates.
const NotifyScreen = lazy(() => import('./admin/notify'));
const DocumentTemplatesScreen = lazy(() => import('./admin/doctemplates'));
// S1.18b: the first-run set-up guide and the readiness checklist.
const SetupScreen = lazy(() => import('./admin/setup'));
// S1.23b: the pack manager (country and industry packs; the wizard's pack step links here).
const PacksScreen = lazy(() => import('./admin/packs'));
// S1.16c: the job centre (jobs, runs, notification log and system health as its tabs, R-291: the shell budget has no room for two more menu entries) and the licence page.
const JobsScreen = lazy(() => import('./admin/jobs'));
const LicenceScreen = lazy(() => import('./admin/licence'));

export const kernelScreens: ScreenDeclaration[] = [
  { id: 'home', path: '/', labelKey: 'nav.home', icon: House, group: 'home', allowedWhenLocked: false, component: HomePage },
  // S1.18b (ruling Q1): the set-up guide is reached from the home page card and the menu; ticking and go-live are checked again by the server.
  { id: 'setup', path: '/setup', labelKey: 'nav.setup', icon: ListChecks, group: 'home', permission: 'cfg.setup_tasks.read', component: SetupScreen },
  { id: 'notifications', path: '/notifications', labelKey: 'nav.notifications', icon: Bell, group: 'home', permission: 'ntf.in_app_items.read', component: NotificationsPage },
  { id: 'approvals', path: '/approvals/*', labelKey: 'nav.approvals', icon: ClipboardCheck, group: 'approvals', permission: 'wf.workflow_tasks.read', component: ApprovalsScreen },
  // S1.14a (ruling Q1: menu group Administration; each entry needs the permission to read what it shows).
  { id: 'companies', path: '/organization/companies/*', labelKey: 'nav.companies', icon: Landmark, group: 'administration', permission: 'org.companies.read', component: CompaniesScreen, tables: ['org.companies'] },
  // S1.12c (FR-KRN-ORG-02): branches; `tables` lists what the screen shows, for the Urdu label check (Build item 15).
  { id: 'branches', path: '/organization/branches/*', labelKey: 'nav.branches', icon: Building2, group: 'administration', permission: 'org.branches.read', component: BranchesScreen, tables: ['org.branches'] },
  { id: 'locations', path: '/organization/locations/*', labelKey: 'nav.locations', icon: MapPin, group: 'administration', permission: 'org.locations.read', component: LocationsScreen, tables: ['org.locations'] },
  // S1.14b (ruling Q1: between Locations and Holiday calendars).
  { id: 'org-units', path: '/organization/org-units/*', labelKey: 'nav.organizationUnits', icon: Network, group: 'administration', permission: 'org.org_units.read', component: UnitsScreen, tables: ['org.org_units', 'org.org_unit_types', 'org.org_hierarchies'] },
  { id: 'org-chart', path: '/organization/org-chart', labelKey: 'nav.orgChart', icon: Workflow, group: 'administration', permission: 'org.org_unit_links.read', component: OrgChartScreen },
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
  // S1.15a (ruling Q1: "Settings", "Feature flags" and "Sign-in page" in the Administration group; each needs the permission to read what it shows).
  { id: 'settings', path: '/settings', labelKey: 'nav.settings', icon: Settings, group: 'administration', permission: 'cfg.setting_values.read', component: SettingsScreen },
  { id: 'feature-flags', path: '/settings/flags', labelKey: 'nav.featureFlags', icon: Flag, group: 'administration', permission: 'cfg.feature_flag_overrides.read', component: FlagsScreen },
  { id: 'sign-in-page', path: '/settings/sign-in-page', labelKey: 'nav.signInPage', icon: ImagePlus, group: 'administration', permission: 'cfg.setting_values.read', component: BrandingScreen },
  // S1.15b (ruling Q1): "Labels", "Option lists" and "Custom fields" after "Sign-in page". The labels editor is also reached with ?table= from the
  // screens that name a table (R-210: the unit types screen).
  { id: 'labels', path: '/settings/labels', labelKey: 'nav.labels', icon: FileText, group: 'administration', permission: 'cfg.labels.read', component: LabelsScreen },
  { id: 'option-lists', path: '/settings/option-lists', labelKey: 'nav.optionLists', icon: ClipboardCheck, group: 'administration', permission: 'cfg.option_lists.read', component: OptionListsScreen },
  { id: 'custom-fields', path: '/settings/custom-fields', labelKey: 'nav.customFields', icon: Filter, group: 'administration', permission: 'cfg.custom_field_defs.read', component: CustomFieldsScreen },
  // S1.15c (ruling Q1): "Numbering" and "Approval routes" after "Custom fields".
  { id: 'numbering', path: '/settings/numbering', labelKey: 'nav.numbering', icon: ClipboardCheck, group: 'administration', permission: 'cfg.numbering_schemes.read', component: NumberingScreen },
  { id: 'approval-routes', path: '/settings/approval-routes', labelKey: 'nav.approvalRoutes', icon: Workflow, group: 'administration', permission: 'wf.workflow_definitions.read', component: RoutesScreen },
  // S1.16a (ruling Q1): "Imports" in the Administration group; the template designer is a route of it for people who may design templates.
  { id: 'imports', path: '/admin/imports/*', labelKey: 'nav.imports', icon: Upload, group: 'administration', permission: 'itg.imports.manage', component: ImportsScreen },
  { id: 'packs', path: '/settings/packs', labelKey: 'nav.packs', icon: Package, group: 'administration', permission: 'cfg.installed_packs.read', component: PacksScreen },
  // S1.15d (ruling Q1): "Notification texts" and "Document templates" after "Approval routes".
  { id: 'notification-texts', path: '/settings/notification-texts', labelKey: 'nav.notificationTexts', icon: Bell, group: 'administration', permission: 'ntf.templates.read', component: NotifyScreen },
  { id: 'document-templates', path: '/settings/document-templates', labelKey: 'nav.documentTemplates', icon: FileText, group: 'administration', permission: 'doc.templates.read', component: DocumentTemplatesScreen },
  // S1.16c: "Licence" and "Background jobs" in the Administration group. Under the jobs route: runs, messages (the notification log) and health (the path the
  // notification catalogue links to, /admin/jobs/health). The licence page answers in the locked state.
  { id: 'licence', path: '/settings/licence', labelKey: 'nav.licence', icon: KeyRound, group: 'administration', permission: 'cfg.licence.read', allowedWhenLocked: true, component: LicenceScreen },
  { id: 'jobs', path: '/admin/jobs/*', labelKey: 'nav.jobs', icon: Activity, group: 'administration', permission: 'job.runs.read', component: JobsScreen },
  { id: 'audit', path: '/admin/audit/*', labelKey: 'nav.audit', icon: ScrollText, group: 'administration', permission: 'aud.changes.view', component: AuditScreen },
  { id: 'exports', path: '/admin/exports', labelKey: 'nav.exports', icon: Download, permission: 'aud.export', allowedWhenLocked: true, comesWith: 'S1.16' },
  { id: 'quarantine', path: '/admin/documents/quarantine', labelKey: 'nav.quarantine', icon: ShieldAlert, comesWith: 'S1.16' },
  // A file follows its record (S1.11c1 ruling Q23): the server decides when the screen asks for it.
  { id: 'document', path: '/documents/:id', labelKey: 'nav.document', icon: FileText, comesWith: 'S1.24' },
];
