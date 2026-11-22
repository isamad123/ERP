// Shared parts of the user console (slice S1.13a): the status chip, the confirmation dialog that states its effect and asks
// for a reason, the person picker over the people lookup (ruling Q7), and the plain-words text of a refused request.
import { useCallback, useState, type ReactNode } from 'react';
import { Button, ComboBoxField, Dialog, StatusChip, TextAreaField, type Option, type Status } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { isUserLimit, users, type UserStatus } from '../../api/users';

export const CLASH_RULE = 'ck_users_username_email_clash';

const CHIP: Record<UserStatus, Status> = { invited: 'pending', active: 'active', locked: 'blocked', disabled: 'inactive', archived: 'draft' };

export function UserStatusChip({ status }: { status: UserStatus }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status]} label={t(`users.status.${status}`)} />;
}

/** What a refused request says in plain words: the limit, the sign-in clash, or the server's own text. */
export function useProblemText() {
  const { t, server } = useText();
  return useCallback((error: unknown): string => {
    if (error instanceof ApiProblem) {
      if (isUserLimit(error.key, error.detail)) return t('users.error.limit');
      if (error.detail === CLASH_RULE) return t('users.error.clash');
      if (error.key === 'errors.data.duplicate') return t('users.error.duplicate');
      return server(error.errors[0] ?? error.key) ?? t('users.error.generic');
    }
    return t('users.error.generic');
  }, [t, server]);
}

export interface ReasonDialogProps {
  title: string;
  /** What will happen, in plain words (every action asks for confirmation stating its effect). */
  effect: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  isOpen: boolean;
  onClose: () => void;
  /** Runs the action; throws to keep the dialog open with the problem shown. */
  onConfirm: (reason: string) => Promise<void>;
  /** Extra fields between the effect and the reason. */
  children?: ReactNode;
  reasonOptional?: boolean;
  isDisabled?: boolean;
}

export function ReasonDialog({ title, effect, confirmLabel, danger, isOpen, onClose, onConfirm, children, reasonOptional, isDisabled }: ReasonDialogProps) {
  const { t } = useText();
  const problemText = useProblemText();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const finish = () => { setReason(''); setError(null); onClose(); };
  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm(reason.trim());
      setReason('');
      onClose();
    } catch (e) {
      setError(problemText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog role="alertdialog" title={title} isOpen={isOpen} onOpenChange={(open) => { if (!open) finish(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={finish}>{t('common.cancel')}</Button>
          <Button variant={danger ? 'danger' : 'primary'} isPending={busy} isDisabled={isDisabled || (!reasonOptional && reason.trim().length === 0)} onPress={() => { void run(); }}>{confirmLabel}</Button>
        </>
      )}>
      <div className="erp-stack">
        <p>{effect}</p>
        {children}
        <TextAreaField label={t('users.reason')} help={reasonOptional ? undefined : t('users.reason.help')} isRequired={!reasonOptional} value={reason} onChange={setReason} maxLength={500} error={error} />
      </div>
    </Dialog>
  );
}

/** A person from the lookup: name and username, active people of the caller's companies only. */
export function PersonPicker({ label, value, onChange, help, isRequired, exclude }: {
  label: string;
  value: Option | null;
  onChange: (option: Option | null) => void;
  help?: string;
  isRequired?: boolean;
  exclude?: string;
}) {
  const search = useCallback(async (text: string, signal: AbortSignal): Promise<Option[]> => {
    const found = await users.people(text, signal);
    return found.filter((p) => p.id !== exclude).map((p) => ({ id: p.id, label: p.displayName, detail: p.username }));
  }, [exclude]);
  return <ComboBoxField label={label} {...(help ? { help } : {})} {...(isRequired ? { isRequired } : {})} value={value} onChange={onChange} search={search} />;
}

export const accessKey = (userId: string) => ['iam', 'user-access', userId] as const;
export const recordKey = (userId: string) => ['iam', 'user', userId] as const;
export const listKey = ['iam', 'user-overview'] as const;

export type { MessageKey };
