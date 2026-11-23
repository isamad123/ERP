// Document templates (slice S1.15d Build item 12; FR-KRN-DOC-05, FR-KRN-LOC-07; S1.15 ruling Q10): per template its versions (draft, active, retired), a text editor with the
// placeholders the record type offers, one template per language (a variant is a copy in the other language), the letterhead that applies, a preview with invented sample
// values (a PDF opens in a new tab: the application's content policy allows no embedded frame), and activation. The server checks every body (Scriban syntax, field names,
// size) and the salary right; the screen shows its answers as confirmed, never assumed.
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Plus } from 'lucide-react';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, TextAreaField, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { FORMATS, templatesApi, type DocPreview, type EntityFields, type Format, type TemplateRow, type TemplateVersion } from '../../api/templates';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { useRefused } from './config-parts';
import { ConfirmDialog, PlaceholderList } from './tpl-parts';
import { NewTemplate } from './doctemplates-new';
import './doctemplates-messages';
import './users.css';
import './configure.css';

export default function DocumentTemplatesScreen() {
  const { t } = useText();
  useDocumentTitle(t('tpl.title'));
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const open = params.get('template');
  const [search, setSearch] = useState('');
  const [creating, setCreating] = useState<Partial<TemplateRow & { body: string; format: Format }> | null>(null);
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });
  const list = useQuery({ queryKey: ['doc', 'templates'], queryFn: templatesApi.list, refetchOnMount: 'always' });
  const fields = useQuery({ queryKey: ['doc', 'entity-fields'], queryFn: templatesApi.entityFields, retry: false });
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const shown = useMemo(() => (list.data ?? []).filter((x) => `${x.code} ${nameOf(x.nameI18n, locale)}`.toLowerCase().includes(search.trim().toLowerCase())), [list.data, search, locale]);
  const selected = list.data?.find((x) => x.id === open) ?? null;
  const canCreate = writable && can('doc.templates.create');
  return (
    <div className="erp-page">
      <PageHeader title={t('tpl.title')} description={t('tpl.description')} actions={canCreate ? <Button variant="primary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => setCreating({})}>{t('tpl.new')}</Button> : undefined} />
      {list.isPending && !problem && <Skeleton lines={6} />}
      {problem && (
        <ErrorPanel message={problem.status === 403 ? t('tpl.denied') : t('tpl.loadError')} reference={problem.reference}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {list.isSuccess && list.data.length === 0 && <EmptyState title={t('tpl.empty')}>{t('tpl.empty.body')}</EmptyState>}
      {list.isSuccess && list.data.length > 0 && (
        <div className="erp-config-split">
          <div className="erp-stack">
            <TextField label={t('tpl.search')} value={search} onChange={setSearch} />
            {shown.length === 0 && <p className="erp-muted">{t('tpl.search.none')}</p>}
            <ul className="erp-config-list erp-config-scroll" aria-label={t('tpl.list.title')}>
              {shown.map((x) => (
                <li key={x.id}>
                  <div className="erp-config-card">
                    <span className="erp-stack">
                      <span className="erp-settings-name"><bdi>{nameOf(x.nameI18n, locale)}</bdi></span>
                      <span className="erp-small erp-muted erp-code"><bdi>{x.code}</bdi></span>
                    </span>
                    <span className="erp-row erp-wrap">
                      <StatusChip status={x.isActive ? 'active' : 'inactive'} label={t(x.isActive ? 'tpl.status.active' : 'tpl.status.inactive')} />
                      <span className="erp-small">{t(`tpl.kind.${x.kind}` as MessageKey)} · {t(`tpl.language.${x.language}` as MessageKey)}</span>
                    </span>
                    <Button variant={open === x.id ? 'primary' : 'secondary'} aria-pressed={open === x.id} aria-label={t('tpl.open.aria', { name: nameOf(x.nameI18n, locale) })} onPress={() => patch({ template: x.id, version: null })}>{t('tpl.open')}</Button>
                  </div>
                </li>
              ))}
            </ul>
          </div>
          {selected
            ? <TemplateDetail key={selected.id} template={selected} all={list.data} fields={fields.data ?? []} versionId={params.get('version')} onVersion={(v) => patch({ version: v })} onVariant={setCreating} />
            : <EmptyState title={t('tpl.pick')}>{t('tpl.pick.body')}</EmptyState>}
        </div>
      )}
      {creating && <NewTemplate seed={creating} fields={fields.data ?? []} onClose={() => setCreating(null)} onCreated={async (id) => { await queryClient.invalidateQueries({ queryKey: ['doc'] }); setCreating(null); patch({ template: id, version: null }); }} />}
    </div>
  );
}

function TemplateDetail({ template, all, fields, versionId, onVersion, onVariant }: { template: TemplateRow; all: TemplateRow[]; fields: EntityFields[]; versionId: string | null; onVersion: (id: string) => void; onVariant: (seed: Partial<TemplateRow & { body: string; format: Format }>) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const versions = useQuery({ queryKey: ['doc', 'versions', template.id], queryFn: () => templatesApi.versions(template.id), refetchOnMount: 'always' });
  const problem = versions.error instanceof ApiProblem ? versions.error : null;
  if (versions.isPending && !problem) return <Skeleton lines={5} />;
  if (problem || !versions.data) {
    return <ErrorPanel message={problem?.status === 403 ? t('tpl.denied') : t('tpl.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void versions.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  const list = versions.data;
  const draft = list.find((v) => v.status === 'draft');
  const shown = list.find((v) => v.id === versionId) ?? draft ?? list.find((v) => v.id === template.activeVersionId) ?? list[0] ?? null;
  const letterhead = all.filter((x) => x.kind === 'part' && x.code === 'letterhead' && x.isActive && x.language === template.language && (x.companyId === null || x.companyId === template.companyId))
    .sort((a, b) => Number(b.companyId !== null) - Number(a.companyId !== null))[0] ?? null;
  const entity = fields.find((f) => f.entityType === template.entityType);
  return (
    <section className="erp-stack" aria-labelledby="tpl-name">
      <div className="erp-row erp-wrap">
        <h2 id="tpl-name" className="erp-h3"><bdi>{nameOf(template.nameI18n, locale)}</bdi></h2>
        <StatusChip status={template.isActive ? 'active' : 'inactive'} label={t(template.isActive ? 'tpl.status.active' : 'tpl.status.inactive')} />
      </div>
      <p className="erp-small erp-muted">{t('tpl.facts', { kind: t(`tpl.kind.${template.kind}` as MessageKey), language: t(`tpl.language.${template.language}` as MessageKey), source: t(`tpl.source.${template.source}` as MessageKey) })}</p>
      {template.kind !== 'part' && <p className="erp-small">{letterhead ? t('tpl.letterhead.uses', { name: nameOf(letterhead.nameI18n, locale) }) : t('tpl.letterhead.none')}</p>}
      {template.kind === 'part' && template.code === 'letterhead' && <Banner tone="info">{t('tpl.letterhead.part')}</Banner>}
      <TemplateVersions key={shown?.id ?? 'none'} template={template} versions={list} shown={shown} entity={entity ?? null} onVersion={onVersion} onVariant={onVariant} />
    </section>
  );
}

const statusChip = (s: TemplateVersion['status']) => (s === 'active' ? 'active' : s === 'draft' ? 'draft' : 'inactive');

function TemplateVersions({ template, versions, shown, entity, onVersion, onVariant }: { template: TemplateRow; versions: TemplateVersion[]; shown: TemplateVersion | null; entity: EntityFields | null; onVersion: (id: string) => void; onVariant: (seed: Partial<TemplateRow & { body: string; format: Format }>) => void }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const draft = versions.find((v) => v.status === 'draft');
  const editable = writable && can('doc.template_versions.update') && shown?.status === 'draft';
  const [body, setBody] = useState(shown?.body ?? '');
  const [format, setFormat] = useState<Format>(shown?.outputFormat ?? 'pdf');
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ result: DocPreview; url: string | null } | null>(null);
  const [activating, setActivating] = useState(false);
  const [retiring, setRetiring] = useState(false);
  const dirty = shown ? body !== shown.body || format !== shown.outputFormat : false;
  const dir = template.language === 'ur' ? 'rtl' : 'ltr';
  const formatOptions: Option[] = FORMATS.map((f) => ({ id: f, label: t(`tpl.format.${f}` as MessageKey) }));
  const reload = async () => { await queryClient.invalidateQueries({ queryKey: ['doc'] }); };
  const act = async (name: string, run: () => Promise<void>) => {
    setBusy(name);
    setFailure(null);
    try { await run(); } catch (error) { setFailure(refused(error).summary); } finally { setBusy(null); }
  };
  const items = (entity?.fields ?? []).map((f) => ({ name: f.path, ...(f.fieldClass === 'general' ? {} : { note: t(f.fieldClass === 'salary' ? 'tpl.placeholder.salary' : 'tpl.placeholder.personal') }) }));
  const doPreview = () => act('preview', async () => {
    const result = await templatesApi.preview({ body, outputFormat: format, language: template.language, entityType: template.entityType, companyId: template.companyId });
    const url = result.pdf ? URL.createObjectURL(new Blob([Uint8Array.from(atob(result.pdf), (c) => c.charCodeAt(0))], { type: 'application/pdf' })) : null;
    setPreview((old) => { if (old?.url) URL.revokeObjectURL(old.url); return { result, url }; });
  });
  return (
    <>
      <h3 className="erp-h3">{t('tpl.versions.title')}</h3>
      <ul className="erp-route-versions" aria-label={t('tpl.versions.title')}>
        {versions.map((v) => (
          <li key={v.id} className="erp-config-card">
            <span className="erp-row erp-wrap"><strong>{t('tpl.version', { n: v.version })}</strong><StatusChip status={statusChip(v.status)} label={t(`tpl.version.status.${v.status}` as MessageKey)} /></span>
            <Button variant={shown?.id === v.id ? 'primary' : 'secondary'} aria-pressed={shown?.id === v.id} aria-label={t('tpl.version.open', { n: v.version })} onPress={() => onVersion(v.id)}>{t('tpl.open')}</Button>
          </li>
        ))}
      </ul>
      {failure && <Banner tone="danger">{failure}</Banner>}
      {writable && can('doc.template_versions.create') && !draft && shown && (
        <div><Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} isPending={busy === 'draft'} onPress={() => { void act('draft', async () => { const v = await templatesApi.newDraft(template.id, { body: shown.body, outputFormat: shown.outputFormat }); await reload(); onVersion(v.id); toast.success(t('tpl.draft.created')); }); }}>{t('tpl.draft.newFrom', { n: shown.version })}</Button></div>
      )}
      {shown && shown.status !== 'draft' && <Banner tone="info">{t('tpl.readonly', { status: t(`tpl.version.status.${shown.status}` as MessageKey).toLowerCase() })}</Banner>}
      {shown && (
        <>
          <div dir={dir} className="erp-stack">
            <TextAreaField label={t('tpl.body')} help={t('tpl.body.help')} value={body} onChange={setBody} isReadOnly={!editable} />
          </div>
          <SelectField label={t('tpl.format')} options={formatOptions} value={format} onChange={(f) => setFormat(f as Format)} isDisabled={!editable} />
          {editable && <PlaceholderList label={t('tpl.placeholders')} items={items} onPick={(name) => setBody(`${body}{{ ${name} }}`)} />}
          {editable && items.length === 0 && <p className="erp-small erp-muted">{t('tpl.placeholders.none')}</p>}
          {dirty && editable && <p className="erp-small erp-muted">{t('tpl.unsaved')}</p>}
          <div className="erp-route-actions">
            {editable && <Button variant="primary" isPending={busy === 'save'} isDisabled={!dirty} onPress={() => { void act('save', async () => { await templatesApi.saveDraft(shown.id, { body, outputFormat: format, rowVersion: shown.rowVersion }); await reload(); toast.success(t('tpl.saved')); }); }}>{t('tpl.save')}</Button>}
            <Button variant="secondary" isPending={busy === 'preview'} isDisabled={body.trim() === ''} onPress={() => { void doPreview(); }}>{t('tpl.preview')}</Button>
            {editable && <Button variant="secondary" isDisabled={dirty || busy !== null} onPress={() => setActivating(true)}>{t('tpl.activate')}</Button>}
            {writable && can('doc.templates.update') && template.isActive && <Button variant="quiet" onPress={() => setRetiring(true)}>{t('tpl.retire')}</Button>}
            {writable && can('doc.templates.create') && <Button variant="quiet" onPress={() => onVariant({ kind: template.kind, entityType: template.entityType, language: template.language === 'ur' ? 'en' : 'ur', body: shown.body, format: shown.outputFormat, code: `${template.code}-${template.language === 'ur' ? 'en' : 'ur'}`, companyId: template.companyId })}>{t('tpl.variant')}</Button>}
          </div>
          {preview && (
            <section className="erp-stack" aria-label={t('tpl.preview.title')}>
              <h3 className="erp-h3">{t('tpl.preview.title')}</h3>
              <p>{t('tpl.preview.fields', { count: preview.result.usedFields.length })} {preview.result.usedFields.length > 0 && <span className="erp-code"><bdi>{preview.result.usedFields.join(', ')}</bdi></span>}</p>
              {preview.url ? <a href={preview.url} target="_blank" rel="noopener noreferrer" className="erp-link">{t('tpl.preview.pdf')}</a> : <p className="erp-muted">{t('tpl.preview.nopdf')}</p>}
              <p className="erp-small erp-muted">{t('tpl.preview.note')}</p>
            </section>
          )}
        </>
      )}
      {shown && activating && (
        <ConfirmDialog title={t('tpl.activate.title', { n: shown.version })} effect={t('tpl.activate.effect')} confirmLabel={t('tpl.activate.confirm', { n: shown.version })} onClose={() => setActivating(false)}
          onConfirm={async () => { await templatesApi.activate(shown.id, shown.rowVersion); await reload(); toast.success(t('tpl.activated', { n: shown.version })); }} />
      )}
      {retiring && (
        <ConfirmDialog title={t('tpl.retire.title')} effect={t('tpl.retire.effect')} confirmLabel={t('tpl.retire.confirm')} danger onClose={() => setRetiring(false)}
          onConfirm={async () => { await templatesApi.retire(template.id, template.rowVersion); await reload(); toast.success(t('tpl.retired')); }} />
      )}
    </>
  );
}

