// The import centre (slice S1.16a Build item 4; FR-ADM-DAT-01, FR-KRN-INT-03, FR-KRN-INT-04): choose a type and template, download
// the template file, upload a file, and follow the batches. The server decides everything (limits, scan, permissions); the
// screen shows its answers in plain words.
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, Upload } from 'lucide-react';
import { Banner, Button, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, useToast } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { BATCH_STATUSES, importsApi, isRunning, POLL_MS, type ImportBatch, type ImportFormat, type ImportType } from '../../api/imports';
import { useShell } from '../../shell/context';
import { BATCHES_KEY, BatchStatusChip, FilePicker, IMPORTS_PATH, useNameOf, useRefusalText } from './imports-parts';
import './users.css';

export function ImportCentre() {
  const { t } = useText();
  const { can, writable } = useShell();
  useDocumentTitle(t('imp.title'));
  const types = useQuery({ queryKey: ['itg', 'imports', 'types'], queryFn: importsApi.types, refetchOnMount: 'always' });
  const problem = types.error instanceof ApiProblem ? types.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('imp.title')} description={t('imp.description')}
        actions={can('itg.imports.design') ? <Link href={`${IMPORTS_PATH}/templates`}>{t('imp.designer.open')}</Link> : undefined} />
      {types.isPending && <Skeleton lines={5} />}
      {types.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('imp.denied') : t('imp.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void types.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {types.isSuccess && types.data.length === 0 && <EmptyState title={t('imp.types.empty')}>{t('imp.types.empty.body')}</EmptyState>}
      {types.isSuccess && types.data.length > 0 && (
        <>
          {writable ? <NewImport types={types.data} /> : <Banner tone="warning" title={t('imp.readOnly')}>{t('imp.readOnly.body')}</Banner>}
          <Batches />
        </>
      )}
    </div>
  );
}

function NewImport({ types }: { types: ImportType[] }) {
  const { t } = useText();
  const nameOf = useNameOf();
  const { numbers } = useLocaleSettings();
  const toast = useToast();
  const navigate = useNavigate();
  const refusal = useRefusalText();
  const queryClient = useQueryClient();
  const [typeCode, setTypeCode] = useState<string | null>(types[0]?.code ?? null);
  const [templateId, setTemplateId] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState<string | null>(null);
  const type = types.find((x) => x.code === typeCode) ?? null;
  const templates = useQuery({ queryKey: ['itg', 'imports', 'templates', typeCode], queryFn: () => importsApi.templates(typeCode ?? undefined), enabled: !!typeCode, refetchOnMount: 'always' });
  const usable = useMemo(() => (templates.data ?? []).filter((x) => x.isActive), [templates.data]);
  const template = usable.find((x) => x.id === templateId) ?? usable.find((x) => x.isDefault) ?? usable[0] ?? null;
  const send = useMutation({
    mutationFn: () => importsApi.upload(file as File, (template as { id: string }).id),
    onSuccess: (batch) => {
      void queryClient.invalidateQueries({ queryKey: BATCHES_KEY });
      toast.success(t('imp.uploaded'));
      void navigate(`${IMPORTS_PATH}/batches/${batch.id}`);
    },
    onError: (e) => setError(refusal(e)),
  });
  const download = async (format: ImportFormat) => {
    if (!template) return;
    try { await importsApi.downloadTemplate(template, format); } catch (e) { toast.error(refusal(e)); }
  };
  const accept = template?.format === 'xlsx' ? '.xlsx' : '.csv,text/csv';
  return (
    <section className="erp-stack" aria-labelledby="imp-new">
      <h2 id="imp-new" className="erp-h3">{t('imp.new')}</h2>
      <div className="erp-filters">
        <SelectField label={t('imp.type')} value={typeCode} onChange={(v) => { setTypeCode(v); setTemplateId(null); setError(null); }}
          options={types.map((x) => ({ id: x.code, label: nameOf(x.name, x.code), detail: x.canRun ? undefined : t('imp.type.denied') }))} />
        <SelectField label={t('imp.template')} help={t('imp.template.help')} value={template?.id ?? null} onChange={setTemplateId}
          options={usable.map((x) => ({ id: x.id, label: nameOf(x.name, x.code), detail: t('imp.template.version', { version: formatInteger(x.version, numbers) }) }))} />
      </div>
      {type && !type.canRun && <Banner tone="warning" title={t('imp.type.denied')}>{t('imp.type.denied.body')}</Banner>}
      {type?.canRun && templates.isSuccess && usable.length === 0 && <EmptyState title={t('imp.templates.empty')}>{t('imp.templates.empty.body')}</EmptyState>}
      {type?.canRun && template && (
        <div className="erp-stack">
          <p>{t(template.mode === 'create' ? 'imp.mode.create.explain' : 'imp.mode.update.explain')}</p>
          <div className="erp-row erp-wrap">
            <Button variant="secondary" icon={<Download size={16} aria-hidden="true" />} onPress={() => { void download('csv'); }}>{t('imp.template.download.csv')}</Button>
            <Button variant="secondary" icon={<Download size={16} aria-hidden="true" />} onPress={() => { void download('xlsx'); }}>{t('imp.template.download.xlsx')}</Button>
          </div>
          <FilePicker label={t('imp.file')} help={t('imp.file.help')} accept={accept} onChange={(f) => { setFile(f); setError(null); }} error={error} />
          <div><Button variant="primary" icon={<Upload size={16} aria-hidden="true" />} isPending={send.isPending} isDisabled={!file} onPress={() => send.mutate()}>{t('imp.upload')}</Button></div>
        </div>
      )}
    </section>
  );
}

function Batches() {
  const { t } = useText();
  const nameOf = useNameOf();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const [status, setStatus] = useState('any');
  const params = { status: status === 'any' ? undefined : status };
  const list = useQuery({
    queryKey: [...BATCHES_KEY, params],
    queryFn: ({ signal }) => importsApi.batches(params, signal),
    refetchOnMount: 'always',
    refetchInterval: (query) => ((query.state.data?.items ?? []).some((b) => isRunning(b.status)) ? POLL_MS : false),
  });
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const rows: ImportBatch[] = list.data?.items ?? [];
  const count = (value: number | null) => (value === null ? '–' : formatInteger(value, numbers));
  return (
    <section className="erp-stack" aria-labelledby="imp-batches">
      <h2 id="imp-batches" className="erp-h3">{t('imp.batches')}</h2>
      <div className="erp-filters" role="search" aria-label={t('imp.filters')}>
        <SelectField label={t('imp.col.status')} value={status} onChange={setStatus}
          options={[{ id: 'any', label: t('imp.any') }, ...BATCH_STATUSES.map((s) => ({ id: s, label: t(`imp.status.${s}` as MessageKey) }))]} />
      </div>
      {list.isPending ? <Skeleton lines={4} /> : list.isError ? (
        <ErrorPanel message={problem?.status === 403 ? t('imp.denied') : t('imp.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      ) : rows.length === 0 ? <EmptyState title={t('imp.batches.empty')}>{t('imp.batches.empty.body')}</EmptyState> : (
        <>
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('imp.batches')}</caption>
            <thead><tr>
              <th scope="col">{t('imp.col.file')}</th><th scope="col">{t('imp.col.type')}</th><th scope="col">{t('imp.col.status')}</th>
              <th scope="col">{t('imp.col.rows')}</th><th scope="col">{t('imp.col.errors')}</th><th scope="col">{t('imp.col.when')}</th>
            </tr></thead>
            <tbody>
              {rows.map((b) => (
                <tr key={b.id}>
                  <th scope="row"><Link href={`${IMPORTS_PATH}/batches/${b.id}`}><bdi>{b.fileName ?? b.id}</bdi></Link></th>
                  <td><bdi>{nameOf(b.templateName, b.entityType)}</bdi></td>
                  <td><BatchStatusChip status={b.status} />{isRunning(b.status) && b.progressPct !== null && <span className="erp-small erp-muted"> {formatInteger(Math.round(b.progressPct), numbers)}%</span>}</td>
                  <td>{count(b.totalRows)}</td>
                  <td>{count(b.errorRows)}</td>
                  <td>{formatDateTime(b.createdAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
