// Small parts shared by the two template screens (slice S1.15d): a confirmation that states its effect, and the placeholder buttons.
import { useState, type ReactNode } from 'react';
import { Button, Dialog } from '@erp/ui';
import { useText } from '@erp/i18n';
import { useRefused } from './config-parts';

/** Asks before an action whose effect should be read first; a refusal of the server stays in the dialog. */
export function ConfirmDialog({ title, effect, confirmLabel, danger, onClose, onConfirm }: { title: string; effect: ReactNode; confirmLabel: string; danger?: boolean; onClose: () => void; onConfirm: () => Promise<void> }) {
  const { t } = useText();
  const refused = useRefused();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async () => {
    setBusy(true);
    setError(null);
    try { await onConfirm(); onClose(); } catch (e) { setError(refused(e).summary); } finally { setBusy(false); }
  };
  return (
    <Dialog role="alertdialog" title={title} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant={danger ? 'danger' : 'primary'} isPending={busy} onPress={() => { void run(); }}>{confirmLabel}</Button>
        </>
      )}>
      <div className="erp-stack"><p>{effect}</p>{error && <p role="alert" className="erp-error">{error}</p>}</div>
    </Dialog>
  );
}

/** A list of names a text may use; pressing one adds it where the text ends (the editors have no caret to track). */
export function PlaceholderList({ label, items, onPick, isDisabled }: { label: string; items: { name: string; note?: string }[]; onPick: (text: string) => void; isDisabled?: boolean }) {
  const { t } = useText();
  return (
    <div className="erp-stack">
      <span className="erp-small erp-muted" id="tpl-placeholders">{label}</span>
      <ul className="erp-row erp-wrap erp-tpl-placeholders" aria-labelledby="tpl-placeholders">
        {items.map((i) => (
          <li key={i.name}>
            <Button variant="quiet" isDisabled={isDisabled} aria-label={t('tpl.insert.aria', { name: i.name })} onPress={() => onPick(i.name)}><span className="erp-code"><bdi>{i.name}</bdi></span></Button>
            {i.note && <span className="erp-small erp-muted"> {i.note}</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}
