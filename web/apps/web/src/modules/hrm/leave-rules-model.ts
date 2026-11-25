// The shape of a leave policy's rules for the screen (slice S1.33b Build item 6; FR-HRM-LVE-03). The policy type's JSON Schema is the source of every field, label and limit;
// this file only says how the fields of one leave type's block are grouped into sections. A field the schema has and no section names lands in "More rules", so nothing is hidden,
// and no number or default is filled in for anybody.
import type { Json, JsonObject, ParamSchema } from '../../api/policies';

export type SectionId = 'entitlement' | 'prorata' | 'carry' | 'negative' | 'notice' | 'eligibility' | 'combine' | 'approval' | 'other';

interface Group { id: SectionId; typeKeys: string[]; entitlementKeys: string[] }

const GROUPS: Group[] = [
  { id: 'entitlement', typeKeys: ['yearStartMonth'], entitlementKeys: ['model', 'quantity', 'per', 'frequency', 'tiers'] },
  { id: 'prorata', typeKeys: [], entitlementKeys: ['proRata', 'rounding', 'roundingStep'] },
  { id: 'carry', typeKeys: ['carryForward', 'encashment'], entitlementKeys: [] },
  { id: 'negative', typeKeys: ['negativeLimit'], entitlementKeys: [] },
  { id: 'notice', typeKeys: ['minNoticeDays', 'maxConsecutiveDays'], entitlementKeys: [] },
  { id: 'eligibility', typeKeys: ['eligibility'], entitlementKeys: [] },
  { id: 'combine', typeKeys: ['sandwich', 'notCombinedWith'], entitlementKeys: [] },
  { id: 'approval', typeKeys: ['hrApproval', 'cancellationApproval', 'compOffExpiryDays'], entitlementKeys: [] },
];

/** The key of the leave type inside a block (the schema's `leaveType`). */
export const TYPE_KEY = 'leaveType';
const ENTITLEMENT = 'entitlement';

/** One part of a section: the fields of the block itself (`scope` empty) or of its entitlement object. */
export interface Part { scope: '' | typeof ENTITLEMENT; schema: ParamSchema; keys: string[] }
export interface Section { id: SectionId; parts: Part[] }

const pick = (schema: ParamSchema, keys: string[]): ParamSchema => ({
  type: 'object',
  properties: Object.fromEntries(keys.filter((k) => schema.properties?.[k]).map((k) => [k, schema.properties?.[k] as ParamSchema])),
  required: (schema.required ?? []).filter((k) => keys.includes(k)),
});

const known = (schema: ParamSchema, keys: string[]) => keys.filter((k) => schema.properties?.[k]);

/** The block schema (`types.items`) of a leave policy's parameter schema, or null when the schema has another shape. */
export function blockSchema(schema: ParamSchema): ParamSchema | null {
  const items = schema.properties?.types?.items;
  return items?.type === 'object' ? items : null;
}

/**
 * The sections of a block from the schema. With `value`, only the fields that hold a value are kept (the read-only view lists what is set);
 * a section with nothing to show is dropped.
 */
export function sectionsOf(block: ParamSchema, value?: JsonObject): Section[] {
  const entitlement = block.properties?.[ENTITLEMENT];
  const entitlementKeys = Object.keys(entitlement?.properties ?? {});
  const typeKeys = Object.keys(block.properties ?? {}).filter((k) => k !== TYPE_KEY && k !== ENTITLEMENT);
  const used = new Set<string>(GROUPS.flatMap((g) => g.typeKeys));
  const usedEntitlement = new Set<string>(GROUPS.flatMap((g) => g.entitlementKeys));
  const groups: Group[] = [
    ...GROUPS.map((g) => ({ id: g.id, typeKeys: known(block, g.typeKeys), entitlementKeys: entitlement ? known(entitlement, g.entitlementKeys) : [] })),
    { id: 'other', typeKeys: typeKeys.filter((k) => !used.has(k)), entitlementKeys: entitlementKeys.filter((k) => !usedEntitlement.has(k)) },
  ];
  const sections: Section[] = groups.map((g) => {
      const own = value ?? {};
      const inner = own[ENTITLEMENT] && typeof own[ENTITLEMENT] === 'object' && !Array.isArray(own[ENTITLEMENT]) ? own[ENTITLEMENT] as JsonObject : {};
      const typeShown = value ? g.typeKeys.filter((k) => own[k] !== undefined) : g.typeKeys;
      const innerShown = value ? g.entitlementKeys.filter((k) => inner[k] !== undefined) : g.entitlementKeys;
      const parts: Part[] = [];
      if (typeShown.length > 0) parts.push({ scope: '', schema: pick(block, typeShown), keys: typeShown });
      if (innerShown.length > 0 && entitlement) parts.push({ scope: ENTITLEMENT, schema: pick(entitlement, innerShown), keys: innerShown });
      return { id: g.id, parts };
    });
  return sections.filter((s) => s.parts.length > 0);
}

const asObject = (v: Json | undefined): JsonObject => (v && typeof v === 'object' && !Array.isArray(v) ? v : {});

/** The value a part edits. */
export function partValue(block: JsonObject, part: Part): JsonObject {
  return part.scope ? asObject(block[part.scope]) : block;
}

/** The block with a part's new value merged in: the part's keys are replaced, every other key stays as it was. */
export function mergePart(block: JsonObject, part: Part, next: JsonObject): JsonObject {
  const base = partValue(block, part);
  const kept = Object.entries(base).filter(([k]) => !part.keys.includes(k));
  const added = Object.entries(next).filter(([k]) => part.keys.includes(k));
  const merged = Object.fromEntries([...kept, ...added]) as JsonObject;
  return part.scope ? { ...block, [part.scope]: merged } : merged;
}

/** The server's errors of one block (`parameters.types[2].entitlement.quantity` for block 2, scope `entitlement` → `quantity`). */
export function blockErrors(errors: Record<string, string>, index: number, scope: '' | typeof ENTITLEMENT = ''): Record<string, string> {
  const prefixes = [`parameters.types[${index}].${scope ? `${scope}.` : ''}`, `types[${index}].${scope ? `${scope}.` : ''}`];
  const out: Record<string, string> = {};
  for (const [key, text] of Object.entries(errors)) {
    const prefix = prefixes.find((p) => key.startsWith(p));
    if (prefix) out[key.slice(prefix.length)] = text;
  }
  return out;
}

/** The error of the block's own leave type field (`parameters.types[2].leaveType`). */
export const typeError = (errors: Record<string, string>, index: number): string | undefined => errors[`parameters.types[${index}].${TYPE_KEY}`] ?? errors[`types[${index}].${TYPE_KEY}`];

/** Errors of the whole list (`parameters.types`) or of the parameters as a whole. */
export const listError = (errors: Record<string, string>): string | undefined => errors['parameters.types'] ?? errors.types ?? errors.parameters;

/** The leave types a block list names, by code (case as written). */
export function listedCodes(parameters: JsonObject): string[] {
  const types = parameters.types;
  return Array.isArray(types) ? types.map((b) => asObject(b)[TYPE_KEY]).filter((c): c is string => typeof c === 'string') : [];
}

export function blocksOf(parameters: JsonObject): JsonObject[] {
  const types = parameters.types;
  return Array.isArray(types) ? types.map(asObject) : [];
}

/** A block for a leave type just added: the code only. The entitlement is left empty: the server refuses the save until its model is chosen, and no number is filled in. */
export const newBlock = (code: string): JsonObject => ({ [TYPE_KEY]: code, [ENTITLEMENT]: {} });
