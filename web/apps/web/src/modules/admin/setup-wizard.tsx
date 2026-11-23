// The set-up guide (slice S1.18b Build items 4 and 6; FR-ADM-ONB-01, -02, -04; rulings Q1, Q5, Q6): the steps of the first-run guide with their
// state, the one the person is on, and what to do there. The work itself happens on the normal screens (so the same checks and audit entries
// apply); the guide only links to them, and ticks, skips and re-opens steps through the readiness API. Closing the browser loses nothing: the
// server holds every state.
import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, EmptyState, Link, TextAreaField, useToast } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { setupApi, stepsOpen, type ReadinessOverview, type SetupTask } from '../../api/setup';
import { useShell } from '../../shell/context';
import { SetupStatusChip, useSetupError, useTaskMessage } from './setup-parts';
import { setupKey } from './setup';
import './setup-messages';

/** Steps that offer the import centre, with the type of data (FR-ADM-ONB-04; the centre itself comes with S1.16, R-106). */
const IMPORT_OFFERS: Record<string, string> = { step_structure: 'org_units', step_users: 'users' };

export function SetupWizard({ overview, companyId, step, onStep, onChecklist }: { overview: ReadinessOverview; companyId: string | null; step: string | null; onStep: (key: string | null) => void; onChecklist: () => void }) {
  const { t } = useText();
  const steps = [...overview.steps].sort((a, b) => a.order - b.order);
  // Resume where the person left off: the first step still to do (ruling Q1).
  const current = steps.find((s) => s.taskKey === step) ?? steps.find((s) => s.status === 'pending') ?? steps[steps.length - 1];
  const done = steps.filter((s) => s.status !== 'pending').length;
  if (steps.length === 0 || !current) return <EmptyState title={t('setup.checklist.empty')}>{t('setup.checklist.empty.body')}</EmptyState>;
  return (
    <div className="erp-stack">
      {stepsOpen(steps) === 0 && (
        <Banner tone="success" action={<Button variant="secondary" onPress={onChecklist}>{t('setup.finished.open')}</Button>}>{t('setup.finished')}</Banner>
      )}
      <div className="erp-setup-layout">
        <section aria-labelledby="setup-steps-title" className="erp-stack">
          <h2 id="setup-steps-title" className="erp-h3">{t('setup.steps.heading')}</h2>
          <p className="erp-small erp-muted">{t('setup.steps.progress', { done, total: steps.length })}</p>
          <ol className="erp-setup-steps">
            {steps.map((s) => (
              <li key={s.taskKey} className="erp-setup-step" aria-current={s.taskKey === current.taskKey ? 'step' : undefined}>
                <Button variant={s.taskKey === current.taskKey ? 'primary' : 'secondary'} aria-label={t('setup.step.open.aria', { name: t(s.titleKey as MessageKey) })} onPress={() => onStep(s.taskKey)}>{t(s.titleKey as MessageKey)}</Button>
                <SetupStatusChip status={s.status} />
              </li>
            ))}
          </ol>
        </section>
        <StepPane key={`${current.taskKey}:${companyId ?? ''}`} task={current} companyId={companyId} onChecklist={onChecklist} />
      </div>
    </div>
  );
}

function StepPane({ task, companyId, onChecklist }: { task: SetupTask; companyId: string | null; onChecklist: () => void }) {
  const { t } = useText();
  const { can, writable, capabilities } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const errorText = useSetupError();
  const message = useTaskMessage();
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const canChange = writable && can('cfg.setup_tasks.update');
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['cfg', 'readiness'] });
  const run = (call: (note: string) => Promise<unknown>, toastKey: MessageKey) => ({
    mutationFn: call,
    onSuccess: () => { setError(null); toast.success(t(toastKey)); void refresh(); },
    onError: (e: unknown) => setError(errorText(e)),
  });
  const complete = useMutation(run(() => setupApi.complete(task, companyId), 'setup.toast.done'));
  const skip = useMutation(run((why: string) => setupApi.skip(task, companyId, why), 'setup.toast.skipped'));
  const reopen = useMutation(run(() => setupApi.reopen(task, companyId), 'setup.toast.reopened'));
  const recheck = useMutation({ mutationFn: refresh });
  const importType = IMPORT_OFFERS[task.taskKey];
  const needsCompany = task.level === 'company' && !companyId;
  return (
    <section className="erp-setup-pane" aria-labelledby="setup-pane-title">
      <div className="erp-row erp-wrap">
        <h2 id="setup-pane-title" className="erp-h3">{t(task.titleKey as MessageKey)}</h2>
        <SetupStatusChip status={task.status} />
      </div>
      <p>{t(`${task.titleKey}.body` as MessageKey)}</p>
      {task.status === 'skipped' && task.note && <p className="erp-small erp-muted">{t('setup.skippedNote', { note: task.note })}</p>}
      {task.status === 'pending' && message(task) && <Banner tone="info">{message(task)}</Banner>}
      {task.taskKey === 'step_modules' && (
        <div className="erp-stack">
          <h3 className="erp-h4">{t('setup.modules.licensed')}</h3>
          {capabilities.modules.length === 0 ? <p className="erp-muted">{t('setup.modules.none')}</p> : (
            <ul>{capabilities.modules.map((m) => <li key={m}><span className="erp-code"><bdi>{m}</bdi></span></li>)}</ul>
          )}
          <p className="erp-small erp-muted">{t('setup.modules.help')}</p>
        </div>
      )}
      {task.taskKey === 'step_summary' && <Button variant="secondary" onPress={onChecklist}>{t('setup.finished.open')}</Button>}
      {importType && <p className="erp-small">{t('setup.import.offer')} <Link href={`/admin/imports?type=${importType}`}>{t('setup.import.open')}</Link></p>}
      <p className="erp-small erp-muted">{t(task.manual ? 'setup.manual' : 'setup.decided')}</p>
      {error && <Banner tone="danger">{error}</Banner>}
      {!canChange && <Banner tone="info">{t('setup.read.only')}</Banner>}
      <div className="erp-row erp-wrap">
        {task.link && <Link href={task.link}>{t('setup.action.openScreen')}</Link>}
        {canChange && !needsCompany && !task.manual && task.status !== 'done' && <Button variant="secondary" isPending={recheck.isPending} onPress={() => recheck.mutate()}>{t('setup.action.recheck')}</Button>}
        {canChange && !needsCompany && task.manual && task.status !== 'done' && <Button variant="primary" isPending={complete.isPending} onPress={() => complete.mutate('')}>{t('setup.action.markDone')}</Button>}
        {canChange && !needsCompany && task.status !== 'pending' && (task.manual || task.status === 'skipped') && <Button variant="secondary" isPending={reopen.isPending} onPress={() => reopen.mutate('')}>{t('setup.action.reopen')}</Button>}
      </div>
      {canChange && !needsCompany && task.status === 'pending' && (
        <div className="erp-stack">
          <TextAreaField label={t('setup.skip.note')} value={note} onChange={setNote} maxLength={500} />
          <div className="erp-row"><Button variant="secondary" isPending={skip.isPending} onPress={() => skip.mutate(note)}>{t('setup.action.skip')}</Button></div>
        </div>
      )}
    </section>
  );
}

export { setupKey };
