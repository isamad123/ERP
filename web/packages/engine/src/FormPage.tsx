// A form page made from a table's metadata (slice S1.12c Build item 18; FR-ADM-UXA-02 to -05, FR-ADM-UXA-08,
// FR-KRN-IAM-13, S1.12 ruling Q13): one renderer per field kind, required markers, help text, checks when a field is
// left and on save (the generated schema), server errors next to the fields and in a summary, read-only, hidden and
// masked fields as the server sends them, "last changed by and when", a warning before leaving with unsaved changes,
// the saved version next to the person's when someone else saved first, and a delete confirmation that says what goes.
// Custom fields (S1.15) and autosaved drafts (S1.21) have their place here and are not built yet.
import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useBlocker } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type DateValue } from '@internationalized/date';
import { History, Power, Save, Trash2 } from 'lucide-react';
import { Banner, Button, DateField, Dialog, ErrorPanel, NumberField, PageHeader, SelectField, Skeleton, SwitchField, TextField, type Option } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText } from '@erp/i18n';
import { fieldProblemsOf, isProblem, tableClient, useEngineApi, type Problem, type Row } from './data';
import { decimalFields, DECIMAL_KINDS, formFields, type FieldMetadata, type TableMetadata, type ValidationSchema } from './metadata';
import { useLabel, type LabelFn } from './labels';
import { checkValue, cleanValues, validate, type FieldProblem, type FormValues } from './validation';
import { HistoryPanel, useHistory } from './HistoryPanel';
import { Value } from './values';
import { useDocumentTitle } from './ListPage';

/**
 * A screen's own editor for one field the engine cannot edit itself (a JSON value such as an address or opening hours,
 * slice S1.14a). It gets the field's value and reports a new one; the engine still trims nothing in it, checks nothing in
 * it and sends it as it is, so the editor keeps the value valid (the server and the database check it again).
 */
export interface FieldEditorProps {
  label: string;
  value: unknown;
  onChange: (value: unknown) => void;
  readOnly: boolean;
  error: string | null;
  help: string | undefined;
  /** Every value of the form, for a field that depends on another (a branch of the chosen company). */
  values: FormValues;
}

export type FieldEditor = (props: FieldEditorProps) => ReactNode;

export interface FormPageProps {
  metadata: TableMetadata;
  schema: ValidationSchema;
  /** The record to edit; null to create one. */
  id: string | null;
  /** Fields in the order shown (default: the table's order without system columns). */
  fields?: string[];
  /** Values a new record starts with (e.g. the company in hand, ruling Q10). */
  defaults?: FormValues;
  /** Choices of a reference to a company: the person's companies where they may create (ruling Q10). */
  companies?: Option[];
  canCreate: boolean;
  canUpdate: boolean;
  canDelete: boolean;
  canHistory: boolean;
  /** A short name of the record (code and name) for titles and confirmations. */
  recordTitle: (row: FormValues) => string;
  /** Texts for database rules a save or delete may break (constraint → message), e.g. "every company keeps a branch". */
  ruleTexts?: Record<string, string>;
  /** Help under a field (field → text). */
  help?: Record<string, string>;
  /** Own editors for fields the engine shows read-only (field → editor), e.g. an address or opening hours (S1.14a). */
  editors?: Record<string, FieldEditor>;
  /** What deleting removes and keeps, said in the confirmation. */
  deleteText?: (row: FormValues) => string;
  /** After a save: `activation` when the Activate or Deactivate button saved it (the screen usually stays on the record). */
  onSaved: (row: Row, created: boolean, how: 'form' | 'activation') => void;
  onDeleted: () => void;
  onCancel: () => void;
}

type Problems = Record<string, FieldProblem>;

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

export function FormPage(props: FormPageProps) {
  const { metadata, id } = props;
  const api = useEngineApi();
  const client = useMemo(() => tableClient(api, metadata, decimalFields(metadata)), [api, metadata]);
  const record = useQuery({ queryKey: ['engine', metadata.table, 'record', id], queryFn: ({ signal }) => (id === null ? Promise.reject(new Error('no record')) : client.get(id, signal)), enabled: id !== null });
  const { t } = useText();
  const label = useLabel();
  if (id !== null && record.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (id !== null && record.isError) {
    const problem = isProblem(record.error) ? record.error : null;
    return (
      <div className="erp-page">
        <ErrorPanel level={1} title={problem?.status === 404 ? t('engine.form.notFound', { entity: label(`${metadata.labelKey}.one`) }) : problem?.status === 403 ? t('engine.form.denied') : t('engine.form.loadError')}
          message={problem?.status === 404 ? t('engine.form.notFound.body') : problem?.status === 403 ? t('engine.form.denied.body') : t('engine.form.loadError.body')}
          reference={problem?.reference ?? null} action={<Button variant="secondary" onPress={props.onCancel}>{t('engine.form.backToList')}</Button>} />
      </div>
    );
  }
  return <FormBody {...props} saved={record.data ?? null} client={client} key={record.data ? `${record.data.id}:${record.data.rowVersion}` : 'new'} />;
}

function initialValues(fields: FieldMetadata[], saved: Row | null, defaults: FormValues | undefined): FormValues {
  const values: FormValues = {};
  for (const field of fields) {
    if (saved) values[field.field] = saved[field.field];
    else if (defaults && field.field in defaults) values[field.field] = defaults[field.field];
    else if (field.kind === 'Bool') values[field.field] = field.field === 'isActive';
    else if (field.kind === 'I18n') values[field.field] = {};
    else values[field.field] = null;
  }
  return values;
}

function FormBody({ metadata, schema, id, fields: order, defaults, companies, canCreate, canUpdate, canDelete, canHistory, recordTitle, ruleTexts, help, editors, deleteText,
  onSaved, onDeleted, onCancel, saved, client }: FormPageProps & { saved: Row | null; client: ReturnType<typeof tableClient> }) {
  const { t, server } = useText();
  const label = useLabel();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const queryClient = useQueryClient();
  const fields = useMemo(() => formFields(metadata, order), [metadata, order]);
  const creating = id === null;
  const editable = creating ? canCreate : canUpdate;
  const [values, setValues] = useState<FormValues>(() => initialValues(fields, saved, defaults));
  const [problems, setProblems] = useState<Problems>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [reference, setReference] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState<Row | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [done, setDone] = useState(false);
  const summaryRef = useRef<HTMLDivElement>(null);
  // Set at once when a save or delete succeeds, before the page moves on: the state update would come too late for the guard.
  const finished = useRef(false);
  const initial = useMemo(() => initialValues(fields, saved, defaults), [fields, saved, defaults]);
  const dirty = !done && fields.some((f) => !same(values[f.field], initial[f.field]));
  const entity = label(`${metadata.labelKey}.one`);
  const title = creating ? t('engine.form.newTitle', { entity }) : t('engine.form.editTitle', { entity, record: recordTitle(saved ?? values) });
  useDocumentTitle(title);
  const history = useHistory(metadata.table, canHistory ? id : null);
  const lastChanger = history.data?.[0]?.actorName ?? null;

  // FR-ADM-UXA-05 (warning part): leaving with unsaved changes asks first, whatever the way out (link, Back, reload).
  const blocker = useBlocker(({ currentLocation, nextLocation }) => !finished.current && dirty && currentLocation.pathname !== nextLocation.pathname);
  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const readOnlyField = (field: FieldMetadata) => !editable || field.readOnly || (!creating && (field.createOnly || field.field === 'companyId'))
    || (saved !== null && !(field.field in saved)) || (typeof values[field.field] === 'string' && /^\*{2,}/.test(String(values[field.field])) && field.kind !== 'Code' && field.kind !== 'Text');
  const editableFields = fields.filter((f) => !readOnlyField(f)).map((f) => f.field);

  const setValue = (name: string, value: unknown) => {
    setValues((v) => ({ ...v, [name]: value }));
    if (problems[name]) setProblems((p) => without(p, name));
  };
  const leave = (name: string) => {
    const problem = checkValue(values[name], schema.properties[name], schema.required.includes(name));
    setProblems((p) => (problem ? { ...p, [name]: problem } : without(p, name)));
  };

  const show = (problem: Problem) => {
    const mapped = fieldProblemsOf(problem, metadata);
    setProblems(mapped);
    setReference(problem.reference);
    const rule = problem.detail ? ruleTexts?.[problem.detail] : undefined;
    setSummary(rule ?? (Object.keys(mapped).length > 0 ? t('engine.form.fixErrors') : server(problem.key) ?? t('engine.form.saveError')));
    window.setTimeout(() => summaryRef.current?.focus(), 0);
  };

  const save = async (event?: FormEvent, override?: { rowVersion: number; body?: FormValues; how?: 'form' | 'activation' }) => {
    event?.preventDefault();
    const body = override?.body ?? values;
    const found = validate(body, schema, editableFields);
    if (Object.keys(found).length > 0) {
      setProblems(found);
      setSummary(t('engine.form.fixErrors'));
      window.setTimeout(() => summaryRef.current?.focus(), 0);
      return;
    }
    const version = override?.rowVersion ?? saved?.rowVersion;
    if (!creating && (id === null || version === undefined)) return;
    setBusy(true);
    setSummary(null);
    try {
      const clean = cleanValues(body, schema, creating ? editableFields : editableFields.filter((f) => f !== 'companyId'), editors ? new Set(Object.keys(editors)) : undefined);
      const row = id === null || version === undefined ? await client.create(clean) : await client.update(id, clean, version);
      await queryClient.invalidateQueries({ queryKey: ['engine', metadata.table] });
      if (override?.how === 'activation') {
        // Stays on the record: the saved row becomes the form's starting point.
        queryClient.setQueryData(['engine', metadata.table, 'record', id], row);
        onSaved(row, false, 'activation');
        return;
      }
      finished.current = true;
      setDone(true);
      onSaved(row, creating, 'form');
    } catch (e) {
      if (isProblem(e) && e.status === 409 && e.key === 'errors.data.concurrency_conflict' && id !== null) {
        try { setConflict(await client.get(id)); } catch { show(e); }
      } else if (isProblem(e)) {
        show(e);
      } else {
        setSummary(t('engine.form.saveError'));
      }
    } finally {
      setBusy(false);
    }
  };

  const remove = async (close: () => void) => {
    if (id === null || !saved) return;
    setBusy(true);
    try {
      await client.remove(id, saved.rowVersion);
      await queryClient.invalidateQueries({ queryKey: ['engine', metadata.table] });
      finished.current = true;
      setDone(true);
      close();
      onDeleted();
    } catch (e) {
      close();
      if (isProblem(e)) show(e); else setSummary(t('engine.form.saveError'));
    } finally {
      setBusy(false);
    }
  };

  const activeField = fields.find((f) => f.field === 'isActive' && f.kind === 'Bool');
  const toggleActive = () => {
    if (!saved) return;
    const next = { ...values, isActive: !values.isActive };
    setValues(next);
    void save(undefined, { rowVersion: saved.rowVersion, body: next, how: 'activation' });
  };

  const changedAt = saved && typeof saved.updatedAt === 'string' ? formatDateTime(saved.updatedAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : null;
  const problemList = Object.entries(problems);
  return (
    <div className="erp-page erp-form-page">
      <PageHeader title={title}
        description={changedAt ? (lastChanger ? t('engine.form.lastChangedBy', { when: changedAt, who: lastChanger }) : t('engine.form.lastChanged', { when: changedAt })) : undefined}
        actions={!creating ? (
          <span className="erp-row">
            {canHistory && <Button variant="secondary" icon={<History size={18} aria-hidden="true" />} onPress={() => setHistoryOpen(true)}>{t('engine.history.button')}</Button>}
            {activeField && canUpdate && !dirty && (
              <Button variant="secondary" icon={<Power size={18} aria-hidden="true" />} isPending={busy} onPress={toggleActive}>
                {values.isActive ? t('engine.form.deactivate') : t('engine.form.activate')}
              </Button>
            )}
            {canDelete && saved && (
              <Dialog role="alertdialog" title={t('engine.form.deleteTitle', { entity, record: recordTitle(saved) })}
                trigger={<Button variant="danger" icon={<Trash2 size={18} aria-hidden="true" />}>{t('engine.form.delete')}</Button>}
                actions={(close) => (
                  <>
                    <Button variant="secondary" onPress={close}>{t('common.cancel')}</Button>
                    <Button variant="danger" isPending={busy} onPress={() => { void remove(close); }}>{t('engine.form.deleteConfirm', { entity })}</Button>
                  </>
                )}>
                <p>{deleteText?.(saved) ?? t('engine.form.deleteBody')}</p>
              </Dialog>
            )}
          </span>
        ) : undefined} />
      {!editable && <Banner tone="info" title={t('engine.form.readOnly')}>{t('engine.form.readOnly.body')}</Banner>}
      {summary && (
        <div ref={summaryRef} tabIndex={-1} className="erp-form-summary">
          <Banner tone="danger" title={summary}>
            {problemList.length > 0 && (
              <ul>
                {problemList.map(([name, problem]) => {
                  const field = fields.find((f) => f.field === name);
                  return (
                    <li key={name}>
                      <a href={`#field-${name}`} onClick={(e) => { e.preventDefault(); document.getElementById(`field-${name}`)?.querySelector<HTMLElement>('input, button, textarea')?.focus(); }}>
                        {field ? label(field.labelKey) : name}: {problemText(problem, t, server)}
                      </a>
                    </li>
                  );
                })}
              </ul>
            )}
            {reference && <p className="erp-small">{t('common.reference', { reference })}</p>}
          </Banner>
        </div>
      )}
      <form className="erp-form" noValidate onSubmit={(e) => { void save(e); }} aria-label={title}>
        {fields.map((field) => {
          const problem = problems[field.field];
          return (
            <div key={field.field} id={`field-${field.field}`} className="erp-form-field" data-kind={field.kind}>
              <FieldInput field={field} value={values[field.field]} label={label} readOnly={readOnlyField(field)} companies={companies}
                required={schema.required.includes(field.field) || (schema.properties[field.field]?.minProperties ?? 0) > 0}
                error={problem ? problemText(problem, t, server) : null} help={help?.[field.field]} editor={editors?.[field.field]} values={values}
                onChange={(v) => setValue(field.field, v)} onBlur={() => leave(field.field)} hidden={saved !== null && !(field.field in saved)} />
            </div>
          );
        })}
        <div className="erp-form-actions">
          {editable && <Button type="submit" variant="primary" icon={<Save size={18} aria-hidden="true" />} isPending={busy}>{creating ? t('engine.form.create') : t('engine.form.save')}</Button>}
          <Button variant="secondary" onPress={onCancel}>{editable ? t('common.cancel') : t('engine.form.backToList')}</Button>
        </div>
      </form>

      {blocker.state === 'blocked' && (
        <Dialog role="alertdialog" isOpen title={t('engine.form.leaveTitle')}
          actions={() => (
            <>
              <Button variant="secondary" onPress={() => blocker.reset()}>{t('engine.form.stay')}</Button>
              <Button variant="danger" onPress={() => blocker.proceed()}>{t('engine.form.leave')}</Button>
            </>
          )}>
          <p>{t('engine.form.leaveBody')}</p>
        </Dialog>
      )}

      {conflict && (
        <ConflictDialog fields={fields} mine={values} saved={conflict} label={label}
          onKeepMine={() => { const version = conflict.rowVersion; setConflict(null); void save(undefined, { rowVersion: version }); }}
          onTakeSaved={() => { setValues(initialValues(fields, conflict, undefined)); queryClient.setQueryData(['engine', metadata.table, 'record', id], conflict); setConflict(null); }} />
      )}

      {canHistory && id && (
        <Dialog title={t('engine.history.title', { record: recordTitle(saved ?? values) })} isOpen={historyOpen} onOpenChange={setHistoryOpen} drawer>
          <HistoryPanel metadata={metadata} id={id} />
        </Dialog>
      )}
    </div>
  );
}

function problemText(problem: FieldProblem, t: ReturnType<typeof useText>['t'], server: ReturnType<typeof useText>['server']): string {
  if (problem.key === 'engine.error.server') return server(String(problem.values?.text ?? '')) ?? t('engine.error.server');
  return t(problem.key, problem.values);
}

/**
 * Someone else saved this record after the person opened it (row version stale, FR-ADM-UXA-03): their value and the
 * saved one side by side for every field that differs; keep theirs (saved over the newer version) or take the saved one.
 */
function ConflictDialog({ fields, mine, saved, label, onKeepMine, onTakeSaved }: { fields: FieldMetadata[]; mine: FormValues; saved: Row; label: LabelFn; onKeepMine: () => void; onTakeSaved: () => void }) {
  const { t } = useText();
  const differ = fields.filter((f) => f.field in saved && !same(mine[f.field], saved[f.field]));
  return (
    <Dialog role="alertdialog" isOpen title={t('engine.conflict.title')}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onTakeSaved}>{t('engine.conflict.takeSaved')}</Button>
          <Button variant="primary" onPress={onKeepMine}>{t('engine.conflict.keepMine')}</Button>
        </>
      )}>
      <p>{t('engine.conflict.body')}</p>
      <table className="erp-compare">
        <thead>
          <tr><th scope="col">{t('engine.conflict.field')}</th><th scope="col">{t('engine.conflict.yours')}</th><th scope="col">{t('engine.conflict.saved')}</th></tr>
        </thead>
        <tbody>
          {differ.map((f) => (
            <tr key={f.field}>
              <th scope="row">{label(f.labelKey)}</th>
              <td><Value field={f} value={mine[f.field]} label={label} /></td>
              <td><Value field={f} value={saved[f.field]} label={label} /></td>
            </tr>
          ))}
          {differ.length === 0 && <tr><td colSpan={3}>{t('engine.conflict.noDifference')}</td></tr>}
        </tbody>
      </table>
    </Dialog>
  );
}

interface FieldInputProps {
  field: FieldMetadata;
  value: unknown;
  label: LabelFn;
  readOnly: boolean;
  required: boolean;
  error: string | null;
  help: string | undefined;
  hidden: boolean;
  companies: Option[] | undefined;
  editor: FieldEditor | undefined;
  values: FormValues;
  onChange: (value: unknown) => void;
  onBlur: () => void;
}

/** One renderer per field kind (FR-ADM-UXA-02). JSON and binary values are shown, never edited, here. */
function FieldInput({ field, value, label, readOnly, required, error, help, hidden, companies, editor, values, onChange, onBlur }: FieldInputProps): ReactNode {
  const { t } = useText();
  const name = label(field.labelKey);
  if (editor && !hidden) return editor({ label: name, value, onChange, readOnly, error, help, values });
  const common = { label: name, isRequired: required && !readOnly, error, ...(help ? { help } : {}) };
  const company = field.references === 'org.companies' ? companies?.find((c) => c.id === value) : undefined;
  if (hidden || readOnly && (field.kind === 'I18n' || field.kind === 'Json' || field.kind === 'Bool' || field.references || field.kind === 'Enum')) {
    return (
      <div className="erp-field erp-field-readonly" role="group" aria-label={name}>
        <span className="erp-label">{name}</span>
        <span>{hidden ? <span className="erp-muted">{t('engine.value.hidden')}</span> : company
          ? <bdi>{company.label}</bdi> : <Value field={field} value={value} label={label} />}</span>
        {help && <span className="erp-help">{help}</span>}
      </div>
    );
  }
  switch (field.kind) {
    case 'Bool':
      return <SwitchField label={name} isSelected={value === true} onChange={onChange} />;
    case 'I18n': {
      const names = (value && typeof value === 'object' ? value : {}) as Record<string, string>;
      return (
        <fieldset className="erp-fieldset">
          <legend className="erp-label">{name}{required && <span className="erp-required" aria-hidden="true">*</span>}</legend>
          <div className="erp-i18n-pair">
            <TextField label={t('engine.form.inEnglish')} value={names.en ?? ''} onChange={(v) => onChange({ ...names, en: v })} onBlur={onBlur} error={error} />
            <div lang="ur" dir="rtl">
              <TextField label={t('engine.form.inUrdu')} value={names.ur ?? ''} onChange={(v) => onChange({ ...names, ur: v })} onBlur={onBlur} />
            </div>
          </div>
          {help && <span className="erp-help">{help}</span>}
        </fieldset>
      );
    }
    case 'Enum':
      return <SelectField {...common} value={typeof value === 'string' ? value : null} onChange={onChange}
        options={(field.options ?? []).map((o) => ({ id: o.value, label: label(o.labelKey) }))} />;
    case 'Date':
      return <DateField {...common} isReadOnly={readOnly} value={typeof value === 'string' && value ? safeDate(value) : null}
        onChange={(d) => { onChange(d ? d.toString() : null); }} />;
    case 'Int4':
    case 'BigInteger':
      return <NumberField {...common} isReadOnly={readOnly} {...(typeof value === 'number' ? { value } : {})} onChange={(n) => onChange(Number.isNaN(n) ? null : n)} />;
    case 'Uuid':
      if (field.references === 'org.companies' && companies) {
        return <SelectField {...common} value={typeof value === 'string' ? value : null} onChange={onChange} options={companies} />;
      }
      return <TextField {...common} isReadOnly isCode value={typeof value === 'string' ? value : ''} />;
    default:
      if (DECIMAL_KINDS.has(field.kind)) {
        return <TextField {...common} isReadOnly={readOnly} inputMode="numeric" isCode value={value === null || value === undefined ? '' : String(value)} onChange={onChange} />;
      }
      if (field.kind === 'Json' || field.kind === 'TextArray' || field.kind === 'UuidArray' || field.kind === 'Bytea') {
        return <div className="erp-field erp-field-readonly"><span className="erp-label">{name}</span><Value field={field} value={value} label={label} /></div>;
      }
      return (
        <TextField {...common} isReadOnly={readOnly} isCode={field.kind === 'Code' || field.kind === 'FixedText'} value={typeof value === 'string' ? value : ''}
          onChange={onChange} onBlur={onBlur} {...(field.maxLength ? { maxLength: field.maxLength } : {})} />
      );
  }
}

function without<T>(record: Record<string, T>, key: string): Record<string, T> {
  return Object.fromEntries(Object.entries(record).filter(([k]) => k !== key));
}

function safeDate(value: string): DateValue | null {
  try {
    return parseDate(value.slice(0, 10));
  } catch {
    return null;
  }
}
