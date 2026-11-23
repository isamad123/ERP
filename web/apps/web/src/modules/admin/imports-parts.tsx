// Shared parts of the import screens (slice S1.16a): the status chip, the plain-words text of a refusal, the file picker and the
// helpers that pick a name in the person's language.
import { useCallback, useId, type ChangeEvent } from 'react';
import { StatusChip, type Status } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import type { BatchStatus } from '../../api/imports';
import { nameOf } from '../../api/workflow';

export const BATCHES_KEY = ['itg', 'imports', 'batches'] as const;
export const batchKey = (id: string) => ['itg', 'imports', 'batch', id] as const;
export const IMPORTS_PATH = '/admin/imports';

const CHIP: Record<BatchStatus, Status> = {
  uploaded: 'draft', validating: 'pending', validated: 'info', committing: 'pending', committed: 'approved', failed: 'blocked', rolled_back: 'inactive',
};

export function BatchStatusChip({ status }: { status: BatchStatus }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status]} label={t(`imp.status.${status}` as MessageKey)} />;
}

/** The server's refusal keys of the import engine, said in plain words; any other key falls back to the server's own text. */
const REFUSALS: Record<string, MessageKey> = {
  'errors.import.infected': 'imp.refused.infected',
  'errors.import.too_large': 'imp.refused.tooLarge',
  'errors.import.too_many_rows': 'imp.refused.tooManyRows',
  'errors.import.unsupported_file': 'imp.refused.unsupported',
  'errors.import.rollback_changed': 'imp.refused.rollbackChanged',
  'errors.import.rollback_expired': 'imp.refused.rollbackExpired',
  'errors.import.rollback_updated': 'imp.refused.rollbackUpdated',
  'errors.import.rollback_not_committed': 'imp.refused.rollbackNotCommitted',
  'errors.access.forbidden': 'imp.refused.forbidden',
};

export function useRefusalText() {
  const { t, server } = useText();
  return useCallback((error: unknown): string => {
    const keys = error instanceof ApiProblem ? [error.key, ...error.errors] : typeof error === 'string' ? [error] : [];
    for (const key of keys) if (REFUSALS[key]) return t(REFUSALS[key] as MessageKey);
    for (const key of keys) {
      const text = server(key);
      if (text) return text;
    }
    return t('imp.error.generic');
  }, [t, server]);
}

/** A native file input with its visible label (keyboard first: no drag and drop). */
export function FilePicker({ label, help, accept, onChange, error }: { label: string; help?: string; accept: string; onChange: (file: File | null) => void; error?: string | null }) {
  const id = useId();
  return (
    <div className="erp-stack">
      <label htmlFor={id}><strong>{label}</strong></label>
      <input id={id} type="file" accept={accept} aria-describedby={help ? `${id}-help` : undefined} aria-invalid={error ? true : undefined}
        onChange={(event: ChangeEvent<HTMLInputElement>) => onChange(event.target.files?.[0] ?? null)} />
      {help && <span id={`${id}-help`} className="erp-help">{help}</span>}
      {error && <span role="alert" className="erp-field-error">{error}</span>}
    </div>
  );
}

export function useNameOf() {
  const { locale } = useLocaleSettings();
  return (names: Record<string, string> | null | undefined, fallback = '') => nameOf(names, locale) || fallback;
}
