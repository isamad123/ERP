// A confirmation dialog without a reason field (slice S1.13b): create, rename and remove-a-member changes the server does
// not take a reason for. It states the effect, shows the server's refusal inside, and stays open until the change is made.
import { useState, type ReactNode } from 'react';
import { Banner, Button, Dialog } from '@erp/ui';
import { useText } from '@erp/i18n';
import { useProblemText } from './users-parts';

export function FormDialog({ title, effect, confirmLabel, danger, isDisabled, onClose, onConfirm, children }: {
  title: string;
  effect: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  isDisabled?: boolean;
  onClose: () => void;
  onConfirm: () => Promise<void>;
  children?: ReactNode;
}) {
  const { t } = useText();
  const problemText = useProblemText();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
      onClose();
    } catch (e) {
      setError(problemText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog role="alertdialog" title={title} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant={danger ? 'danger' : 'primary'} isPending={busy} isDisabled={isDisabled} onPress={() => { void run(); }}>{confirmLabel}</Button>
        </>
      )}>
      <div className="erp-stack">
        <p>{effect}</p>
        {children}
        {error && <Banner tone="danger">{error}</Banner>}
      </div>
    </Dialog>
  );
}
