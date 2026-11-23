// One import batch (slice S1.16a Build item 4; FR-ADM-DAT-01, FR-KRN-INT-03, FR-KRN-INT-04, Q5): the validation summary, the first
// 100 problems, the error file, and the steps validate, commit and roll back. A running batch is watched every two seconds
// until it stops. Nothing is committed before a validation run found it good; the server decides every step again.
import { useState } from 'react';
import { useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download } from 'lucide-react';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, Skeleton, useToast } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDateTime, formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { importsApi, isRunning, POLL_MS } from '../../api/imports';
import { useShell } from '../../shell/context';
import { ReasonDialog } from './users-parts';
import { BATCHES_KEY, BatchStatusChip, IMPORTS_PATH, batchKey, useNameOf, useRefusalText } from './imports-parts';
import './users.css';

export function BatchPage() {
  const { id = '' } = useParams();
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { writable } = useShell();
  const nameOf = useNameOf();
  const toast = useToast();
  const refusal = useRefusalText();
  const queryClient = useQueryClient();
  const [rolling, setRolling] = useState(false);
  const [refused, setRefused] = useState<string | null>(null);
  const batch = useQuery({
    queryKey: batchKey(id),
    queryFn: () => importsApi.batch(id),
    refetchOnMount: 'always',
    refetchInterval: (query) => (isRunning(query.state.data?.status) ? POLL_MS : false),
  });
  const status = batch.data?.status;
  const showErrors = !!batch.data && (batch.data.errorRows ?? 0) > 0 && !isRunning(status);
  const errors = useQuery({ queryKey: [...batchKey(id), 'errors', status], queryFn: () => importsApi.errors(id, 100), enabled: showErrors });
  useDocumentTitle(batch.data?.fileName ?? t('imp.title'));

  const step = useMutation({
    mutationFn: (kind: 'validate' | 'commit') => (kind === 'validate' ? importsApi.validate(id) : importsApi.commit(id)),
    onSuccess: (next) => {
      setRefused(null);
      queryClient.setQueryData(batchKey(id), next);
      void queryClient.invalidateQueries({ queryKey: BATCHES_KEY });
    },
    onError: (e) => setRefused(refusal(e)),
  });

  if (batch.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (batch.isError || !batch.data) {
    const problem = batch.error instanceof ApiProblem ? batch.error : null;
    return (
      <div className="erp-page">
        <ErrorPanel level={1} title={problem?.status === 404 ? t('imp.batch.notFound') : undefined}
          message={problem?.status === 403 ? t('imp.denied') : problem?.status === 404 ? t('imp.batch.notFound.body') : t('imp.loadError')} reference={problem?.reference ?? null}
          action={problem?.status === 404 ? undefined : <Button variant="primary" onPress={() => { void batch.refetch(); }}>{t('common.retry')}</Button>} />
      </div>
    );
  }
  const b = batch.data;
  const when = (value: string | null) => (value ? formatDateTime(value, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : null);
  const count = (value: number | null) => (value === null ? '–' : formatInteger(value, numbers));
  const pct = Math.round(b.progressPct ?? 0);
  const mayCommit = writable && b.status === 'validated' && b.canCommit;
  const blocked = b.rollbackBlockedKey ? refusal(b.rollbackBlockedKey) : null;

  return (
    <div className="erp-page">
      <PageHeader title={b.fileName ?? t('imp.title')} breadcrumbs={[{ label: t('imp.title'), href: IMPORTS_PATH }, { label: b.fileName ?? b.id }]}
        description={<span className="erp-row"><BatchStatusChip status={b.status} /><bdi>{nameOf(b.templateName, b.entityType)}</bdi></span>} />
      {refused && <Banner tone="danger" title={t('imp.refused.title')} onDismiss={() => setRefused(null)}>{refused}</Banner>}
      {isRunning(b.status) && (
        <div className="erp-stack" role="status" aria-live="polite">
          <p><strong>{t(b.status === 'validating' ? 'imp.progress.validating' : 'imp.progress.committing', { percent: formatInteger(pct, numbers) })}</strong></p>
          <progress className="erp-meter" max={100} value={pct} aria-label={t('imp.progress.label')} />
        </div>
      )}
      <dl className="erp-stack" aria-label={t('imp.summary')}>
        <div><dt className="erp-muted erp-small">{t('imp.summary.total')}</dt><dd>{count(b.totalRows)}</dd></div>
        <div><dt className="erp-muted erp-small">{t('imp.summary.ok')}</dt><dd>{count(b.okRows)}</dd></div>
        <div><dt className="erp-muted erp-small">{t('imp.summary.errors')}</dt><dd>{count(b.errorRows)}</dd></div>
        {(b.status === 'committed' || b.status === 'rolled_back') && (
          <>
            <div><dt className="erp-muted erp-small">{t('imp.summary.created')}</dt><dd>{count(b.createdRows)}</dd></div>
            <div><dt className="erp-muted erp-small">{t('imp.summary.updated')}</dt><dd>{count(b.updatedRows)}</dd></div>
          </>
        )}
        {when(b.validatedAt) && <div><dt className="erp-muted erp-small">{t('imp.summary.validatedAt')}</dt><dd>{when(b.validatedAt)}</dd></div>}
        {when(b.committedAt) && <div><dt className="erp-muted erp-small">{t('imp.summary.committedAt')}</dt><dd>{when(b.committedAt)}</dd></div>}
        {when(b.rolledBackAt) && <div><dt className="erp-muted erp-small">{t('imp.summary.rolledBackAt')}</dt><dd>{when(b.rolledBackAt)}</dd></div>}
      </dl>
      {b.status === 'validated' && (b.errorRows ?? 0) === 0 && <Banner tone="success" title={t('imp.validated.good')}>{t('imp.validated.good.body', { count: count(b.okRows) })}</Banner>}
      {b.status === 'validated' && (b.errorRows ?? 0) > 0 && <Banner tone="warning" title={t('imp.validated.bad', { count: count(b.errorRows) })}>{t('imp.validated.bad.body')}</Banner>}
      {b.status === 'failed' && <Banner tone="danger" title={t('imp.failed')}>{t('imp.failed.body')}</Banner>}
      {writable && (
        <div className="erp-row erp-wrap">
          {(b.status === 'uploaded' || b.status === 'validated' || b.status === 'failed') && (
            <Button variant={b.status === 'uploaded' ? 'primary' : 'secondary'} isPending={step.isPending && step.variables === 'validate'} onPress={() => step.mutate('validate')}>
              {t(b.status === 'uploaded' ? 'imp.validate' : 'imp.validate.again')}
            </Button>
          )}
          <Button variant="primary" isDisabled={!mayCommit} isPending={step.isPending && step.variables === 'commit'} onPress={() => step.mutate('commit')}>{t('imp.commit')}</Button>
          {b.status === 'committed' && <Button variant="danger" isDisabled={!b.canRollback} onPress={() => setRolling(true)}>{t('imp.rollback')}</Button>}
        </div>
      )}
      {b.status === 'validated' && !mayCommit && (b.errorRows ?? 0) > 0 && <p className="erp-muted">{t('imp.commit.blocked')}</p>}
      {b.status === 'committed' && !b.canRollback && <p className="erp-muted">{blocked ?? t('imp.rollback.unavailable')}</p>}
      {b.status === 'committed' && b.canRollback && <p className="erp-muted">{t('imp.rollback.possible')}</p>}

      {showErrors && (
        <section className="erp-stack" aria-labelledby="imp-errors">
          <div className="erp-row erp-wrap">
            <h2 id="imp-errors" className="erp-h3">{t('imp.errors.title')}</h2>
            <Button variant="secondary" icon={<Download size={16} aria-hidden="true" />} onPress={() => { void importsApi.downloadErrors(id).catch((e: unknown) => toast.error(refusal(e))); }}>{t('imp.errors.download')}</Button>
          </div>
          {errors.isPending ? <Skeleton lines={4} /> : errors.isError ? (
            <ErrorPanel message={t('imp.loadError')} action={<Button variant="primary" onPress={() => { void errors.refetch(); }}>{t('common.retry')}</Button>} />
          ) : (errors.data?.items.length ?? 0) === 0 ? <EmptyState title={t('imp.errors.empty')} /> : (
            <>
              <p className="erp-muted">{t('imp.errors.first', { count: formatInteger(errors.data?.items.length ?? 0, numbers) })}</p>
              <table className="erp-table">
                <caption className="erp-visually-hidden">{t('imp.errors.title')}</caption>
                <thead><tr><th scope="col">{t('imp.errors.row')}</th><th scope="col">{t('imp.errors.field')}</th><th scope="col">{t('imp.errors.message')}</th></tr></thead>
                <tbody>
                  {(errors.data?.items ?? []).map((e, index) => (
                    <tr key={`${e.rowNo}-${e.field ?? ''}-${index}`}>
                      <th scope="row">{formatInteger(e.rowNo, numbers)}</th>
                      <td>{e.field ? <bdi className="erp-code">{e.field}</bdi> : <span className="erp-muted">{t('imp.errors.wholeRow')}</span>}</td>
                      <td><bdi>{e.message}</bdi></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </section>
      )}

      {rolling && (
        <ReasonDialog isOpen danger title={t('imp.rollback.title')} confirmLabel={t('imp.rollback')} effect={t('imp.rollback.effect', { count: count(b.createdRows) })}
          onClose={() => setRolling(false)}
          onConfirm={async (reason) => {
            try {
              const next = await importsApi.rollback(id, reason);
              queryClient.setQueryData(batchKey(id), next);
              void queryClient.invalidateQueries({ queryKey: BATCHES_KEY });
              toast.success(t('imp.rolledBack'));
            } catch (e) {
              setRefused(refusal(e));
            }
          }} />
      )}
    </div>
  );
}
