// The texts of the HR leave ledger screen (slice S1.33b), registered when the lazily loaded screen loads, so they are not part of the shell's scripts (R-213). Their keys are not in
// the shell's `MessageKey` list (the catalogue is lazy), so `useLedgerText` gives a `t` typed by this catalogue's own keys.
import en from '@erp/i18n/messages/leave-ledger.en.json';
import ur from '@erp/i18n/messages/leave-ledger.ur.json';
import { registerMessages, useText, type MessageKey } from '@erp/i18n';

registerMessages({ en, ur });

export type LedgerKey = keyof typeof en;

export function useLedgerText() {
  const text = useText();
  return { ...text, t: (key: LedgerKey, values?: Record<string, string | number | Date>) => text.t(key as unknown as MessageKey, values) };
}
