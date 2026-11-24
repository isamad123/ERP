// Screens of the HR web module, declared now so the menu follows the licence and the person's permissions (product owner
// ruling 2026-11-21: People, Time and Payroll show now and open "not available yet" until their slices build them).
// Features are D-24 keys of the licence; permissions are the read permission of each area's main table.
import { lazy } from 'react';
import { Briefcase, CalendarDays, CalendarRange, Clock, FileSearch, GitFork, Grid3x3, MonitorSmartphone, QrCode, ScrollText, Sigma, Users, UsersRound, Wallet } from 'lucide-react';
import type { ScreenDeclaration } from '../navigation/screens';

// S1.23a: the policy framework's screens (policies with versions and assignments; "Which policy applies?"), loaded with their routes.
const PoliciesScreen = lazy(() => import('./hr/policies'));
const ResolutionScreen = lazy(() => import('./hr/policy-resolution'));

// S1.27a: attendance capture screens, loaded with their routes.
const PunchScreen = lazy(() => import('./hrm/attendance-punch'));
const KioskScreen = lazy(() => import('./hrm/attendance-kiosk'));
const QrScreen = lazy(() => import('./hrm/attendance-qr'));
// S1.34a: the pay groups (made by the engine) and the payroll calendar with its readiness list, loaded with their routes.
const PayGroupsScreen = lazy(() => import('./payroll/pay-groups'));
const PayCalendarScreen = lazy(() => import('./payroll/calendar'));

// S1.22b: grades, positions, headcount budgets, the strength report and the chart by position, loaded with their routes.
const GradesScreen = lazy(() => import('./hr/positions').then((m) => ({ default: m.GradesScreen })));
const BudgetsScreen = lazy(() => import('./hr/positions').then((m) => ({ default: m.BudgetsScreen })));
const PositionsScreen = lazy(() => import('./hr/positions'));
const StrengthScreen = lazy(() => import('./hr/strength'));
const PositionChartScreen = lazy(() => import('./hr/position-chart'));

export const hrmScreens: ScreenDeclaration[] = [
  { id: 'people', path: '/people/*', labelKey: 'nav.people', icon: Users, group: 'people', feature: 'hrm.core', permission: 'hrm.persons.read', comesWith: 'S1.21' },
  // S1.22b (FR-HRM-POS-01 to -04, -06; ruling Q1): the People group; each entry needs the permission to read its main table.
  { id: 'positions', path: '/hr/positions/*', labelKey: 'nav.positions', icon: Briefcase, group: 'people', feature: 'hrm.core', permission: 'hrm.positions.read', component: PositionsScreen, tables: ['hrm.positions'] },
  { id: 'headcount', path: '/hr/headcount/*', labelKey: 'nav.headcount', icon: Grid3x3, group: 'people', feature: 'hrm.core', permission: 'hrm.headcount_budgets.read', component: BudgetsScreen, tables: ['hrm.headcount_budgets'] },
  { id: 'grades', path: '/hr/grades/*', labelKey: 'nav.grades', icon: Sigma, group: 'people', feature: 'hrm.core', permission: 'hrm.grades.read', component: GradesScreen, tables: ['hrm.grades'] },
  { id: 'strength-report', path: '/hr/strength-report', labelKey: 'nav.strengthReport', icon: Users, group: 'people', feature: 'hrm.core', permission: 'hrm.positions.read', component: StrengthScreen },
  { id: 'position-chart', path: '/hr/position-chart', labelKey: 'nav.positionChart', icon: GitFork, group: 'people', feature: 'hrm.core', permission: 'hrm.positions.read', component: PositionChartScreen },
  { id: 'attendance', path: '/time/attendance/*', labelKey: 'nav.attendance', icon: Clock, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_days.read', comesWith: 'S1.29' },
  { id: 'punch', path: '/time/punch', labelKey: 'nav.punch', icon: Clock, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.punch', component: PunchScreen },
  { id: 'kiosk', path: '/time/kiosk', labelKey: 'nav.kiosk', icon: MonitorSmartphone, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.kiosk', component: KioskScreen },
  { id: 'qr', path: '/time/qr', labelKey: 'nav.qr', icon: QrCode, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.qr_display', component: QrScreen },
  { id: 'leave', path: '/time/leave/*', labelKey: 'nav.leave', icon: CalendarDays, group: 'time', feature: 'hrm.core', permission: 'hrm.leave_requests.read', comesWith: 'S1.33' },
  // S1.23a (FR-HRM-POL-02, -03; ruling Q1): HR administrators manage policies; both entries need the permission to read policies.
  { id: 'policies', path: '/hr/policies/*', labelKey: 'nav.policies', icon: ScrollText, group: 'people', feature: 'hrm.core', permission: 'hrm.policies.read', component: PoliciesScreen },
  { id: 'policy-resolution', path: '/hr/policy-resolution', labelKey: 'nav.policyResolution', icon: FileSearch, group: 'people', feature: 'hrm.core', permission: 'hrm.policies.read', component: ResolutionScreen },
  // S1.34a (FR-HRM-PAY-01, FR-HRM-PAY-38): the more specific routes win over the placeholder below, which S1.35 replaces.
  { id: 'pay-groups', path: '/payroll/pay-groups/*', labelKey: 'nav.payGroups', icon: UsersRound, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_groups.read', component: PayGroupsScreen, tables: ['hrm.pay_groups'] },
  { id: 'pay-calendar', path: '/payroll/calendar', labelKey: 'nav.payCalendar', icon: CalendarRange, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_periods.read', component: PayCalendarScreen },
  { id: 'payroll', path: '/payroll/*', labelKey: 'nav.payroll', icon: Wallet, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_runs.read', comesWith: 'S1.35' },
];
