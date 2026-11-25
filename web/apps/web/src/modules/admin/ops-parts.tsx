// Shared parts of the operations screens (slice S1.16c): the loading, error and permission-denied states of one query, plain-words
// error text, date and size formats in the person's language and time zone.
import { useCallback, type ReactNode } from 'react';
import type { UseQueryResult } from '@tanstack/react-query';
import type { CalendarDate } from '@internationalized/date';
import { Button, ErrorPanel, Skeleton } from '@erp/ui';
import { formatDate, formatDateTime, formatDecimal, formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { nameOf } from '../../api/workflow';
import './ops-messages';
import './users.css';

/** Loading, permission-denied and error states of one query (spec 13 STA-01 to STA-06); `children` gets the data. */
export function Loaded<T>({ query, children }: { query: UseQueryResult<T>; children: (data: T) => ReactNode }) {
  const { t } = useText();
  if (query.isPending) return <Skeleton lines={4} />;
  if (query.isError) {
    const problem = query.error instanceof ApiProblem ? query.error : null;
    return (
      <ErrorPanel message={problem?.status === 403 ? t('ops.denied') : t('ops.loadError')} reference={problem?.reference ?? null}
        action={problem?.status === 403 ? undefined : <Button variant="primary" onPress={() => { void query.refetch(); }}>{t('common.retry')}</Button>} />
    );
  }
  return <>{children(query.data)}</>;
}

/** The server's text for a refusal (its key is localised), or a plain fallback. */
export function useProblemText() {
  const { t, server } = useText();
  return useCallback((error: unknown, own: Record<string, string> = {}): string => {
    const keys = error instanceof ApiProblem ? [error.key, ...error.errors] : [];
    for (const key of keys) {
      const mapped = own[key];
      if (mapped) return mapped;
    }
    for (const key of keys) {
      const text = server(key);
      if (text) return text;
    }
    return t('ops.error.generic');
  }, [t, server]);
}

/** Dates and numbers the way the person set them up (language, time zone, digits). */
export function useFormats() {
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return {
    when: (iso: string | null | undefined) => (iso ? formatDateTime(iso, lang, timeZone, numbers) : '–'),
    day: (date: string | null | undefined) => (date ? formatDate(`${date.slice(0, 10)}T12:00:00Z`, lang, 'UTC', numbers) : '–'),
    int: (n: number) => formatInteger(n, numbers),
    decimal: (n: number, scale = 1) => formatDecimal(n.toFixed(scale), numbers),
    name: (names: Record<string, string> | null | undefined, fallback = '') => nameOf(names, locale) || fallback,
    /** Whole minutes from `iso` to the server's own `now` (the page never reads the browser clock for business facts). */
    minutesBetween: (iso: string | null | undefined, now: string) => (iso ? Math.max(0, Math.round((new Date(now).getTime() - new Date(iso).getTime()) / 60000)) : null),
  };
}

export const KB = 1024;

/** The first instant of a chosen day and of the day after it (UTC, as the audit search does), for `from` and `to` filters. */
export const dayStart = (d: CalendarDate | null) => (d ? `${d.toString()}T00:00:00Z` : undefined);
export const dayAfter = (d: CalendarDate | null) => (d ? `${d.add({ days: 1 }).toString()}T00:00:00Z` : undefined);
