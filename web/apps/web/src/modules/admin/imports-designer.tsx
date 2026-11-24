// The template designer (slice S1.16a Build item 5; FR-ADM-INT-04): map each column of an import type to a column of the file,
// set transformations and defaults, choose between "create" and "create or update", test with a sample file (validation only,
// nothing is saved) and save; a change makes a new version. The product ships one default template per type.
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { Banner, Button, CheckboxField, EmptyState, ErrorPanel, PageHeader, RadioGroupField, SelectField, Skeleton, StatusChip, TextField, useToast } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import {
  importsApi, type ImportColumn, type ImportFormat, type ImportMode, type ImportTemplate, type ImportType, type MappingColumn, type TemplateMapping, type TestResult,
} from '../../api/imports';
import { useShell } from '../../shell/context';
import { FilePicker, IMPORTS_PATH, useNameOf, useRefusalText } from './imports-parts';
import './users.css';

const TRANSFORMS = ['trim', 'upper', 'lower', 'date', 'number'] as const;
type Transform = (typeof TRANSFORMS)[number];
const DEFAULT_DATE = 'yyyy-MM-dd';
const DEFAULT_CULTURE = 'en-US';

interface ColumnState { source: string; transforms: Set<Transform>; dateFormat: string; culture: string; default: string }

export function mappingOf(type: ImportType, state: Record<string, ColumnState>, format: ImportFormat, delimiter: string): TemplateMapping {
  const columns: MappingColumn[] = type.columns.map((c) => {
    const s = state[c.field];
    const transform: string[] = [];
    if (s) {
      for (const kind of TRANSFORMS) {
        if (!s.transforms.has(kind)) continue;
        transform.push(kind === 'date' ? `date:${s.dateFormat.trim() || DEFAULT_DATE}` : kind === 'number' ? `number:${s.culture.trim() || DEFAULT_CULTURE}` : kind);
      }
    }
    return { field: c.field, source: s?.source.trim() ?? '', transform, default: s && s.default.trim() !== '' ? s.default.trim() : null };
  });
  return { delimiter: format === 'csv' && (delimiter === ',' || delimiter === ';') ? delimiter : null, sheet: null, headerRow: 1, columns };
}

function initialState(type: ImportType, template: ImportTemplate | null, headerOf: (c: ImportColumn) => string): Record<string, ColumnState> {
  const out: Record<string, ColumnState> = {};
  for (const c of type.columns) {
    const saved = template?.mapping.columns.find((m) => m.field === c.field);
    const transforms = new Set<Transform>();
    let dateFormat = DEFAULT_DATE;
    let culture = DEFAULT_CULTURE;
    for (const item of saved?.transform ?? []) {
      const [kind, arg] = [item.split(':')[0] as Transform, item.includes(':') ? item.slice(item.indexOf(':') + 1) : ''];
      if (TRANSFORMS.includes(kind)) transforms.add(kind);
      if (kind === 'date' && arg) dateFormat = arg;
      if (kind === 'number' && arg) culture = arg;
    }
    out[c.field] = { source: saved?.source ?? headerOf(c), transforms, dateFormat, culture, default: saved?.default ?? '' };
  }
  return out;
}

export function TemplateDesigner() {
  const { t } = useText();
  const { can, writable } = useShell();
  const nameOf = useNameOf();
  useDocumentTitle(t('imp.designer.title'));
  const [params, setParams] = useSearchParams();
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const types = useQuery({ queryKey: ['itg', 'imports', 'types'], queryFn: importsApi.types, refetchOnMount: 'always' });
  const typeCode = params.get('type') ?? types.data?.[0]?.code ?? null;
  const type = types.data?.find((x) => x.code === typeCode) ?? null;
  const templates = useQuery({ queryKey: ['itg', 'imports', 'templates', typeCode], queryFn: () => importsApi.templates(typeCode ?? undefined), enabled: !!typeCode, refetchOnMount: 'always' });
  const problem = [types.error, templates.error].find((e) => e instanceof ApiProblem) as ApiProblem | undefined;
  const canDesign = writable && can('itg.imports.design');
  const selected = editing && editing !== 'new' ? templates.data?.find((x) => x.id === editing) ?? null : null;

  return (
    <div className="erp-page">
      <PageHeader title={t('imp.designer.title')} description={t('imp.designer.description')} breadcrumbs={[{ label: t('imp.title'), href: IMPORTS_PATH }, { label: t('imp.designer.title') }]}
        actions={canDesign && type ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setEditing('new')}>{t('imp.designer.new')}</Button> : undefined} />
      {(types.isPending || (!!typeCode && templates.isPending)) && !problem && <Skeleton lines={5} />}
      {problem && (
        <ErrorPanel message={problem.status === 403 ? t('imp.denied') : t('imp.loadError')} reference={problem.reference}
          action={<Button variant="primary" onPress={() => { void types.refetch(); void templates.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {types.isSuccess && types.data.length === 0 && <EmptyState title={t('imp.types.empty')}>{t('imp.types.empty.body')}</EmptyState>}
      {types.isSuccess && type && (
        <>
          <SelectField label={t('imp.type')} value={typeCode} onChange={(v) => { setEditing(null); setParams({ type: v }, { replace: true }); }}
            options={types.data.map((x) => ({ id: x.code, label: nameOf(x.name, x.code) }))} />
          {templates.isSuccess && templates.data.length === 0 && <EmptyState title={t('imp.templates.empty')}>{t('imp.templates.empty.body')}</EmptyState>}
          {templates.isSuccess && templates.data.length > 0 && (
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('imp.designer.list')}</caption>
              <thead><tr>
                <th scope="col">{t('imp.template.name')}</th><th scope="col">{t('imp.template.format')}</th><th scope="col">{t('imp.template.mode')}</th>
                <th scope="col">{t('imp.col.status')}</th><th scope="col"><span className="erp-visually-hidden">{t('imp.col.actions')}</span></th>
              </tr></thead>
              <tbody>
                {templates.data.map((x) => (
                  <tr key={x.id}>
                    <th scope="row"><bdi>{nameOf(x.name, x.code)}</bdi> <span className="erp-small erp-muted erp-code">{x.code}</span>{x.isDefault && <div className="erp-small erp-muted">{t('imp.template.shipped')}</div>}</th>
                    <td>{x.format === 'csv' ? 'CSV' : 'Excel'}</td>
                    <td>{t(x.mode === 'create' ? 'imp.mode.create' : 'imp.mode.update')}</td>
                    <td><StatusChip status={x.isActive ? 'active' : 'inactive'} label={t(x.isActive ? 'imp.template.active' : 'imp.template.inactive')} /> <span className="erp-small erp-muted">{t('imp.template.version', { version: formatInteger(x.version) })}</span></td>
                    <td><Button variant="secondary" aria-label={t(canDesign ? 'imp.designer.edit.aria' : 'imp.designer.view.aria', { name: nameOf(x.name, x.code) })} onPress={() => setEditing(x.id)}>{t(canDesign ? 'imp.designer.edit' : 'imp.designer.view')}</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {editing && (editing === 'new' || selected) && (
            <TemplateForm key={editing + (selected?.rowVersion ?? '')} type={type} template={selected} canDesign={canDesign} onClose={() => setEditing(null)} onSaved={(saved) => setEditing(saved.id)} />
          )}
        </>
      )}
    </div>
  );
}

function TemplateForm({ type, template, canDesign, onClose, onSaved }: { type: ImportType; template: ImportTemplate | null; canDesign: boolean; onClose: () => void; onSaved: (saved: ImportTemplate) => void }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const nameOf = useNameOf();
  const toast = useToast();
  const refusal = useRefusalText();
  const queryClient = useQueryClient();
  const headerOf = (c: ImportColumn) => c.label.en ?? nameOf(c.label, c.name);
  const [code, setCode] = useState(template?.code ?? '');
  const [nameEn, setNameEn] = useState(template?.name.en ?? '');
  const [nameUr, setNameUr] = useState(template?.name.ur ?? '');
  const [format, setFormat] = useState<ImportFormat>(template?.format ?? 'csv');
  const [mode, setMode] = useState<ImportMode>(template?.mode ?? 'create');
  const [delimiter, setDelimiter] = useState<string>(template?.mapping.delimiter ?? 'auto');
  const [columns, setColumns] = useState(() => initialState(type, template, headerOf));
  const [error, setError] = useState<string | null>(null);
  const edit = (field: string, patch: Partial<ColumnState>) => setColumns((c) => ({ ...c, [field]: { ...(c[field] as ColumnState), ...patch } }));
  const missing = type.columns.filter((c) => c.required && (columns[c.field]?.source.trim() ?? '') === '' && (columns[c.field]?.default.trim() ?? '') === '');
  const ready = canDesign && nameEn.trim() !== '' && (template !== null || code.trim() !== '') && missing.length === 0;

  const save = useMutation({
    mutationFn: () => {
      const mapping = mappingOf(type, columns, format, delimiter);
      const name = { en: nameEn.trim(), ...(nameUr.trim() ? { ur: nameUr.trim() } : {}) };
      return template
        ? importsApi.updateTemplate(template.id, { name, mode, mapping, rowVersion: template.rowVersion })
        : importsApi.createTemplate({ code: code.trim(), name, entityType: type.code, format, mode, mapping });
    },
    onSuccess: (saved) => {
      setError(null);
      void queryClient.invalidateQueries({ queryKey: ['itg', 'imports', 'templates'] });
      toast.success(t(template ? 'imp.designer.updated' : 'imp.designer.created'));
      onSaved(saved);
    },
    onError: (e) => setError(refusal(e)),
  });

  return (
    <section className="erp-stack" aria-labelledby="imp-form">
      <h2 id="imp-form" className="erp-h3">{template ? t('imp.designer.editTitle', { name: nameOf(template.name, template.code) }) : t('imp.designer.newTitle')}</h2>
      {error && <Banner tone="danger" title={t('imp.designer.failed')}>{error}</Banner>}
      <div className="erp-filters">
        <TextField label={t('imp.template.code')} help={t('imp.template.code.help')} isRequired isCode value={code} onChange={setCode} isDisabled={template !== null || !canDesign} maxLength={64} />
        <TextField label={t('imp.template.nameEn')} isRequired value={nameEn} onChange={setNameEn} isDisabled={!canDesign} maxLength={200} />
        <TextField label={t('imp.template.nameUr')} value={nameUr} onChange={setNameUr} isDisabled={!canDesign} maxLength={200} />
        <SelectField label={t('imp.template.format')} value={format} onChange={(v) => setFormat(v as ImportFormat)} isDisabled={template !== null || !canDesign}
          options={[{ id: 'csv', label: 'CSV' }, { id: 'xlsx', label: 'Excel (.xlsx)' }]} />
        {format === 'csv' && (
          <SelectField label={t('imp.template.delimiter')} help={t('imp.template.delimiter.help')} value={delimiter} onChange={setDelimiter} isDisabled={!canDesign}
            options={[{ id: 'auto', label: t('imp.delimiter.auto') }, { id: ',', label: t('imp.delimiter.comma') }, { id: ';', label: t('imp.delimiter.semicolon') }]} />
        )}
      </div>
      <RadioGroupField label={t('imp.template.mode')} value={mode} onChange={(v) => setMode(v as ImportMode)}
        options={[{ id: 'create', label: t('imp.mode.create'), detail: t('imp.mode.create.explain') }, ...(type.canUpdate ? [{ id: 'create_or_update', label: t('imp.mode.update'), detail: t('imp.mode.update.explain') }] : [])]} />
      <h3 className="erp-h3">{t('imp.designer.mapping')}</h3>
      <p className="erp-muted">{t('imp.designer.mapping.help')}</p>
      {type.columns.map((c) => {
        const s = columns[c.field] as ColumnState;
        return (
          <fieldset key={c.field} className="erp-stack" disabled={!canDesign}>
            <legend><strong><bdi>{nameOf(c.label, c.name)}</bdi></strong> <span className="erp-small erp-muted erp-code">{c.field}</span> {c.required && <span className="erp-small">{t('imp.column.required')}</span>}</legend>
            <div className="erp-filters">
              <TextField label={t('imp.column.source')} isRequired={c.required && s.default.trim() === ''} value={s.source} onChange={(v) => edit(c.field, { source: v })} />
              <TextField label={t('imp.column.default')} help={t('imp.column.default.help')} value={s.default} onChange={(v) => edit(c.field, { default: v })} />
            </div>
            <div className="erp-row erp-wrap" role="group" aria-label={t('imp.column.transforms', { name: nameOf(c.label, c.name) })}>
              {TRANSFORMS.map((kind) => (
                <CheckboxField key={kind} label={t(`imp.transform.${kind}` as MessageKey)} isSelected={s.transforms.has(kind)}
                  onChange={(on) => { const next = new Set(s.transforms); if (on) next.add(kind); else next.delete(kind); edit(c.field, { transforms: next }); }} />
              ))}
            </div>
            {s.transforms.has('date') && <TextField label={t('imp.column.dateFormat')} help={t('imp.column.dateFormat.help')} value={s.dateFormat} onChange={(v) => edit(c.field, { dateFormat: v })} />}
            {s.transforms.has('number') && <TextField label={t('imp.column.culture')} help={t('imp.column.culture.help')} value={s.culture} onChange={(v) => edit(c.field, { culture: v })} />}
          </fieldset>
        );
      })}
      {missing.length > 0 && <p className="erp-muted" role="status">{t('imp.designer.missing', { count: formatInteger(missing.length, numbers) })}</p>}
      <div className="erp-row erp-wrap">
        {canDesign && <Button variant="primary" isDisabled={!ready} isPending={save.isPending} onPress={() => save.mutate()}>{t(template ? 'imp.designer.save' : 'imp.designer.create')}</Button>}
        <Button variant="secondary" onPress={onClose}>{t('common.close')}</Button>
      </div>
      {template && <SampleTest template={template} />}
      {!template && <p className="erp-muted">{t('imp.test.saveFirst')}</p>}
    </section>
  );
}

function SampleTest({ template }: { template: ImportTemplate }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const refusal = useRefusalText();
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState<string | null>(null);
  const run = useMutation({ mutationFn: () => importsApi.testTemplate(template.id, file as File), onSuccess: () => setError(null), onError: (e) => setError(refusal(e)) });
  const result: TestResult | undefined = run.data;
  const sampleKeys = useMemo(() => Array.from(new Set((result?.sample ?? []).flatMap((r) => Object.keys(r.values)))), [result]);
  return (
    <section className="erp-stack" aria-labelledby="imp-test">
      <h3 id="imp-test" className="erp-h3">{t('imp.test.title')}</h3>
      <p className="erp-muted">{t('imp.test.help')}</p>
      <FilePicker label={t('imp.test.file')} accept={template.format === 'xlsx' ? '.xlsx' : '.csv,text/csv'} onChange={(f) => { setFile(f); setError(null); }} error={error} />
      <div><Button variant="secondary" isDisabled={!file} isPending={run.isPending} onPress={() => run.mutate()}>{t('imp.test.run')}</Button></div>
      {result && (
        <div className="erp-stack" role="status">
          <p><strong>{t('imp.test.result', { total: formatInteger(result.totalRows, numbers), errors: formatInteger(result.errorRows, numbers) })}</strong></p>
          {result.errors.length > 0 && (
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('imp.errors.title')}</caption>
              <thead><tr><th scope="col">{t('imp.errors.row')}</th><th scope="col">{t('imp.errors.field')}</th><th scope="col">{t('imp.errors.message')}</th></tr></thead>
              <tbody>
                {result.errors.map((e, index) => (
                  <tr key={`${e.rowNo}-${index}`}><th scope="row">{formatInteger(e.rowNo, numbers)}</th><td>{e.field ? <bdi className="erp-code">{e.field}</bdi> : <span className="erp-muted">{t('imp.errors.wholeRow')}</span>}</td><td><bdi>{e.message}</bdi></td></tr>
                ))}
              </tbody>
            </table>
          )}
          {result.sample.length > 0 && (
            <>
              <h4 className="erp-h3">{t('imp.test.sample')}</h4>
              <table className="erp-table">
                <caption className="erp-visually-hidden">{t('imp.test.sample')}</caption>
                <thead><tr><th scope="col">{t('imp.errors.row')}</th>{sampleKeys.map((k) => <th key={k} scope="col"><bdi className="erp-code">{k}</bdi></th>)}</tr></thead>
                <tbody>
                  {result.sample.map((r) => (
                    <tr key={r.rowNo}><th scope="row">{formatInteger(r.rowNo, numbers)}</th>{sampleKeys.map((k) => <td key={k}><bdi>{r.values[k] === undefined || r.values[k] === null ? '' : String(r.values[k])}</bdi></td>)}</tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </div>
      )}
    </section>
  );
}
