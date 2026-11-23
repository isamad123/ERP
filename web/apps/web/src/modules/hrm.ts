// Screens of the HR web module, declared now so the menu follows the licence and the person's permissions (product owner
// ruling 2026-11-21: People, Time and Payroll show now and open "not available yet" until their slices build them).
// Features are D-24 keys of the licence; permissions are the read permission of each area's main table.
import { lazy } from 'react';
import { CalendarDays, CalendarRange, Clock, MonitorSmartphone, QrCode, Users, UsersRound, Wallet } from 'lucide-react';
import type { ScreenDeclaration } from '../navigation/screens';

// S1.27a: attendance capture screens, loaded with their routes.
const PunchScreen = lazy(() => import('./hrm/attendance-punch'));
const KioskScreen = lazy(() => import('./hrm/attendance-kiosk'));
const QrScreen = lazy(() => import('./hrm/attendance-qr'));
// S1.34a: the pay groups (made by the engine) and the payroll calendar with its readiness list, loaded with their routes.
const PayGroupsScreen = lazy(() => import('./payroll/pay-groups'));
const PayCalendarScreen = lazy(() => import('./payroll/calendar'));

export const hrmScreens: ScreenDeclaration[] = [
  { id: 'people', path: '/people/*', labelKey: 'nav.people', icon: Users, group: 'people', feature: 'hrm.core', permission: 'hrm.persons.read', comesWith: 'S1.21' },
  { id: 'attendance', path: '/time/attendance/*', labelKey: 'nav.attendance', icon: Clock, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_days.read', comesWith: 'S1.29' },
  { id: 'punch', path: '/time/punch', labelKey: 'nav.punch', icon: Clock, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.punch', component: PunchScreen },
  { id: 'kiosk', path: '/time/kiosk', labelKey: 'nav.kiosk', icon: MonitorSmartphone, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.kiosk', component: KioskScreen },
  { id: 'qr', path: '/time/qr', labelKey: 'nav.qr', icon: QrCode, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance.qr_display', component: QrScreen },
  { id: 'leave', path: '/time/leave/*', labelKey: 'nav.leave', icon: CalendarDays, group: 'time', feature: 'hrm.core', permission: 'hrm.leave_requests.read', comesWith: 'S1.33' },
  // S1.34a (FR-HRM-PAY-01, FR-HRM-PAY-38): the more specific routes win over the placeholder below, which S1.35 replaces.
  { id: 'pay-groups', path: '/payroll/pay-groups/*', labelKey: 'nav.payGroups', icon: UsersRound, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_groups.read', component: PayGroupsScreen, tables: ['hrm.pay_groups'] },
  { id: 'pay-calendar', path: '/payroll/calendar', labelKey: 'nav.payCalendar', icon: CalendarRange, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_periods.read', component: PayCalendarScreen },
  { id: 'payroll', path: '/payroll/*', labelKey: 'nav.payroll', icon: Wallet, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_runs.read', comesWith: 'S1.35' },
];
