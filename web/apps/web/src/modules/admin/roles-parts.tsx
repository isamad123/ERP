// Shared parts of the role screens (slice S1.13b Build items 6 and 7): query keys, the "shipped / custom" and status chips,
// the answer handler for changes that may wait for a second administrator, and the rename dialog.
import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Button, Dialog, StatusChip, TextField, useToast } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import type { Names } from '../../api/workflow';
import { isPending, type AccessAnswer } from '../../api/access';
import { useProblemText } from './users-parts';

export const ROLES_PATH = '/admin/roles';
export const TENANT_ADMIN = 'tenant_admin';
export const rolesKey = ['iam', 'roles', 'all'] as const;
export const summaryKey = (roleId: string) => ['iam', 'role-summary', roleId] as const;
export const catalogKey = ['iam', 'permission-catalog'] as const;

/** The protected Tenant Admin role (CR-024): its permissions are never edited here. */
export const isProtectedRole = (role: { code: string; baselineCode?: string | null }) =>
  role.code.toLowerCase() === TENANT_ADMIN || role.baselineCode?.toLowerCase() === TENANT_ADMIN;

export function SourceChip({ role }: { role: { source: string; baselineCode?: string | null } }) {
  const { t } = useText();
  const shipped = role.source !== 'custom' || !!role.baselineCode;
  return <StatusChip status={shipped ? 'info' : 'draft'} label={shipped ? t('roles.kind.shipped') : t('roles.kind.custom')} />;
}

export function ActiveChip({ isActive }: { isActive: boolean }) {
  const { t } = useText();
  return <StatusChip status={isActive ? 'active' : 'inactive'} label={isActive ? t('roles.status.active') : t('roles.status.archived')} />;
}

/** After a change: refresh the role views, then say "done" or "waiting for a second administrator" (FR-KRN-IAM-14). */
export function useRoleAnswer() {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  return (answer: AccessAnswer | undefined, done: MessageKey) => {
    void queryClient.invalidateQueries({ queryKey: ['iam'] });
    if (isPending(answer)) toast.info(t('roles.pending'));
    else toast.success(t(done));
  };
}

/** Name in English and Urdu (the languages of Phase 1). */
export function NameDialog({ title, initial, confirmLabel, onClose, onSave }: {
  title: string;
  initial: Names;
  confirmLabel: string;
  onClose: () => void;
  onSave: (name: Names) => Promise<void>;
}) {
  const { t } = useText();
  const problemText = useProblemText();
  const [en, setEn] = useState(initial.en ?? '');
  const [ur, setUr] = useState(initial.ur ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSave({ en: en.trim(), ...(ur.trim() ? { ur: ur.trim() } : {}) });
      onClose();
    } catch (e) {
      setError(problemText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={title} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} isDisabled={en.trim().length === 0} onPress={() => { void save(); }}>{confirmLabel}</Button>
        </>
      )}>
      <TextField label={t('roles.name.en')} isRequired value={en} onChange={setEn} maxLength={200} error={error} />
      <TextField label={t('roles.name.ur')} value={ur} onChange={setUr} maxLength={200} />
    </Dialog>
  );
}
