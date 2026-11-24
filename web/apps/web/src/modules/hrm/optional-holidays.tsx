// Optional holidays and the shift of a day (slice S1.28b Build items 7 and 8; FR-HRM-ATT-15, FR-HRM-ATT-16). HR picks an employee and sees the
// holidays of their calendar with Hijri dates; the optional ones can be chosen up to the company's yearly limit and until its cut-off. The
// second tab answers what the employee works on a day and why (roster, weekly off, holiday, Ramzan or seasonal timing). Employee self-service
// for choosing holidays belongs to S1.26 and calls the same service.
import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Banner, Button, CheckboxField, DateField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, Tabs, TextField, useToast } from '@erp/ui';
import { formatDate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, requestJson } from '../../api/client';
import { policiesApi } from '../../api/policies';
import { holidayApi, type ChoiceRow, type HijriDate } from '../../api/rosters';
import { companyName, useShell } from '../../shell/context';
import './rosters-messages';

interface EffectiveAnswer {
  dayType: string; source: string; shiftCode: string | null; start: string | null; end: string | null; breakMinutes: number | null; baseStart: string | null; baseEnd: string | null;
  reasons: { key: string; values: Record<string, string> | null }[];
}

export default function OptionalHolidaysScreen() {
  const { t } = useText();
  const { can, me, activeCompany } = useShell();
  const { locale } = useLocaleSettings();
  useDocumentTitle(t('ros.optional.title'));
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.companies[0]?.id ?? '');
  const [filter, setFilter] = useState('');
  const [employee, setEmployee] = useState('');
  const [tab, setTab] = useState('holidays');
  const people = useQuery({ queryKey: ['hrm', 'policy-employments', company], queryFn: () => policiesApi.employments(company), enabled: !!company && can('hrm.holidays.read'), staleTime: 60_000 });
  const found = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    return (people.data ?? []).filter((e) => !needle || e.employeeNo.toLowerCase().includes(needle)).slice(0, 50).map((e) => ({ id: e.id, label: e.employeeNo }));
  }, [people.data, filter]);
  const chosen = (people.data ?? []).find((e) => e.id === employee);
  if (!can('hrm.holidays.read')) return <div className="erp-page"><PageHeader title={t('ros.optional.title')} /><EmptyState title={t('ros.optional.denied')} /></div>;
  const companies = me.companies.map((c) => ({ id: c.id, label: companyName(c, locale) }));
  return (
    <div className="erp-page">
      <PageHeader title={t('ros.optional.title')} description={t('ros.optional.description')} />
      <div className="erp-stack">
        {companies.length > 1 && <SelectField label={t('ros.optional.company')} options={companies} value={company} onChange={(v) => { setCompany(v); setEmployee(''); setFilter(''); }} />}
        <TextField label={t('ros.optional.employee')} help={t('ros.optional.employee.help')} value={filter} onChange={(v) => { setFilter(v); setEmployee(''); }} isCode />
        <SelectField label={t('ros.field.employee')} options={found} value={employee} onChange={setEmployee} />
        {chosen && (
          <Tabs label={t('ros.optional.title')} selectedKey={tab} onSelectionChange={setTab} tabs={[
            { id: 'holidays', title: t('ros.optional.title'), content: <Holidays employmentId={chosen.id} /> },
            { id: 'effective', title: t('ros.effective.title'), content: <Effective employmentId={chosen.id} /> },
          ]} />
        )}
      </div>
    </div>
  );
}

function useHijri(): (h: HijriDate | null) => string {
  const { t } = useText();
  return (h) => (h ? t('ros.hijri', { day: String(h.day), month: t(`ros.hijri.month.${h.month}` as MessageKey), year: String(h.year) }) : '');
}

function Holidays({ employmentId }: { employmentId: string }) {
  const { t, server } = useText();
  const { can, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const hijri = useHijri();
  const toast = useToast();
  const queryClient = useQueryClient();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const [year, setYear] = useState(String(new Date().getFullYear()));
  const [draft, setDraft] = useState<Set<string> | null>(null);
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const key = ['hrm', 'employment-holidays', employmentId, year];
  const query = useQuery({ queryKey: key, queryFn: ({ signal }) => holidayApi.forEmployment(employmentId, Number(year), signal), enabled: /^\d{4}$/.test(year) });
  const rows = query.data?.holidays ?? [];
  const selected = draft ?? new Set(rows.filter((r) => r.chosen).map((r) => r.holiday.id));
  const save = useMutation({
    mutationFn: () => holidayApi.choose(employmentId, Number(year), [...selected]),
    onSuccess: async () => { setProblem(null); setDraft(null); toast.success(t('ros.optional.saved')); await queryClient.invalidateQueries({ queryKey: key }); },
    onError: (e) => setProblem(e instanceof ApiProblem ? e : null),
  });
  const day = (v: string) => formatDate(`${v}T12:00:00Z`, lang, timeZone, numbers);
  const toggle = (id: string, on: boolean) => { const next = new Set(selected); if (on) next.add(id); else next.delete(id); setDraft(next); };
  const refusal = problem ? server(problem.fieldErrors['holidayIds']?.[0] ?? problem.errors[0] ?? problem.key) ?? t('ros.error.generic') : null;
  return (
    <div className="erp-stack">
      <TextField label={t('ros.optional.year')} value={year} onChange={(v) => { setYear(v); setDraft(null); }} inputMode="numeric" isCode />
      {query.isPending && <Skeleton />}
      {query.isError && <ErrorPanel title={t('ros.loadError')} message={query.error instanceof ApiProblem ? server(query.error.key) ?? t('ros.error.generic') : t('ros.error.generic')} />}
      {query.data && (
        <>
          <p>{query.data.optionalLimit === 0 ? t('ros.optional.limitZero') : t('ros.optional.summary', { chosen: selected.size, limit: query.data.optionalLimit, days: query.data.cutoffDays })}</p>
          {refusal && <Banner tone="danger" title={refusal}>{null}</Banner>}
          {rows.length === 0 ? <EmptyState title={t('ros.optional.empty')}>{t('ros.optional.empty.body')}</EmptyState> : (
            <table className="erp-pos-table">
              <thead><tr><th scope="col">{t('ros.optional.col.date')}</th><th scope="col">{t('ros.optional.col.hijri')}</th><th scope="col">{t('ros.optional.col.name')}</th><th scope="col">{t('ros.optional.col.kind')}</th><th scope="col">{t('ros.optional.chosen')}</th></tr></thead>
              <tbody>{rows.map((r: ChoiceRow) => (
                <tr key={r.holiday.id}>
                  <td><bdi>{day(r.holiday.date)}</bdi></td>
                  <td><bdi>{hijri(r.holiday.hijri)}</bdi></td>
                  <td><bdi>{r.holiday.name[lang] ?? r.holiday.name.en ?? ''}</bdi></td>
                  <td>{t(`ros.kind.${r.holiday.kind}` as MessageKey)}</td>
                  <td>{r.holiday.isOptional
                    ? <CheckboxField label={t('ros.optional.pick', { holiday: r.holiday.name[lang] ?? r.holiday.name.en ?? '' })} isSelected={selected.has(r.holiday.id)} isDisabled={!writable || !can('hrm.optional_holidays.choose') || (!r.open && selected.has(r.holiday.id) === r.chosen)} onChange={(on) => toggle(r.holiday.id, on)} />
                    : null}</td>
                </tr>))}</tbody>
            </table>
          )}
          <div><Button variant="primary" isPending={save.isPending} isDisabled={!writable || !can('hrm.optional_holidays.choose') || draft === null} onPress={() => save.mutate()}>{t('ros.save')}</Button></div>
        </>
      )}
    </div>
  );
}

function Effective({ employmentId }: { employmentId: string }) {
  const { t, server } = useText();
  const { can } = useShell();
  const [date, setDate] = useState<CalendarDate | null>(null);
  const day = date?.toString() ?? '';
  const answer = useQuery({
    queryKey: ['hrm', 'effective-shift', employmentId, day],
    queryFn: ({ signal }) => requestJson<EffectiveAnswer>('GET', `/api/hrm/shifts/effective?employment=${employmentId}&date=${day}`, { signal }),
    enabled: day !== '' && can('hrm.shift_effective.read'),
  });
  const a = answer.data;
  return (
    <div className="erp-stack">
      <p>{t('ros.effective.description')}</p>
      <DateField label={t('ros.effective.date')} value={date} onChange={(v) => setDate(v ? parseDate(v.toString()) : null)} />
      {!date && <p>{t('ros.effective.none')}</p>}
      {answer.isPending && !!date && <Skeleton />}
      {answer.isError && <ErrorPanel title={t('ros.loadError')} message={answer.error instanceof ApiProblem ? server(answer.error.key) ?? t('ros.error.generic') : t('ros.error.generic')} />}
      {a && (
        <dl className="erp-defs">
          <dt>{t('ros.effective.day')}</dt><dd>{t(`ros.dayType.${a.dayType === 'none' ? 'working' : a.dayType}` as MessageKey)}{a.dayType === 'none' ? ' —' : ''}</dd>
          <dt>{t('ros.effective.source')}</dt><dd>{t(`ros.source.${a.source}` as MessageKey)}</dd>
          {a.shiftCode && <><dt>{t('ros.effective.shift')}</dt><dd><bdi>{a.shiftCode}</bdi> <bdi>{t('ros.effective.times', { start: a.start?.slice(0, 5) ?? '', end: a.end?.slice(0, 5) ?? '' })}</bdi>{a.breakMinutes ? <> · {t('ros.effective.break', { minutes: a.breakMinutes })}</> : null}</dd></>}
          <dt>{t('ros.effective.reasons')}</dt>
          <dd><ul className="erp-list">{a.reasons.map((r) => <li key={r.key}>{server(r.key, r.values ?? undefined) ?? r.key}</li>)}</ul></dd>
        </dl>
      )}
    </div>
  );
}
