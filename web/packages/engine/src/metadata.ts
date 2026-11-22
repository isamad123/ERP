// The generated metadata of a table (tools/Erp.CodeGen, web/generated/metadata/*.json) and its validation schema
// (web/generated/validation/*.json), as the list and form engine reads them (slice S1.12c, Build items 14, 17, 18).

export type FieldKind =
  | 'Uuid' | 'Code' | 'Text' | 'I18n' | 'Date' | 'Timestamp' | 'Int4' | 'BigInteger' | 'Bool' | 'Money' | 'Qty' | 'Rate'
  | 'Pct' | 'Json' | 'Encrypted' | 'Hmac' | 'Enum' | 'FixedText' | 'Numeric' | 'TextArray' | 'UuidArray' | 'Bytea' | 'Time' | 'Inet';

export interface FieldOption {
  value: string;
  labelKey: string;
}

export interface FieldMetadata {
  column: string;
  field: string;
  kind: FieldKind;
  required: boolean;
  readOnly: boolean;
  system: boolean;
  filter: boolean;
  labelKey: string;
  references?: string;
  options?: FieldOption[];
  maxLength?: number;
  sortable?: boolean;
  createOnly?: boolean;
  fieldClass?: string;
}

export interface TableMetadata {
  table: string;
  resource: string;
  route: string;
  labelKey: string;
  requirements: string[];
  actions: { action: string; permission: string }[];
  companyScoped: boolean;
  effectiveDated: boolean;
  softDelete: boolean;
  appendOnly: boolean;
  listFields: string[];
  /** Fields the list search looks in (code and name); empty: the list has no search. */
  search?: string[];
  /** Database constraint name → the field it is about, to show a refused save next to the field. */
  constraints?: Record<string, string>;
  fields: FieldMetadata[];
}

/** A subset of JSON Schema 2020-12 (what the generator writes). */
export interface PropertySchema {
  type?: 'string' | 'integer' | 'boolean' | 'object' | 'array';
  format?: string;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  enum?: string[];
  minimum?: number;
  maximum?: number;
  minProperties?: number;
  propertyNames?: { enum: string[] };
  additionalProperties?: PropertySchema;
  items?: PropertySchema;
  nullable?: boolean;
  readOnly?: boolean;
}

export interface ValidationSchema {
  title: string;
  required: string[];
  properties: Record<string, PropertySchema>;
}

/** Decimal kinds: kept as exact text from the server's JSON to the screen and back (never a floating-point number). */
export const DECIMAL_KINDS: ReadonlySet<FieldKind> = new Set(['Money', 'Qty', 'Rate', 'Pct', 'Numeric']);

export function fieldOf(metadata: TableMetadata, name: string): FieldMetadata | undefined {
  return metadata.fields.find((f) => f.field === name);
}

export function permissionOf(metadata: TableMetadata, action: string): string | null {
  return metadata.actions.find((a) => a.action === action)?.permission ?? null;
}

/** The fields a form shows: everything but the system columns, in the table's order (or the screen's own order). */
export function formFields(metadata: TableMetadata, order?: readonly string[]): FieldMetadata[] {
  const shown = metadata.fields.filter((f) => !f.system && f.kind !== 'Encrypted' && f.kind !== 'Hmac');
  if (!order) return shown;
  return order.map((name) => shown.find((f) => f.field === name)).filter((f): f is FieldMetadata => !!f);
}

export function decimalFields(metadata: TableMetadata): ReadonlySet<string> {
  return new Set(metadata.fields.filter((f) => DECIMAL_KINDS.has(f.kind)).map((f) => f.field));
}
