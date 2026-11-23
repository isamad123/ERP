// Small parts shared by the set-up guide and the readiness checklist (slice S1.18b): the status chip, the server's message beside an open item,
// and turning a refused request into a plain sentence.
import { StatusChip, type Status } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import type { SetupStatus, SetupTask } from '../../api/setup';
import './setup-messages';

const CHIP: Record<SetupStatus, Status> = { pending: 'pending', done: 'active', skipped: 'inactive' };

export function SetupStatusChip({ status }: { status: SetupStatus }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status]} label={t(`setup.status.${status}` as MessageKey)} />;
}

/** The sentence beside an open task: the server's message key with its numbers; a task that is done or has no message shows nothing. */
export function useTaskMessage() {
  const { t } = useText();
  return (task: SetupTask): string | null => (task.messageKey ? t(task.messageKey as MessageKey, task.messageArguments ?? {}) : null);
}

/** The plain-words sentence of a refused request (the server's own text for its key when it has one). */
export function useSetupError() {
  const { t, server } = useText();
  return (error: unknown): string => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return t('setup.error.network');
      if (error.status === 403) return t('setup.error.denied');
      const field = Object.values(error.fieldErrors)[0]?.[0];
      return server(field ?? error.errors[0] ?? error.key) ?? t('setup.error.generic');
    }
    return t('setup.error.generic');
  };
}
