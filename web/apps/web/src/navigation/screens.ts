// Screens and menus (slice S1.12b Build item 11, S1.12 ruling Q8; spec 13 FR-UX-IA-01, FR-UX-SEC-02): each web module
// declares its screens with the menu group, label, icon, the permission the screen needs and the licensed feature it
// belongs to. The shell shows a menu entry only when the feature is licensed and the person holds the permission; an
// unlicensed screen answers "not found", exactly like an address that does not exist (FR-KRN-ENT-03). This only shows
// and hides: the server checks every request again.
import type { ComponentType } from 'react';
import type { LucideIcon } from 'lucide-react';
import type { MessageKey } from '@erp/i18n';
import type { LicenceState, PermissionGrant } from '../api/kernel';

/** Menu areas in the fixed product order (ruling Q8). */
export const MENU_GROUPS = ['home', 'approvals', 'people', 'time', 'payroll', 'administration'] as const;
export type MenuGroup = (typeof MENU_GROUPS)[number];

export interface ScreenDeclaration {
  /** Stable id, unique across modules. */
  id: string;
  /** Route pattern (react-router); an area ends with "/*" so every address under it follows the same rule. */
  path: string;
  labelKey: MessageKey;
  icon: LucideIcon;
  /** Screens with a group appear in the side menu; the others are reached by links only. */
  group?: MenuGroup;
  /** The permission the screen needs (any company). Absent: the screen's own data decides (the server answers). */
  permission?: string;
  /** The licensed feature (D-24 key); absent: kernel screens, always licensed. */
  feature?: string;
  /** Still opens while the licence is locked (the licence page and exports, ruling Q17). */
  allowedWhenLocked?: boolean;
  /** The screen; absent: a later slice builds it and the address opens "not available yet". */
  component?: ComponentType;
  /** The slice that builds a screen that is not available yet (shown to nobody; for the build plan). */
  comesWith?: string;
}

/** What the shell knows about the person and the licence (from /api/kernel/me and /api/kernel/capabilities). */
export interface AccessFacts {
  features: ReadonlySet<string>;
  permissions: ReadonlyMap<string, readonly string[] | null>;
  state: LicenceState;
}

export function accessFacts(features: readonly string[], grants: readonly PermissionGrant[], state: LicenceState): AccessFacts {
  return { features: new Set(features), permissions: new Map(grants.map((g) => [g.code, g.companies])), state };
}

/** Whether the person holds a permission, for one company or (without one) for any company. */
export function holds(facts: AccessFacts, code: string, companyId?: string | null): boolean {
  if (!facts.permissions.has(code)) return false;
  const companies = facts.permissions.get(code);
  return companies === null || companies === undefined || !companyId || companies.includes(companyId);
}

export type Decision = 'show' | 'notFound' | 'locked' | 'denied' | 'notYet';

/**
 * What an address shows: not licensed → "not found" (the same page as a nonsense address); locked licence → the locked
 * page, unless the screen stays open when locked; permission missing → "permission denied"; a screen of a later slice →
 * "not available yet"; otherwise the screen.
 */
export function decide(screen: ScreenDeclaration, facts: AccessFacts): Decision {
  if (screen.feature && !facts.features.has(screen.feature)) return 'notFound';
  if (facts.state === 'locked' && !screen.allowedWhenLocked) return 'locked';
  if (screen.permission && !holds(facts, screen.permission)) return 'denied';
  return screen.component ? 'show' : 'notYet';
}

export interface MenuSection {
  group: MenuGroup;
  entries: ScreenDeclaration[];
}

/** The side menu: entries the person can open (or that open "not available yet"), grouped in the product order. */
export function menuFor(screens: readonly ScreenDeclaration[], facts: AccessFacts): MenuSection[] {
  return MENU_GROUPS.map((group) => ({
    group,
    entries: screens.filter((s) => s.group === group && ['show', 'notYet'].includes(decide(s, facts))),
  })).filter((section) => section.entries.length > 0);
}

/** The menu address of a screen ("/payroll/*" → "/payroll"). */
export function hrefOf(screen: ScreenDeclaration): string {
  return screen.path.replace(/\/\*$/, '') || '/';
}
