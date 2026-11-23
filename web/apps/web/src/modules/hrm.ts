// Screens of the HR web module, declared now so the menu follows the licence and the person's permissions (product owner
// ruling 2026-11-21: People, Time and Payroll show now and open "not available yet" until their slices build them).
// Features are D-24 keys of the licence; permissions are the read permission of each area's main table.
import { lazy } from 'react';
import { CalendarDays, Clock, FileSearch, ScrollText, Users, Wallet } from 'lucide-react';
import type { ScreenDeclaration } from '../navigation/screens';

// S1.23a: the policy framework's screens (policies with versions and assignments; "Which policy applies?"), loaded with their routes.
const PoliciesScreen = lazy(() => import('./hr/policies'));
const ResolutionScreen = lazy(() => import('./hr/policy-resolution'));

export const hrmScreens: ScreenDeclaration[] = [
  { id: 'people', path: '/people/*', labelKey: 'nav.people', icon: Users, group: 'people', feature: 'hrm.core', permission: 'hrm.persons.read', comesWith: 'S1.21' },
  { id: 'attendance', path: '/time/attendance/*', labelKey: 'nav.attendance', icon: Clock, group: 'time', feature: 'hrm.core', permission: 'hrm.attendance_days.read', comesWith: 'S1.29' },
  { id: 'leave', path: '/time/leave/*', labelKey: 'nav.leave', icon: CalendarDays, group: 'time', feature: 'hrm.core', permission: 'hrm.leave_requests.read', comesWith: 'S1.33' },
  // S1.23a (FR-HRM-POL-02, -03; ruling Q1): HR administrators manage policies; both entries need the permission to read policies.
  { id: 'policies', path: '/hr/policies/*', labelKey: 'nav.policies', icon: ScrollText, group: 'people', feature: 'hrm.core', permission: 'hrm.policies.read', component: PoliciesScreen },
  { id: 'policy-resolution', path: '/hr/policy-resolution', labelKey: 'nav.policyResolution', icon: FileSearch, group: 'people', feature: 'hrm.core', permission: 'hrm.policies.read', component: ResolutionScreen },
  { id: 'payroll', path: '/payroll/*', labelKey: 'nav.payroll', icon: Wallet, group: 'payroll', feature: 'hrm.payroll', permission: 'hrm.pay_runs.read', comesWith: 'S1.35' },
];
