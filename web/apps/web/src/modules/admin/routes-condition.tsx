// The condition builder of a route step (slice S1.15c Build item 10; FR-KRN-WFL-02, -03; S1.15 ruling Q9): tests on the document type's declared fields with the
// operators the engine supports, all-of / any-of groups to the engine's depth, and a "between" shortcut. Each condition reads back as a plain sentence.
import { Plus, Trash2 } from 'lucide-react';
import { parseDate } from '@internationalized/date';
import { Button, CheckboxField, DateField, IconButton, SelectField, TextField, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import type { ConditionJson, RouteField } from '../../api/routes';
import { nameOf } from '../../api/workflow';
import { useShell, companyName } from '../../shell/context';
import { addTo, between, groupKind, isBetween, isLeaf, itemsOf, MAX_GROUP_DEPTH, newTest, operatorsFor, retarget, updateAt, type Operator } from './routes-model';
import './routes-messages';

interface Props { condition: ConditionJson; fields: RouteField[]; readOnly: boolean; onChange: (next: ConditionJson | null) => void; label: string }

/** The editor of one step's condition. A removed last test removes the condition (the step then always applies). */
export function ConditionEditor({ condition, fields, readOnly, onChange, label }: Props) {
  const change = (path: number[], next: (c: ConditionJson) => ConditionJson | null) => {
    const updated = updateAt(condition, path, next);
    onChange(updated && (groupKind(updated) ? itemsOf(updated).length > 0 : true) ? updated : null);
  };
  return <GroupView group={condition} path={[]} fields={fields} readOnly={readOnly} change={change} add={(path, item) => onChange(addTo(condition, path, item))} label={label} />;
}

interface GroupProps {
  group: ConditionJson;
  path: number[];
  fields: RouteField[];
  readOnly: boolean;
  change: (path: number[], next: (c: ConditionJson) => ConditionJson | null) => void;
  add: (path: number[], item: ConditionJson) => void;
  label: string;
}

function GroupView({ group, path, fields, readOnly, change, add, label }: GroupProps) {
  const { t } = useText();
  const kind = groupKind(group) ?? 'all';
  const depth = path.length + 1;
  const kinds: Option[] = [{ id: 'all', label: t('routes.when.all') }, { id: 'any', label: t('routes.when.any') }];
  const rangeFields = fields.filter((f) => ['number', 'money', 'date'].includes(f.type));
  return (
    <div className="erp-route-group" role="group" aria-label={label}>
      <SelectField label={t('routes.when.title')} options={kinds} value={kind} isDisabled={readOnly}
        onChange={(next) => change(path, (g) => ({ [next]: itemsOf(g) }))} />
      <ul className="erp-route-items">
        {itemsOf(group).map((item, index) => {
          const itemPath = [...path, index];
          const key = `${index}:${item.field ?? kind}`;
          return (
            <li key={key} className="erp-route-item">
              {isBetween(item)
                ? <BetweenView test={item} path={itemPath} fields={fields} readOnly={readOnly} change={change} />
                : isLeaf(item)
                  ? <TestView test={item} path={itemPath} fields={fields} readOnly={readOnly} change={change} />
                  : <GroupView group={item} path={itemPath} fields={fields} readOnly={readOnly} change={change} add={add} label={label} />}
              {!readOnly && <IconButton label={t('routes.when.removeItem')} icon={<Trash2 size={16} aria-hidden="true" />} onPress={() => change(itemPath, () => null)} />}
            </li>
          );
        })}
      </ul>
      {!readOnly && fields.length > 0 && (
        <div className="erp-route-actions">
          <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => add(path, newTest(fields))}>{t('routes.when.addTest')}</Button>
          {rangeFields[0] && <Button variant="quiet" onPress={() => add(path, between(rangeFields[0] as RouteField))}>{t('routes.when.addBetween')}</Button>}
          {depth <= MAX_GROUP_DEPTH - 1 && <Button variant="quiet" onPress={() => add(path, { any: [newTest(fields)] })}>{t('routes.when.addGroup')}</Button>}
        </div>
      )}
    </div>
  );
}

function fieldOptions(fields: RouteField[], locale: string): Option[] {
  return fields.map((f) => ({ id: f.name, label: nameOf(f.label, locale) || f.name, detail: f.name }));
}

function TestView({ test, path, fields, readOnly, change }: { test: ConditionJson; path: number[]; fields: RouteField[]; readOnly: boolean; change: GroupProps['change'] }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const field = fields.find((f) => f.name === test.field);
  const ops = (field ? operatorsFor(field.type) : []).map((op) => ({ id: op, label: t(`routes.op.${op}` as MessageKey) }));
  const op = (test.op ?? 'eq') as Operator;
  return (
    <div className="erp-route-test">
      <SelectField label={t('routes.when.field')} options={fieldOptions(fields, locale)} value={test.field ?? ''} isDisabled={readOnly} onChange={(name) => change(path, (c) => retarget(c, fields, { field: name }))} />
      <SelectField label={t('routes.when.op')} options={ops} value={op} isDisabled={readOnly} onChange={(next) => change(path, (c) => retarget(c, fields, { op: next as Operator }))} />
      {field && op !== 'empty' && op !== 'not_empty' && <ValueInput field={field} op={op} value={test.value} readOnly={readOnly} label={t('routes.when.value')} onChange={(value) => change(path, (c) => ({ ...c, value }))} />}
    </div>
  );
}

function BetweenView({ test, path, fields, readOnly, change }: { test: ConditionJson; path: number[]; fields: RouteField[]; readOnly: boolean; change: GroupProps['change'] }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const [low, high] = test.all ?? [];
  const field = fields.find((f) => f.name === low?.field);
  const ranged = fields.filter((f) => ['number', 'money', 'date'].includes(f.type));
  const set = (part: 0 | 1, value: unknown) => change(path, (c) => ({ all: (c.all ?? []).map((x, i) => (i === part ? { ...x, value } : x)) }));
  return (
    <div className="erp-route-test">
      <SelectField label={t('routes.when.field')} options={fieldOptions(ranged, locale)} value={low?.field ?? ''} isDisabled={readOnly}
        onChange={(name) => change(path, (c) => ({ all: (c.all ?? []).map((x) => ({ ...x, field: name, value: '' })) }))} />
      {field && <ValueInput field={field} op="ge" value={low?.value} readOnly={readOnly} label={t('routes.when.low')} onChange={(v) => set(0, v)} />}
      {field && <ValueInput field={field} op="le" value={high?.value} readOnly={readOnly} label={t('routes.when.high')} onChange={(v) => set(1, v)} />}
    </div>
  );
}

/** The input of a test's value by the field's type; lists (is one of) are typed with commas, or ticked for a choice. */
function ValueInput({ field, op, value, readOnly, label, onChange }: { field: RouteField; op: Operator; value: unknown; readOnly: boolean; label: string; onChange: (value: unknown) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const list = op === 'in' || op === 'not_in';
  if (list && field.type === 'choice') {
    const chosen = Array.isArray(value) ? (value as string[]) : [];
    return (
      <fieldset className="erp-fieldset">
        <legend className="erp-label">{label}</legend>
        {field.choices.map((c) => (
          <CheckboxField key={c} label={c} isSelected={chosen.includes(c)} isDisabled={readOnly} onChange={(on) => onChange(on ? [...chosen, c] : chosen.filter((x) => x !== c))} />
        ))}
      </fieldset>
    );
  }
  if (list) {
    const text = Array.isArray(value) ? value.map(String).join(', ') : '';
    return <TextField label={t('routes.when.values')} value={text} isReadOnly={readOnly} onChange={(v) => onChange(v.split(',').map((x) => x.trim()).filter((x) => x.length > 0))} />;
  }
  switch (field.type) {
    case 'bool':
      return <SelectField label={label} options={[{ id: 'true', label: t('routes.yes') }, { id: 'false', label: t('routes.no') }]} value={String(value !== false)} isDisabled={readOnly} onChange={(v) => onChange(v === 'true')} />;
    case 'choice':
      return <SelectField label={label} options={field.choices.map((c) => ({ id: c, label: c }))} value={typeof value === 'string' ? value : ''} isDisabled={readOnly} onChange={onChange} />;
    case 'company':
      return <SelectField label={label} options={me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))} value={typeof value === 'string' ? value : ''} isDisabled={readOnly} onChange={onChange} />;
    case 'date':
      return <DateField label={label} isReadOnly={readOnly} value={typeof value === 'string' && value ? parseDate(value) : null} onChange={(v) => onChange(v ? parseDate(v.toString()).toString() : '')} />;
    case 'number':
    case 'money':
      return <TextField label={label} isCode inputMode="numeric" value={value === undefined || value === null ? '' : String(value)} isReadOnly={readOnly} onChange={(v) => onChange(v.trim())} />;
    default:
      return <TextField label={label} value={value === undefined || value === null ? '' : String(value)} isReadOnly={readOnly} isCode={field.type !== 'text'} onChange={onChange} />;
  }
}
