// Custom field definitions (slice S1.15b Build item 7; FR-KRN-MET-01, -02, -05, S1.15 rulings Q5 and Q8): for each table that can
// carry extra fields, the customer's own fields with their type, labels and help in both languages, required, unique, default,
// validation, section and order. A field is retired (hidden on screens, values kept), never deleted: purge is not offered in
// 1.0. The types are those of the approved data design (Q5); the file type waits for the documents screens.
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { parseDate } from '@internationalized/date';
import { useDocumentTitle, useLabel } from '@erp/engine';
import { Plus } from 'lucide-react';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, SwitchField, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { CUSTOM_TYPES, customFieldsApi, optionListsApi, type CustomDef, type CustomEntity, type CustomType, type CustomValidation } from '../../api/config';
import { nameOf, type Names } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { cleanNames, NamePair, useRefused } from './config-parts';
import './config-messages';
import './users.css';
import './settings.css';
import './configure.css';

const KEY = /^[a-z][a-z0-9_]{0,39}$/;
const DISABLED_TYPES: readonly CustomType[] = ['file'];
const NO_UNIQUE: readonly CustomType[] = ['multi_choice', 'bool', 'long_text', 'file'];
const NEEDS_LIST: readonly CustomType[] = ['choice', 'multi_choice'];

export const typeKey = (type: CustomType): MessageKey => `cf.type.${type}` as MessageKey;

export default function CustomFieldsScreen() {
  const { t } = useText();
  const label = useLabel();
  useDocumentTitle(t('cf.title'));
  const [params, setParams] = useSearchParams();
  const entities = useQuery({ queryKey: ['cfg', 'custom-entities'], queryFn: customFieldsApi.entities, refetchOnMount: 'always' });
  const items = entities.data?.items ?? [];
  const chosen = items.find((e) => e.entityType === params.get('entity')) ?? items[0] ?? null;
  const problem = entities.error instanceof ApiProblem ? entities.error : null;
  const options: Option[] = items.map((e) => ({ id: e.entityType, label: label(e.labelKey) }));
  return (
    <div className="erp-page">
      <PageHeader title={t('cf.title')} description={t('cf.description')} />
      {entities.isPending && <Skeleton lines={4} />}
      {entities.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('cf.denied') : t('cf.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void entities.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {entities.isSuccess && items.length === 0 && <EmptyState title={t('cf.noEntities')}>{t('cf.noEntities.body')}</EmptyState>}
      {chosen && (
        <>
          <div className="erp-settings-tools">
            <SelectField label={t('cf.entity.label')} help={t('cf.entity.help')} options={options} value={chosen.entityType}
              onChange={(id) => setParams((c) => { const next = new URLSearchParams(c); next.set('entity', id); return next; }, { replace: true })} />
          </div>
          <Definitions key={chosen.entityType} entity={chosen} entityName={label(chosen.labelKey)} />
        </>
      )}
    </div>
  );
}

function Definitions({ entity, entityName }: { entity: CustomEntity; entityName: string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const canCreate = writable && can('cfg.custom_field_defs.create');
  const canUpdate = writable && can('cfg.custom_field_defs.update');
  const [editing, setEditing] = useState<CustomDef | 'new' | null>(null);
  const [retiring, setRetiring] = useState<CustomDef | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const key = ['cfg', 'custom-fields', entity.entityType];
  const list = useQuery({ queryKey: key, queryFn: () => customFieldsApi.list(entity.entityType), refetchOnMount: 'always' });
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const done = async (message: string) => { await queryClient.invalidateQueries({ queryKey: ['cfg', 'custom-fields'] }); await queryClient.invalidateQueries({ queryKey: ['engine'] }); toast.success(message); };
  const restore = async (def: CustomDef) => {
    setBusy(true);
    setFailure(null);
    try { await customFieldsApi.restore(def.id, def.rowVersion); await done(t('cf.restored')); } catch (error) { setFailure(refused(error).summary); } finally { setBusy(false); }
  };
  const defs = useMemo(() => [...(list.data?.items ?? [])].sort((a, b) => a.seq - b.seq || a.fieldKey.localeCompare(b.fieldKey)), [list.data]);
  return (
    <section className="erp-stack" aria-labelledby="cf-defs-title">
      <div className="erp-row erp-wrap">
        <h2 id="cf-defs-title" className="erp-h3">{t('cf.defs.title', { entity: entityName })}</h2>
        {canCreate && <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setEditing('new')}>{t('cf.new')}</Button>}
      </div>
      {failure && <Banner tone="danger">{failure}</Banner>}
      {list.isPending && <Skeleton lines={4} />}
      {list.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('cf.denied') : t('cf.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {list.isSuccess && defs.length === 0 && (
        <EmptyState title={t('cf.empty', { entity: entityName })} action={canCreate ? <Button variant="primary" onPress={() => setEditing('new')}>{t('cf.new')}</Button> : undefined}>{t('cf.empty.body')}</EmptyState>
      )}
      {list.isSuccess && defs.length > 0 && (
        <div className="erp-table-scroll">
          <table className="erp-table" aria-label={t('cf.defs.title', { entity: entityName })}>
          <thead>
            <tr>
              <th scope="col">{t('cf.col.label')}</th>
              <th scope="col">{t('cf.col.key')}</th>
              <th scope="col">{t('cf.col.type')}</th>
              <th scope="col">{t('cf.col.rules')}</th>
              <th scope="col">{t('cf.col.order')}</th>
              <th scope="col">{t('cf.col.status')}</th>
              <th scope="col"><span className="erp-visually-hidden">{t('cf.col.actions')}</span></th>
            </tr>
          </thead>
          <tbody>
            {defs.map((def) => {
              const name = nameOf(def.label, locale);
              return (
                <tr key={def.id}>
                  <th scope="row"><bdi>{name}</bdi>{def.section && <span className="erp-small erp-muted"> · <bdi>{def.section}</bdi></span>}</th>
                  <td><span className="erp-code"><bdi>{def.fieldKey}</bdi></span></td>
                  <td>{t(typeKey(def.dataType))}</td>
                  <td>
                    <span className="erp-row erp-wrap">
                      {def.isRequired && <StatusChip status="draft" label={t('cf.rule.required')} />}
                      {def.isUnique && <StatusChip status="draft" label={t('cf.rule.unique')} />}
                    </span>
                  </td>
                  <td>{def.seq}</td>
                  <td><StatusChip status={def.status === 'active' ? 'active' : 'inactive'} label={t(def.status === 'active' ? 'cf.status.active' : 'cf.status.retired')} /></td>
                  <td>
                    <span className="erp-config-actions">
                      <Button variant="secondary" aria-label={t(canUpdate ? 'cf.edit.aria' : 'cf.view.aria', { name })} onPress={() => setEditing(def)}>{canUpdate ? t('cf.edit') : t('cf.view')}</Button>
                      {canUpdate && def.status === 'active' && <Button variant="quiet" aria-label={t('cf.retire.aria', { name })} onPress={() => setRetiring(def)}>{t('cf.retire')}</Button>}
                      {canUpdate && def.status === 'retired' && <Button variant="quiet" isDisabled={busy} aria-label={t('cf.restore.aria', { name })} onPress={() => { void restore(def); }}>{t('cf.restore')}</Button>}
                    </span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
          </div>
      )}
      <p className="erp-small erp-muted">{t('cf.note.noPurge')}</p>
      {editing && (
        <DefinitionDrawer entity={entity} entityName={entityName} def={editing === 'new' ? null : editing} canWrite={editing === 'new' ? canCreate : canUpdate} nextSeq={(defs.at(-1)?.seq ?? 0) + 10}
          onClose={() => setEditing(null)} onSaved={async (isNew) => { setEditing(null); await done(isNew ? t('cf.created') : t('cf.saved')); }} />
      )}
      {retiring && (
        <Dialog role="alertdialog" isOpen title={t('cf.retire.title', { name: nameOf(retiring.label, locale) })} onOpenChange={(open) => { if (!open) setRetiring(null); }}
          actions={() => (
            <>
              <Button variant="secondary" onPress={() => setRetiring(null)}>{t('common.cancel')}</Button>
              <Button variant="danger" isPending={busy} onPress={() => {
                setBusy(true);
                customFieldsApi.retire(retiring.id, retiring.rowVersion)
                  .then(async () => { setRetiring(null); await done(t('cf.retired')); })
                  .catch((error: unknown) => { setRetiring(null); setFailure(refused(error).summary); })
                  .finally(() => setBusy(false));
              }}>{t('cf.retire.confirm')}</Button>
            </>
          )}>
          <p>{t('cf.retire.body')}</p>
        </Dialog>
      )}
    </section>
  );
}

interface Draft {
  fieldKey: string;
  dataType: CustomType;
  label: Names;
  help: Names;
  section: string;
  seq: string;
  isRequired: boolean;
  isUnique: boolean;
  optionListId: string;
  validation: Record<string, string>;
  defaultValue: string;
  defaultBool: boolean;
}

const VALIDATION_FIELDS: Record<CustomType, readonly string[]> = {
  text: ['minLength', 'maxLength', 'pattern'], long_text: ['minLength', 'maxLength'], number: ['min', 'max'], decimal: ['min', 'max'], date: ['minDate', 'maxDate'],
  bool: [], choice: [], multi_choice: ['maxItems'], user: [], employee: [], file: [],
};
const INTEGER_RULES = ['minLength', 'maxLength', 'maxItems'];

/** The draft of a definition: text boxes for numbers, so a half-typed value is never lost. */
function draftOf(def: CustomDef | null, nextSeq: number): Draft {
  const validation: Record<string, string> = {};
  for (const [k, v] of Object.entries(def?.validation ?? {})) if (v !== null && v !== undefined && v !== '') validation[k] = String(v);
  return {
    fieldKey: def?.fieldKey ?? '', dataType: def?.dataType ?? 'text', label: { ...(def?.label ?? {}) }, help: { ...(def?.help ?? {}) }, section: def?.section ?? '', seq: String(def?.seq ?? nextSeq),
    isRequired: def?.isRequired ?? false, isUnique: def?.isUnique ?? false, optionListId: def?.optionListId ?? '', validation,
    defaultValue: typeof def?.defaultValue === 'string' || typeof def?.defaultValue === 'number' ? String(def.defaultValue) : '', defaultBool: def?.defaultValue === true,
  };
}

/** The validation object to send: only the rules of the type that have a value, numbers as numbers (decimals as exact text). */
export function validationOf(type: CustomType, raw: Record<string, string>): { ok: true; value: CustomValidation } | { ok: false; field: string; problem: 'whole' | 'decimal' | 'order' } {
  const value: Record<string, unknown> = {};
  for (const name of VALIDATION_FIELDS[type]) {
    const text = (raw[name] ?? '').trim();
    if (text === '') continue;
    if (INTEGER_RULES.includes(name) || (type === 'number' && (name === 'min' || name === 'max'))) {
      if (!/^-?\d+$/.test(text)) return { ok: false, field: name, problem: 'whole' };
      value[name] = Number(text);
    } else if (type === 'decimal' && (name === 'min' || name === 'max')) {
      if (!/^-?\d+(\.\d+)?$/.test(text)) return { ok: false, field: name, problem: 'decimal' };
      value[name] = text;
    } else {
      value[name] = text;
    }
  }
  const low = value.min ?? value.minLength ?? value.minDate;
  const high = value.max ?? value.maxLength ?? value.maxDate;
  if (low !== undefined && high !== undefined && Number.isFinite(Number(low)) && Number.isFinite(Number(high)) && Number(low) > Number(high)) return { ok: false, field: value.min !== undefined ? 'min' : 'minLength', problem: 'order' };
  if (typeof low === 'string' && typeof high === 'string' && /^\d{4}-/.test(low) && low > high) return { ok: false, field: 'minDate', problem: 'order' };
  return { ok: true, value: value as CustomValidation };
}

/** The default value to send for the draft (null: none). */
function defaultOf(draft: Draft): unknown {
  switch (draft.dataType) {
    case 'bool': return draft.defaultBool ? true : null;
    case 'number': return /^-?\d+$/.test(draft.defaultValue.trim()) ? Number(draft.defaultValue.trim()) : null;
    case 'text': case 'long_text': case 'decimal': case 'date': case 'choice': return draft.defaultValue.trim() === '' ? null : draft.defaultValue.trim();
    default: return null;
  }
}

function DefinitionDrawer({ entity, entityName, def, canWrite, nextSeq, onClose, onSaved }: {
  entity: CustomEntity; entityName: string; def: CustomDef | null; canWrite: boolean; nextSeq: number; onClose: () => void; onSaved: (isNew: boolean) => Promise<void>;
}) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const refused = useRefused();
  const [draft, setDraft] = useState<Draft>(() => draftOf(def, nextSeq));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const creating = def === null;
  const lists = useQuery({ queryKey: ['cfg', 'option-lists'], queryFn: optionListsApi.list, enabled: NEEDS_LIST.includes(draft.dataType), retry: false });
  const listItems = useQuery({ queryKey: ['cfg', 'option-list', draft.optionListId], queryFn: () => optionListsApi.get(draft.optionListId), enabled: draft.dataType === 'choice' && !!draft.optionListId, retry: false });
  const set = <K extends keyof Draft>(name: K, value: Draft[K]) => { setDraft((d) => ({ ...d, [name]: value })); setErrors((e) => Object.fromEntries(Object.entries(e).filter(([k]) => k !== name))); };
  const typeOptions: Option[] = CUSTOM_TYPES.map((type) => ({ id: type, label: DISABLED_TYPES.includes(type) ? t('cf.type.disabled', { type: t(typeKey(type)) }) : t(typeKey(type)), isDisabled: DISABLED_TYPES.includes(type) }));
  const listOptions: Option[] = (lists.data?.items ?? []).map((l) => ({ id: l.id, label: nameOf(l.name, locale), detail: l.code }));
  const rules = VALIDATION_FIELDS[draft.dataType];

  const submit = async () => {
    const found: Record<string, string> = {};
    if (creating && !KEY.test(draft.fieldKey)) found.fieldKey = t('cf.error.key');
    if (!(draft.label.en ?? '').trim()) found['label.en'] = t('cf.error.label');
    if (NEEDS_LIST.includes(draft.dataType) && !draft.optionListId) found.optionListId = t('cf.error.list');
    if (!/^-?\d+$/.test(draft.seq.trim())) found.seq = t('cf.error.seq');
    const validation = validationOf(draft.dataType, draft.validation);
    if (!validation.ok) found[`validation.${validation.field}`] = t(`cf.error.${validation.problem}` as MessageKey);
    setErrors(found);
    if (Object.keys(found).length > 0 || !validation.ok) { setSummary(t('config.error.fix')); return; }
    setBusy(true);
    setSummary(null);
    const common = {
      label: cleanNames(draft.label), help: Object.keys(cleanNames(draft.help)).length > 0 ? cleanNames(draft.help) : null, validation: validation.value, defaultValue: defaultOf(draft),
      isRequired: draft.isRequired, isUnique: draft.isUnique && !NO_UNIQUE.includes(draft.dataType), section: draft.section.trim() || null, seq: Number(draft.seq.trim()),
    };
    try {
      if (def) await customFieldsApi.update(def.id, { ...common, rowVersion: def.rowVersion });
      else await customFieldsApi.create({ ...common, entityType: entity.entityType, fieldKey: draft.fieldKey, dataType: draft.dataType, optionListId: NEEDS_LIST.includes(draft.dataType) ? draft.optionListId : null });
      await onSaved(creating);
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };

  const ruleLabel = (name: string): string => t(`cf.rule.${name}` as MessageKey);
  const dateOf = (text: string | undefined) => { try { return text ? parseDate(text) : null; } catch { return null; } };
  return (
    <Dialog drawer isOpen title={creating ? t('cf.new.title', { entity: entityName }) : t('cf.edit.title', { name: nameOf(def.label, locale) })} onOpenChange={(open) => { if (!open) onClose(); }}>
      {() => (
        <form className="erp-stack" noValidate onSubmit={(e) => { e.preventDefault(); void submit(); }} aria-label={creating ? t('cf.new.title', { entity: entityName }) : t('cf.edit.title', { name: nameOf(def.label, locale) })}>
          {summary && <Banner tone="danger">{summary}</Banner>}
          {!canWrite && <Banner tone="info">{t('cf.readOnly')}</Banner>}
          <TextField label={t('cf.field.key')} help={creating ? t('cf.field.key.help') : t('cf.field.key.fixed')} isCode isRequired={creating} isReadOnly={!creating || !canWrite} value={draft.fieldKey} onChange={(v) => set('fieldKey', v)} maxLength={40} error={errors.fieldKey ?? null} />
          <SelectField label={t('cf.field.type')} help={creating ? t('cf.field.type.help') : t('cf.field.type.fixed')} options={typeOptions} value={draft.dataType} isDisabled={!creating || !canWrite} isRequired
            onChange={(v) => setDraft((d) => ({ ...d, dataType: v as CustomType, validation: {}, defaultValue: '', defaultBool: false, optionListId: '', isUnique: false }))} />
          <NamePair label={t('cf.field.label')} value={draft.label} onChange={(v) => set('label', v)} isReadOnly={!canWrite} errors={{ en: errors['label.en'], ur: errors['label.ur'] }} />
          <NamePair label={t('cf.field.help')} value={draft.help} onChange={(v) => set('help', v)} isReadOnly={!canWrite} required={false} maxLength={300} errors={{ en: errors['help.en'], ur: errors['help.ur'] }} />
          {NEEDS_LIST.includes(draft.dataType) && (
            <SelectField label={t('cf.field.list')} help={creating ? t('cf.field.list.help') : t('cf.field.list.fixed')} options={listOptions} value={draft.optionListId || null} isRequired isDisabled={!creating || !canWrite}
              onChange={(v) => set('optionListId', v)} error={errors.optionListId ?? null} />
          )}
          <TextField label={t('cf.field.section')} help={t('cf.field.section.help')} isReadOnly={!canWrite} value={draft.section} onChange={(v) => set('section', v)} maxLength={60} error={errors.section ?? null} />
          <TextField label={t('cf.field.seq')} help={t('cf.field.seq.help')} isReadOnly={!canWrite} inputMode="numeric" value={draft.seq} onChange={(v) => set('seq', v)} error={errors.seq ?? null} />
          <SwitchField label={t('cf.field.required')} isSelected={draft.isRequired} onChange={(v) => set('isRequired', v)} isDisabled={!canWrite} />
          {!NO_UNIQUE.includes(draft.dataType) && <SwitchField label={t('cf.field.unique')} isSelected={draft.isUnique} onChange={(v) => set('isUnique', v)} isDisabled={!canWrite} />}
          {rules.length > 0 && (
            <fieldset className="erp-fieldset">
              <legend className="erp-label">{t('cf.field.validation')}</legend>
              {rules.map((name) => (
                name === 'minDate' || name === 'maxDate'
                  ? <DateField key={name} label={ruleLabel(name)} isReadOnly={!canWrite} value={dateOf(draft.validation[name])} error={errors[`validation.${name}`] ?? null}
                    onChange={(d) => set('validation', { ...draft.validation, [name]: d ? d.toString() : '' })} />
                  : <TextField key={name} label={ruleLabel(name)} isReadOnly={!canWrite} isCode={name === 'pattern'} inputMode={name === 'pattern' ? 'text' : 'numeric'} value={draft.validation[name] ?? ''}
                    help={name === 'pattern' ? t('cf.rule.pattern.help') : undefined} onChange={(v) => set('validation', { ...draft.validation, [name]: v })} error={errors[`validation.${name}`] ?? null} />
              ))}
            </fieldset>
          )}
          {draft.dataType === 'bool' && <SwitchField label={t('cf.field.default.bool')} isSelected={draft.defaultBool} onChange={(v) => set('defaultBool', v)} isDisabled={!canWrite} />}
          {draft.dataType === 'choice' && draft.optionListId && listItems.data && (
            <SelectField label={t('cf.field.default')} isDisabled={!canWrite} value={draft.defaultValue || '__none__'} onChange={(v) => set('defaultValue', v === '__none__' ? '' : v)}
              options={[{ id: '__none__', label: t('cf.field.default.none') }, ...listItems.data.items.filter((i) => i.isActive).map((i) => ({ id: i.code, label: nameOf(i.label, locale) }))]} />
          )}
          {['text', 'long_text', 'number', 'decimal', 'date'].includes(draft.dataType) && (
            <TextField label={t('cf.field.default')} help={draft.dataType === 'date' ? t('cf.field.default.date') : undefined} isReadOnly={!canWrite} isCode={draft.dataType !== 'text' && draft.dataType !== 'long_text'}
              value={draft.defaultValue} onChange={(v) => set('defaultValue', v)} error={errors.defaultValue ?? null} />
          )}
          <p className="erp-small erp-muted">{t('cf.note.rules')}</p>
          <div className="erp-row erp-wrap">
            {canWrite && <Button type="submit" variant="primary" isPending={busy}>{creating ? t('cf.create') : t('cf.save')}</Button>}
            <Button variant="secondary" onPress={onClose}>{canWrite ? t('common.cancel') : t('common.close')}</Button>
          </div>
        </form>
      )}
    </Dialog>
  );
}
