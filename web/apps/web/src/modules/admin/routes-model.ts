// The route editor's model (slice S1.15c Build item 10; FR-KRN-WFL-01 to -03, S1.15 ruling Q9): reading and changing a route in its stored form, the
// operators each field type allows, and conditions written as plain sentences ("when Amount is more than 50,000"). The server's check (`RouteFormat`
// and the administration service) is the only judge of whether a route can be activated; nothing here decides that.
import type { ApproverJson, ConditionJson, RouteField, RouteJson, StepJson } from '../../api/routes';
import { nameOf, type Names } from '../../api/workflow';

export const MAX_STEPS = 20;
/** Groups may nest to the third level of the engine's condition depth: a group at depth 1, a group inside it at depth 2, and only tests below that. */
export const MAX_GROUP_DEPTH = 2;

export const APPROVER_KINDS = ['user', 'role', 'document_user', 'manager', 'department_head', 'position'] as const;
export type ApproverKind = (typeof APPROVER_KINDS)[number];
/** What an escalation (and a fallback) may be: a named user or a role queue (the engine's `Fixed` kinds). */
export const ESCALATION_KINDS = ['user', 'role'] as const;

export const OPERATORS = ['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'in', 'not_in', 'empty', 'not_empty'] as const;
export type Operator = (typeof OPERATORS)[number];

const ORDERED = new Set(['number', 'money', 'date']);

/** The operators a field type offers (the engine refuses the others: ordered comparisons need numbers, money or dates; lists do not fit yes/no, dates or money). */
export function operatorsFor(type: string): Operator[] {
  return OPERATORS.filter((op) => {
    if (op === 'gt' || op === 'ge' || op === 'lt' || op === 'le') return ORDERED.has(type);
    if (op === 'in' || op === 'not_in') return !(type === 'bool' || type === 'date' || type === 'money');
    return true;
  });
}

export const isLeaf = (c: ConditionJson): boolean => c.field !== undefined;
export const groupKind = (c: ConditionJson): 'all' | 'any' | null => (c.all ? 'all' : c.any ? 'any' : null);
export const itemsOf = (c: ConditionJson): ConditionJson[] => c.all ?? c.any ?? [];

/** A new test on the first declared field. */
export function newTest(fields: RouteField[]): ConditionJson {
  const field = fields[0];
  if (!field) return { field: '', op: 'eq', value: '' };
  return { field: field.name, op: 'eq', value: defaultValue(field, 'eq') };
}

export function defaultValue(field: RouteField, op: Operator): unknown {
  if (op === 'empty' || op === 'not_empty') return undefined;
  if (op === 'in' || op === 'not_in') return [];
  if (field.type === 'bool') return true;
  if (field.type === 'choice') return field.choices[0] ?? '';
  return '';
}

/** A test with another field or operator: the value is reset when it no longer fits. */
export function retarget(test: ConditionJson, fields: RouteField[], change: { field?: string; op?: Operator }): ConditionJson {
  const field = fields.find((f) => f.name === (change.field ?? test.field));
  if (!field) return { ...test, ...change };
  const op = change.op ?? (change.field && !operatorsFor(field.type).includes(test.op as Operator) ? 'eq' : (test.op as Operator));
  const listBefore = test.op === 'in' || test.op === 'not_in';
  const listAfter = op === 'in' || op === 'not_in';
  const keep = !change.field && listBefore === listAfter && op !== 'empty' && op !== 'not_empty' && test.op !== 'empty' && test.op !== 'not_empty';
  const next: ConditionJson = { field: field.name, op };
  const value = keep ? test.value : defaultValue(field, op);
  if (value !== undefined) next.value = value;
  return next;
}

/** An immutable change of the condition at a path of item indexes. */
export function updateAt(root: ConditionJson, path: number[], change: (c: ConditionJson) => ConditionJson | null): ConditionJson | null {
  const [head, ...rest] = path;
  if (head === undefined) return change(root);
  const kind = groupKind(root);
  if (!kind) return root;
  const items = [...itemsOf(root)];
  const child = items[head];
  if (!child) return root;
  const updated = updateAt(child, rest, change);
  if (updated === null) items.splice(head, 1); else items[head] = updated;
  return { [kind]: items };
}

export function addTo(root: ConditionJson, path: number[], item: ConditionJson): ConditionJson {
  return updateAt(root, path, (group) => {
    const kind = groupKind(group) ?? 'all';
    return { [kind]: [...itemsOf(group), item] };
  }) ?? root;
}

/** A "between" test: at least the low value and at most the high value (the engine has no range operator, so it is two tests under "all of"). */
export function between(field: RouteField): ConditionJson {
  return { all: [{ field: field.name, op: 'ge', value: '' }, { field: field.name, op: 'le', value: '' }] };
}

export function isBetween(c: ConditionJson): boolean {
  const items = c.all;
  if (!items || items.length !== 2) return false;
  const [low, high] = items;
  return !!low && !!high && low.field !== undefined && low.field === high.field && low.op === 'ge' && high.op === 'le';
}

// ---- reading and changing steps ----

export function stepKeyAfter(steps: StepJson[]): string {
  let n = steps.length + 1;
  while (steps.some((s) => s.key === `step_${n}`)) n += 1;
  return `step_${n}`;
}

export function newStep(steps: StepJson[]): StepJson {
  return { key: stepKeyAfter(steps), name: { en: '', ur: '' }, approver: { kind: 'role', role: '' }, bulk_approval: false };
}

export function move<T>(items: T[], index: number, by: number): T[] {
  const target = index + by;
  if (target < 0 || target >= items.length) return items;
  const next = [...items];
  const [item] = next.splice(index, 1);
  if (item === undefined) return items;
  next.splice(target, 0, item);
  return next;
}

/** The approver of a new kind starts with that kind's empty parameters. */
export function approverOf(kind: string): ApproverJson {
  switch (kind) {
    case 'user': return { kind, user_id: '' };
    case 'role': return { kind, role: '' };
    case 'document_user': return { kind, field: '' };
    case 'manager': return { kind, levels: 1 };
    case 'position': return { kind, position: '' };
    default: return { kind };
  }
}

/** The route as text with every object's keys in order, so two routes that say the same thing compare equal however the server or the editor ordered them. */
export const canonical = (route: RouteJson): string => JSON.stringify(route, (_key, value: unknown) =>
  (value && typeof value === 'object' && !Array.isArray(value)
    ? Object.fromEntries(Object.entries(value as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)))
    : value));

export const sameRoute = (a: RouteJson, b: RouteJson): boolean => canonical(a) === canonical(b);

/** The step a problem path names (`steps[2].condition`), or null for a problem of the whole route. */
export function stepOfProblem(path: string): number | null {
  const found = /^steps\[(\d+)\]/.exec(path);
  return found?.[1] ? Number(found[1]) : null;
}

/** Drops the keys an empty editor leaves behind, so the route is written as the server's canonical form (empty condition, no fallback, no escalation). */
export function tidy(route: RouteJson): RouteJson {
  return { steps: route.steps.map((s): StepJson => {
    const step: StepJson = { key: s.key, name: s.name, approver: s.approver, bulk_approval: s.bulk_approval ?? false };
    if (s.condition) step.condition = s.condition;
    if (s.fallback) step.fallback = s.fallback;
    if (s.time_limit_working_days) step.time_limit_working_days = s.time_limit_working_days;
    if (s.escalation) step.escalation = s.escalation;
    return step;
  }) };
}

// ---- conditions as sentences ----

export interface SentenceContext {
  fields: RouteField[];
  locale: string;
  /** A message in the person's language: `routes.op.eq`, `routes.and`, ... */
  say: (key: string, values?: Record<string, string | number>) => string;
  companyName: (id: string) => string;
}

const fieldName = (ctx: SentenceContext, name: string): string => {
  const field = ctx.fields.find((f) => f.name === name);
  return field ? nameOf(field.label, ctx.locale) || field.name : name;
};

function valueText(ctx: SentenceContext, field: RouteField | undefined, value: unknown): string {
  if (Array.isArray(value)) return value.map((v) => valueText(ctx, field, v)).join(', ');
  if (typeof value === 'boolean') return ctx.say(value ? 'routes.yes' : 'routes.no');
  if (value === undefined || value === null || value === '') return '…';
  if (field?.type === 'company' && typeof value === 'string') return ctx.companyName(value) || value;
  return String(value);
}

/** "Amount is more than 50000", "Amount is between 10 and 50", "all of (…) / any of (…)". */
export function describe(c: ConditionJson, ctx: SentenceContext): string {
  if (isBetween(c)) {
    const [low, high] = c.all ?? [];
    const field = ctx.fields.find((f) => f.name === low?.field);
    return ctx.say('routes.sentence.between', { field: fieldName(ctx, low?.field ?? ''), low: valueText(ctx, field, low?.value), high: valueText(ctx, field, high?.value) });
  }
  const kind = groupKind(c);
  if (kind) {
    const joined = itemsOf(c).map((item) => (groupKind(item) && !isBetween(item) ? `(${describe(item, ctx)})` : describe(item, ctx))).join(` ${ctx.say(kind === 'all' ? 'routes.and' : 'routes.or')} `);
    return joined;
  }
  const field = ctx.fields.find((f) => f.name === c.field);
  return ctx.say('routes.sentence.test', { field: fieldName(ctx, c.field ?? ''), op: ctx.say(`routes.op.${c.op ?? 'eq'}`), value: c.op === 'empty' || c.op === 'not_empty' ? '' : valueText(ctx, field, c.value) }).trim();
}

/** Who decides a step, in words. */
export function describeApprover(a: ApproverJson, ctx: SentenceContext & { roleName: (code: string) => string; userName: (id: string) => string }): string {
  switch (a.kind) {
    case 'user': return ctx.say('routes.approver.user', { name: ctx.userName(a.user_id ?? '') });
    case 'role': return ctx.say('routes.approver.role', { name: ctx.roleName(a.role ?? '') || '…' });
    case 'document_user': return ctx.say('routes.approver.document_user', { field: fieldName(ctx, a.field ?? '') });
    case 'manager': return ctx.say('routes.approver.manager', { levels: a.levels ?? 1 });
    case 'department_head': return ctx.say('routes.approver.department_head');
    default: return ctx.say('routes.approver.position', { position: a.position ?? '' });
  }
}

export const stepTitle = (step: StepJson, locale: string): string => nameOf(step.name as Names, locale) || step.key;
