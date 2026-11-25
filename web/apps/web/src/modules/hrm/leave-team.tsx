// The team leave calendar (slice S1.33a Build item 4; FR-HRM-LVE-15, LVE-05; Q1, Q3, Q4): a month grid of the people the caller may see (the server limits the answer to the data
// scope of the leave-request read grant), leave by person and day, holidays, weekly offs, and a coverage row that warns, and never blocks, when too many are away. Colours are the leave
// type's design token; waiting requests are hatched and italic with a clock so they differ from approved ones without colour. The grid is a real grid with arrow keys. Print is the
// browser's (a print stylesheet). The month starts at the company's current month.
import { useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Clock, TriangleAlert, ChevronLeft, ChevronRight, Printer } from 'lucide-react';
import { useDocumentTitle } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton } from '@erp/ui';
import { formatDecimal, formatInteger, intlLocale, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { leaveCalendarApi, type CalendarCoverage, type CalendarPerson, type CalendarType, type LeaveCalendar } from '../../api/leave-calendar';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { isDenied } from './live-parts';
import { useRefusal } from './requests-forms';
import { todayIn } from '../hr/policies-model';
import { cellFor, colourOf, coverageData, coverageIndex, daysOfMonth, holidayIndex, monthOf, monthRange, moveFocus, shiftMonth, weekdayOf, type Month, type Move } from './leave-team-model';
import './leave-calendar-messages';
import './leave-team.css';

const PAGE = 50;
const ALL = 'all';
const KEYS: Record<string, Move> = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down', Home: 'home', End: 'end' };

export default function LeaveTeamScreen() {
  const { t } = useText();
  const { can, me, activeCompany } = useShell();
  const refusal = useRefusal();
  const { locale, numbers } = useLocaleSettings();
  useDocumentTitle(t('nav.leaveTeam'));
  const allowed = can('hrm.leave_requests.read');
  const [month, setMonth] = useState<Month>(() => monthOf(todayIn(me.timeZone)));
  const [company, setCompany] = useState(activeCompany && me.companies.length > 1 ? activeCompany.id : ALL);
  const [unit, setUnit] = useState(ALL);
  const [type, setType] = useState(ALL);
  const [offset, setOffset] = useState(0);
  const range = monthRange(month);
  const filter = { companyId: company === ALL ? undefined : company, unitId: unit === ALL ? undefined : unit, leaveTypeId: type === ALL ? undefined : type, ...range, limit: PAGE, offset };
  const calendar = useQuery({ queryKey: ['hrm', 'leave-calendar', filter], queryFn: ({ signal }) => leaveCalendarApi.calendar(filter, signal), enabled: allowed });
  const units = useQuery({ queryKey: ['hrm', 'leave-calendar', 'units'], queryFn: ({ signal }) => leaveCalendarApi.units(signal), enabled: allowed, staleTime: 60_000 });
  const types = useQuery({ queryKey: ['hrm', 'leave-calendar', 'types'], queryFn: ({ signal }) => leaveCalendarApi.leaveTypes(signal), enabled: allowed, staleTime: 60_000 });
  const lang = locale === 'ur' ? 'ur' : 'en';
  const num = (n: number) => formatInteger(n, numbers);
  const decimal = (n: number) => formatDecimal(String(n), numbers);
  const dateFmt = useMemo(() => new Intl.DateTimeFormat(intlLocale(lang, numbers), { day: 'numeric', month: 'long', timeZone: 'UTC' }), [lang, numbers]);
  const monthFmt = useMemo(() => new Intl.DateTimeFormat(intlLocale(lang, numbers), { month: 'long', year: 'numeric', timeZone: 'UTC' }), [lang, numbers]);
  const weekdayFmt = useMemo(() => new Intl.DateTimeFormat(intlLocale(lang, numbers), { weekday: 'short', timeZone: 'UTC' }), [lang, numbers]);
  const at = (iso: string) => new Date(`${iso}T12:00:00Z`);
  const monthName = monthFmt.format(at(range.from));

  const go = (by: number) => { setMonth((m) => shiftMonth(m, by)); setOffset(0); };
  const choose = (set: (v: string) => void) => (v: string) => { set(v); setOffset(0); };
  const unitOptions = [{ id: ALL, label: t('lcal.all') }, ...(units.data?.items ?? []).map((u) => ({ id: u.id, label: nameOf(u.nameI18n, locale) || u.code }))];
  const typeOptions = [{ id: ALL, label: t('lcal.all') }, ...(types.data?.items ?? []).map((x) => ({ id: x.id, label: nameOf(x.nameI18n, locale) || x.code }))];
  const companyOptions = [{ id: ALL, label: t('lcal.all') }, ...me.companies.map((c) => ({ id: c.id, label: c.name[locale] ?? c.name.en ?? c.code }))];

  if (!allowed) return <div className="erp-page"><PageHeader title={t('nav.leaveTeam')} /><EmptyState title={t('lcal.denied')} /></div>;
  const data = calendar.data;
  return (
    <div className="erp-page">
      <PageHeader title={t('nav.leaveTeam')} description={t('lcal.description')} />
      <div className="erp-stack">
        <div className="erp-lcal-filters erp-lcal-noprint" role="group" aria-label={t('lcal.filters')}>
          <div className="erp-lcal-month" role="group" aria-label={t('lcal.month')}>
            <Button variant="secondary" icon={locale === 'ur' ? <ChevronRight size={16} aria-hidden="true" /> : <ChevronLeft size={16} aria-hidden="true" />} onPress={() => go(-1)}><span className="erp-visually-hidden">{t('lcal.month.prev')}</span></Button>
            <span className="erp-lcal-month-name" aria-live="polite">{monthName}</span>
            <Button variant="secondary" icon={locale === 'ur' ? <ChevronLeft size={16} aria-hidden="true" /> : <ChevronRight size={16} aria-hidden="true" />} onPress={() => go(1)}><span className="erp-visually-hidden">{t('lcal.month.next')}</span></Button>
          </div>
          {me.companies.length > 1 && <SelectField label={t('lcal.company')} options={companyOptions} value={company} onChange={choose(setCompany)} />}
          <SelectField label={t('lcal.unit')} options={unitOptions} value={unit} onChange={choose(setUnit)} />
          <SelectField label={t('lcal.type')} options={typeOptions} value={type} onChange={choose(setType)} />
          <Button variant="secondary" icon={<Printer size={16} aria-hidden="true" />} onPress={() => { window.print(); }}>{t('lcal.print')}</Button>
        </div>
        {calendar.isPending && <Skeleton lines={5} />}
        {calendar.isError && (
          <ErrorPanel title={t('lcal.loadError')} message={isDenied(calendar.error) ? t('lcal.denied') : refusal(calendar.error)}
            action={isDenied(calendar.error) ? null : <Button variant="primary" onPress={() => { void calendar.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {data && data.people.length === 0 && <EmptyState title={t('lcal.empty')}>{t('lcal.empty.body')}</EmptyState>}
        {data && data.people.length > 0 && (
          <>
            <CalendarGrid data={data} month={month} monthName={monthName} num={num} decimal={decimal} date={(iso) => dateFmt.format(at(iso))} weekday={(iso) => weekdayFmt.format(at(iso))}
              typeName={(ty) => nameOf(ty.nameI18n, locale) || ty.code} companyName={(id) => { const c = me.companies.find((x) => x.id === id); return c ? (c.name[locale] ?? c.name.en ?? c.code) : ''; }} locale={locale}
              onPage={go} />
            {(data.more || offset > 0) && (
              <div className="erp-row erp-wrap erp-lcal-noprint">
                <span>{t('lcal.paging', { from: num(offset + 1), to: num(offset + data.people.length), total: num(data.total) })}</span>
                <Button variant="secondary" isDisabled={offset === 0} onPress={() => setOffset(Math.max(0, offset - PAGE))}>{t('lcal.paging.prev')}</Button>
                <Button variant="secondary" isDisabled={!data.more} onPress={() => setOffset(offset + PAGE)}>{t('lcal.paging.next')}</Button>
              </div>
            )}
            <Legend data={data} types={types.data?.items ?? []} typeName={(ty) => nameOf(ty.nameI18n, locale) || ty.code} />
          </>
        )}
      </div>
    </div>
  );
}

interface GridProps {
  data: LeaveCalendar; month: Month; monthName: string; locale: string;
  num: (n: number) => string; decimal: (n: number) => string; date: (iso: string) => string; weekday: (iso: string) => string;
  typeName: (type: CalendarType) => string; companyName: (id: string) => string; onPage: (by: number) => void;
}

function CalendarGrid({ data, month, monthName, num, decimal, date, weekday, typeName, companyName, locale, onPage }: GridProps) {
  const { t } = useText();
  const days = useMemo(() => daysOfMonth(month), [month]);
  const holidays = useMemo(() => holidayIndex(data.holidays), [data.holidays]);
  const coverage = useMemo(() => coverageIndex(data.coverage), [data.coverage]);
  const types = useMemo(() => new Map(data.types.map((x) => [x.id, x])), [data.types]);
  const todays = new Map(data.companies.map((c) => [c.companyId, c.today]));
  const limits = new Map(data.companies.map((c) => [c.companyId, c.limitPercent]));
  const coverRows = data.companies.map((c) => c.companyId);
  const rows = coverRows.length + data.people.length;
  const [focus, setFocus] = useState({ r: 0, c: 0 });
  const grid = useRef<HTMLTableElement>(null);
  const at = { r: Math.min(focus.r, rows - 1), c: Math.min(focus.c, days.length - 1) };

  const holidayOf = (iso: string): string | null => {
    for (const byDate of holidays.values()) { const names = byDate.get(iso); if (names) return nameOf(names, locale); }
    return null;
  };
  const onKey = (e: KeyboardEvent<HTMLTableElement>) => {
    const cell = (e.target as HTMLElement).closest<HTMLElement>('[data-r]');
    if (!cell) return;
    if (e.key === 'PageUp' || e.key === 'PageDown') { e.preventDefault(); onPage(e.key === 'PageUp' ? -1 : 1); return; }
    const move = KEYS[e.key];
    if (!move) return;
    e.preventDefault();
    const next = moveFocus({ r: Number(cell.dataset.r), c: Number(cell.dataset.c) }, move, rows, days.length);
    setFocus(next);
    grid.current?.querySelector<HTMLElement>(`[data-r="${next.r}"][data-c="${next.c}"]`)?.focus();
  };
  const tab = (r: number, c: number) => (at.r === r && at.c === c ? 0 : -1);

  const coverCell = (companyId: string, r: number, c: number, iso: string) => {
    const row: CalendarCoverage | undefined = coverage.get(`${companyId}|${iso}`);
    const d = coverageData(row);
    const values = { away: num(d?.away ?? 0), size: num(d?.teamSize ?? 0), date: date(iso), limit: num(limits.get(companyId) ?? 0) };
    const name = !d ? t('lcal.coverage.none', { date: date(iso) }) : d.warn ? t('lcal.coverage.warn', values) : t('lcal.coverage.cell', values);
    return (
      <td key={iso} role="gridcell" className="erp-lcal-cover" data-r={r} data-c={c} tabIndex={tab(r, c)} aria-label={name} title={name} {...(d?.warn ? { 'data-warn': true } : {})}
        {...(todays.get(companyId) === iso ? { 'data-today': true } : {})}>
        {d && (
          <>
            <span aria-hidden="true">{values.away}/{values.size}</span>
            <span className="erp-lcal-cover-percent" aria-hidden="true">{d.warn ? <TriangleAlert size={12} /> : null}{t('lcal.coverage.percent', { percent: decimal(d.percent) })}</span>
          </>
        )}
      </td>
    );
  };

  const personCell = (p: CalendarPerson, r: number, c: number, iso: string) => {
    const cell = cellFor(p, iso, holidays);
    const person = `${p.firstName ?? ''} ${p.lastName ?? ''}`.trim() || p.employeeNo;
    const common = { role: 'gridcell' as const, className: 'erp-lcal-cell', 'data-r': r, 'data-c': c, 'data-kind': cell.kind, tabIndex: tab(r, c), ...(todays.get(p.companyId) === iso ? { 'data-today': true } : {}) };
    if (cell.kind === 'leave') {
      const first = cell.leaves[0];
      if (!first) return null;
      const ty = types.get(first.day.leaveTypeId);
      const label = ty?.code ?? t('lcal.unknownType');
      const parts = cell.leaves.map((l) => {
        const tyName = types.get(l.day.leaveTypeId);
        return tyName ? typeName(tyName) : t('lcal.unknownType');
      });
      const status = t(`lcal.status.${first.day.status}` as MessageKey) + (first.sandwich ? `, ${t('lcal.cell.sandwich')}` : '');
      const name = t(first.half ? 'lcal.cell.leaveHalf' : 'lcal.cell.leave', { name: person, date: date(iso), type: parts.join(' + '), status });
      return (
        <td key={iso} {...common} aria-label={name} title={name} data-colour={colourOf(ty?.colorToken)} {...(first.waiting ? { 'data-waiting': true } : {})} {...(first.sandwich ? { 'data-sandwich': true } : {})}>
          <span aria-hidden="true"><bdi>{label}</bdi>{first.half ? '½' : ''}{cell.leaves.length > 1 ? '+' : ''}{first.waiting ? <Clock size={10} /> : null}</span>
        </td>
      );
    }
    if (cell.kind === 'holiday') {
      const name = t('lcal.cell.holiday.name', { name: person, date: date(iso) });
      return <td key={iso} {...common} aria-label={name} title={holidayOf(iso) ?? name}><span aria-hidden="true">{t('lcal.cell.holiday')}</span></td>;
    }
    if (cell.kind === 'off') {
      const name = t('lcal.cell.off.name', { name: person, date: date(iso) });
      return <td key={iso} {...common} aria-label={name} title={name}><span aria-hidden="true">{t('lcal.cell.off')}</span></td>;
    }
    return <td key={iso} {...common} aria-label={t('lcal.cell.none', { name: person, date: date(iso) })} />;
  };

  return (
    <div className="erp-lcal-scroll">
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-to-interactive-role -- APG data grid on a table, as in the engine's grid */}
      <table ref={grid} className="erp-lcal-grid" role="grid" aria-label={t('lcal.grid', { month: monthName })} aria-rowcount={rows + 1} aria-colcount={days.length + 1} onKeyDown={onKey}>
        <thead>
          <tr>
            <th scope="col" className="erp-lcal-corner">{t('lcal.col.person')}</th>
            {days.map((iso) => {
              const holiday = holidayOf(iso);
              const weekend = [0, 6].includes(weekdayOf(iso));
              const today = [...todays.values()].includes(iso);
              const label = `${date(iso)}${holiday ? `, ${t('lcal.holiday.header', { holiday })}` : ''}${today ? `, ${t('lcal.today')}` : ''}`;
              return (
                <th key={iso} scope="col" role="columnheader" aria-label={label} title={label} {...(holiday ? { 'data-holiday': true } : {})} {...(weekend ? { 'data-weekend': true } : {})} {...(today ? { 'data-today': true } : {})}>
                  <span className="erp-lcal-weekday" aria-hidden="true">{weekday(iso)}</span>
                  <span aria-hidden="true">{num(Number(iso.slice(8)))}</span>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {coverRows.map((id, r) => (
            <tr key={`cover-${id}`}>
              <th scope="row" role="rowheader">{coverRows.length > 1 ? t('lcal.coverage.company', { company: companyName(id) }) : t('lcal.coverage')}</th>
              {days.map((iso, c) => coverCell(id, r, c, iso))}
            </tr>
          ))}
          {data.people.map((p, i) => {
            const r = coverRows.length + i;
            return (
              <tr key={p.employmentId}>
                <th scope="row" role="rowheader"><bdi>{`${p.firstName ?? ''} ${p.lastName ?? ''}`.trim()}</bdi><span className="erp-lcal-person-no"><bdi>{p.employeeNo}</bdi></span></th>
                {days.map((iso, c) => personCell(p, r, c, iso))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function Legend({ data, types, typeName }: { data: LeaveCalendar; types: { id: string; code: string; nameI18n: Record<string, string>; colorToken?: string | null }[]; typeName: (type: CalendarType) => string }) {
  const { t } = useText();
  const sample = t('lcal.legend.sample');
  const shown = new Map<string, CalendarType>();
  for (const x of types) shown.set(x.id, { id: x.id, code: x.code, nameI18n: x.nameI18n, colorToken: x.colorToken ?? null, unit: '' });
  for (const x of data.types) shown.set(x.id, x);
  return (
    <section className="erp-stack" aria-label={t('lcal.legend')}>
      <h2>{t('lcal.legend')}</h2>
      <ul className="erp-lcal-legend" aria-label={t('lcal.legend.types')}>
        {[...shown.values()].map((x) => (
          <li key={x.id}><span className="erp-lcal-swatch" data-colour={colourOf(x.colorToken)}><bdi>{x.code}</bdi></span><span><bdi>{typeName(x)}</bdi></span></li>
        ))}
      </ul>
      <ul className="erp-lcal-legend" aria-label={t('lcal.legend.marks')}>
        <li><span className="erp-lcal-swatch" data-colour="neutral">{sample}</span>{t('lcal.legend.approved')}</li>
        <li><span className="erp-lcal-swatch" data-colour="neutral" data-waiting="true">{sample}<Clock size={10} aria-hidden="true" /></span>{t('lcal.legend.waiting')}</li>
        <li><span className="erp-lcal-swatch" data-colour="neutral">{sample}{'½'}</span>{t('lcal.legend.half')}</li>
        <li><span className="erp-lcal-swatch" data-colour="neutral" data-sandwich="true">{sample}</span>{t('lcal.legend.sandwich')}</li>
        <li><span className="erp-lcal-swatch erp-lcal-cell" data-kind="off">{t('lcal.cell.off')}</span>{t('lcal.legend.off')}</li>
        <li><span className="erp-lcal-swatch erp-lcal-cell" data-kind="holiday">{t('lcal.cell.holiday')}</span>{t('lcal.legend.holiday')}</li>
        <li><TriangleAlert size={14} aria-hidden="true" />{t('lcal.legend.warn')}</li>
      </ul>
    </section>
  );
}
