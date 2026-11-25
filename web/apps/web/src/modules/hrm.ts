// Screens of the HR web module, declared now so the menu follows the licence and the person's permissions (product owner
// ruling 2026-11-21: People, Time and Payroll show now and open "not available yet" until their slices build them).
// Features are D-24 keys of the licence; permissions are the read permission of each area's main table.
import { lazy } from 'react';
import { Activity, AlertTriangle, Briefcase, BarChart3, ClipboardEdit, CalendarDays, CalendarRange, Clock, FileSearch, GitFork, Grid3x3, LayoutGrid, MonitorSmartphone, QrCode, Router, ScrollText, Sigma, SunMoon, Timer, Users, UsersRound, Wallet } from 'lucide-react';
import type { ScreenDeclaration } from '../navigation/screens';
import { registerApprovalCard } from './kernel/approval-cards';

// S1.23a: the policy framework's screens (policies with versions and assignments; "Which policy applies?"), loaded with their routes.
const PoliciesScreen = lazy(() => import('./hr/policies'));
const ResolutionScreen = lazy(() => import('./hr/policy-resolution'));

// S1.27a: attendance capture screens, loaded with their routes.
const PunchScreen = lazy(() => import('./hrm/attendance-punch'));
const KioskScreen = lazy(() => import('./hrm/attendance-kiosk'));
const QrScreen = lazy(() => import('./hrm/attendance-qr'));
// S1.27b: devices, device users, unmapped punches and the punch log, loaded with its route.
const DevicesScreen = lazy(() => import('./hrm/devices'));
// S1.28b: shifts (engine), the roster grid and the optional holidays with the shift of a day, loaded with their routes.
const ShiftsScreen = lazy(() => import('./hrm/shifts'));
const RostersScreen = lazy(() => import('./hrm/rosters'));
const OptionalHolidaysScreen = lazy(() => import('./hrm/optional-holidays'));
// S1.30a: attendance requests (corrections and duty) and the exception queue, loaded with their routes.
const RequestsScreen = lazy(() => import('./hrm/requests'));
const ExceptionsScreen = lazy(() => import('./hrm/exceptions'));
// S1.30b: the live attendance dashboard, the attendance reports and the manager's team view, loaded with their routes.
const DashboardScreen = lazy(() => import('./hrm/dashboard'));
const ReportsScreen = lazy(() => import('./hrm/reports'));
const TeamScreen = lazy(() => import('./hrm/team'));
// S1.33a: leave (my leave, apply, request page) and the team leave calendar, loaded with their routes.
const LeaveScreen = lazy(() => import('./hrm/leave'));
const LeaveTeamScreen = lazy(() => import('./hrm/leave-team'));
// S1.33a: the approvals inbox shows this card under the fields of a leave request or a leave cancellation (lazy, so the shell does not grow).
const LeaveApprovalCard = lazy(() => import('./hrm/leave-approval-card'));
registerApprovalCard('hrm.leave_request', LeaveApprovalCard);
registerApprovalCard('hrm.leave_cancellation', LeaveApprovalCard);
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
  // S1.30a (FR-HRM-ATT-11, ATT-14; slice S1.30 Build item 4): the more specific routes win over the attendance placeholder above, which S1.30b replaces.
  { id: 'attendance-exceptions', path: '/time/exceptions', labelKey: 'nav.exceptions', icon: AlertTriangle, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_exceptions.read', component: ExceptionsScreen },
  // S1.30b (FR-HRM-ATT-18, ATT-19, SSP-02, RPT-05): live attendance, reports and the team view.
  { id: 'attendance-dashboard', path: '/time/dashboard', labelKey: 'nav.liveAttendance', icon: Activity, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_dashboard.read', component: DashboardScreen },
  { id: 'attendance-team', path: '/time/team', labelKey: 'nav.teamAttendance', icon: UsersRound, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_dashboard.read', component: TeamScreen },
  { id: 'attendance-reports', path: '/time/reports', labelKey: 'nav.attendanceReports', icon: BarChart3, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_reports.read', component: ReportsScreen },
  { id: 'attendance-requests', path: '/time/requests/*', labelKey: 'nav.attendanceRequests', icon: ClipboardEdit, group: 'time', feature: 'hrm.core', permission: 'hrm.regularization_requests.read', component: RequestsScreen },
  { id: 'punch', path: '/time/punch', labelKey: 'nav.punch', icon: Clock, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.punch', component: PunchScreen },
  { id: 'kiosk', path: '/time/kiosk', labelKey: 'nav.kiosk', icon: MonitorSmartphone, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.kiosk', component: KioskScreen },
  { id: 'qr', path: '/time/qr', labelKey: 'nav.qr', icon: QrCode, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.qr_display', component: QrScreen },
  { id: 'devices', path: '/time/devices', labelKey: 'nav.devices', icon: Router, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_devices.read', component: DevicesScreen },
  { id: 'shifts', path: '/time/shifts/*', labelKey: 'nav.shifts', icon: Timer, group: 'time', feature: 'hrm.core', permission: 'hrm.shifts.read', component: ShiftsScreen, tables: ['hrm.shifts'] },
  { id: 'rosters', path: '/time/rosters/*', labelKey: 'nav.rosters', icon: LayoutGrid, group: 'time', feature: 'hrm.core', permission: 'hrm.rosters.read', component: RostersScreen, tables: ['hrm.rosters'] },
  { id: 'optional-holidays', path: '/time/optional-holidays', labelKey: 'nav.optionalHolidays', icon: SunMoon, group: 'time', feature: 'hrm.core', permission: 'hrm.holidays.read', component: OptionalHolidaysScreen },
  { id: 'leave', path: '/time/leave/*', labelKey: 'nav.leave', icon: CalendarDays, group: 'time', feature: 'hrm.core', permission: 'hrm.leave_requests.read', component: LeaveScreen },
  { id: 'leave-team', path: '/time/leave-team', labelKey: 'nav.leaveTeam', icon: UsersRound, group: 'time', feature: 'hrm.core', permission: 'hrm.leave_requests.read', component: LeaveTeamScreen },
  // S1.23a (FR-HRM-POL-02, -03; ruling Q1): HR administrators manage policies; both entries need the permission to read policies.
  { id: 'policies', path: '/hr/policies/*', labelKey: 'nav.policies', icon: ScrollText, group: 'people', feature: 'hrm.core', permission: 'hrm.policies.read', component: PoliciesScreen },
  { id: 'policy-resolution', path: '/hr/policy-resolution', labelKey: 'nav.policyResolution', icon: FileSearch, group: 'people', feature: 'hrm.core', permission: 'hrm.policies.read', component: ResolutionScreen },
  // S1.34a (FR-HRM-PAY-01, FR-HRM-PAY-38): the more specific routes win over the placeholder below, which S1.35 replaces.
  { id: 'pay-groups', path: '/payroll/pay-groups/*', labelKey: 'nav.payGroups', icon: UsersRound, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_groups.read', component: PayGroupsScreen, tables: ['hrm.pay_groups'] },
  { id: 'pay-calendar', path: '/payroll/calendar', labelKey: 'nav.payCalendar', icon: CalendarRange, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_periods.read', component: PayCalendarScreen },
  { id: 'payroll', path: '/payroll/*', labelKey: 'nav.payroll', icon: Wallet, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_runs.read', comesWith: 'S1.35' },
];
