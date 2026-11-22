// Form checks from the generated validation schema (slice S1.12c Build items 14 and 18, ruling Q11): the same rules the
// server and the database apply (required, code length and spacing, choices, decimal digits), so the two cannot drift.
// Checks run when a field is left and on save; the server's answer is still the judge (its errors map back to fields).
import type { MessageKey } from '@erp/i18n';
import type { PropertySchema, ValidationSchema } from './metadata';

export interface FieldProblem {
  key: MessageKey;
  values?: Record<string, string | number>;
}

export type FormValues = Record<string, unknown>;

function isBlank(value: unknown): boolean {
  if (value === undefined || value === null) return true;
  if (typeof value === 'string') return value.trim().length === 0;
  if (typeof value === 'object' && !Array.isArray(value)) return Object.values(value as Record<string, unknown>).every(isBlank);
  return false;
}

/**
 * The problem with one value, or null. `required` comes from the schema's required list. Text is checked as it will be
 * sent: without spaces at the ends (the form trims them, so they are never an error the person has to fix).
 */
export function checkValue(raw: unknown, schema: PropertySchema | undefined, required: boolean): FieldProblem | null {
  if (!schema || schema.readOnly) return null;
  const value = typeof raw === 'string' ? raw.trim() : raw;
  if (isBlank(value)) return required || (schema.minProperties ?? 0) > 0 ? { key: 'engine.error.required' } : null;
  switch (schema.type) {
    case 'string': {
      if (typeof value !== 'string') return { key: 'engine.error.invalid' };
      if (schema.enum && !schema.enum.includes(value)) return { key: 'engine.error.choice' };
      if (schema.minLength !== undefined && value.length < schema.minLength) return { key: 'engine.error.required' };
      if (schema.maxLength !== undefined && value.length > schema.maxLength) return { key: 'engine.error.tooLong', values: { max: schema.maxLength } };
      if (schema.format === 'decimal' && schema.pattern && !new RegExp(schema.pattern).test(value.trim())) return { key: 'engine.error.decimal' };
      if (schema.format === 'uuid' && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) return { key: 'engine.error.invalid' };
      if (schema.pattern && schema.format !== 'decimal' && !new RegExp(schema.pattern, 'u').test(value)) return { key: 'engine.error.invalid' };
      return null;
    }
    case 'integer': {
      const n = typeof value === 'number' ? value : Number(value);
      if (!Number.isInteger(n)) return { key: 'engine.error.integer' };
      if ((schema.minimum !== undefined && n < schema.minimum) || (schema.maximum !== undefined && n > schema.maximum)) return { key: 'engine.error.integer' };
      return null;
    }
    case 'boolean':
      return typeof value === 'boolean' ? null : { key: 'engine.error.invalid' };
    case 'object': {
      if (typeof value !== 'object' || Array.isArray(value)) return { key: 'engine.error.invalid' };
      const entries = Object.entries(value as Record<string, unknown>).filter(([, v]) => !isBlank(v));
      if (entries.length < (schema.minProperties ?? 0)) return { key: 'engine.error.required' };
      const allowed = schema.propertyNames?.enum;
      if (allowed && entries.some(([k]) => !allowed.includes(k))) return { key: 'engine.error.invalid' };
      const max = schema.additionalProperties?.maxLength;
      if (max !== undefined && entries.some(([, v]) => typeof v === 'string' && v.length > max)) {
        return { key: 'engine.error.tooLong', values: { max } };
      }
      return null;
    }
    default:
      return null;
  }
}

/** Every problem of a form, by field. */
export function validate(values: FormValues, schema: ValidationSchema, fields: readonly string[]): Record<string, FieldProblem> {
  const problems: Record<string, FieldProblem> = {};
  for (const field of fields) {
    const problem = checkValue(values[field], schema.properties[field], schema.required.includes(field));
    if (problem) problems[field] = problem;
  }
  return problems;
}

/**
 * The body to send: text trimmed, empty language entries of a two-language name left out, empty optional values sent as
 * null, decimals kept as their digits (written to the JSON as numbers by {@link toJson}).
 */
export function cleanValues(values: FormValues, schema: ValidationSchema, fields: readonly string[]): FormValues {
  const body: FormValues = {};
  for (const field of fields) {
    const property = schema.properties[field];
    if (!property || property.readOnly) continue;
    const value = values[field];
    if (property.type === 'object' && value && typeof value === 'object') {
      body[field] = Object.fromEntries(Object.entries(value as Record<string, unknown>)
        .filter(([, v]) => typeof v === 'string' && v.trim().length > 0).map(([k, v]) => [k, (v as string).trim()]));
    } else if (typeof value === 'string') {
      const trimmed = value.trim();
      body[field] = trimmed.length === 0 && property.nullable ? null : trimmed;
    } else {
      body[field] = value === undefined && property.nullable ? null : value;
    }
  }
  return body;
}

/** Exact decimal text as a JSON number, without passing through a floating-point number (FR-UX-CMP-04). */
const DECIMAL = /^-?\d+(\.\d+)?$/;

export function toJson(body: FormValues, decimals: ReadonlySet<string>): string {
  const raw: string[] = [];
  const text = JSON.stringify(body, (key, value: unknown) => {
    if (decimals.has(key) && typeof value === 'string' && DECIMAL.test(value)) {
      raw.push(value);
      return `\u0000decimal${raw.length - 1}\u0000`;
    }
    return value;
  });
  return text.replace(/"\\u0000decimal(\d+)\\u0000"/g, (_, i: string) => raw[Number(i)] ?? 'null');
}

/**
 * Parses the server's JSON keeping decimal fields as their exact digits (a JSON number such as 1234567890123456.7891
 * would lose digits as a JavaScript number).
 */
export function fromJson<T>(text: string, decimals: ReadonlySet<string>): T {
  if (decimals.size === 0) return JSON.parse(text) as T;
  const marked = text.replace(/"([A-Za-z0-9_]+)"\s*:\s*(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?=\s*[,}])/g,
    (all, key: string, digits: string) => (decimals.has(key) ? `"${key}":"${digits}"` : all));
  return JSON.parse(marked) as T;
}
