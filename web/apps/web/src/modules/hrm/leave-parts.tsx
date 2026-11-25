// Pieces shared by the leave screens (slice S1.33a; FR-HRM-LVE-04 to -06): status chips, leave type labels with their colour token, quantities, the day-by-day list with
// the server's explanation of each day, the ledger entries behind a balance, and the refusal text of the server's answers. Colour is never the only sign: a dot is
// always next to the name, and status and sandwich marks carry text and an icon.
import './leave.css';
import { useQuery } from '@tanstack/react-query';
import { Layers } from 'lucide-react';
import { leaveTypes, type LeaveType } from '@erp/generated/api/hrm';
import { StatusChip, type Status, ScrollRegion } from '@erp/ui';
import { formatDate, formatDecimal, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import type { LedgerEntry, LeaveDetails } from '../../api/leave';
import { nameOf } from '../../api/workflow';
import './leave-messages';

export type RequestStatus = LeaveDetails['status'];

const CHIP: Record<RequestStatus, Status> = { draft: 'draft', submitted: 'pending', approved: 'approved', rejected: 'rejected', cancel_requested: 'pending', cancelled: 'inactive' };

export function LeaveStatus({ status }: { status: RequestStatus }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status]} label={t(`leave.status.${status}` as MessageKey)} />;
}

/** The server's words for an answer that refused (a key, or a text of its own), else a general line. */
export function useLeaveRefusal(): (error: unknown) => string {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return t('leave.error.network');
      const first = Object.values(error.fieldErrors)[0]?.[0];
      return server(error.key) ?? (first ? server(first) : null) ?? t('leave.error.generic');
    }
    return t('leave.error.generic');
  };
}

/** Every reason of a refused request in plain words: the one reason, its values, and each field's reasons. */
export function useReasons(): (error: string | null, errors: Record<string, string[]> | null, details: Record<string, unknown> | null) => string[] {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const num = (v: unknown) => formatDecimal(String(v), numbers);
  return (error, errors, details) => {
    const out: string[] = [];
    if (error) {
      let line = server(error) ?? t('leave.error.generic');
      const d = details ?? {};
      if (typeof d.date === 'string') line += ` ${t('leave.detail.date', { date: formatDate(`${d.date}T12:00:00Z`, lang, timeZone, numbers) })}`;
      if (d.lowest !== undefined && d.allowed !== undefined) line += ` ${t('leave.detail.lowest', { lowest: num(d.lowest), allowed: num(d.allowed) })}`;
      if (typeof d.yearEnd === 'string') line += ` ${t('leave.detail.yearEnd', { date: formatDate(`${d.yearEnd}T12:00:00Z`, lang, timeZone, numbers) })}`;
      out.push(line);
    }
    for (const keys of Object.values(errors ?? {})) for (const key of keys) out.push(server(key) ?? t('leave.error.generic'));
    return [...new Set(out)];
  };
}

/** The leave types of a company by id (colour token, unit, rules), from the generated list. */
export function useLeaveTypes(companyId: string, activeOnly = false) {
  return useQuery({
    queryKey: ['hrm', 'leave', 'types', companyId, activeOnly],
    queryFn: async () => (await leaveTypes.list({ companyId, limit: 200, ...(activeOnly ? { isActive: true } : {}) })).items,
    enabled: !!companyId,
    staleTime: 60_000,
  });
}

export function typeNameOf(type: Pick<LeaveType, 'nameI18n' | 'code'> | undefined, locale: string): string {
  return type ? nameOf(type.nameI18n, locale) || type.code : '';
}

/** The name of a leave type with its colour dot (the dot is decoration: the name is always there). */
export function TypeLabel({ name, token }: { name: string; token: string | null | undefined }) {
  return (
    <span className="erp-row">
      <span className="erp-leave-dot" data-token={token ?? 'neutral'} aria-hidden="true" />
      <bdi>{name}</bdi>
    </span>
  );
}

/** "2 days", "1 day", "3 hours" in the person's number style. */
export function useQuantity(): (value: number | string | null | undefined, unit: string | undefined) => string {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  return (value, unit) => {
    if (value === null || value === undefined) return '';
    const n = Number(value);
    const shown = formatDecimal(String(value), numbers);
    return t(unit === 'hour' ? 'leave.qty.hours' : 'leave.qty.days', { count: Math.abs(n), n: shown });
  };
}

export interface DayRow { date: string; quantity: number; kind: string; text: string | null }

/** The days of a request one by one, with the server's explanation of each (sandwich days and days that are not counted are marked). */
export function DayList({ days, unit }: { days: DayRow[]; unit: string | undefined }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const qty = useQuantity();
  return (
    <ScrollRegion className="erp-leave-scroll" label={t('leave.days.caption')}>
      <table className="erp-leave-table">
        <caption className="erp-visually-hidden">{t('leave.days.caption')}</caption>
        <thead><tr><th scope="col">{t('leave.days.date')}</th><th scope="col">{t('leave.days.counted')}</th><th scope="col">{t('leave.days.why')}</th></tr></thead>
        <tbody>
          {days.map((d) => (
            <tr key={d.date}>
              <td><bdi>{formatDate(`${d.date}T12:00:00Z`, lang, timeZone, numbers)}</bdi></td>
              <td>{qty(d.quantity, unit)}</td>
              <td>
                {d.kind === 'sandwich' && <span className="erp-chip" data-tone="info"><Layers size={14} aria-hidden="true" />{t('leave.day.sandwich')}</span>}
                {d.kind === 'off' && <span className="erp-chip" data-tone="neutral">{t('leave.day.off')}</span>}
                {d.text ? <span> {d.text}</span> : null}
              </td>
            </tr>))}
        </tbody>
      </table>
    </ScrollRegion>
  );
}

/** The ledger entries behind a balance: date, what it was, the quantity with its sign, the reason or period, and when it expires. */
export function EntryList({ entries, unit }: { entries: LedgerEntry[]; unit: string | undefined }) {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers);
  if (entries.length === 0) return <p className="erp-muted">{t('leave.entries.empty')}</p>;
  return (
    <ScrollRegion className="erp-leave-scroll" label={t('leave.entries.caption')}>
      <table className="erp-leave-table">
        <caption className="erp-visually-hidden">{t('leave.entries.caption')}</caption>
        <thead><tr><th scope="col">{t('leave.entries.date')}</th><th scope="col">{t('leave.entries.what')}</th><th scope="col">{t('leave.entries.quantity')}</th><th scope="col">{t('leave.entries.note')}</th></tr></thead>
        <tbody>
          {entries.map((e) => (
            <tr key={e.id}>
              <td><bdi>{day(e.entryDate)}</bdi></td>
              <td>{server(e.explanation) ?? e.kind}</td>
              <td><bdi dir="ltr">{e.quantity > 0 ? '+' : ''}{formatDecimal(String(e.quantity), numbers)}</bdi> {unit === 'hour' ? t('leave.unit.hours') : t('leave.unit.days')}</td>
              <td>{[e.reason, e.period, e.expiresOn ? t('leave.entries.expires', { date: day(e.expiresOn) }) : null, e.reversedById ? t('leave.entries.reversed') : null].filter(Boolean).join(' · ')}</td>
            </tr>))}
        </tbody>
      </table>
    </ScrollRegion>
  );
}
