// Custom fields on forms and lists (slice S1.15b Build item 8; FR-KRN-MET-02, FR-KRN-MET-05, S1.15 rulings Q5 and Q6): the
// definitions a person may see for a table come from `GET /api/kernel/custom-fields/{entityType}` (hidden fields are left out
// by the server), each with its access for the caller (editable, read-only or masked). The form shows them in its custom section
// and sends `custom` with the keys of the editable fields only; the server checks type, required, unique and validation again
// and answers `custom.<key>` field errors. The list can show them as columns the person chooses.
import { useMemo, type ReactNode } from 'react';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { CheckboxField, DateField, NumberField, SelectField, SwitchField, TextAreaField, TextField, type Option } from '@erp/ui';
import { formatDate, formatDecimal, formatInteger, registerMessages, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import en from '@erp/i18n/messages/config.en.json';
import ur from '@erp/i18n/messages/config.ur.json';
import { queryString, useEngineApi } from './data';
import { nameIn } from './values';
import type { FormValues } from './validation';

// The texts of the custom section live in the lazily registered catalogue (R-213), registered when the engine loads.
registerMessages({ en, ur });

export type CustomDataType = 'text' | 'long_text' | 'number' | 'decimal' | 'date' | 'bool' | 'choice' | 'multi_choice' | 'user' | 'employee' | 'file';

export interface CustomFormField {
  fieldKey: string;
  dataType: CustomDataType;
  label: Record<string, string>;
  help: Record<string, string> | null;
  validation: { minLength?: number | null; maxLength?: number | null; pattern?: string | null; min?: number | string | null; max?: number | string | null; minDate?: string | null; maxDate?: string | null; maxItems?: number | null };
  defaultValue: unknown;
  isRequired: boolean;
  isUnique: boolean;
  section: string | null;
  seq: number;
  retired: boolean;
  access: 'editable' | 'readonly' | 'masked';
  options: { code: string; label: Record<string, string> }[];
}

export const CUSTOM_PREFIX = 'custom.';

/**
 * The custom field definitions of a table for the caller (null: the table has none or the caller may not read them: the
 * form then shows no custom section). The answer is cached with the table's other engine queries.
 */
export function useCustomFields(table: string, companyId: string | null, enabled: boolean): UseQueryResult<CustomFormField[] | null> {
  const api = useEngineApi();
  return useQuery({
    queryKey: ['engine', table, 'custom-fields', companyId],
    enabled,
    retry: false,
    staleTime: 30_000,
    queryFn: async ({ signal }) => {
      try {
        const text = await api.send('GET', `/api/kernel/custom-fields/${encodeURIComponent(table)}${queryString({ companyId })}`, undefined, signal);
        return (JSON.parse(text) as { items: CustomFormField[] }).items;
      } catch {
        // No right to read the definitions, or the server has none for this table: the form has no custom section.
        return null;
      }
    },
  });
}

/** Whether the person can type into the field: active, editable, and not a type that needs a picker. */
export const isEditableCustom = (field: CustomFormField): boolean => field.access === 'editable' && !field.retired;

const hasValue = (value: unknown): boolean => value !== undefined && value !== null && value !== '' && !(Array.isArray(value) && value.length === 0);

/** The fields a form shows: active ones, and retired ones only while the record holds a value (shown read-only). */
export function shownCustom(fields: readonly CustomFormField[], stored: Record<string, unknown>): CustomFormField[] {
  return fields.filter((f) => !f.retired || hasValue(stored[f.fieldKey]));
}

/** The values a new record starts with: each active editable field's default. */
export function customDefaults(fields: readonly CustomFormField[]): Record<string, unknown> {
  return Object.fromEntries(fields.filter((f) => isEditableCustom(f) && hasValue(f.defaultValue)).map((f) => [f.fieldKey, f.defaultValue]));
}

/** The `custom` object to send: only the editable fields with a value (an emptied field is left out, which removes it). */
export function customBody(fields: readonly CustomFormField[], values: Record<string, unknown>): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  for (const field of fields) {
    if (!isEditableCustom(field)) continue;
    const value = values[field.fieldKey];
    if (typeof value === 'string') {
      if (value.trim() !== '') body[field.fieldKey] = value.trim();
    } else if (hasValue(value)) {
      body[field.fieldKey] = value;
    }
  }
  return body;
}

/** The client's own check before sending: a required editable field must have a value (the server checks everything else). */
export function customProblems(fields: readonly CustomFormField[], values: Record<string, unknown>): Record<string, string> {
  const problems: Record<string, string> = {};
  for (const field of fields) {
    if (isEditableCustom(field) && field.isRequired && field.dataType !== 'bool' && !hasValue(typeof values[field.fieldKey] === 'string' ? (values[field.fieldKey] as string).trim() : values[field.fieldKey])) {
      problems[field.fieldKey] = 'errors.custom.required';
    }
  }
  return problems;
}

/** Text for an error key the server gave for a custom value (`errors.custom.<name>`): the form's own wording, else null. */
export function customErrorKey(serverKey: string): MessageKey | null {
  const name = serverKey.startsWith('errors.custom.') ? serverKey.slice('errors.custom.'.length) : null;
  return name && CUSTOM_ERRORS.includes(name) ? (`custom.error.${name}` as MessageKey) : null;
}

const CUSTOM_ERRORS = ['required', 'invalid_type', 'too_long', 'too_short', 'too_small', 'too_large', 'pattern', 'not_in_list', 'duplicate', 'unknown_field', 'field_retired', 'unknown_reference', 'invalid_date'];

const labelOf = (names: Record<string, string> | null | undefined, locale: string): string => nameIn(names, locale);

/** A stored custom value as text for lists and read-only fields. */
export function CustomValue({ field, value }: { field: CustomFormField; value: unknown }): ReactNode {
  const { t } = useText();
  const { locale: uiLocale, numbers } = useLocaleSettings();
  const locale = uiLocale === 'ur' ? 'ur' : 'en';
  if (value === undefined || value === null || value === '') return <span className="erp-muted" aria-label={t('engine.value.none')}>–</span>;
  if (typeof value === 'string' && /^\*{2,}/.test(value)) return <span className="erp-code"><bdi>{value}</bdi></span>;
  switch (field.dataType) {
    case 'bool':
      return <span>{value === true ? t('engine.value.yes') : t('engine.value.no')}</span>;
    case 'number':
      return <span>{formatInteger(Number(value), numbers)}</span>;
    case 'decimal':
      return <span>{formatDecimal(String(value), numbers)}</span>;
    case 'date':
      return <span>{formatDate(String(value), locale, 'UTC', numbers)}</span>;
    case 'choice': {
      const option = field.options.find((o) => o.code === value);
      return <bdi>{option ? labelOf(option.label, uiLocale) : String(value)}</bdi>;
    }
    case 'multi_choice':
      return <bdi>{(Array.isArray(value) ? value : []).map((v) => { const o = field.options.find((x) => x.code === v); return o ? labelOf(o.label, uiLocale) : String(v); }).join(', ')}</bdi>;
    case 'user':
    case 'employee':
      return <span className="erp-code"><bdi>{String(value)}</bdi></span>;
    default:
      return <bdi>{String(value)}</bdi>;
  }
}

/** A text for a field problem key: the form's wording, else the server's own text, else a general one. */
export function useCustomProblemText(): (key: string | undefined) => string | null {
  const { t, server } = useText();
  return (key) => {
    if (!key) return null;
    const own = customErrorKey(key);
    return own ? t(own) : (server(key) ?? t('custom.error.generic'));
  };
}

export interface CustomSectionProps {
  fields: readonly CustomFormField[];
  /** The values as the form holds them (key → value). */
  values: Record<string, unknown>;
  /** The record's stored values, to know which retired fields still hold a value. */
  stored: Record<string, unknown>;
  /** Field problems by key (the text is made here from the key). */
  problems: Record<string, string>;
  /** False when the whole form is read-only (no right to change, read-only licence). */
  editable: boolean;
  onChange: (key: string, value: unknown) => void;
}

/** The "Additional fields" section of a form: the fields grouped by section name, ordered as the customer arranged them. */
export function CustomSection({ fields, values, stored, problems, editable, onChange }: CustomSectionProps) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const shown = shownCustom(fields, stored);
  const groups = useMemo(() => {
    const map = new Map<string, CustomFormField[]>();
    for (const field of shown) {
      const key = field.section ?? '';
      map.set(key, [...(map.get(key) ?? []), field]);
    }
    return [...map.entries()];
  }, [shown]);
  if (shown.length === 0) return null;
  return (
    <section className="erp-custom-section" aria-labelledby="erp-custom-title" data-testid="custom-section">
      <h2 id="erp-custom-title" className="erp-h3">{t('custom.section.title')}</h2>
      {groups.map(([section, list]) => {
        const body = list.map((field) => (
          <div key={field.fieldKey} id={`field-${CUSTOM_PREFIX}${field.fieldKey}`} className="erp-form-field" data-kind={`custom-${field.dataType}`}>
            <CustomInput field={field} value={values[field.fieldKey]} error={problems[field.fieldKey] ?? null} readOnly={!editable || !isEditableCustom(field)}
              onChange={(v) => onChange(field.fieldKey, v)} locale={locale} />
          </div>
        ));
        return section === '' ? <div key="default" className="erp-form">{body}</div> : (
          <fieldset key={section} className="erp-fieldset erp-custom-group">
            <legend className="erp-label">{section}</legend>
            <div className="erp-form">{body}</div>
          </fieldset>
        );
      })}
    </section>
  );
}

function CustomInput({ field, value, error, readOnly, onChange, locale }: { field: CustomFormField; value: unknown; error: string | null; readOnly: boolean; onChange: (value: unknown) => void; locale: string }) {
  const { t } = useText();
  const problemText = useCustomProblemText();
  const name = labelOf(field.label, locale) || field.fieldKey;
  const help = field.help ? labelOf(field.help, locale) : undefined;
  const message = error ? problemText(error) : null;
  const required = field.isRequired && !readOnly;
  const common = { label: name, isRequired: required, error: message, ...(help ? { help } : {}) };
  // Retired fields, masked values and types that need a picker are shown, not typed (a picker comes with the people lookup, S1.20).
  if (readOnly || field.dataType === 'user' || field.dataType === 'employee' || field.dataType === 'file') {
    return (
      <div className="erp-field erp-field-readonly" role="group" aria-label={name}>
        <span className="erp-label">{name}{field.retired && <span className="erp-muted"> ({t('custom.retired')})</span>}</span>
        <span><CustomValue field={field} value={value} /></span>
        {help && <span className="erp-help">{help}</span>}
        {message && <span className="erp-field-error" role="alert">{message}</span>}
      </div>
    );
  }
  switch (field.dataType) {
    case 'bool':
      return <SwitchField label={name} isSelected={value === true} onChange={onChange} />;
    case 'long_text':
      return <TextAreaField {...common} value={typeof value === 'string' ? value : ''} onChange={onChange} {...(field.validation.maxLength ? { maxLength: field.validation.maxLength } : {})} />;
    case 'number':
      return <NumberField {...common} {...(typeof value === 'number' ? { value } : {})} onChange={(n) => onChange(Number.isNaN(n) ? null : n)} />;
    case 'decimal':
      return <TextField {...common} inputMode="numeric" isCode value={value === null || value === undefined ? '' : String(value)} onChange={onChange} />;
    case 'date':
      return <DateField {...common} value={typeof value === 'string' && value ? safeDate(value) : null} onChange={(d) => onChange(d ? d.toString() : null)} />;
    case 'choice': {
      const options: Option[] = [
        ...(field.isRequired ? [] : [{ id: NONE, label: t('custom.choice.none') }]),
        ...field.options.map((o) => ({ id: o.code, label: labelOf(o.label, locale) || o.code })),
      ];
      return <SelectField {...common} options={options} value={typeof value === 'string' && value ? value : (field.isRequired ? null : NONE)} onChange={(id) => onChange(id === NONE ? null : id)} />;
    }
    case 'multi_choice': {
      const chosen = Array.isArray(value) ? value.map(String) : [];
      return (
        <fieldset className="erp-fieldset" aria-describedby={message ? `custom-error-${field.fieldKey}` : undefined}>
          <legend className="erp-label">{name}{required && <span className="erp-required" aria-hidden="true" title={t('common.required')}>*</span>}</legend>
          {field.options.map((o) => (
            <CheckboxField key={o.code} label={labelOf(o.label, locale) || o.code} isSelected={chosen.includes(o.code)}
              onChange={(on) => onChange(on ? [...chosen, o.code] : chosen.filter((c) => c !== o.code))} />
          ))}
          {help && <span className="erp-help">{help}</span>}
          {message && <span id={`custom-error-${field.fieldKey}`} className="erp-field-error" role="alert">{message}</span>}
        </fieldset>
      );
    }
    default:
      return <TextField {...common} value={typeof value === 'string' ? value : ''} onChange={onChange} {...(field.validation.maxLength ? { maxLength: field.validation.maxLength } : {})} />;
  }
}

const NONE = '__none__';

function safeDate(value: string) {
  try {
    return parseDate(value.slice(0, 10));
  } catch {
    return null;
  }
}

/** The shape of a record's `custom` object from a row (an object, else empty). */
export function customOf(row: FormValues | null | undefined): Record<string, unknown> {
  const value = row?.custom;
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}
