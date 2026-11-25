// The readiness checklist (slice S1.18b Build item 5; FR-ADM-ONB-03; ruling Q4): per module and company the items with their state, the sentence that
// says what is missing, a link to the screen that fixes it, the progress, and "Go live". A module goes live only when its required items are done, or
// an administrator gives a reason; before that the module is in set-up mode and nothing else is blocked.
import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, Dialog, EmptyState, Link, StatusChip, TextAreaField, useToast, ScrollRegion } from '@erp/ui';
import { formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { setupApi, type ModuleReadiness, type ReadinessOverview, type SetupTask } from '../../api/setup';
import { useShell } from '../../shell/context';
import { SetupStatusChip, useSetupError, useTaskMessage } from './setup-parts';
import './setup-messages';

export function SetupChecklist({ overview, companyId }: { overview: ReadinessOverview; companyId: string | null }) {
  const { t } = useText();
  if (!companyId) return <EmptyState title={t('setup.checklist.company.needed')} />;
  if (overview.modules.length === 0) return <EmptyState title={t('setup.checklist.empty')}>{t('setup.checklist.empty.body')}</EmptyState>;
  return <div className="erp-stack">{overview.modules.map((m) => <ModuleCard key={m.moduleId} module={m} companyId={companyId} />)}</div>;
}

function ModuleCard({ module, companyId }: { module: ModuleReadiness; companyId: string }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  const message = useTaskMessage();
  const queryClient = useQueryClient();
  const toast = useToast();
  const errorText = useSetupError();
  const [dialog, setDialog] = useState(false);
  const name = t(module.nameKey as MessageKey);
  const open = module.requiredTotal - module.requiredDone;
  const canGoLive = writable && can('cfg.setup_tasks.go_live') && !module.isLive;
  const canTick = writable && can('cfg.setup_tasks.update');
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['cfg', 'readiness'] });
  const tick = useMutation({
    mutationFn: ({ task, done }: { task: SetupTask; done: boolean }) => (done ? setupApi.complete(task, null) : setupApi.reopen(task, null)),
    onSuccess: () => { void refresh(); },
    onError: (e: unknown) => toast.error(errorText(e)),
  });
  return (
    <section className="erp-card erp-stack" aria-labelledby={`setup-mod-${module.moduleId}`}>
      <div className="erp-row erp-wrap">
        <h2 id={`setup-mod-${module.moduleId}`} className="erp-h3">{name}</h2>
        {module.isLive
          ? <StatusChip status="active" label={t('setup.checklist.live', { date: module.liveAt ? formatDateTime(module.liveAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : '' })} />
          : <StatusChip status="draft" label={t('setup.checklist.setupMode')} />}
      </div>
      <p className="erp-small">{t('setup.checklist.progress', { done: formatInteger(module.requiredDone, numbers), total: formatInteger(module.requiredTotal, numbers) })}</p>
      <progress className="erp-setup-progress" max={Math.max(module.requiredTotal, 1)} value={module.requiredDone} aria-label={t('setup.checklist.progress.aria', { name })} />
      {module.optionalTotal > 0 && <p className="erp-small erp-muted">{t('setup.checklist.optionalProgress', { done: formatInteger(module.optionalDone, numbers), total: formatInteger(module.optionalTotal, numbers) })}</p>}
      {module.isLive && module.liveNote && <Banner tone="warning">{t('setup.checklist.liveOverride', { reason: module.liveNote })}</Banner>}
      {module.goLiveActions.length > 0 && <p className="erp-small erp-muted">{t('setup.checklist.actions', { actions: module.goLiveActions.join(', ') })}</p>}
      <ScrollRegion className="erp-table-scroll" label={name}>
        <table className="erp-table" aria-label={name}>
          <thead><tr>
            <th scope="col">{t('setup.checklist.col.item')}</th><th scope="col">{t('setup.checklist.col.status')}</th><th scope="col">{t('setup.checklist.col.fix')}</th>
          </tr></thead>
          <tbody>
            {module.items.map((item) => (
              <tr key={item.taskKey}>
                <th scope="row">
                  {t(item.titleKey as MessageKey)}
                  <div className="erp-small erp-muted">{t(item.required ? 'setup.checklist.required' : 'setup.checklist.optional')}</div>
                </th>
                <td><SetupStatusChip status={item.status} />{item.status !== 'done' && message(item) && <div className="erp-small">{message(item)}</div>}</td>
                <td>
                  <div className="erp-row erp-wrap">
                    {item.status !== 'done' && item.link && <Link href={item.link} aria-label={t('setup.checklist.fix.aria', { name: t(item.titleKey as MessageKey) })}>{t('setup.checklist.fix')}</Link>}
                    {canTick && item.manual && <Button variant="secondary" isPending={tick.isPending} onPress={() => tick.mutate({ task: item, done: item.status !== 'done' })}>{t(item.status === 'done' ? 'setup.checklist.untick' : 'setup.checklist.tick')}</Button>}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </ScrollRegion>
      {canGoLive && <div className="erp-row"><Button variant="primary" aria-label={t('setup.checklist.goLive.aria', { name })} onPress={() => setDialog(true)}>{t('setup.checklist.goLive')}</Button></div>}
      {dialog && <GoLiveDialog name={name} moduleId={module.moduleId} companyId={companyId} open={open} onClose={() => setDialog(false)} />}
    </section>
  );
}

function GoLiveDialog({ name, moduleId, companyId, open, onClose }: { name: string; moduleId: string; companyId: string; open: number; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const errorText = useSetupError();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const needsReason = open > 0;
  const go = useMutation({
    mutationFn: (why: string) => setupApi.goLive(moduleId, companyId, why),
    onSuccess: () => { toast.success(t('setup.goLive.done', { name })); void queryClient.invalidateQueries({ queryKey: ['cfg', 'readiness'] }); onClose(); },
    onError: (e: unknown) => setError(errorText(e)),
  });
  return (
    <Dialog title={t('setup.goLive.title', { name })} isOpen onOpenChange={(isOpen) => { if (!isOpen) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('setup.goLive.cancel')}</Button>
          <Button variant="primary" isPending={go.isPending} isDisabled={needsReason && reason.trim().length === 0} onPress={() => go.mutate(needsReason ? reason : '')}>{t('setup.goLive.confirm')}</Button>
        </>
      )}>
      <div className="erp-stack">
        <p>{needsReason ? t('setup.goLive.open', { count: open }) : t('setup.goLive.ready')}</p>
        {needsReason && <TextAreaField label={t('setup.goLive.reason')} help={t('setup.goLive.reason.help')} value={reason} onChange={setReason} maxLength={500} isRequired />}
        {error && <Banner tone="danger">{error}</Banner>}
      </div>
    </Dialog>
  );
}
