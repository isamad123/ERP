// Rosters (slice S1.28b Build item 9; FR-HRM-ATT-07, FR-HRM-ATT-15): the list of rosters, a new roster, and the roster grid by employee and day
// with the check messages, assigning a shift, filling days, copying forward and publishing. Holiday days of the company's calendar show in the
// header with their Hijri date. The server decides every change; the grid shows its answer. A cell opens a dialog (keyboard first); "Fill days"
// sets a stretch of days for one person, which is what dragging across a row would do.
import { useMemo, useState } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EngineProvider, FormPage, ListPage, useDocumentTitle, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { Banner, Button, CheckboxField, DateField, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, TextField, useToast } from '@erp/ui';
import { formatDate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { rosterLines, rosters, shifts as shiftApi, type Roster, type RosterLine } from '@erp/generated/api/hrm';
import metadataJson from '@erp/generated/metadata/hrm.rosters.json';
import schemaJson from '@erp/generated/validation/hrm.rosters.json';
import { ApiProblem, sendText } from '../../api/client';
import { policiesApi } from '../../api/policies';
import { holidayApi, rosterApi, type DayChange, type HijriDate, type HolidayRow } from '../../api/rosters';
import { companyName, useShell } from '../../shell/context';
import './rosters-messages';
import './rosters.css';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const ROSTERS_PATH = '/time/rosters';

const DAY_TYPES = ['working', 'weekly_off', 'holiday'] as const;

export default function RostersScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<RosterList />} />
        <Route path="new" element={<RosterForm />} />
        <Route path=":id" element={<RosterGrid />} />
      </Routes>
    </EngineProvider>
  );
}

function useRefusal(): (error: unknown) => string {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return t('ros.error.network');
      const key = error.errors[0] ?? Object.values(error.fieldErrors)[0]?.[0] ?? error.key;
      return server(key) ?? t('ros.error.generic');
    }
    return t('ros.error.generic');
  };
}

function useHijri(): (h: HijriDate | null) => string {
  const { t } = useText();
  return (h) => (h ? t('ros.hijri', { day: String(h.day), month: t(`ros.hijri.month.${h.month}` as MessageKey), year: String(h.year) }) : '');
}

const days = (from: string, to: string): string[] => {
  const out: string[] = [];
  const end = parseDate(to);
  for (let d = parseDate(from); d.compare(end) <= 0 && out.length < 400; d = d.add({ days: 1 })) out.push(d.toString());
  return out;
};

// ---------------------------------------------------------------------------------------------------- list and new roster

function RosterList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany, me } = useShell();
  const { t } = useText();
  const { locale } = useLocaleSettings();
  useDocumentTitle(t('ros.title'));
  const options = me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }));
  return (
    <ListPage metadata={metadata} columns={['name', 'periodStart', 'periodEnd', 'status', 'companyId']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('hrm.rosters.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')}
      description={t('ros.description')}
      onCreate={() => { void navigate(`${ROSTERS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${ROSTERS_PATH}/${row.id}`); }}
      rowLabel={(row) => String(row.name ?? '')} />
  );
}

function RosterForm() {
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany, me } = useShell();
  const { locale } = useLocaleSettings();
  const creatable = me.companies.filter((c) => can('hrm.rosters.create', c.id)).map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }));
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  return (
    <FormPage metadata={metadata} schema={schema} id={null} fields={['companyId', 'name', 'orgUnitId', 'periodStart', 'periodEnd']}
      defaults={{ companyId: inHand?.id ?? creatable[0]?.id ?? null } as FormValues} companies={creatable}
      canCreate={writable && creatable.length > 0} canUpdate={false} canDelete={false} canHistory={false}
      recordTitle={(row) => String(row.name ?? t('ros.form.title'))}
      onSaved={(row) => { toast.success(String(row.name ?? '')); void navigate(`${ROSTERS_PATH}/${row.id}`, { replace: true }); }}
      onDeleted={() => { void navigate(ROSTERS_PATH); }} onCancel={() => { void navigate(ROSTERS_PATH); }} />
  );
}

// ---------------------------------------------------------------------------------------------------- the grid

type Cell = RosterLine | undefined;

function RosterGrid() {
  const { id = '' } = useParams();
  const { t } = useText();
  const navigate = useNavigate();
  const { can, writable } = useShell();
  const refusal = useRefusal();
  const queryClient = useQueryClient();
  const toast = useToast();
  const [dialog, setDialog] = useState<'assign' | 'fill' | 'copy' | { employmentId: string; date: string } | null>(null);
  const roster = useQuery({ queryKey: ['hrm', 'roster', id], queryFn: () => rosters.get(id) });
  const lines = useQuery({
    queryKey: ['hrm', 'roster-lines', id],
    queryFn: async () => {
      const all: RosterLine[] = [];
      let after: string | undefined;
      for (let page = 0; page < 60; page++) {
        const answer = await rosterLines.list({ rosterId: id, limit: 200, ...(after ? { after } : {}) });
        all.push(...answer.items);
        if (!answer.next) break;
        after = answer.next;
      }
      return all;
    },
  });
  const r = roster.data;
  const companyId = r?.companyId ?? '';
  const people = useQuery({ queryKey: ['hrm', 'policy-employments', companyId], queryFn: () => policiesApi.employments(companyId), enabled: !!companyId, staleTime: 60_000 });
  const shiftList = useQuery({ queryKey: ['hrm', 'shifts-open', companyId], queryFn: () => shiftApi.list({ companyId, limit: 200 }), enabled: !!companyId, staleTime: 60_000 });
  const years = r ? [...new Set([Number(r.periodStart.slice(0, 4)), Number(r.periodEnd.slice(0, 4))])] : [];
  const holidays = useQuery({
    queryKey: ['hrm', 'roster-holidays', companyId, years.join(',')],
    queryFn: async () => (await Promise.all(years.map((y) => holidayApi.list(companyId, y)))).flatMap((h) => h.holidays),
    enabled: !!r && can('hrm.holidays.read'),
    retry: false,
  });
  const checks = useQuery({ queryKey: ['hrm', 'roster-checks', id, lines.dataUpdatedAt], queryFn: ({ signal }) => rosterApi.checks(id, signal), enabled: !!r && can('hrm.rosters.read') });
  const refresh = async () => {
    await Promise.all([queryClient.invalidateQueries({ queryKey: ['hrm', 'roster', id] }), queryClient.invalidateQueries({ queryKey: ['hrm', 'roster-lines', id] })]);
  };
  const publish = useMutation({
    mutationFn: () => (r ? rosterApi.publish(id, r.rowVersion) : Promise.reject(new Error('roster not loaded'))),
    onSuccess: async (a) => { toast.success(t('ros.publish.done', { people: a.people })); await refresh(); },
    onError: (e) => toast.error(refusal(e)),
  });
  useDocumentTitle(r?.name ?? t('ros.title'));
  if (roster.isPending || lines.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (roster.isError || !r) return <div className="erp-page"><ErrorPanel level={1} title={t('ros.loadError')} message={refusal(roster.error)} action={<Button variant="secondary" onPress={() => { void navigate(ROSTERS_PATH); }}>{t('ros.back')}</Button>} /></div>;
  const status = r.status;
  const errors = (checks.data?.messages ?? []).filter((m) => m.severity === 'error').length;
  const editable = writable && (status === 'draft' ? can('hrm.rosters.update') : status === 'published' && can('hrm.rosters.amend'));
  return (
    <div className="erp-page">
      <PageHeader title={r.name} description={<GridSubtitle roster={r} />} />
      <div className="erp-stack">
        <div className="erp-row erp-ros-controls">
          <Button variant="quiet" onPress={() => { void navigate(ROSTERS_PATH); }}>{t('ros.back')}</Button>
          <Button variant="secondary" isDisabled={!writable || status !== 'draft' || !can('hrm.rosters.update')} onPress={() => setDialog('assign')}>{t('ros.action.assign')}</Button>
          <Button variant="secondary" isDisabled={!editable} onPress={() => setDialog('fill')}>{t('ros.action.fill')}</Button>
          <Button variant="secondary" isDisabled={!writable || !can('hrm.rosters.create')} onPress={() => setDialog('copy')}>{t('ros.action.copy')}</Button>
          <Button variant="primary" isDisabled={!writable || status !== 'draft' || !can('hrm.rosters.publish') || errors > 0} isPending={publish.isPending} onPress={() => publish.mutate()}>{t('ros.action.publish')}</Button>
        </div>
        {errors > 0 && status === 'draft' && <Banner tone="warning" title={t('ros.publish.blocked')}>{null}</Banner>}
        <Grid roster={r} lines={lines.data ?? []} people={people.data ?? []} shifts={shiftList.data?.items ?? []} holidays={holidays.data ?? []} editable={editable}
          onOpen={(employmentId, date) => setDialog({ employmentId, date })} />
        <section className="erp-section" aria-labelledby="ros-checks">
          <div className="erp-row">
            <h2 id="ros-checks" className="erp-h2">{t('ros.checks.title')}</h2>
            <Button variant="quiet" onPress={() => { void checks.refetch(); }}>{t('ros.action.checks')}</Button>
          </div>
          <Checks result={checks.data} people={people.data ?? []} pending={checks.isPending} />
        </section>
      </div>
      {dialog === 'assign' && <AssignDialog roster={r} shifts={shiftList.data?.items ?? []} onClose={() => setDialog(null)} onDone={refresh} />}
      {dialog === 'fill' && <FillDialog roster={r} people={people.data ?? []} shifts={shiftList.data?.items ?? []} lines={lines.data ?? []} onClose={() => setDialog(null)} onDone={refresh} />}
      {dialog === 'copy' && <CopyDialog roster={r} onClose={() => setDialog(null)} onCopied={(copy) => { void navigate(`${ROSTERS_PATH}/${copy}`); }} />}
      {typeof dialog === 'object' && dialog !== null && <FillDialog roster={r} people={people.data ?? []} shifts={shiftList.data?.items ?? []} lines={lines.data ?? []} single={dialog} onClose={() => setDialog(null)} onDone={refresh} />}
    </div>
  );
}

function GridSubtitle({ roster }: { roster: Roster }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const day = (v: string) => formatDate(`${v}T12:00:00Z`, lang, timeZone, numbers);
  return <><StatusChip status={roster.status === 'published' ? 'active' : 'pending'} label={t(`ros.status.${roster.status}` as MessageKey)} /> <bdi>{t('ros.period', { from: day(roster.periodStart), to: day(roster.periodEnd) })}</bdi></>;
}

function Grid({ roster, lines, people, shifts, holidays, editable, onOpen }: {
  roster: Roster; lines: RosterLine[]; people: { id: string; employeeNo: string }[]; shifts: { id: string; code: string }[]; holidays: HolidayRow[]; editable: boolean;
  onOpen: (employmentId: string, date: string) => void;
}) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const hijri = useHijri();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const dates = useMemo(() => days(roster.periodStart, roster.periodEnd), [roster.periodStart, roster.periodEnd]);
  const byKey = useMemo(() => new Map(lines.map((l) => [`${l.employmentId}|${l.workDate}`, l] as const)), [lines]);
  const employees = useMemo(() => {
    const names = new Map(people.map((p) => [p.id, p.employeeNo] as const));
    return [...new Set(lines.map((l) => l.employmentId))].map((id) => ({ id, no: names.get(id) ?? id.slice(0, 8) })).sort((a, b) => a.no.localeCompare(b.no));
  }, [lines, people]);
  const code = (id: string | null) => shifts.find((s) => s.id === id)?.code ?? '';
  const holiday = useMemo(() => new Map(holidays.filter((h) => h.kind !== 'optional').map((h) => [h.date, h] as const)), [holidays]);
  if (employees.length === 0) return <EmptyState title={t('ros.grid.empty')}>{t('ros.grid.empty.body')}</EmptyState>;
  const dayLabel = (d: string) => formatDate(`${d}T12:00:00Z`, lang, 'UTC', numbers);
  const what = (c: Cell) => (!c ? t('ros.grid.off') : c.dayType === 'working' ? code(c.shiftId) : t(c.dayType === 'holiday' ? 'ros.grid.holiday' : 'ros.grid.off'));
  return (
    // A scrolling region must be reachable by keyboard even when every cell is disabled (WCAG 2.1.1).
    // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
    <div className="erp-ros-scroll" role="region" aria-label={t('ros.grid.label')} tabIndex={0}>
      <table className="erp-ros-grid">
        <thead>
          <tr>
            <th scope="col">{t('ros.grid.employee')}</th>
            {dates.map((d) => {
              const h = holiday.get(d);
              return (
                <th scope="col" key={d} className={h ? 'erp-ros-holiday' : undefined}
                  title={h ? t('ros.grid.holidayHeader', { name: h.name[lang] ?? h.name.en ?? '', hijri: hijri(h.hijri) }) : undefined}>
                  <bdi>{d.slice(8)}</bdi>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {employees.map((e) => (
            <tr key={e.id}>
              <th scope="row"><bdi>{e.no}</bdi></th>
              {dates.map((d) => {
                const c = byKey.get(`${e.id}|${d}`);
                return (
                  <td key={d} className={holiday.has(d) ? 'erp-ros-holiday' : undefined}>
                    <button type="button" className={`erp-ros-cell${c && c.dayType !== 'working' ? ' erp-ros-off' : ''}`} disabled={!editable}
                      aria-label={t('ros.grid.cell', { employee: e.no, date: dayLabel(d), what: what(c) })} onClick={() => onOpen(e.id, d)}>
                      <bdi>{c ? (c.dayType === 'working' ? code(c.shiftId) : t(c.dayType === 'holiday' ? 'ros.grid.holiday' : 'ros.grid.off')) : '·'}</bdi>
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Checks({ result, people, pending }: { result: { messages: { code: string; severity: string; employmentId: string | null; date: string | null; detail: string | null }[] } | undefined; people: { id: string; employeeNo: string }[]; pending: boolean }) {
  const { t, server } = useText();
  if (pending) return <Skeleton />;
  const messages = result?.messages ?? [];
  if (messages.length === 0) return <p>{t('ros.checks.none')}</p>;
  const no = (id: string | null) => people.find((p) => p.id === id)?.employeeNo ?? '';
  return (
    <ul className="erp-list">
      {messages.map((m, i) => (
        <li key={`${m.code}-${m.employmentId}-${m.date}-${i}`}>
          <span className={m.severity === 'error' ? 'erp-ros-check-error' : undefined}>{t(m.severity === 'error' ? 'ros.checks.error' : 'ros.checks.warning')}: </span>
          {t(`ros.check.${m.code}` as MessageKey)} <bdi>{no(m.employmentId)} {m.date ?? ''}</bdi>{m.detail ? <> <bdi>{server(m.detail) ?? m.detail}</bdi></> : null}
        </li>
      ))}
    </ul>
  );
}

// ---------------------------------------------------------------------------------------------------- dialogs

function useAction(onDone: () => Promise<void>, onClose: () => void) {
  const refusal = useRefusal();
  const toast = useToast();
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  return {
    problem, refusal,
    toast,
    fail: (e: unknown) => setProblem(e instanceof ApiProblem ? e : null),
    finish: async (message?: string) => { await onDone(); if (message) toast.success(message); onClose(); },
    field: (name: string) => { const e = problem?.fieldErrors[name]; return e ? e.join(' ') : null; },
  };
}

function AssignDialog({ roster, shifts, onClose, onDone }: { roster: Roster; shifts: { id: string; code: string; nameI18n: Record<string, string> }[]; onClose: () => void; onDone: () => Promise<void> }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const a = useAction(onDone, onClose);
  const [shift, setShift] = useState('');
  const [everyone, setEveryone] = useState(true);
  const [replace, setReplace] = useState(false);
  const run = useMutation({
    mutationFn: () => rosterApi.assign(roster.id, { shiftId: shift, everyone, replace }),
    onSuccess: (r) => a.finish(t('ros.assign.done', { people: r.people, created: r.created, kept: r.kept })),
    onError: a.fail,
  });
  return (
    <Dialog title={t('ros.assign.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => (<><Button variant="quiet" onPress={close}>{t('ros.cancel')}</Button><Button variant="primary" isPending={run.isPending} isDisabled={!shift} onPress={() => run.mutate()}>{t('ros.save')}</Button></>)}>
      {a.problem && <Banner tone="danger" title={a.refusal(a.problem)}>{null}</Banner>}
      <SelectField label={t('ros.field.shift')} isRequired options={shifts.map((s) => ({ id: s.id, label: `${s.code} ${s.nameI18n[locale] ?? s.nameI18n.en ?? ''}`.trim() }))} value={shift} onChange={setShift} error={a.field('shiftId')} />
      <CheckboxField label={t('ros.field.everyone')} isSelected={everyone} onChange={setEveryone} />
      <CheckboxField label={t('ros.field.replace')} isSelected={replace} onChange={setReplace} />
    </Dialog>
  );
}

function FillDialog({ roster, people, shifts, lines, single, onClose, onDone }: {
  roster: Roster; people: { id: string; employeeNo: string }[]; shifts: { id: string; code: string; nameI18n: Record<string, string> }[]; lines: RosterLine[];
  single?: { employmentId: string; date: string }; onClose: () => void; onDone: () => Promise<void>;
}) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const a = useAction(onDone, onClose);
  const employments = [...new Set(lines.map((l) => l.employmentId))].map((id) => ({ id, label: people.find((p) => p.id === id)?.employeeNo ?? id.slice(0, 8) }));
  const current = single ? lines.find((l) => l.employmentId === single.employmentId && l.workDate === single.date) : undefined;
  const [employment, setEmployment] = useState(single?.employmentId ?? '');
  const [from, setFrom] = useState<CalendarDate | null>(parseDate(single?.date ?? roster.periodStart));
  const [to, setTo] = useState<CalendarDate | null>(parseDate(single?.date ?? roster.periodEnd));
  const [dayType, setDayType] = useState<string>(current?.dayType ?? 'working');
  const [shift, setShift] = useState(current?.shiftId ?? '');
  const [reason, setReason] = useState(current?.reason ?? '');
  const published = roster.status === 'published';
  const run = useMutation({
    mutationFn: () => {
      const changes: DayChange[] = from && to ? days(from.toString(), to.toString()).map((d) => ({
        employmentId: employment, workDate: d, dayType: dayType as DayChange['dayType'], ...(dayType === 'working' ? { shiftId: shift } : {}), ...(reason.trim() ? { reason: reason.trim() } : {}),
      })) : [];
      return rosterApi.changeDays(roster.id, { changes, ...(reason.trim() ? { reason: reason.trim() } : {}) });
    },
    onSuccess: () => a.finish(),
    onError: a.fail,
  });
  return (
    <Dialog title={t(single ? 'ros.day.title' : 'ros.fill.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => (<><Button variant="quiet" onPress={close}>{t('ros.cancel')}</Button>
        <Button variant="primary" isPending={run.isPending} isDisabled={!employment || !from || !to || (dayType === 'working' && !shift) || (published && !reason.trim())} onPress={() => run.mutate()}>{t('ros.save')}</Button></>)}>
      {!single && <p>{t('ros.fill.help')}</p>}
      {a.problem && <Banner tone="danger" title={a.refusal(a.problem)}>{null}</Banner>}
      <SelectField label={t('ros.field.employee')} isRequired isDisabled={!!single} options={employments} value={employment} onChange={setEmployment} />
      <DateField label={t('ros.field.from')} isRequired isDisabled={!!single} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
      <DateField label={t('ros.field.to')} isRequired isDisabled={!!single} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
      <SelectField label={t('ros.field.dayType')} isRequired options={DAY_TYPES.map((d) => ({ id: d, label: t(`ros.dayType.${d}` as MessageKey) }))} value={dayType} onChange={setDayType} />
      {dayType === 'working' && <SelectField label={t('ros.field.shift')} isRequired options={shifts.map((s) => ({ id: s.id, label: `${s.code} ${s.nameI18n[locale] ?? s.nameI18n.en ?? ''}`.trim() }))} value={shift} onChange={setShift} error={a.field('shiftId')} />}
      <TextField label={t('ros.field.reason')} help={t('ros.field.reason.help')} isRequired={published} value={reason} onChange={setReason} />
    </Dialog>
  );
}

function CopyDialog({ roster, onClose, onCopied }: { roster: Roster; onClose: () => void; onCopied: (id: string) => void }) {
  const { t } = useText();
  const a = useAction(async () => undefined, onClose);
  const [name, setName] = useState('');
  const run = useMutation({
    mutationFn: () => rosterApi.copyForward(roster.id, name.trim() ? { name: name.trim() } : {}),
    onSuccess: (r) => { a.toast.success(t('ros.copy.done')); onCopied(r.id); },
    onError: a.fail,
  });
  return (
    <Dialog title={t('ros.copy.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => (<><Button variant="quiet" onPress={close}>{t('ros.cancel')}</Button><Button variant="primary" isPending={run.isPending} onPress={() => run.mutate()}>{t('ros.save')}</Button></>)}>
      {a.problem && <Banner tone="danger" title={a.refusal(a.problem)}>{null}</Banner>}
      <TextField label={t('ros.field.name')} value={name} onChange={setName} error={a.field('name')} />
    </Dialog>
  );
}
