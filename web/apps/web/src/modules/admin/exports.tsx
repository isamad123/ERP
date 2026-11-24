// The export centre (slice S1.16b Build item 8; FR-ADM-DAT-04, FR-ADM-NAV-02): one's own exports with their status and a download, and for holders of
// the export-log right the exports of their companies (the row, never the file, Q9). Exports work in read-only and locked states too.
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Download } from 'lucide-react';
import { Banner, Button, CheckboxField, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, StatusChip, useToast, type Status } from '@erp/ui';
import { useDocumentTitle, useLabel } from '@erp/engine';
import { formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { EXPORT_POLL_MS, EXPORT_STATUSES, exportsApi, isExportRunning, type ExportStatus, type ExportView } from '../../api/exports';
import { useShell } from '../../shell/context';
import './users.css';
import './exports-messages';

const KEY = ['itg', 'exports'] as const;
const CHIP: Record<ExportStatus, Status> = { queued: 'pending', running: 'pending', done: 'approved', failed: 'blocked', expired: 'inactive', awaiting_approval: 'pending', rejected: 'blocked' };

export default function ExportsScreen() {
  const { t, server } = useText();
  const { can } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const label = useLabel();
  const toast = useToast();
  const queryClient = useQueryClient();
  useDocumentTitle(t('exp.title'));
  const [status, setStatus] = useState('any');
  const [all, setAll] = useState(false);
  const allowed = can('itg.exports.run');
  const params = { all: all && can('itg.export_jobs.read'), status: status === 'any' ? undefined : status };
  const list = useQuery({
    queryKey: [...KEY, params],
    queryFn: ({ signal }) => exportsApi.list(params, signal),
    enabled: allowed,
    refetchOnMount: 'always',
    refetchInterval: (query) => ((query.state.data?.items ?? []).some((e) => isExportRunning(e.status)) ? EXPORT_POLL_MS : false),
  });
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const rows: ExportView[] = list.data?.items ?? [];
  const when = (value: string | null) => (value ? formatDateTime(value, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : '–');
  // Generated tables are named by their table label (customer overrides apply); the screens' own lists have their own names.
  const listName = (source: string) => (source === 'aud.change_log' ? t('exp.source.aud.change_log') : source === 'iam.user_overview' ? t('exp.source.iam.user_overview') : label(`entities.${source}`));
  const download = async (view: ExportView) => {
    try { await exportsApi.download(view); void queryClient.invalidateQueries({ queryKey: KEY }); } catch (e) { toast.error(e instanceof ApiProblem ? server(e.key) ?? t('exp.download.error') : t('exp.download.error')); }
  };
  if (!allowed) return <div className="erp-page"><ErrorPanel level={1} message={t('exp.denied')} /></div>;
  return (
    <div className="erp-page">
      <PageHeader title={t('exp.title')} description={t('exp.description')} />
      <div className="erp-filters" role="search" aria-label={t('exp.filters')}>
        <SelectField label={t('exp.col.status')} value={status} onChange={setStatus}
          options={[{ id: 'any', label: t('exp.any') }, ...EXPORT_STATUSES.map((s) => ({ id: s, label: t(`exp.status.${s}` as MessageKey) }))]} />
        {can('itg.export_jobs.read') && <CheckboxField label={t('exp.all')} isSelected={all} onChange={setAll} />}
      </div>
      {list.isPending ? <Skeleton lines={5} /> : list.isError && rows.length === 0 ? (
        <ErrorPanel message={problem?.status === 403 ? t('exp.denied') : t('exp.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      ) : rows.length === 0 ? <EmptyState title={t('exp.empty')}>{t('exp.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('exp.title')}</caption>
          <thead><tr>
            <th scope="col">{t('exp.col.list')}</th><th scope="col">{t('exp.col.format')}</th><th scope="col">{t('exp.col.rows')}</th><th scope="col">{t('exp.col.status')}</th>
            <th scope="col">{t('exp.col.when')}</th><th scope="col">{t('exp.col.expires')}</th>{all && <th scope="col">{t('exp.col.who')}</th>}<th scope="col">{t('exp.col.actions')}</th>
          </tr></thead>
          <tbody>
            {rows.map((e) => (
              <tr key={e.id}>
                <th scope="row"><bdi>{listName(e.source)}</bdi></th>
                <td><bdi>{e.format.toUpperCase()}</bdi></td>
                <td>{e.rowCount === null ? '–' : formatInteger(e.rowCount, numbers)}</td>
                <td>
                  <StatusChip status={CHIP[e.status]} label={t(`exp.status.${e.status}` as MessageKey)} />
                  {e.status === 'failed' && e.errorKey && <div className="erp-small erp-muted">{server(e.errorKey) ?? ''}</div>}
                  {e.status === 'awaiting_approval' && <div className="erp-small erp-muted">{t('exp.waiting.body')} <Link href="/approvals">{t('nav.approvals')}</Link></div>}
                  {e.status === 'rejected' && <div className="erp-small erp-muted">{t('exp.rejected.body')}</div>}
                </td>
                <td>{when(e.createdAt)}</td>
                <td>{e.status === 'done' ? when(e.expiresAt) : '–'}</td>
                {all && <td><bdi>{e.createdByName ?? '–'}</bdi></td>}
                <td>
                  {e.canDownload && (
                    <Button variant="secondary" icon={<Download size={16} aria-hidden="true" />} aria-label={t('exp.download.label', { list: listName(e.source) })} onPress={() => { void download(e); }}>{t('exp.download')}</Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <Banner tone="info">{t('exp.readOnly')}</Banner>
    </div>
  );
}
