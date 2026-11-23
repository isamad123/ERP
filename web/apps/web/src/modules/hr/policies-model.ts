// Pure rules of the policy screens (slice S1.23a Build item 6; FR-HRM-POL-03): the difference between two versions' parameters, the
// default parameters of a type's schema, assignment rules in plain words, and the editable form of an assignment set. The server
// checks every value again; these only help the person see and type.
import type { Json, JsonObject, ParamSchema, Condition, Operator, Attribute, RuleNames, AssignmentRow, AssignmentInput, PolicyVersion } from '../../api/policies';
import { nameOf, type Names } from '../../api/workflow';

// ---------------------------------------------------------------- version difference

export type DiffKind = 'added' | 'removed' | 'changed';
export interface DiffLine { path: string; kind: DiffKind; before: Json | undefined; after: Json | undefined }

const isObject = (v: Json | undefined): v is JsonObject => typeof v === 'object' && v !== null && !Array.isArray(v);

/** The leaves of a JSON value by path (`bands[0].days`); an empty object or array is a leaf of its own. */
function leaves(value: Json | undefined, path: string, out: Map<string, Json>): void {
  if (isObject(value) && Object.keys(value).length > 0) {
    for (const key of Object.keys(value).sort()) leaves(value[key], path ? `${path}.${key}` : key, out);
  } else if (Array.isArray(value) && value.length > 0) {
    value.forEach((item, i) => leaves(item, `${path}[${i}]`, out));
  } else if (value !== undefined) {
    out.set(path, value);
  }
}

/** Key by key: what the later version adds, removes or changes against the earlier one (unchanged values are left out). */
export function diffParameters(before: JsonObject, after: JsonObject): DiffLine[] {
  const a = new Map<string, Json>();
  const b = new Map<string, Json>();
  leaves(before, '', a);
  leaves(after, '', b);
  const paths = [...new Set([...a.keys(), ...b.keys()])].sort((x, y) => x.localeCompare(y, 'en', { numeric: true }));
  const lines: DiffLine[] = [];
  for (const path of paths) {
    const old = a.get(path);
    const now = b.get(path);
    if (old === undefined) lines.push({ path, kind: 'added', before: undefined, after: now });
    else if (now === undefined) lines.push({ path, kind: 'removed', before: old, after: undefined });
    else if (JSON.stringify(old) !== JSON.stringify(now)) lines.push({ path, kind: 'changed', before: old, after: now });
  }
  return lines;
}

/** A JSON value as one short line of text (texts without quotes). */
export function showValue(value: Json | undefined): string {
  if (value === undefined) return '';
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

/** Versions newest first. */
export const newestFirst = (versions: readonly PolicyVersion[]): PolicyVersion[] => [...versions].sort((x, y) => y.version - x.version);

// ---------------------------------------------------------------- parameters from the schema

/** A starting value for a schema: required properties filled with their lowest value or first choice, optional ones left out. */
export function defaultFor(schema: ParamSchema): Json {
  if (schema.enum && schema.enum.length > 0) return schema.enum[0] ?? null;
  switch (schema.type) {
    case 'object': {
      const out: JsonObject = {};
      for (const key of schema.required ?? []) {
        const sub = schema.properties?.[key];
        if (sub) out[key] = defaultFor(sub);
      }
      return out;
    }
    case 'array': {
      const item = schema.items;
      return item && (schema.minItems ?? 0) > 0 ? Array.from({ length: schema.minItems ?? 0 }, () => defaultFor(item)) : [];
    }
    case 'integer':
    case 'number': return schema.minimum ?? 0;
    case 'boolean': return false;
    default: return '';
  }
}

/** The properties of an object schema in their declared order, with whether each is required. */
export function fieldsOf(schema: ParamSchema): { key: string; schema: ParamSchema; required: boolean }[] {
  return Object.entries(schema.properties ?? {}).map(([key, sub]) => ({ key, schema: sub, required: (schema.required ?? []).includes(key) }));
}

/** A number typed as text: exact (no floating point is introduced beyond what JSON carries); null when not a number. */
export function parseNumber(text: string, integer: boolean): number | null {
  const trimmed = text.trim();
  if (!(integer ? /^-?\d+$/ : /^-?\d+(\.\d+)?$/).test(trimmed)) return null;
  return Number(trimmed);
}

/** Server field errors under a path (`parameters.bands[0].days` → the field `bands[0].days`). */
export function errorAt(errors: Record<string, string>, path: string): string | undefined {
  return errors[`parameters.${path}`] ?? errors[path];
}

// ---------------------------------------------------------------- rules in words

export interface WordContext {
  names: RuleNames;
  locale: string;
  yes: string;
  no: string;
}

/** The plain-words value of one condition value: the name of a grade, unit, branch or location, an employee type, a custom choice. */
export function valueLabel(attribute: Attribute, field: string | null | undefined, value: Json, ctx: WordContext): string {
  if (attribute === 'employee_type') {
    const type = ctx.names.employeeTypes.find((e) => e.code === value);
    return type ? nameOf(type.name, ctx.locale) || String(value) : String(value);
  }
  if (attribute === 'custom') {
    const def = ctx.names.customFields.find((f) => f.key === field);
    if (typeof value === 'boolean') return value ? ctx.yes : ctx.no;
    const option = def?.options.find((o) => o.code === value);
    return option ? nameOf(option.label, ctx.locale) || String(value) : showValue(value);
  }
  const record = typeof value === 'string' ? ctx.names.records[value] : undefined;
  if (!record) return showValue(value);
  const name = nameOf(record.name, ctx.locale);
  return name && name !== record.code ? `${name} (${record.code})` : record.code;
}

/** The label of a custom field (its key when the definition is unknown). */
export function fieldLabel(field: string | null | undefined, ctx: WordContext): string {
  const def = ctx.names.customFields.find((f) => f.key === field);
  return def ? nameOf(def.label as Names, ctx.locale) || def.key : (field ?? '');
}

/** A condition's parts for a sentence: the message key (by operator), the attribute's own label is added by the screen. */
export function conditionWords(c: Pick<Condition, 'attribute' | 'field' | 'operator' | 'values' | 'subUnits'>, ctx: WordContext): { key: `pol.cond.${Operator}`; values: string; subUnits: boolean; field: string } {
  const values = c.values.map((v) => valueLabel(c.attribute, c.field, v, ctx)).join(', ');
  return { key: `pol.cond.${c.operator}`, values, subUnits: c.attribute === 'unit' && !!c.subUnits, field: c.attribute === 'custom' ? fieldLabel(c.field, ctx) : '' };
}

// ---------------------------------------------------------------- the set editor

export interface DraftCondition { attribute: Attribute; field: string; operator: Operator; values: Json[]; subUnits: boolean }
export interface DraftAssignment { key: string; policyId: string; precedence: number; conditions: DraftCondition[] }

let counter = 0;
const nextKey = () => { counter += 1; return `a${counter}`; };

/** The set in force as the editor's starting point. */
export function draftOf(rows: readonly AssignmentRow[]): DraftAssignment[] {
  return [...rows].sort((a, b) => a.precedence - b.precedence).map((r) => ({
    key: nextKey(),
    policyId: r.policyId,
    precedence: r.precedence,
    conditions: (r.rule.conditions ?? []).map((c) => ({ attribute: c.attribute, field: c.field ?? '', operator: c.operator, values: [...c.values], subUnits: !!c.subUnits })),
  }));
}

/** A new assignment after the last one (precedence ten above the highest, so others can be put between later). */
export function addAssignment(set: readonly DraftAssignment[], policyId: string): DraftAssignment[] {
  const top = set.reduce((m, a) => Math.max(m, a.precedence), 0);
  return [...set, { key: nextKey(), policyId, precedence: Math.min(9999, top + 10), conditions: [] }];
}

/** Moves an assignment one place up or down by swapping precedences with its neighbour (the order is tried top to bottom). */
export function move(set: readonly DraftAssignment[], key: string, direction: -1 | 1): DraftAssignment[] {
  const sorted = [...set].sort((a, b) => a.precedence - b.precedence);
  const i = sorted.findIndex((a) => a.key === key);
  const j = i + direction;
  const a = sorted[i];
  const b = sorted[j];
  if (!a || !b) return sorted;
  sorted[i] = { ...b, precedence: a.precedence };
  sorted[j] = { ...a, precedence: b.precedence };
  return sorted.sort((x, y) => x.precedence - y.precedence);
}

/** The body the server takes; the order of the list is the order on screen (errors come back by index). */
export function toInputs(set: readonly DraftAssignment[]): AssignmentInput[] {
  return set.map((a) => ({
    policyId: a.policyId,
    precedence: a.precedence,
    rule: {
      conditions: a.conditions.map((c) => ({
        attribute: c.attribute, operator: c.operator, values: c.values,
        ...(c.attribute === 'custom' ? { field: c.field } : {}),
        ...(c.attribute === 'unit' && c.subUnits ? { subUnits: true } : {}),
      })),
    },
  }));
}

/** The server's errors of one assignment (`assignments[2].rule` → `rule`). */
export function errorsOf(errors: Record<string, string[]>, index: number): Record<string, string[]> {
  const prefix = `assignments[${index}].`;
  return Object.fromEntries(Object.entries(errors).filter(([k]) => k.startsWith(prefix)).map(([k, v]) => [k.slice(prefix.length), v]));
}

/** A blank condition for an attribute (the first allowed custom field for custom). */
export function blankCondition(attribute: Attribute, names: RuleNames): DraftCondition {
  return { attribute, field: attribute === 'custom' ? (names.customFields.find((f) => f.status === 'active')?.key ?? '') : '', operator: 'in', values: [], subUnits: false };
}

/** Today's date (yyyy-mm-dd) in a time zone. */
export function todayIn(timeZone: string): string {
  try {
    return new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
  } catch {
    return new Date().toISOString().slice(0, 10);
  }
}
