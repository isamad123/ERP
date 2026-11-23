// The parameters of a policy version as a form made from the type's JSON Schema (slice S1.23a Build items 2 and 6; ruling Q2): whole
// numbers, decimals, texts, choices, yes/no and lists of rows. Labels are the server texts named by the schema's `title`; the server
// checks the values against the same schema on every save and its refusals are shown beside the fields.
import { Plus, Trash2 } from 'lucide-react';
import { Button, IconButton, SelectField, SwitchField, TextField, type Option } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import type { Json, JsonObject, ParamSchema } from '../../api/policies';
import { defaultFor, errorAt, fieldsOf, parseNumber, showValue } from './policies-model';
import './policies-messages';

/** Choice values with a web text (the server's schema has none for its enum values yet). */
const KNOWN_CHOICES = ['line_manager_and_hr', 'hr'] as const;

interface Props {
  schema: ParamSchema;
  value: JsonObject;
  onChange?: (value: JsonObject) => void;
  /** Server field errors by path (`parameters.months` or `months`), already in words. */
  errors?: Record<string, string>;
  readOnly?: boolean;
}

export function ParameterForm({ schema, value, onChange, errors = {}, readOnly = false }: Props) {
  return (
    <div className="erp-stack">
      {fieldsOf(schema).map((f) => (
        <ParameterField key={f.key} path={f.key} name={f.key} schema={f.schema} required={f.required} value={value[f.key]} errors={errors} readOnly={readOnly || !onChange}
          onChange={(v) => {
            // The key keeps its place, so an unchanged form compares equal to the stored parameters.
            const kept = Object.entries(value).filter(([k]) => k !== f.key || v !== undefined).map(([k, old]) => [k, k === f.key ? v : old] as const);
            onChange?.(Object.fromEntries(f.key in value || v === undefined ? kept : [...kept, [f.key, v]]) as JsonObject);
          }} />
      ))}
    </div>
  );
}

function useLabel() {
  const { server } = useText();
  return (schema: ParamSchema, name: string) => (schema.title ? server(schema.title) : null) ?? name;
}

function ParameterField({ path, name, schema, required, value, onChange, errors, readOnly }: {
  path: string; name: string; schema: ParamSchema; required: boolean; value: Json | undefined; onChange: (v: Json | undefined) => void; errors: Record<string, string>; readOnly: boolean;
}) {
  const { t, server } = useText();
  const label = useLabel()(schema, name);
  const error = errorAt(errors, path) ?? null;
  if (schema.enum) {
    const options: Option[] = [
      ...(required ? [] : [{ id: '', label: t('pol.form.none') }]),
      ...schema.enum.map((v) => {
        const text = showValue(v);
        const known = (KNOWN_CHOICES as readonly string[]).includes(text) ? t(`pol.choice.${text}` as MessageKey) : null;
        return { id: text, label: (schema.title ? server(`${schema.title}.${text}`) : null) ?? known ?? text };
      }),
    ];
    return (
      <SelectField label={label} options={options} value={value === undefined ? '' : showValue(value)} isRequired={required} isDisabled={readOnly} error={error}
        onChange={(id) => onChange(id === '' ? undefined : (schema.enum ?? []).find((v) => showValue(v) === id) ?? id)} />
    );
  }
  switch (schema.type) {
    case 'boolean':
      return <SwitchField label={label} isSelected={value === true} onChange={(v) => onChange(v)} isDisabled={readOnly} />;
    case 'integer':
    case 'number':
      return <NumberText label={label} integer={schema.type === 'integer'} value={value} required={required} readOnly={readOnly} error={error} onChange={onChange}
        help={schema.minimum !== undefined || schema.maximum !== undefined ? t('pol.form.range', { min: schema.minimum ?? '–', max: schema.maximum ?? '–' }) : undefined} />;
    case 'array':
      return <RowList path={path} label={label} schema={schema} value={Array.isArray(value) ? value : []} onChange={onChange} errors={errors} readOnly={readOnly} error={error} />;
    case 'object':
      return (
        <fieldset className="erp-fieldset">
          <legend className="erp-label">{label}</legend>
          <ParameterForm schema={schema} value={value && typeof value === 'object' && !Array.isArray(value) ? value : {}} errors={Object.fromEntries(Object.entries(errors).filter(([k]) => k.includes(`${path}.`)).map(([k, v]) => [k.replace(`parameters.${path}.`, '').replace(`${path}.`, ''), v]))}
            readOnly={readOnly} onChange={(v) => onChange(v)} />
        </fieldset>
      );
    default:
      return <TextField label={label} value={typeof value === 'string' ? value : ''} isRequired={required} isReadOnly={readOnly} error={error} onChange={(v) => onChange(v === '' && !required ? undefined : v)}
        {...(schema.maxLength !== undefined ? { maxLength: schema.maxLength } : {})} />;
  }
}

/** A number typed as text so that it stays exact; a value that is not a number is kept as text and refused by the server's check. */
function NumberText({ label, integer, value, required, readOnly, error, onChange, help }: {
  label: string; integer: boolean; value: Json | undefined; required: boolean; readOnly: boolean; error: string | null; onChange: (v: Json | undefined) => void; help: string | undefined;
}) {
  const { t } = useText();
  const text = value === undefined || value === null ? '' : String(value);
  const local = text !== '' && parseNumber(text, integer) === null ? t(integer ? 'pol.form.wholeNumber' : 'pol.form.number') : null;
  return (
    <TextField label={label} value={text} inputMode={integer ? 'numeric' : 'text'} isRequired={required} isReadOnly={readOnly} error={error ?? local} help={help}
      onChange={(v) => {
        if (v.trim() === '') { onChange(undefined); return; }
        const n = parseNumber(v, integer);
        onChange(n === null ? v : n);
      }} />
  );
}

function RowList({ path, label, schema, value, onChange, errors, readOnly, error }: {
  path: string; label: string; schema: ParamSchema; value: Json[]; onChange: (v: Json[]) => void; errors: Record<string, string>; readOnly: boolean; error: string | null;
}) {
  const { t } = useText();
  const item = schema.items ?? { type: 'string' };
  const full = schema.maxItems !== undefined && value.length >= schema.maxItems;
  return (
    <fieldset className="erp-fieldset erp-pol-rows">
      <legend className="erp-label">{label}</legend>
      {error && <p className="erp-field-error" role="alert">{error}</p>}
      {value.length === 0 && <p className="erp-small erp-muted">{t('pol.form.rows.none')}</p>}
      <ol className="erp-pol-row-list">
        {value.map((row, i) => (
          // Rows have no identity of their own: the index is their position in the list the server checks.
          <li key={i} className="erp-pol-row">
            <span className="erp-small erp-muted">{t('pol.form.row', { n: i + 1 })}</span>
            {item.type === 'object'
              ? <ParameterForm schema={item} value={row && typeof row === 'object' && !Array.isArray(row) ? row : {}} readOnly={readOnly}
                  errors={Object.fromEntries(Object.entries(errors).filter(([k]) => k.includes(`${path}[${i}].`)).map(([k, v]) => [k.replace(`parameters.${path}[${i}].`, '').replace(`${path}[${i}].`, ''), v]))}
                  onChange={(v) => onChange(value.map((r, j) => (j === i ? v : r)))} />
              : <ParameterField path={`${path}[${i}]`} name={t('pol.form.row', { n: i + 1 })} schema={item} required value={row} errors={errors} readOnly={readOnly}
                  onChange={(v) => onChange(value.map((r, j) => (j === i ? (v ?? null) : r)))} />}
            {!readOnly && <IconButton label={t('pol.form.row.remove', { n: i + 1 })} icon={<Trash2 size={16} aria-hidden="true" />} onPress={() => onChange(value.filter((_, j) => j !== i))} />}
          </li>
        ))}
      </ol>
      {!readOnly && !full && <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => onChange([...value, defaultFor(item)])}>{t('pol.form.row.add')}</Button>}
    </fieldset>
  );
}
