// Menus and screen gates (slice S1.12b Build item 11; FR-KRN-ENT-03, FR-KRN-ENT-04, FR-KRN-TEN-09; S1.12 ruling Q8 and
// the product owner's ruling of 2026-11-21 on the HR entries).
import { describe, expect, it } from 'vitest';
import { House } from 'lucide-react';
import { hrmScreens } from '../modules/hrm';
import { kernelScreens } from '../modules/kernel';
import { accessFacts, decide, holds, hrefOf, menuFor, type ScreenDeclaration } from './screens';

const Page = () => null;
const screens = [...kernelScreens, ...hrmScreens];
const everything = ['hrm.core', 'hrm.payroll'];
const grant = (code: string, companies: string[] | null = null) => ({ code, companies });

describe('FR-KRN-ENT-03 screens of unlicensed modules do not exist', () => {
  it('a customer without hrm.payroll gets "not found" on payroll addresses, the same answer as an unknown screen', () => {
    const facts = accessFacts(['hrm.core'], [grant('hrm.pay_runs.read'), grant('hrm.persons.read')], 'active');
    const payroll = screens.find((s) => s.id === 'payroll')!;
    expect(decide(payroll, facts)).toBe('notFound');
    expect(menuFor(screens, facts).flatMap((s) => s.entries.map((e) => e.id))).not.toContain('payroll');
    expect(menuFor(screens, facts).flatMap((s) => s.entries.map((e) => e.id))).toContain('people');
  });

  it('licence comes before the locked state and the permission, so nothing tells an unlicensed screen apart', () => {
    const payroll = screens.find((s) => s.id === 'payroll')!;
    expect(decide(payroll, accessFacts([], [], 'locked'))).toBe('notFound');
    expect(decide(payroll, accessFacts([], [], 'active'))).toBe('notFound');
  });
});

describe('FR-KRN-IAM-10 menus follow the permissions (the server still decides)', () => {
  it('an entry shows only with its permission; the address answers "permission denied" without it', () => {
    const without = accessFacts(everything, [grant('hrm.persons.read')], 'active');
    const people = screens.find((s) => s.id === 'people')!;
    const payroll = screens.find((s) => s.id === 'payroll')!;
    expect(decide(people, without)).toBe('notYet');
    expect(decide(payroll, without)).toBe('denied');
    const ids = menuFor(screens, without).flatMap((s) => s.entries.map((e) => e.id));
    expect(ids).toContain('people');
    expect(ids).not.toContain('payroll');
    expect(ids).not.toContain('licence');
    expect(ids).toContain('home'); // the home page needs no permission
  });

  it('groups come in the fixed product order and each holds its own entries', () => {
    const all = accessFacts(everything, ['ntf.in_app_items.read', 'wf.workflow_tasks.read', 'hrm.persons.read', 'hrm.attendance_days.read',
      'hrm.leave_requests.read', 'hrm.pay_runs.read', 'cfg.licence.read', 'job.runs.read', 'aud.changes.view'].map((c) => grant(c)), 'active');
    const menu = menuFor(screens, all);
    expect(menu.map((s) => s.group)).toEqual(['home', 'approvals', 'people', 'time', 'payroll', 'administration']);
    expect(menu.find((s) => s.group === 'time')!.entries.map((e) => e.id)).toEqual(['attendance', 'leave']);
    // Deep-link targets without a menu group stay out of the menu.
    expect(menu.flatMap((s) => s.entries.map((e) => e.id))).not.toContain('document');
  });

  it('holds a permission for one company or for all of them', () => {
    const facts = accessFacts([], [grant('org.branches.read', ['c1']), grant('iam.self.manage')], 'active');
    expect(holds(facts, 'org.branches.read')).toBe(true);
    expect(holds(facts, 'org.branches.read', 'c1')).toBe(true);
    expect(holds(facts, 'org.branches.read', 'c2')).toBe(false);
    expect(holds(facts, 'iam.self.manage', 'c2')).toBe(true);
    expect(holds(facts, 'hrm.persons.read')).toBe(false);
  });
});

describe('FR-KRN-ENT-04 FR-KRN-TEN-09 locked licence', () => {
  it('every screen shows the locked page except the licence page and exports, which keep their permission check', () => {
    const reader = accessFacts(everything, [grant('cfg.licence.read'), grant('ntf.in_app_items.read')], 'locked');
    const byId = (id: string) => screens.find((s) => s.id === id)!;
    expect(decide(byId('home'), reader)).toBe('locked');
    expect(decide(byId('notifications'), reader)).toBe('locked');
    expect(decide(byId('licence'), reader)).toBe('show'); // S1.16c: the licence page exists and answers when locked
    expect(decide(byId('exports'), reader)).toBe('denied');
    const other = accessFacts(everything, [], 'locked');
    expect(decide(byId('licence'), other)).toBe('denied');
  });
});

describe('FR-HRM-NTF-07 deep links of the notification catalogue have a screen', () => {
  it('every link of S1.11b1 matches a declared screen (later screens open "not available yet")', () => {
    const links = ['/settings/licence', '/admin/jobs/runs', '/admin/jobs/health', '/admin/audit', '/admin/documents/quarantine', '/documents/0190'];
    const matches = (pattern: string, path: string) => {
      const re = new RegExp(`^${pattern.replace(/\/\*$/, '(/.*)?').replace(/:[a-z]+/g, '[^/]+')}$`);
      return re.test(path);
    };
    for (const link of links) expect(screens.some((s) => matches(s.path, link)), link).toBe(true);
  });

  it('a declared screen with a component shows it; menu addresses drop the area wildcard', () => {
    const screen: ScreenDeclaration = { id: 'x', path: '/x/*', labelKey: 'nav.home', icon: House, group: 'home', component: Page };
    expect(decide(screen, accessFacts([], [], 'read_only'))).toBe('show');
    expect(hrefOf(screen)).toBe('/x');
    expect(hrefOf(screens.find((s) => s.id === 'home')!)).toBe('/');
  });
});
