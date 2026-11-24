// Shared pieces of the attendance capture screens (slice S1.27a): the answer of a punch as one line of words, and refusals as the server's own texts.
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import type { PunchResult } from '../../api/attendance';

export function useRefusal(): (error: unknown) => string {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return t('att.error.network');
      return server(error.key) ?? t('att.error.generic');
    }
    return t('att.error.generic');
  };
}

export const directionKey = (d: string): MessageKey => `att.direction.${d}` as MessageKey;
export const sourceKey = (s: string): MessageKey => `att.source.${s}` as MessageKey;

export function doneKey(result: PunchResult): MessageKey {
  if (result.status === 'already_received') return 'att.done.again';
  return result.direction === 'out' ? 'att.done.out' : 'att.done.in';
}
