// System health (slice S1.16c Build item 11, backup status of item 12; FR-ADM-OPS-05, FR-KRN-JOB-06, FR-ADM-DAT-08): the worker, the
// job and event queues, files waiting for the scan, storage and database size, and this customer's last backup and restore drill.
// Read-only: starting a backup or a restore is a provider (or on-premises, 1.1) action. No slow-query figures (ruling Q11).
import { useQuery } from '@tanstack/react-query';
import { Banner, Button, EmptyState, Link, StatusChip } from '@erp/ui';
import { useText } from '@erp/i18n';
import { opsApi, type SystemHealth } from '../../api/ops';
import { useShell } from '../../shell/context';
import { KB, Loaded, useFormats } from './ops-parts';

const POLL_MS = 15000;

/** System health without its page frame: the "System health" tab of the job centre. */
export function HealthBody() {
  const { t } = useText();
  const { can } = useShell();
  const query = useQuery({ queryKey: ['hlt'], queryFn: opsApi.systemHealth, refetchOnMount: 'always', refetchInterval: POLL_MS, enabled: can('job.runs.read') });
  if (!can('job.runs.read')) return <Banner tone="danger">{t('ops.denied')}</Banner>;
  return (
    <div className="erp-stack">
      <p className="erp-muted">{t('hlt.description')}</p>
      <div><Button variant="secondary" onPress={() => { void query.refetch(); }}>{t('ops.refresh')}</Button></div>
      <Loaded query={query}>{(h) => <Body h={h} />}</Loaded>
    </div>
  );
}

function useSize() {
  const { t } = useText();
  const f = useFormats();
  return (bytes: number) => bytes >= KB ** 3 ? t('hlt.gb', { n: f.decimal(bytes / KB ** 3) }) : bytes >= KB ** 2 ? t('hlt.mb', { n: f.decimal(bytes / KB ** 2) }) : t('hlt.kb', { n: f.decimal(bytes / KB) });
}

function Body({ h }: { h: SystemHealth }) {
  const { t } = useText();
  const { can } = useShell();
  const f = useFormats();
  const size = useSize();
  const j = h.jobs;
  const lag = (iso: string | null) => { const m = f.minutesBetween(iso, h.at); return m === null ? t('hlt.lag.none') : t('hlt.lag', { minutes: f.int(m) }); };
  const waitingEvents = j.events.filter((e) => e.waiting > 0 || e.failing > 0);
  return (
    <>
      <p className="erp-small erp-muted">{t('hlt.asOf', { when: f.when(h.at) })}</p>
      <section className="erp-stack" aria-labelledby="hlt-worker">
        <h2 id="hlt-worker" className="erp-h3">{t('hlt.worker')}</h2>
        <div className="erp-row erp-wrap">
          <StatusChip status={j.workerSeenRecently ? 'active' : 'blocked'} label={j.workerSeenRecently ? t('hlt.worker.ok') : t('hlt.worker.quiet')} />
          <span className="erp-muted">{j.lastWorkerActivityAt ? t('hlt.worker.last', { when: f.when(j.lastWorkerActivityAt) }) : t('hlt.worker.never')}</span>
        </div>
        {!j.workerSeenRecently && <Banner tone="warning" title={t('hlt.worker.quiet')}>{t('hlt.worker.quiet.body')}</Banner>}
      </section>

      <section className="erp-stack" aria-labelledby="hlt-jobs">
        <h2 id="hlt-jobs" className="erp-h3">{t('hlt.jobs')}</h2>
        <dl className="erp-row erp-wrap">
          <div><dt className="erp-small erp-muted">{t('hlt.jobs.queued')}</dt><dd><strong>{f.int(j.queued)}</strong></dd></div>
          <div><dt className="erp-small erp-muted">{t('hlt.jobs.running')}</dt><dd><strong>{f.int(j.running)}</strong></dd></div>
          <div><dt className="erp-small erp-muted">{t('hlt.jobs.failed')}</dt><dd><strong>{f.int(j.failedLast24Hours)}</strong></dd></div>
        </dl>
        {j.oldestQueuedAt && <p className="erp-small erp-muted">{t('hlt.jobs.oldest', { when: f.when(j.oldestQueuedAt) })}</p>}
        {j.failedLast24Hours > 0 && <div><Link href="/admin/jobs/runs">{t('hlt.jobs.open')}</Link></div>}
      </section>

      <section className="erp-stack" aria-labelledby="hlt-events">
        <h2 id="hlt-events" className="erp-h3">{t('hlt.events')}</h2>
        {waitingEvents.length === 0 ? <p className="erp-muted">{t('hlt.events.empty')}</p> : (
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('hlt.events')}</caption>
            <thead><tr>
              <th scope="col">{t('hlt.events.col.module')}</th><th scope="col">{t('hlt.events.col.waiting')}</th>
              <th scope="col">{t('hlt.events.col.failing')}</th><th scope="col">{t('hlt.events.col.lag')}</th>
            </tr></thead>
            <tbody>
              {waitingEvents.map((e) => (
                <tr key={e.schema}><th scope="row"><bdi dir="ltr">{e.schema}</bdi></th><td>{f.int(e.waiting)}</td><td>{f.int(e.failing)}</td><td>{lag(e.oldestAt)}</td></tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="erp-stack" aria-labelledby="hlt-storage">
        <h2 id="hlt-storage" className="erp-h3">{t('hlt.storage')}</h2>
        <dl className="erp-row erp-wrap">
          <div><dt className="erp-small erp-muted">{t('hlt.storage.files')}</dt><dd><strong>{size(h.storageUsedBytes)}</strong></dd></div>
          <div><dt className="erp-small erp-muted">{t('hlt.storage.db')}</dt><dd><strong>{size(h.databaseSizeBytes)}</strong></dd></div>
          <div><dt className="erp-small erp-muted">{t('hlt.storage.unscanned')}</dt><dd><strong>{f.int(h.filesNotScanned)}</strong></dd></div>
        </dl>
        {h.filesNotScanned > 0 && <Banner tone="warning" title={t('hlt.storage.unscanned')}>{t('hlt.storage.unscanned.body', { when: f.when(h.oldestUnscannedAt) })}</Banner>}
        <p className="erp-small erp-muted">{t('hlt.slow')}</p>
      </section>

      <section className="erp-stack" aria-labelledby="hlt-backup">
        <h2 id="hlt-backup" className="erp-h3">{t('hlt.backup')}</h2>
        {!h.lastBackupAt && !h.lastRestoreTestAt ? <EmptyState title={t('hlt.backup')}>{t('hlt.backup.none')}</EmptyState> : (
          <dl className="erp-stack">
            <div><dt className="erp-small erp-muted">{t('hlt.backup.last')}</dt><dd><strong>{h.lastBackupAt ? f.when(h.lastBackupAt) : t('ops.never')}</strong></dd></div>
            <div><dt className="erp-small erp-muted">{t('hlt.backup.restore')}</dt><dd><strong>{h.lastRestoreTestAt ? f.when(h.lastRestoreTestAt) : t('ops.never')}</strong></dd></div>
          </dl>
        )}
        <p className="erp-small erp-muted">{t('hlt.backup.note')}</p>
      </section>

      {can('ntf.subscriptions.read') && (
        <section className="erp-stack" aria-labelledby="hlt-alerts">
          <h2 id="hlt-alerts" className="erp-h3">{t('hlt.alerts')}</h2>
          <p>{t('hlt.alerts.body')}</p>
          <div><Link href="/settings/notification-texts">{t('hlt.alerts.open')}</Link></div>
        </section>
      )}
    </>
  );
}
