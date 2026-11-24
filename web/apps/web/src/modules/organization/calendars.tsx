// Holiday calendars (slice S1.14a Build item 5; FR-KRN-LOC-06, FR-ADM-LOC-04, FR-HRM-ATT-15): the calendars list and form come
// from the engine; a calendar's record adds its holidays by year (date, names in two languages, kind, a tentative mark for
// moon-sighting dates and the action that confirms them), the copy of a year forward and where the calendar is used (companies
// and locations). Calendars of a country pack are read-only here (S1.23 installs them). The server decides everything.
import './organization.css';
import { useMemo, useState } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, Copy, Pencil, Plus } from 'lucide-react';
import { DataGrid, EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FieldEditorProps, type FormValues, type GridColumn, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { companies as companyApi, locations as locationApi } from '@erp/generated/api/org';
import { calendars as calendarApi, holidays as holidayApi, type Holiday } from '@erp/generated/api/ref';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, Link, SelectField, StatusChip, SwitchField, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import metadataJson from '@erp/generated/metadata/ref.calendars.json';
import schemaJson from '@erp/generated/validation/ref.calendars.json';
import { parseDate } from '@internationalized/date';
import { ApiProblem, sendText } from '../../api/client';
import { org } from '../../api/org';
import { systemClock } from '../../session/clock';
import { useShell } from '../../shell/context';
import { useProblemText } from '../admin/users-parts';
import { useDates } from './org-parts';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const CALENDARS_PATH = '/organization/holiday-calendars';
const KINDS = ['public', 'religious', 'optional', 'restricted'] as const;
const holidaysKey = (calendarId: string) => ['org', 'holidays', calendarId] as const;

export default function CalendarsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<CalendarList />} />
        <Route path="new" element={<CalendarRecord />} />
        <Route path=":id" element={<CalendarRecord />} />
      </Routes>
    </EngineProvider>
  );
}

function useTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function CalendarList() {
  const navigate = useNavigate();
  const { can, writable } = useShell();
  const { t } = useText();
  const title = useTitle();
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'countryCode', 'source', 'isActive']} canCreate={writable && can('ref.calendars.create')}
      canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')} description={t('calendars.description')}
      onCreate={() => { void navigate(`${CALENDARS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${CALENDARS_PATH}/${row.id}`); }} rowLabel={(row) => title(row)} />
  );
}

/** A calendar made here always belongs to the customer; pack calendars come with a country pack (S1.23). */
const sourceEditor = ({ label, value }: FieldEditorProps) => {
  return <SourceText label={label} value={value} />;
};

function SourceText({ label, value }: { label: string; value: unknown }) {
  const { t } = useText();
  return (
    <div className="erp-field erp-field-readonly" role="group" aria-label={label}>
      <span className="erp-label">{label}</span>
      <span>{t(value === 'pack' ? 'calendars.source.pack' : 'calendars.source.tenant')}</span>
    </div>
  );
}

function CalendarRecord() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable } = useShell();
  const title = useTitle();
  const known = useQuery({ queryKey: ['org', 'calendar', id], queryFn: () => calendarApi.get(id ?? ''), enabled: !!id });
  const isPack = known.data?.source === 'pack';
  return (
    <>
      <FormPage metadata={metadata} schema={schema} id={id ?? null} fields={['code', 'nameI18n', 'countryCode', 'source', 'isActive']}
        defaults={{ source: 'tenant', isActive: true, nameI18n: {}, countryCode: 'PK' }} editors={{ source: sourceEditor }}
        canCreate={writable && can('ref.calendars.create')} canUpdate={writable && can('ref.calendars.update') && !isPack} canDelete={false} canHistory={can('aud.history.view')} recordTitle={title}
        help={{ countryCode: t('calendars.help.country') }}
        onSaved={(row, created, how) => {
          toast.success(created ? t('calendars.created', { calendar: title(row) }) : t('calendars.saved', { calendar: title(row) }));
          if (how === 'form' && created) void navigate(`${CALENDARS_PATH}/${row.id}`, { replace: true });
        }}
        onDeleted={() => { void navigate(CALENDARS_PATH); }} onCancel={() => { void navigate(CALENDARS_PATH); }} />
      {id && (
        <div className="erp-page erp-calendar-parts">
          {isPack && <Banner tone="info" title={t('calendars.pack')}>{t('calendars.pack.body')}</Banner>}
          <HolidaysPanel calendarId={id} editable={writable && !isPack && can('ref.holidays.update')} creatable={writable && !isPack && can('ref.holidays.create')} />
          <UsedBy calendarId={id} />
        </div>
      )}
    </>
  );
}

// ---------------------------------------------------------------------------------------------------- holidays

function HolidaysPanel({ calendarId, editable, creatable }: { calendarId: string; editable: boolean; creatable: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const date = useDates();
  const { locale } = useLocaleSettings();
  const thisYear = new Date(systemClock.now()).getUTCFullYear();
  const [year, setYear] = useState(thisYear);
  const [dialog, setDialog] = useState<Holiday | 'new' | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const list = useQuery({ queryKey: holidaysKey(calendarId), queryFn: () => holidayApi.list({ calendarId, limit: 500, sort: 'holidayDate' }) });
  const all = useMemo(() => list.data?.items ?? [], [list.data]);
  const years = useMemo(() => [...new Set([thisYear, thisYear + 1, ...all.map((h) => Number(h.holidayDate.slice(0, 4)))])].sort((a, b) => a - b), [all, thisYear]);
  const rows = useMemo(() => all.filter((h) => h.holidayDate.startsWith(String(year))), [all, year]);
  const copy = useMutation({
    mutationFn: () => org.copyYear(calendarId, year, year + 1),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: holidaysKey(calendarId) });
      toast.success(t('calendars.copied', { created: result.created, skipped: result.skipped, from: year, to: year + 1 }));
      setYear(year + 1);
    },
    onError: (e) => { setProblem(problemText(e)); },
  });
  const columns = useMemo<GridColumn<Holiday>[]>(() => [
    { id: 'date', header: t('calendars.holiday.date'), width: 190, cell: (h) => <bdi>{date(h.holidayDate)}</bdi> },
    { id: 'name', header: t('calendars.holiday.name'), width: 260, cell: (h) => <bdi>{h.nameI18n[locale] ?? h.nameI18n.en ?? ''}</bdi> },
    { id: 'kind', header: t('calendars.holiday.kind'), width: 140, cell: (h) => t(`calendars.kind.${h.kind}`) },
    { id: 'state', header: t('calendars.holiday.state'), width: 160, cell: (h) => <StatusChip status={h.isTentative ? 'pending' : 'active'} label={t(h.isTentative ? 'calendars.tentative' : 'calendars.confirmed')} /> },
    { id: 'actions', header: t('org.col.actions'), width: 260, cell: (h) => editable ? (
      <span className="erp-row">
        {h.isTentative && <Button variant="quiet" icon={<Check size={16} aria-hidden="true" />} onPress={() => setDialog(h)}>{t('calendars.confirm')}</Button>}
        <Button variant="quiet" icon={<Pencil size={16} aria-hidden="true" />} onPress={() => setDialog(h)}>{t('org.edit')}</Button>
      </span>) : null },
  ], [t, date, locale, editable]);
  return (
    <section className="erp-section" aria-labelledby="cal-holidays">
      <div className="erp-row erp-section-head">
        <h2 id="cal-holidays" className="erp-h2">{t('calendars.holidays')}</h2>
        <SelectField label={t('calendars.year')} options={years.map((y): Option => ({ id: String(y), label: String(y) }))} value={String(year)} onChange={(v) => setYear(Number(v))} />
        {creatable && <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => setDialog('new')}>{t('calendars.add')}</Button>}
        {creatable && <Button variant="secondary" icon={<Copy size={16} aria-hidden="true" />} isPending={copy.isPending} isDisabled={rows.length === 0} onPress={() => { setProblem(null); copy.mutate(); }}>{t('calendars.copy', { to: year + 1 })}</Button>}
      </div>
      <p className="erp-help">{t('calendars.help.tentative')}</p>
      {problem && <Banner tone="danger" title={problem} onDismiss={() => setProblem(null)}>{null}</Banner>}
      <DataGrid label={t('calendars.holidays')} columns={columns} rows={rows} rowKey={(h) => h.id} rowLabel={(h) => `${h.holidayDate} ${h.nameI18n.en ?? ''}`}
        status={list.isPending ? 'loading' : list.isError ? 'error' : 'ready'}
        error={<ErrorPanel message={list.error instanceof ApiProblem && list.error.status === 403 ? t('org.denied') : t('org.loadError')} />}
        empty={<EmptyState title={t('calendars.empty', { year })}>{t('calendars.empty.body')}</EmptyState>} />
      {dialog !== null && <HolidayDialog calendarId={calendarId} holiday={dialog === 'new' ? null : dialog} year={year} onClose={() => setDialog(null)} />}
    </section>
  );
}

function HolidayDialog({ calendarId, holiday, year, onClose }: { calendarId: string; holiday: Holiday | null; year: number; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const creating = holiday === null;
  const [day, setDay] = useState(holiday?.holidayDate ?? `${year}-01-01`);
  const [en, setEn] = useState(holiday?.nameI18n.en ?? '');
  const [ur, setUr] = useState(holiday?.nameI18n.ur ?? '');
  const [kind, setKind] = useState<Holiday['kind']>(holiday?.kind ?? 'public');
  const [tentative, setTentative] = useState(holiday?.isTentative ?? false);
  const [error, setError] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: () => {
      const body = { calendarId, holidayDate: day, nameI18n: { ...(en.trim() ? { en: en.trim() } : {}), ...(ur.trim() ? { ur: ur.trim() } : {}) }, kind, isTentative: tentative };
      return creating ? holidayApi.create(body) : holidayApi.update(holiday.id, { ...body, rowVersion: holiday.rowVersion });
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: holidaysKey(calendarId) });
      toast.success(t(creating ? 'calendars.added' : 'calendars.holidaySaved'));
      onClose();
    },
    onError: (e) => { setError(e instanceof ApiProblem && e.key === 'errors.data.duplicate' ? t('calendars.duplicate') : problemText(e)); },
  });
  const safe = (() => { try { return parseDate(day); } catch { return null; } })();
  return (
    <Dialog title={t(creating ? 'calendars.addTitle' : holiday.isTentative ? 'calendars.confirmTitle' : 'calendars.editTitle')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={save.isPending} isDisabled={!safe || (!en.trim() && !ur.trim())} onPress={() => { setError(null); save.mutate(); }}>{t(!creating && holiday.isTentative && tentative === false ? 'calendars.confirm' : 'org.save')}</Button>
        </>
      )}>
      <div className="erp-stack">
        <DateField label={t('calendars.holiday.date')} isRequired value={safe} onChange={(d) => setDay(d ? d.toString() : '')} />
        <TextField label={t('calendars.holiday.nameEn')} isRequired value={en} onChange={setEn} maxLength={120} />
        <div lang="ur" dir="rtl"><TextField label={t('calendars.holiday.nameUr')} value={ur} onChange={setUr} maxLength={120} /></div>
        <SelectField label={t('calendars.holiday.kind')} options={KINDS.map((k): Option => ({ id: k, label: t(`calendars.kind.${k}`) }))} value={kind} onChange={(k) => setKind(k as Holiday['kind'])} />
        <SwitchField label={t('calendars.holiday.tentativeSwitch')} isSelected={tentative} onChange={setTentative} />
        {error && <Banner tone="danger" title={error}>{null}</Banner>}
      </div>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------------------------------- where it is used

function UsedBy({ calendarId }: { calendarId: string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can } = useShell();
  const companies = useQuery({ queryKey: ['org', 'calendar-companies', calendarId], queryFn: () => companyApi.list({ holidayCalendarId: calendarId, limit: 100 }), enabled: can('org.companies.read') });
  const locations = useQuery({ queryKey: ['org', 'calendar-locations', calendarId], queryFn: () => locationApi.list({ holidayCalendarId: calendarId, limit: 100 }), enabled: can('org.locations.read') });
  const name = (n: Record<string, string>, code: string) => n[locale] ?? n.en ?? code;
  const none = (companies.data?.items.length ?? 0) === 0 && (locations.data?.items.length ?? 0) === 0;
  return (
    <section className="erp-section" aria-labelledby="cal-used">
      <h2 id="cal-used" className="erp-h2">{t('calendars.usedBy')}</h2>
      {none ? <p>{t('calendars.usedBy.none')}</p> : (
        <ul className="erp-plain-list">
          {(companies.data?.items ?? []).map((c) => <li key={c.id}>{t('calendars.usedBy.company')}: <Link href={`/organization/companies/${c.id}`}><bdi>{name(c.nameI18n, c.code)}</bdi></Link></li>)}
          {(locations.data?.items ?? []).map((l) => <li key={l.id}>{t('calendars.usedBy.location')}: <Link href={`/organization/locations/${l.id}`}><bdi>{name(l.nameI18n, l.code)}</bdi></Link></li>)}
        </ul>
      )}
    </section>
  );
}
