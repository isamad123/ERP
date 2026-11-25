// Shared pieces of the live attendance dashboard and the team view (slice S1.30b; FR-HRM-ATT-18, FR-HRM-SSP-02): the company in hand, unit and location names (the
// server answers with ids), the count cards, the count tables and the list of people behind a count (paged with "show more").
import { useMemo, useState } from 'react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { Button, EmptyState, ErrorPanel, Skeleton, ScrollRegion } from '@erp/ui';
import { formatInteger, formatDate, formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { attendanceApi } from '../../api/attendance';
import { attendanceReportsApi, type Counts, type DashboardFilter, type PersonToday } from '../../api/attendance-reports';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import './attendance-reports-messages';

export const PAGE = 50;

export function useCompanyChoice() {
  const { me, activeCompany } = useShell();
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.defaultCompanyId ?? me.companies[0]?.id ?? '');
  return { company, setCompany, options: me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code })) };
}

/** Names of the units and locations the answers refer to by id; an unknown id shows as "Not assigned". */
export function useOrgNames(company: string, enabled = true) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const units = useQuery({ queryKey: ['hrm', 'live', 'units'], queryFn: ({ signal }) => attendanceReportsApi.units(signal), enabled, staleTime: 60_000 });
  const locations = useQuery({ queryKey: ['hrm', 'live', 'locations', company], queryFn: ({ signal }) => attendanceApi.locations(company, signal), enabled: enabled && !!company, staleTime: 60_000 });
  return useMemo(() => {
    const unitMap = new Map((units.data?.items ?? []).map((u) => [u.id, nameOf(u.nameI18n, locale) || u.code]));
    const locationMap = new Map((locations.data?.items ?? []).map((l) => [l.id, nameOf(l.nameI18n, locale) || l.code]));
    return {
      units: (units.data?.items ?? []).map((u) => ({ id: u.id, label: unitMap.get(u.id) ?? u.code })),
      locations: (locations.data?.items ?? []).map((l) => ({ id: l.id, label: locationMap.get(l.id) ?? l.code })),
      unitName: (id: string | null) => (id ? unitMap.get(id) ?? t('alive.unknown') : t('alive.unknown')),
      locationName: (id: string | null) => (id ? locationMap.get(id) ?? t('alive.unknown') : t('alive.unknown')),
    };
  }, [units.data, locations.data, locale, t]);
}

export function useNumberText() {
  const { numbers, locale, timeZone } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return {
    count: (n: number) => formatInteger(n, numbers),
    date: (iso: string) => formatDate(`${iso}T12:00:00Z`, lang, timeZone, numbers),
    dateTime: (iso: string) => formatDateTime(iso, lang, timeZone, numbers),
  };
}

export function isDenied(error: unknown): boolean {
  return error instanceof ApiProblem && error.status === 403;
}

const CARDS = ['present', 'absent', 'on_leave', 'on_duty', 'not_yet_punched', 'not_due', 'not_scheduled', 'no_policy'] as const;
const COUNT_OF: Record<(typeof CARDS)[number] | 'late', keyof Counts> = {
  present: 'present', late: 'late', absent: 'absent', on_leave: 'onLeave', on_duty: 'onDuty', not_yet_punched: 'notYetPunched', not_due: 'notDue', not_scheduled: 'notScheduled', no_policy: 'noPolicy',
};

/** Today's counts as cards; each is a button that opens the people behind it. */
export function CountCards({ counts, selected, onOpen }: { counts: Counts; selected: string | null; onOpen: (status: string) => void }) {
  const { t } = useText();
  const text = useNumberText();
  const card = (status: (typeof CARDS)[number] | 'late', headline: boolean) => {
    const label = t(`alive.card.${status}` as MessageKey);
    return (
      <button key={status} type="button" className="erp-live-card" data-selected={selected === status || undefined} data-headline={headline || undefined}
        aria-pressed={selected === status} onClick={() => onOpen(status)}>
        <span className="erp-live-card-count">{text.count(counts[COUNT_OF[status]])}</span>
        <span className="erp-live-card-label">{label}</span>
      </button>
    );
  };
  return (
    <div className="erp-live-cards" role="group" aria-label={t('alive.cards')}>
      <div className="erp-live-card" data-static="true">
        <span className="erp-live-card-count">{text.count(counts.headcount)}</span>
        <span className="erp-live-card-label">{t('alive.card.headcount')}</span>
      </div>
      {CARDS.map((status) => (status === 'present' ? [card('present', true), card('late', false)] : [card(status, false)]))}
    </div>
  );
}

const TABLE_COLUMNS = ['headcount', 'present', 'late', 'absent', 'on_leave', 'on_duty', 'not_yet_punched'] as const;

/** The table alternative of the counts: one row per unit or location. */
export function CountsTable({ caption, labelHeader, rows }: { caption: string; labelHeader: string; rows: { key: string; label: string; counts: Counts }[] }) {
  const { t } = useText();
  const text = useNumberText();
  return (
    <ScrollRegion className="erp-live-scroll" label={caption}><table className="erp-pos-table">
      <caption>{caption}</caption>
      <thead><tr><th scope="col">{labelHeader}</th>{TABLE_COLUMNS.map((c) => <th key={c} scope="col">{t(`alive.col.${c}` as MessageKey)}</th>)}</tr></thead>
      <tbody>{rows.map((row) => (
        <tr key={row.key}>
          <th scope="row"><bdi>{row.label}</bdi></th>
          {TABLE_COLUMNS.map((c) => <td key={c}>{text.count(row.counts[c === 'headcount' ? 'headcount' : COUNT_OF[c]])}</td>)}
        </tr>))}</tbody>
    </table></ScrollRegion>
  );
}

/** The people behind a count, with paging ("Show more") and the same refresh as the counts. */
export function PeopleList({ filter, status, onClose, refetchMs, names }: {
  filter: DashboardFilter; status: string; onClose?: () => void; refetchMs: number; names: ReturnType<typeof useOrgNames>;
}) {
  const { t } = useText();
  const text = useNumberText();
  const people = useInfiniteQuery({
    queryKey: ['hrm', 'attendance-live', 'people', filter, status],
    queryFn: ({ pageParam, signal }) => attendanceReportsApi.people({ ...filter, status, limit: PAGE, offset: pageParam }, signal),
    initialPageParam: 0,
    getNextPageParam: (last, all) => (last.more ? all.reduce((n, p) => n + p.items.length, 0) : undefined),
    refetchInterval: refetchMs,
  });
  const rows: PersonToday[] = people.data?.pages.flatMap((p) => p.items) ?? [];
  const total = people.data?.pages[0]?.total ?? 0;
  const title = t('alive.people.title', { status: t(`alive.status.${status}` as MessageKey) });
  return (
    <section className="erp-stack" aria-label={title}>
      <div className="erp-row erp-wrap">
        <h2>{title}</h2>
        {people.data && <span className="erp-muted">{t('alive.people.count', { count: text.count(total) })}</span>}
        {onClose && <Button variant="quiet" onPress={onClose}>{t('alive.people.close')}</Button>}
      </div>
      {people.isPending && <Skeleton />}
      {people.isError && <ErrorPanel title={t('alive.people.error')} message={isDenied(people.error) ? t('alive.denied') : t('alive.people.error')} action={<Button variant="primary" onPress={() => { void people.refetch(); }}>{t('common.retry')}</Button>} />}
      {people.data && rows.length === 0 && <EmptyState title={t('alive.people.empty')} />}
      {rows.length > 0 && (
        <ScrollRegion className="erp-live-scroll" label={title}><table className="erp-pos-table">
          <caption className="erp-visually-hidden">{title}</caption>
          <thead><tr>
            <th scope="col">{t('alive.col.employeeNo')}</th><th scope="col">{t('alive.col.name')}</th><th scope="col">{t('alive.col.unit')}</th><th scope="col">{t('alive.col.location')}</th>
            <th scope="col">{t('alive.col.status')}</th><th scope="col">{t('alive.col.shift')}</th><th scope="col">{t('alive.col.firstPunch')}</th>
          </tr></thead>
          <tbody>{rows.map((p) => (
            <tr key={p.employmentId}>
              <td><bdi>{p.employeeNo}</bdi></td>
              <td><bdi>{`${p.firstName} ${p.lastName}`.trim()}</bdi></td>
              <td><bdi>{names.unitName(p.unitId)}</bdi></td>
              <td><bdi>{names.locationName(p.locationId)}</bdi></td>
              <td>{t(`alive.status.${p.status}` as MessageKey)}</td>
              <td><bdi>{p.shiftStart && p.shiftEnd ? t('alive.shift', { from: text.dateTime(p.shiftStart), to: text.dateTime(p.shiftEnd) }) : ''}</bdi></td>
              <td><bdi>{p.firstPunch ? text.dateTime(p.firstPunch) : ''}</bdi></td>
            </tr>))}</tbody>
        </table></ScrollRegion>
      )}
      {people.hasNextPage && <div><Button variant="secondary" isPending={people.isFetchingNextPage} onPress={() => { void people.fetchNextPage(); }}>{t('alive.people.more')}</Button></div>}
    </section>
  );
}
