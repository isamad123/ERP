// One step of a route (slice S1.15c Build item 10; FR-KRN-WFL-01 to -03): its name in both languages, who decides (named person, role queue, person field of the
// document, line manager, department head, position), an optional condition, a time limit and who it passes to when the time runs out. The server's check
// has the last word; a problem it names is shown on the step.
import { ArrowDown, ArrowUp, Trash2 } from 'lucide-react';
import { Banner, Button, IconButton, NumberField, SelectField, SwitchField, TextField, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import type { ApproverJson, RouteDocumentType, StepJson } from '../../api/routes';
import type { RoleOption } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { NamePair } from './config-parts';
import { ConditionEditor } from './routes-condition';
import { APPROVER_KINDS, approverOf, describe, describeApprover, ESCALATION_KINDS, newTest, type SentenceContext } from './routes-model';
import { PersonPicker } from './users-parts';
import './routes-messages';

export interface StepProps {
  step: StepJson;
  index: number;
  count: number;
  type: RouteDocumentType;
  roles: RoleOption[];
  readOnly: boolean;
  /** Names of problems the server found on this step (already in words). */
  problems: string[];
  people: Record<string, string>;
  sentence: SentenceContext;
  onChange: (step: StepJson) => void;
  onMove: (by: number) => void;
  onRemove: () => void;
  onPerson: (id: string, name: string) => void;
}

/** The named person of an approver or an escalation: the name once chosen (with a way to choose another), the lookup until then. */
function PersonLine({ id, people, readOnly, onPick, onClear }: { id: string | undefined; people: Record<string, string>; readOnly: boolean; onPick: (id: string, name: string) => void; onClear: () => void }) {
  const { t } = useText();
  if (!id) return readOnly ? null : <PersonPicker label={t('routes.approver.user.label')} isRequired value={null} onChange={(o) => { if (o) onPick(o.id, o.label); }} />;
  return (
    <div className="erp-stack">
      <span className="erp-label">{t('routes.approver.user.label')}</span>
      <div className="erp-row erp-wrap">
        <bdi>{people[id] || t('routes.approver.unknown')}</bdi>
        {!readOnly && <Button variant="quiet" onPress={onClear}>{t('routes.approver.user.change')}</Button>}
      </div>
    </div>
  );
}

export function StepCard({ step, index, count, type, roles, readOnly, problems, people, sentence, onChange, onMove, onRemove, onPerson }: StepProps) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const n = index + 1;
  const userFields = type.fields.filter((f) => f.type === 'user');
  const roleOptions: Option[] = roles.map((r) => ({ id: r.code, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }));
  const kindOptions: Option[] = APPROVER_KINDS.map((k) => ({ id: k, label: t(`routes.kind.${k}` as MessageKey) }));
  const roleName = (code: string) => roles.find((r) => r.code === code)?.nameI18n ? nameOf(roles.find((r) => r.code === code)?.nameI18n, locale) : code;
  const who = describeApprover(step.approver, { ...sentence, roleName, userName: (id) => people[id] || t('routes.approver.unknown') });
  const when = step.condition ? t('routes.when.sentence', { sentence: describe(step.condition, sentence) }) : t('routes.when.always');
  const title = nameOf(step.name, locale) || step.key;
  const setApprover = (approver: ApproverJson) => onChange({ ...step, approver });
  const setEscalation = (kind: string) => onChange({ ...step, escalation: kind === 'none' ? null : approverOf(kind) });
  const escalationOptions: Option[] = [{ id: 'none', label: t('routes.step.escalation.none') }, ...ESCALATION_KINDS.map((k) => ({ id: k, label: t(`routes.step.escalation.${k}` as MessageKey) }))];
  return (
    <section className="erp-route-step" aria-label={t('routes.step.title', { n })}>
      <div className="erp-row erp-wrap">
        <h3 className="erp-h3">{t('routes.step.title', { n })}: <bdi>{title}</bdi></h3>
        {!readOnly && (
          <span className="erp-config-actions">
            <IconButton label={t('routes.step.up', { n })} icon={<ArrowUp size={16} aria-hidden="true" />} isDisabled={index === 0} onPress={() => onMove(-1)} />
            <IconButton label={t('routes.step.down', { n })} icon={<ArrowDown size={16} aria-hidden="true" />} isDisabled={index === count - 1} onPress={() => onMove(1)} />
            <IconButton label={t('routes.step.remove', { n })} icon={<Trash2 size={16} aria-hidden="true" />} onPress={onRemove} />
          </span>
        )}
      </div>
      <p className="erp-small erp-muted">{t('routes.step.summary', { who, when })}</p>
      {problems.length > 0 && <Banner tone="danger"><ul className="erp-route-problems">{problems.map((p) => <li key={p}>{p}</li>)}</ul></Banner>}
      <NamePair label={t('routes.step.name')} value={step.name} isReadOnly={readOnly} onChange={(name) => onChange({ ...step, name })} />
      <TextField label={t('routes.step.key')} help={t('routes.step.key.help')} isCode isReadOnly={readOnly} value={step.key} maxLength={40} onChange={(key) => onChange({ ...step, key })} />
      <SelectField label={t('routes.step.who')} options={kindOptions} value={step.approver.kind} isDisabled={readOnly} onChange={(kind) => setApprover(approverOf(kind))} />
      {step.approver.kind === 'user' && (
        <PersonLine id={step.approver.user_id} people={people} readOnly={readOnly} onPick={(id, name) => { onPerson(id, name); setApprover({ kind: 'user', user_id: id }); }} onClear={() => setApprover({ kind: 'user', user_id: '' })} />
      )}
      {step.approver.kind === 'role' && (
        <SelectField label={t('routes.approver.role.label')} isRequired options={roleOptions} placeholder={t('routes.approver.role.pick')} value={step.approver.role ?? ''} isDisabled={readOnly} onChange={(role) => setApprover({ kind: 'role', role })} />
      )}
      {step.approver.kind === 'document_user' && (
        <SelectField label={t('routes.approver.field.label')} isRequired placeholder={t('routes.approver.field.pick')} isDisabled={readOnly}
          options={userFields.map((f) => ({ id: f.name, label: nameOf(f.label, locale) || f.name, detail: f.name }))} value={step.approver.field ?? ''} onChange={(field) => setApprover({ kind: 'document_user', field })} />
      )}
      {step.approver.kind === 'manager' && (
        <NumberField label={t('routes.approver.levels.label')} help={t('routes.approver.levels.help')} minValue={1} maxValue={10} isReadOnly={readOnly} value={step.approver.levels ?? 1} onChange={(levels) => setApprover({ kind: 'manager', levels })} />
      )}
      {step.approver.kind === 'position' && (
        <TextField label={t('routes.approver.position.label')} isCode isRequired isReadOnly={readOnly} value={step.approver.position ?? ''} onChange={(position) => setApprover({ kind: 'position', position })} />
      )}
      {['manager', 'department_head', 'position'].includes(step.approver.kind) && <p className="erp-small erp-muted">{t('routes.approver.directory.note')}</p>}
      <div className="erp-stack">
        <span className="erp-label">{t('routes.when.title')}</span>
        {step.condition
          ? (
            <>
              <ConditionEditor label={t('routes.when.title')} condition={step.condition} fields={type.fields} readOnly={readOnly} onChange={(condition) => onChange({ ...step, condition })} />
              {!readOnly && <div><Button variant="quiet" onPress={() => onChange({ ...step, condition: null })}>{t('routes.when.remove')}</Button></div>}
            </>
          )
          : (
            <>
              <p>{t('routes.when.always')}</p>
              {!readOnly && (type.fields.length > 0
                ? <div><Button variant="secondary" onPress={() => onChange({ ...step, condition: { all: [newTest(type.fields)] } })}>{t('routes.when.add')}</Button></div>
                : <p className="erp-small erp-muted">{t('routes.when.noFields')}</p>)}
            </>
          )}
      </div>
      <NumberField label={t('routes.step.limit')} help={t('routes.step.limit.help')} minValue={1} maxValue={365} isReadOnly={readOnly}
        value={step.time_limit_working_days ?? Number.NaN} onChange={(days) => onChange({ ...step, time_limit_working_days: Number.isFinite(days) && days > 0 ? days : null })} />
      <SelectField label={t('routes.step.escalation')} options={escalationOptions} value={step.escalation?.kind ?? 'none'} isDisabled={readOnly} onChange={setEscalation} />
      {step.escalation?.kind === 'user' && (
        <PersonLine id={step.escalation.user_id} people={people} readOnly={readOnly} onPick={(id, name) => { onPerson(id, name); onChange({ ...step, escalation: { kind: 'user', user_id: id } }); }} onClear={() => onChange({ ...step, escalation: { kind: 'user', user_id: '' } })} />
      )}
      {step.escalation?.kind === 'role' && (
        <SelectField label={t('routes.approver.role.label')} isRequired options={roleOptions} placeholder={t('routes.approver.role.pick')} value={step.escalation.role ?? ''} isDisabled={readOnly} onChange={(role) => onChange({ ...step, escalation: { kind: 'role', role } })} />
      )}
      {type.bulkApproval && <SwitchField label={t('routes.step.bulk')} isSelected={step.bulk_approval ?? false} isDisabled={readOnly} onChange={(bulk_approval) => onChange({ ...step, bulk_approval })} />}
    </section>
  );
}
