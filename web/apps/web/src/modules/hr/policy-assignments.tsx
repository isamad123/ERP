// Policy assignments (slice S1.23a Build items 3, 5 and 6; FR-HRM-POL-02, rulings Q4 and Q5): the set of one policy type in one company as
// of a date, each rule in plain sentences, tried top to bottom (the first match wins, or every match for a type of which several may apply);
// the editor of a new set (policies, order, conditions on location, branch, grade, employee type, unit with its sub-units and custom fields
// of the employment, joined by "and"; "or" is another assignment), the check (refusals per assignment, and the current employees no rule
// would cover) and activation from a date. The server checks the whole set again on activation.
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { Banner, Button, CheckboxField, EmptyState, ErrorPanel, IconButton, PageHeader, SelectField, Skeleton, StatusChip, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, requestJson } from '../../api/client';
import {
  policiesApi, type AssignmentSet, type Attribute, type Json, type Operator, OPERATORS, type PolicyRow, type PolicyType, type RuleNames, type SetCheck,
} from '../../api/policies';
import { nameOf, type Names } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { useRefused } from '../admin/config-parts';
import { CompanyPicker, DateInput, Sections, attributeKey, useCompany, useConditionText, useDay, useParamsState, useTypes } from './policies-common';
import { addAssignment, blankCondition, draftOf, errorsOf, move, parseNumber, toInputs, type DraftAssignment, type DraftCondition } from './policies-model';
import './policies-messages';
import '../admin/users.css';
import '../admin/configure.css';
import './policies.css';

export default function AssignmentsPage() {
  const { t } = useText();
  useDocumentTitle(t('pol.assign.title'));
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const companyId = useCompany();
  const { params, patch } = useParamsState();
  const types = useTypes();
  const typeCode = params.get('type') ?? types.data?.items[0]?.code ?? '';
  const asOf = params.get('asOf');
  const type = types.data?.items.find((x) => x.code === typeCode);
  const set = useQuery({ queryKey: ['hrm', 'policy-set', companyId, typeCode, asOf ?? ''], queryFn: () => policiesApi.set(companyId, typeCode, asOf), enabled: !!companyId && !!typeCode, refetchOnMount: 'always' });
  const [editing, setEditing] = useState(false);
  const canManage = writable && can('hrm.policies.manage', companyId);
  const typeOptions: Option[] = (types.data?.items ?? []).map((x) => ({ id: x.code, label: nameOf(x.name, locale) || x.code }));
  const error = types.error ?? set.error;
  return (
    <div className="erp-page">
      <PageHeader title={t('pol.assign.title')} description={t('pol.assign.description')} />
      <Sections />
      <div className="erp-pol-filters">
        <CompanyPicker onChange={() => setEditing(false)} />
        {typeOptions.length > 0 && <SelectField label={t('pol.type')} options={typeOptions} value={typeCode} onChange={(id) => { patch({ type: id }); setEditing(false); }} />}
        <DateInput label={t('pol.assign.asOf')} help={t('pol.assign.asOf.help')} value={asOf} onChange={(d) => patch({ asOf: d })} />
      </div>
      {(types.isPending || (set.isPending && !!typeCode)) && !error && <Skeleton lines={6} />}
      {error && (
        <ErrorPanel message={error instanceof ApiProblem && error.status === 403 ? t('pol.denied') : t('pol.loadError')} reference={error instanceof ApiProblem ? error.reference : null}
          action={<Button variant="primary" onPress={() => { void types.refetch(); void set.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {types.isSuccess && typeOptions.length === 0 && <EmptyState title={t('pol.type.none')} />}
      {type && set.isSuccess && (editing
        ? <SetEditor key={`${companyId}:${typeCode}`} type={type} companyId={companyId} current={set.data} onClose={() => setEditing(false)} />
        : <SetView type={type} set={set.data} onEdit={canManage ? () => setEditing(true) : undefined} />)}
    </div>
  );
}

function SetView({ type, set, onEdit }: { type: PolicyType; set: AssignmentSet; onEdit: (() => void) | undefined }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const day = useDay();
  const sentence = useConditionText();
  return (
    <section className="erp-stack" aria-labelledby="pol-set">
      <h2 id="pol-set" className="erp-h3">{set.validFrom ? t('pol.assign.set.from', { date: day(set.validFrom) }) : t('pol.assign.set.title')}</h2>
      <p className="erp-small erp-muted">{t(type.cardinality === 'many' ? 'pol.assign.many' : 'pol.assign.one')}</p>
      {onEdit && <div className="erp-row"><Button variant="primary" onPress={onEdit}>{t('pol.assign.edit')}</Button></div>}
      {set.assignments.length === 0 ? <EmptyState title={t('pol.assign.empty')}>{t('pol.assign.empty.body')}</EmptyState> : (
        <ol className="erp-config-list" aria-label={t('pol.assign.list')}>
          {set.assignments.map((a) => (
            <li key={a.id} className="erp-config-card">
              <span className="erp-pol-name">{t('pol.assign.order', { n: a.precedence })} · <bdi>{nameOf(a.policyName, locale) || a.policyCode}</bdi> <span className="erp-code erp-small"><bdi>{a.policyCode}</bdi></span></span>
              {(a.rule.conditions ?? []).length === 0
                ? <p className="erp-small">{t('pol.rule.everyone')}</p>
                : (
                  <ul className="erp-pol-conditions" aria-label={t('pol.rule.conditions')}>
                    {(a.rule.conditions ?? []).map((c, i) => <li key={`${c.attribute}:${c.field ?? ''}:${i}`}>{i > 0 && <span className="erp-muted">{t('pol.rule.and')} </span>}{sentence(c, set.names)}</li>)}
                  </ul>
                )}
            </li>
          ))}
        </ol>
      )}
      {set.sets.length > 0 && (
        <>
          <h3 className="erp-h3">{t('pol.assign.history')}</h3>
          <ul className="erp-config-list" aria-label={t('pol.assign.history')}>
            {set.sets.map((s) => (
              <li key={s.validFrom} className="erp-small">
                {s.validTo ? t('pol.assign.history.closed', { from: day(s.validFrom), to: day(s.validTo), count: s.count }) : t('pol.assign.history.open', { from: day(s.validFrom), count: s.count })}
                {s.inForce && <> <StatusChip status="active" label={t('pol.version.inForce')} /></>}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

/** Options of the records a rule may name in the company: grades (HR), units, branches and locations (kernel). */
function useRecords(attribute: Attribute, companyId: string) {
  const { locale } = useLocaleSettings();
  const path = attribute === 'grade' ? '/api/hrm/grades' : attribute === 'unit' ? '/api/org/org-units' : attribute === 'branch' ? '/api/org/branches' : attribute === 'location' ? '/api/org/locations' : null;
  const list = useQuery({
    queryKey: ['hrm', 'policy-records', attribute, companyId],
    queryFn: async () => (await requestJson<{ items: { id: string; code: string; nameI18n: Names }[] }>('GET', `${path ?? ''}?companyId=${encodeURIComponent(companyId)}&limit=200&sort=code`)).items,
    enabled: path !== null, staleTime: 60_000,
  });
  return useMemo(() => (list.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code })), [list.data, locale]);
}

function SetEditor({ type, companyId, current, onClose }: { type: PolicyType; companyId: string; current: AssignmentSet; onClose: () => void }) {
  const { t, server } = useText();
  const { locale } = useLocaleSettings();
  const toast = useToast();
  const refused = useRefused();
  const queryClient = useQueryClient();
  const policies = useQuery({ queryKey: ['hrm', 'policies', companyId], queryFn: () => policiesApi.list(companyId) });
  const choices = (policies.data ?? []).filter((p: PolicyRow) => p.policyType === type.code && p.isActive);
  const [draft, setDraft] = useState<DraftAssignment[]>(() => draftOf(current.assignments));
  const [from, setFrom] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [check, setCheck] = useState<SetCheck | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const body = () => ({ companyId, policyType: type.code, effectiveFrom: from, basedOn: current.latestFrom, assignments: toInputs(draft), ...(reason.trim() ? { reason: reason.trim() } : {}) });
  const update = (key: string, change: (a: DraftAssignment) => DraftAssignment) => { setDraft((d) => d.map((a) => (a.key === key ? change(a) : a))); setCheck(null); };
  const policyOptions: Option[] = choices.map((p) => ({ id: p.id, label: nameOf(p.nameI18n, locale) || p.code, detail: p.code }));
  const runCheck = async () => {
    setBusy(true);
    setSummary(null);
    try {
      const answer = await policiesApi.check(body());
      setCheck(answer);
      setErrors(answer.errors);
    } catch (error) {
      setSummary(refused(error).summary);
    } finally {
      setBusy(false);
    }
  };
  const activate = async () => {
    setBusy(true);
    setSummary(null);
    try {
      const answer = await policiesApi.activateSet(body());
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'policy-set'] });
      toast.success(t(answer.status === 'pending' ? 'pol.assign.pending' : 'pol.assign.done'));
      onClose();
    } catch (error) {
      if (error instanceof ApiProblem) setErrors(Object.fromEntries(Object.entries(error.fieldErrors).map(([k, v]) => [k, [...v]])));
      setSummary(refused(error).summary);
    } finally {
      setBusy(false);
    }
  };
  const text = (keys: string[] | undefined) => (keys && keys[0] ? server(keys[0]) ?? t('config.error.field') : null);
  return (
    <section className="erp-stack" aria-labelledby="pol-edit">
      <h2 id="pol-edit" className="erp-h3">{t('pol.assign.editor')}</h2>
      <p className="erp-small erp-muted">{t('pol.assign.editor.help')}</p>
      {summary && <Banner tone="danger">{summary}</Banner>}
      {text(errors.assignments) && <Banner tone="danger">{text(errors.assignments)}</Banner>}
      {policies.isSuccess && choices.length === 0 && <Banner tone="warning">{t('pol.assign.noPolicies')}</Banner>}
      <ol className="erp-config-list" aria-label={t('pol.assign.list')}>
        {draft.map((a, index) => {
          const own = errorsOf(errors, index);
          return (
            <li key={a.key} className="erp-config-card">
              <div className="erp-row erp-wrap">
                <span className="erp-pol-name">{t('pol.assign.order', { n: a.precedence })}</span>
                <IconButton label={t('pol.assign.up', { n: a.precedence })} icon={<ArrowUp size={16} aria-hidden="true" />} isDisabled={index === 0} onPress={() => { setDraft((d) => move(d, a.key, -1)); setCheck(null); }} />
                <IconButton label={t('pol.assign.down', { n: a.precedence })} icon={<ArrowDown size={16} aria-hidden="true" />} isDisabled={index === draft.length - 1} onPress={() => { setDraft((d) => move(d, a.key, 1)); setCheck(null); }} />
                <IconButton label={t('pol.assign.remove', { n: a.precedence })} icon={<Trash2 size={16} aria-hidden="true" />} onPress={() => { setDraft((d) => d.filter((x) => x.key !== a.key)); setCheck(null); }} />
              </div>
              {text(own.precedence) && <Banner tone="danger">{text(own.precedence)}</Banner>}
              <SelectField label={t('pol.assign.policy')} isRequired options={policyOptions} value={a.policyId} onChange={(id) => update(a.key, (x) => ({ ...x, policyId: id }))} error={text(own.policyId)} />
              <RuleEditor type={type} companyId={companyId} names={current.names} conditions={a.conditions} error={text(own.rule)}
                onChange={(conditions) => update(a.key, (x) => ({ ...x, conditions }))} />
            </li>
          );
        })}
      </ol>
      <div className="erp-row erp-wrap">
        <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} isDisabled={choices.length === 0} onPress={() => { setDraft((d) => addAssignment(d, choices[0]?.id ?? '')); setCheck(null); }}>{t('pol.assign.add')}</Button>
      </div>
      <DateInput label={t('pol.assign.from')} help={current.latestFrom ? t('pol.assign.from.help', { date: current.latestFrom }) : t('pol.assign.from.first')} value={from}
        onChange={(d) => { setFrom(d); setCheck(null); }} isRequired error={text(errors.effectiveFrom)} />
      <TextField label={t('pol.reason')} help={t('pol.reason.help')} value={reason} onChange={setReason} maxLength={500} />
      {check && <CheckResult check={check} />}
      <div className="erp-config-actions">
        <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
        <Button variant="secondary" isPending={busy} onPress={() => { void runCheck(); }}>{t('pol.assign.check')}</Button>
        <Button variant="primary" isPending={busy} isDisabled={!from} onPress={() => { void activate(); }}>{t('pol.assign.activate')}</Button>
      </div>
    </section>
  );
}

function CheckResult({ check }: { check: SetCheck }) {
  const { t } = useText();
  return (
    <div className="erp-stack" aria-live="polite">
      {check.valid ? <Banner tone="success">{t('pol.check.valid')}</Banner> : <Banner tone="danger">{t('pol.check.invalid')}</Banner>}
      {check.unmatched && check.unmatched.count > 0 && (
        <Banner tone="warning" title={t('pol.check.unmatched', { count: check.unmatched.count })}>
          <ul className="erp-config-list" aria-label={t('pol.check.unmatched.list')}>
            {check.unmatched.items.map((u) => <li key={u.employmentId} className="erp-small"><bdi className="erp-code">{u.employeeNo}</bdi> <bdi>{u.name}</bdi> · {t(`pol.res.reason.${u.reason}` as MessageKey)}</li>)}
          </ul>
          {check.unmatched.count - (check.unmatched.notShown ?? 0) > check.unmatched.items.length && <p className="erp-small">{t('pol.check.unmatched.more', { count: check.unmatched.count - (check.unmatched.notShown ?? 0) - check.unmatched.items.length })}</p>}
          {(check.unmatched.notShown ?? 0) > 0 && <p className="erp-small">{t('pol.check.unmatched.notShown', { count: check.unmatched.notShown ?? 0 })}</p>}
        </Banner>
      )}
      {check.unmatched && check.unmatched.count === 0 && <p className="erp-small erp-muted">{t('pol.check.allCovered')}</p>}
    </div>
  );
}

function RuleEditor({ type, companyId, names, conditions, error, onChange }: {
  type: PolicyType; companyId: string; names: RuleNames; conditions: DraftCondition[]; error: string | null; onChange: (c: DraftCondition[]) => void;
}) {
  const { t } = useText();
  const [adding, setAdding] = useState<Attribute>(type.attributes[0] ?? 'grade');
  const usable = type.attributes.filter((a) => a === 'custom' || !conditions.some((c) => c.attribute === a));
  const chosen: Attribute = usable.includes(adding) ? adding : (usable[0] ?? adding);
  return (
    <fieldset className="erp-fieldset">
      <legend className="erp-label">{t('pol.rule.conditions')}</legend>
      {error && <Banner tone="danger">{error}</Banner>}
      {conditions.length === 0 && <p className="erp-small erp-muted">{t('pol.rule.everyone')}</p>}
      {conditions.map((c, i) => (
        <ConditionEditor key={`${c.attribute}:${i}`} condition={c} companyId={companyId} names={names} first={i === 0}
          onChange={(next) => onChange(conditions.map((x, j) => (j === i ? next : x)))} onRemove={() => onChange(conditions.filter((_, j) => j !== i))} />
      ))}
      {usable.length > 0 && (
        <div className="erp-pol-filters">
          <SelectField label={t('pol.rule.addWhich')} options={usable.map((a) => ({ id: a, label: t(attributeKey(a)) }))} value={chosen} onChange={(id) => setAdding(id as Attribute)} />
          <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => onChange([...conditions, blankCondition(chosen, names)])}>{t('pol.rule.add')}</Button>
        </div>
      )}
    </fieldset>
  );
}

function ConditionEditor({ condition, companyId, names, first, onChange, onRemove }: {
  condition: DraftCondition; companyId: string; names: RuleNames; first: boolean; onChange: (c: DraftCondition) => void; onRemove: () => void;
}) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const records = useRecords(condition.attribute, companyId);
  const field = names.customFields.find((f) => f.key === condition.field);
  const label = condition.attribute === 'custom' ? (field ? nameOf(field.label, locale) || field.key : t('pol.attr.custom')) : t(attributeKey(condition.attribute));
  const operatorOptions: Option[] = OPERATORS.map((o) => ({ id: o, label: t(`pol.op.${o}` as MessageKey) }));
  const set = (values: Json[]) => onChange({ ...condition, values: condition.operator === 'equals' ? values.slice(0, 1) : values });
  // The choices of the condition: records of the company, employee types, or a custom field's options.
  let choices: Option[] | null = null;
  if (['grade', 'unit', 'branch', 'location'].includes(condition.attribute)) choices = records;
  else if (condition.attribute === 'employee_type') choices = names.employeeTypes.map((e) => ({ id: e.code, label: nameOf(e.name, locale) || e.code }));
  else if (field?.dataType === 'choice') choices = field.options.map((o) => ({ id: o.code, label: nameOf(o.label, locale) || o.code }));
  return (
    <div className="erp-pol-condition" role="group" aria-label={label}>
      <div className="erp-row erp-wrap">
        <span className="erp-pol-name">{first ? label : t('pol.rule.andThen', { attribute: label })}</span>
        <IconButton label={t('pol.rule.remove', { attribute: label })} icon={<Trash2 size={16} aria-hidden="true" />} onPress={onRemove} />
      </div>
      {condition.attribute === 'custom' && (
        <SelectField label={t('pol.rule.field')} options={names.customFields.filter((f) => f.status === 'active' || f.key === condition.field).map((f) => ({ id: f.key, label: nameOf(f.label, locale) || f.key, detail: f.key }))}
          value={condition.field} onChange={(id) => onChange({ ...condition, field: id, values: [] })} />
      )}
      {field?.status === 'retired' && <Banner tone="warning">{t('pol.rule.fieldRetired')}</Banner>}
      <SelectField label={t('pol.rule.operator')} options={operatorOptions} value={condition.operator}
        onChange={(id) => onChange({ ...condition, operator: id as Operator, values: id === 'equals' ? condition.values.slice(0, 1) : condition.values })} />
      {choices !== null && condition.operator === 'equals' && (
        <SelectField label={t('pol.rule.value')} options={choices} value={typeof condition.values[0] === 'string' ? condition.values[0] : null} onChange={(id) => set([id])} />
      )}
      {choices !== null && condition.operator !== 'equals' && (
        <fieldset className="erp-fieldset">
          <legend className="erp-label">{t('pol.rule.values')}</legend>
          {choices.length === 0 ? <p className="erp-small erp-muted">{t('pol.rule.noChoices')}</p> : (
            <div className="erp-pol-choices">
              {choices.map((o) => (
                <CheckboxField key={o.id} label={o.detail && o.detail !== o.label ? `${o.label} (${o.detail})` : o.label} isSelected={condition.values.includes(o.id)}
                  onChange={(on) => set(on ? [...condition.values, o.id] : condition.values.filter((v) => v !== o.id))} />
              ))}
            </div>
          )}
        </fieldset>
      )}
      {choices === null && field?.dataType === 'bool' && (
        <SelectField label={t('pol.rule.value')} options={[{ id: 'true', label: t('pol.value.yes') }, { id: 'false', label: t('pol.value.no') }]}
          value={condition.values[0] === true ? 'true' : condition.values[0] === false ? 'false' : null} onChange={(id) => set([id === 'true'])} />
      )}
      {choices === null && field && field.dataType !== 'bool' && <TypedValues condition={condition} dataType={field.dataType} onChange={set} />}
      {condition.attribute === 'unit' && (
        <CheckboxField label={t('pol.rule.subUnits')} isSelected={condition.subUnits} onChange={(on) => onChange({ ...condition, subUnits: on })} />
      )}
    </div>
  );
}

/** Values typed as text (text, number, decimal, date fields), several separated by commas. */
function TypedValues({ condition, dataType, onChange }: { condition: DraftCondition; dataType: string; onChange: (values: Json[]) => void }) {
  const { t } = useText();
  const numeric = dataType === 'number' || dataType === 'decimal';
  const [text, setText] = useState(condition.values.map((v) => String(v)).join(', '));
  const parse = (value: string): Json[] => value.split(',').map((s) => s.trim()).filter((s) => s.length > 0)
    .map((s) => (numeric ? (parseNumber(s, dataType === 'number') ?? s) : s));
  return (
    <TextField label={condition.operator === 'equals' ? t('pol.rule.value') : t('pol.rule.values')} help={t(dataType === 'date' ? 'pol.rule.typed.date' : condition.operator === 'equals' ? 'pol.rule.typed.one' : 'pol.rule.typed.many')}
      value={text} onChange={(v) => { setText(v); onChange(parse(v)); }} {...(dataType === 'number' ? { inputMode: 'numeric' as const } : {})} />
  );
}
