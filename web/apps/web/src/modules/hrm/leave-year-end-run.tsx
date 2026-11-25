// One year-end run (slice S1.33b; FR-HRM-LVE-11, FR-XM-05): the preview or the applied record. A preview offers Confirm and Roll back (with the permission); an applied run is read-only and
// says who confirmed, when and how many entries were posted, with the warning that the leave year is closed (R-306); a rolled-back or replaced preview is only shown.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Banner, Button, ErrorPanel, Skeleton, StatusChip } from '@erp/ui';
import { digitsOf, formatDateTime, formatDecimal, useLocaleSettings } from '@erp/i18n';
import { leaveLedgerApi, summaryOf, type YearEndAnswer } from '../../api/leave-ledger';
import { useShell } from '../../shell/context';
import { useLeaveRefusal, useLeaveTypes } from './leave-parts';
import { PreviewTable } from './leave-year-end-preview';
import { ClosedWarning, ConfirmDialog, ConfirmOutcome, RollbackDialog } from './leave-year-end-dialogs';
import { useYearEndText } from './leave-year-end-messages';

export const STATUS_CHIP = { preview: 'pending', applied: 'approved', rolled_back: 'inactive' } as const;

export function RunView({ runId, companyId, onClosed }: { runId: string; companyId: string; onClosed: () => void }) {
  const { t } = useYearEndText();
  const { can, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const refusal = useLeaveRefusal();
  const types = useLeaveTypes(companyId);
  const run = useQuery({ queryKey: ['hrm', 'leave', 'year-end-run', runId], queryFn: () => leaveLedgerApi.yearEndRun(runId) });
  const [dialog, setDialog] = useState<'confirm' | 'rollback' | null>(null);
  const [outcome, setOutcome] = useState<YearEndAnswer | null>(null);
  if (run.isPending) return <Skeleton lines={6} />;
  if (run.isError) return <ErrorPanel title={t('lye.loadError')} message={refusal(run.error)} action={<Button variant="primary" onPress={() => { void run.refetch(); }}>{t('lye.retry')}</Button>} />;
  const r = run.data;
  const summary = summaryOf(r);
  const preview = r.status === 'preview';
  const canDecide = can('hrm.leave_year_end_runs.confirm') && writable && preview && outcome?.status !== 'pending';
  const year = digitsOf(String(r.leaveYear), numbers.digits);
  return (
    <section aria-labelledby="erp-lye-run" className="erp-stack">
      <div className="erp-row erp-wrap">
        <h2 id="erp-lye-run">{t('lye.run.title', { year })}</h2>
        <StatusChip status={STATUS_CHIP[r.status]} label={t(`lye.run.status.${r.status}`)} />
        {summary.preparedAt && <span className="erp-muted">{t('lye.run.prepared', { at: formatDateTime(summary.preparedAt, lang, timeZone, numbers) })}</span>}
      </div>
      {preview && <Banner tone="info">{t('lye.preview.note')}</Banner>}
      {r.status === 'rolled_back' && <Banner tone="info" title={t('lye.rolledBack.title')}>{t('lye.rolledBack.body')}</Banner>}
      {r.status === 'applied' && (
        <>
          <Banner tone="success" title={t('lye.applied.title')}>
            {t('lye.applied.body', {
              at: summary.confirmedAt ? formatDateTime(summary.confirmedAt, lang, timeZone, numbers) : t('lye.applied.unknown'),
              by: summary.confirmedBy ?? t('lye.applied.unknown'), count: formatDecimal(String(summary.posted ?? 0), numbers),
            })}
          </Banner>
          <ClosedWarning />
        </>)}
      {outcome && <ConfirmOutcome answer={outcome} />}
      {canDecide && (
        <div className="erp-row erp-wrap">
          <Button variant="primary" onPress={() => setDialog('confirm')}>{t('lye.confirm.open')}</Button>
          <Button variant="secondary" onPress={() => setDialog('rollback')}>{t('lye.rollback.open')}</Button>
        </div>)}
      <PreviewTable summary={summary} types={types.data ?? []} />
      {dialog === 'confirm' && <ConfirmDialog run={r} onClose={() => setDialog(null)} onDone={(answer) => { setOutcome(answer); void run.refetch(); }} />}
      {dialog === 'rollback' && <RollbackDialog run={r} onClose={() => setDialog(null)} onDone={() => { void run.refetch(); onClosed(); }} />}
    </section>
  );
}
