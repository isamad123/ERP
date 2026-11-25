// The attendance lock console (slice S1.31 Build item 7; FR-XM-26, FR-HRM-ATT-17, ATT-21, ATT-64, GHR-04, FR-XM-32): the pay periods of the person's
// companies with their attendance state, who locked and why, what the lock accepted and the adjustments; HR's lock request after the cut-off date,
// the request to cancel the locking run and reopen, the punches received after the lock; and the retroactive adjustments with their impact before
// they are asked for. Every request is decided by someone else through the approvals; the server checks everything again and the screen shows its
// answers, never its own guesses (spec 13 STA-06).
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { useNavigate, useParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, CheckboxField, DateField, Dialog, EmptyState, ErrorPanel, NumberField, PageHeader, SelectField, Skeleton, StatusChip, TextAreaField, useToast, type Status } from '@erp/ui';
import { formatDate, formatDateTime, formatDecimal, formatInteger, intlLocale, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, requestJson } from '../../api/client';
import { DAY_RESULTS, periodsApi, type AdjustedDayInput, type AdjustmentKind, type AdjustmentRow, type AdjustmentStatus, type DayResult, type DaySummary, type Impact, type LockPeriod, type LockState } from '../../api/periods';
import { policiesApi } from '../../api/policies';
import { useRefused } from '../admin/config-parts';
import { useShell } from '../../shell/context';
import './periods-messages';

const STATE_CHIP: Record<LockState, Status> = { open: 'info', lock_requested: 'pending', locked: 'approved', reopen_requested: 'pending' };
const ADJ_CHIP: Record<AdjustmentStatus, Status> = { submitted: 'pending', approved: 'approved', rejected: 'rejected', withdrawn: 'draft', applied: 'active' };
const KINDS: AdjustmentKind[] = ['attendance', 'leave_cancellation', 'overtime'];

function useDay() {
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return {
    day: (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers),
    at: (d: string) => formatDateTime(d, lang, timeZone, numbers),
    int: (n: number) => formatInteger(n, numbers),
    qty: (n: number) => formatDecimal(String(n), numbers),
    year: (n: number) => new Intl.NumberFormat(intlLocale(lang, numbers), { useGrouping: false }).format(n),
  };
}

function useCompany() {
  const { me, activeCompany } = useShell();
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.companies[0]?.id ?? '');
  const options = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code }));
  return { company, setCompany, options };
}

// ------------------------------------------------------------------ periods

export default function PeriodsScreen() {
  const { t, server } = useText();
  const { can, writable } = useShell();
  const f = useDay();
  const navigate = useNavigate();
  const { '*': selected = '' } = useParams();
  const { company, setCompany, options } = useCompany();
  useDocumentTitle(t('lock.title'));
  const allowed = can('hrm.attendance_periods.read');
  const list = useQuery({ queryKey: ['hrm', 'attendance-periods', company], queryFn: () => periodsApi.list(company || null), enabled: allowed, refetchOnMount: 'always' });
  if (!allowed) return <div className="erp-page"><PageHeader title={t('lock.title')} /><EmptyState title={t('lock.denied')} /></div>;
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const name = (p: LockPeriod) => t('lock.periodName', { year: f.year(p.taxYear), no: f.int(p.periodNo) });
  const who = (p: LockPeriod) => p.lockKind && p.lockedAt
    ? t('lock.by', { kind: t(`lock.kind.${p.lockKind}`), who: p.lockedByEmployeeNo ?? t('lock.by.unknown'), when: f.at(p.lockedAt) }) : '';
  return (
    <div className="erp-page">
      <PageHeader title={t('lock.title')} description={t('lock.description')} />
      {options.length > 1 && <div className="erp-row erp-wrap"><SelectField label={t('lock.company')} options={options} value={company} onChange={setCompany} /></div>}
      <div aria-busy={list.isPending}>
        {list.isPending && <Skeleton lines={5} />}
        {list.isError && (
          <ErrorPanel message={problem?.status === 403 ? t('lock.denied') : t('lock.loadError')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {list.isSuccess && list.data.items.length === 0 && <EmptyState title={t('lock.empty')}>{t('lock.empty.body')}</EmptyState>}
        {list.isSuccess && list.data.items.length > 0 && (
          <div className="erp-table-scroll">
            <table className="erp-table" aria-label={t('lock.title')}>
              <thead><tr>
                <th scope="col">{t('lock.col.group')}</th><th scope="col">{t('lock.col.period')}</th><th scope="col">{t('lock.col.dates')}</th>
                <th scope="col">{t('lock.col.cutoff')}</th><th scope="col">{t('lock.col.state')}</th><th scope="col">{t('lock.col.lock')}</th>
                <th scope="col">{t('lock.col.adjustments')}</th><th scope="col"><span className="erp-visually-hidden">{t('lock.col.actions')}</span></th>
              </tr></thead>
              <tbody>
                {list.data.items.map((p) => (
                  <tr key={p.payPeriodId}>
                    <td><span className="erp-code">{p.payGroupCode ?? ''}</span></td>
                    <td>{name(p)}</td>
                    <td>{f.day(p.startDate)} – {f.day(p.endDate)}</td>
                    <td>{f.day(p.cutOffDate)}</td>
                    <td><StatusChip status={STATE_CHIP[p.state]} label={t(`lock.state.${p.state}`)} /></td>
                    <td><span className="erp-stack"><span>{who(p)}</span>{p.lockReason && <span className="erp-small erp-muted">{p.lockReason}</span>}</span></td>
                    <td>{t('lock.adjustments', { waiting: f.int(p.adjustmentsWaiting), carried: f.int(p.adjustmentsCarriedIn) })}</td>
                    <td>
                      <Button variant="quiet" aria-label={t('lock.details.aria', { period: name(p), group: p.payGroupCode ?? '' })} onPress={() => { void navigate(`/time/periods/${p.payPeriodId}`); }}>
                        {t('lock.details')}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {selected && <PeriodDialog id={selected} canLock={writable && can('hrm.attendance_periods.lock')} canReopen={writable && can('hrm.attendance_periods.reopen')}
        canSeePunches={can('hrm.attendance_events.read')} server={server} onClose={() => { void navigate('/time/periods'); }} />}
    </div>
  );
}

function PeriodDialog({ id, canLock, canReopen, canSeePunches, server, onClose }: {
  id: string; canLock: boolean; canReopen: boolean; canSeePunches: boolean; server: (key: string) => string | null; onClose: () => void;
}) {
  const { t } = useText();
  const f = useDay();
  const [asking, setAsking] = useState<'lock' | 'reopen' | null>(null);
  const detail = useQuery({ queryKey: ['hrm', 'attendance-period', id], queryFn: () => periodsApi.get(id), refetchOnMount: 'always' });
  const late = useQuery({ queryKey: ['hrm', 'attendance-period', id, 'late'], queryFn: () => periodsApi.lateEvents(id), enabled: canSeePunches && detail.data?.period.state !== undefined && detail.data.period.state !== 'open' });
  const problem = detail.error instanceof ApiProblem ? detail.error : null;
  const d = detail.data;
  const title = d ? t('lock.detail.title', { period: t('lock.periodName', { year: f.year(d.period.taxYear), no: f.int(d.period.periodNo) }), group: d.period.payGroupCode ?? '' }) : t('lock.title');
  return (
    <Dialog title={title} isOpen onOpenChange={(open) => { if (!open) onClose(); }} actions={() => <Button variant="secondary" onPress={onClose}>{t('common.close')}</Button>}>
      <div className="erp-stack" aria-busy={detail.isPending}>
        {detail.isPending && <Skeleton lines={4} />}
        {detail.isError && (
          <ErrorPanel message={problem?.status === 403 ? t('lock.denied') : t('lock.loadError')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {d && (
          <>
            <span className="erp-row erp-wrap">
              <StatusChip status={STATE_CHIP[d.period.state]} label={t(`lock.state.${d.period.state}`)} />
              <span>{f.day(d.period.startDate)} – {f.day(d.period.endDate)}</span>
            </span>
            {d.period.state === 'locked' || d.period.state === 'reopen_requested'
              ? <p>{t('lock.detail.accepted', { days: f.int(d.openItems.unprocessedDays), exceptions: f.int(d.openItems.openExceptions) })}</p>
              : <p>{t('lock.detail.openItems', { days: f.int(d.openItems.unprocessedDays), exceptions: f.int(d.openItems.openExceptions) })}</p>}
            {d.period.lockReason && d.period.state !== 'lock_requested' && <p>{t('lock.detail.reason', { reason: d.period.lockReason })}</p>}
            {d.period.state === 'lock_requested' && d.period.lockReason && <p>{t('lock.detail.requested', { who: d.period.lockRequestedByEmployeeNo ?? t('lock.by.unknown'), reason: d.period.lockReason })}</p>}
            {d.period.reopenedAt && d.period.reopenReason && <p className="erp-small erp-muted">{t('lock.detail.reopened', { when: f.at(d.period.reopenedAt), reason: d.period.reopenReason })}</p>}
            <div className="erp-row erp-wrap">
              {canLock && (d.canRequestLock
                ? <Button variant="primary" onPress={() => setAsking('lock')}>{t('lock.ask')}</Button>
                : d.lockRefusal && d.period.state !== 'locked' && d.period.state !== 'reopen_requested' && <Banner tone="info">{server(d.lockRefusal) ?? d.lockRefusal}</Banner>)}
              {canReopen && (d.canRequestReopen
                ? <Button variant="secondary" onPress={() => setAsking('reopen')}>{t('lock.reopen')}</Button>
                : d.reopenRefusal && d.period.state !== 'open' && d.period.state !== 'lock_requested' && <Banner tone="info">{server(d.reopenRefusal) ?? d.reopenRefusal}</Banner>)}
            </div>
            {canSeePunches && d.period.state !== 'open' && d.period.state !== 'lock_requested' && (
              <section className="erp-stack" aria-label={t('lock.late.title')}>
                <h2 className="erp-section-title">{t('lock.late.title')}</h2>
                {late.isPending && <Skeleton lines={2} />}
                {late.isSuccess && late.data.total === 0 && <p className="erp-muted">{t('lock.late.none')}</p>}
                {late.isSuccess && late.data.total > 0 && (
                  <>
                    <p>{t('lock.late.count', { count: f.int(late.data.total) })}</p>
                    <div className="erp-table-scroll">
                      <table className="erp-table" aria-label={t('lock.late.title')}>
                        <thead><tr><th scope="col">{t('lock.late.col.employee')}</th><th scope="col">{t('lock.late.col.day')}</th><th scope="col">{t('lock.late.col.punch')}</th><th scope="col">{t('lock.late.col.received')}</th></tr></thead>
                        <tbody>{late.data.items.map((e) => (
                          <tr key={e.id}><td><span className="erp-code">{e.employeeNo ?? ''}</span></td><td>{f.day(e.eventDate)}</td><td>{f.at(e.eventAt)}</td><td>{f.at(e.receivedAt)}</td></tr>
                        ))}</tbody>
                      </table>
                    </div>
                  </>
                )}
              </section>
            )}
          </>
        )}
      </div>
      {d && asking === 'lock' && <LockDialog period={d.period} open={d.openItems} onClose={() => setAsking(null)} />}
      {d && asking === 'reopen' && <ReopenDialog period={d.period} onClose={() => setAsking(null)} />}
    </Dialog>
  );
}

function LockDialog({ period, open, onClose }: { period: LockPeriod; open: { unprocessedDays: number; openExceptions: number }; onClose: () => void }) {
  const { t } = useText();
  const f = useDay();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const [reason, setReason] = useState('');
  const [accept, setAccept] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const hasOpen = open.unprocessedDays > 0 || open.openExceptions > 0;
  const submit = async () => {
    setBusy(true);
    setErrors({});
    setSummary(null);
    try {
      await periodsApi.requestLock(period.payPeriodId, { reason: reason.trim(), acceptOpenItems: accept });
      await queryClient.invalidateQueries({ queryKey: ['hrm'] });
      toast.success(t('lock.lockForm.done'));
      onClose();
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  const name = t('lock.periodName', { year: f.year(period.taxYear), no: f.int(period.periodNo) });
  return (
    <Dialog title={t('lock.lockForm.title', { period: name, group: period.payGroupCode ?? '' })} isOpen onOpenChange={(o) => { if (!o) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button><Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('lock.lockForm.submit')}</Button></>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <p className="erp-small erp-muted">{t('lock.lockForm.explain')}</p>
        <TextAreaField label={t('lock.lockForm.reason')} isRequired value={reason} onChange={setReason} maxLength={1000} error={errors.reason ?? null} />
        {hasOpen && <CheckboxField label={t('lock.lockForm.accept', { days: f.int(open.unprocessedDays), exceptions: f.int(open.openExceptions) })} isSelected={accept} onChange={setAccept} />}
      </div>
    </Dialog>
  );
}

function ReopenDialog({ period, onClose }: { period: LockPeriod; onClose: () => void }) {
  const { t } = useText();
  const f = useDay();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const submit = async () => {
    setBusy(true);
    setErrors({});
    setSummary(null);
    try {
      await periodsApi.requestReopen(period.payPeriodId, { reason: reason.trim() });
      await queryClient.invalidateQueries({ queryKey: ['hrm'] });
      toast.success(t('lock.reopenForm.done'));
      onClose();
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  const name = t('lock.periodName', { year: f.year(period.taxYear), no: f.int(period.periodNo) });
  return (
    <Dialog title={t('lock.reopenForm.title', { period: name, group: period.payGroupCode ?? '' })} isOpen onOpenChange={(o) => { if (!o) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button><Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('lock.reopenForm.submit')}</Button></>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <p className="erp-small erp-muted">{t('lock.reopenForm.explain')}</p>
        <TextAreaField label={t('lock.reopenForm.reason')} isRequired value={reason} onChange={setReason} maxLength={1000} error={errors.reason ?? null} />
      </div>
    </Dialog>
  );
}

// ------------------------------------------------------------------ adjustments

export function AdjustmentsScreen() {
  const { t } = useText();
  const { can, writable } = useShell();
  const f = useDay();
  const navigate = useNavigate();
  const { '*': selected = '' } = useParams();
  const { company, setCompany, options } = useCompany();
  const [creating, setCreating] = useState(false);
  useDocumentTitle(t('adj.title'));
  const allowed = can('hrm.attendance_adjustments.read');
  const list = useQuery({ queryKey: ['hrm', 'attendance-adjustments', company], queryFn: () => periodsApi.adjustments(company || null), enabled: allowed, refetchOnMount: 'always' });
  if (!allowed) return <div className="erp-page"><PageHeader title={t('adj.title')} /><EmptyState title={t('adj.denied')} /></div>;
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const canSubmit = writable && can('hrm.attendance_adjustments.submit');
  const dates = (a: AdjustmentRow) => (a.fromDate === a.toDate ? f.day(a.fromDate) : `${f.day(a.fromDate)} – ${f.day(a.toDate)}`);
  return (
    <div className="erp-page">
      <PageHeader title={t('adj.title')} description={t('adj.description')}
        actions={canSubmit ? <Button variant="primary" onPress={() => setCreating(true)}>{t('adj.new')}</Button> : undefined} />
      {options.length > 1 && <div className="erp-row erp-wrap"><SelectField label={t('lock.company')} options={options} value={company} onChange={setCompany} /></div>}
      <div aria-busy={list.isPending}>
        {list.isPending && <Skeleton lines={5} />}
        {list.isError && (
          <ErrorPanel message={problem?.status === 403 ? t('adj.denied') : t('adj.loadError')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {list.isSuccess && list.data.items.length === 0 && <EmptyState title={t('adj.empty')}>{t('adj.empty.body')}</EmptyState>}
        {list.isSuccess && list.data.items.length > 0 && (
          <div className="erp-table-scroll">
            <table className="erp-table" aria-label={t('adj.title')}>
              <thead><tr>
                <th scope="col">{t('adj.col.employee')}</th><th scope="col">{t('adj.col.kind')}</th><th scope="col">{t('adj.col.dates')}</th>
                <th scope="col">{t('adj.col.status')}</th><th scope="col">{t('adj.col.reason')}</th><th scope="col"><span className="erp-visually-hidden">{t('adj.col.actions')}</span></th>
              </tr></thead>
              <tbody>
                {list.data.items.map((a) => (
                  <tr key={a.id}>
                    <td><span className="erp-code">{a.employeeNo ?? ''}</span></td>
                    <td>{t(`adj.kind.${a.kind}`)}</td>
                    <td>{dates(a)}</td>
                    <td><StatusChip status={ADJ_CHIP[a.status]} label={t(`adj.status.${a.status}`)} /></td>
                    <td>{a.reason}</td>
                    <td>
                      <Button variant="quiet" aria-label={t('adj.details.aria', { employee: a.employeeNo ?? '', dates: dates(a) })} onPress={() => { void navigate(`/time/adjustments/${a.id}`); }}>
                        {t('adj.details')}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {selected && <AdjustmentDialog id={selected} onClose={() => { void navigate('/time/adjustments'); }} />}
      {creating && <NewAdjustmentDialog company={company} onClose={() => setCreating(false)} />}
    </div>
  );
}

function useSummary() {
  const { t } = useText();
  const f = useDay();
  return (s: DaySummary) => {
    const main = t('adj.day.summary', { result: s.result ? t(`adj.result.${s.result}`) : t('adj.day.noResult'), worked: f.int(s.workedMinutes), late: f.int(s.lateMinutes), overtime: f.int(s.overtimeMinutes) });
    return s.unpaidLeave > 0 || s.paidLeave > 0 ? `${main}; ${t('adj.day.leave', { unpaid: f.qty(s.unpaidLeave), paid: f.qty(s.paidLeave) })}` : main;
  };
}

function AdjustmentDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useText();
  const f = useDay();
  const summary = useSummary();
  const detail = useQuery({ queryKey: ['hrm', 'attendance-adjustment', id], queryFn: () => periodsApi.adjustment(id), refetchOnMount: 'always' });
  const problem = detail.error instanceof ApiProblem ? detail.error : null;
  const a = detail.data;
  return (
    <Dialog title={a ? t('adj.detail.title', { employee: a.employeeNo ?? '' }) : t('adj.title')} isOpen onOpenChange={(o) => { if (!o) onClose(); }}
      actions={() => <Button variant="secondary" onPress={onClose}>{t('common.close')}</Button>}>
      <div className="erp-stack" aria-busy={detail.isPending}>
        {detail.isPending && <Skeleton lines={4} />}
        {detail.isError && (
          <ErrorPanel message={problem?.status === 404 ? t('adj.denied') : t('adj.loadError')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {a && (
          <>
            <span className="erp-row erp-wrap"><StatusChip status={ADJ_CHIP[a.status]} label={t(`adj.status.${a.status}`)} /><span>{t(`adj.kind.${a.kind}`)}</span></span>
            <p>{a.reason}</p>
            {a.targetPayPeriod && <p>{t('adj.detail.carried', { from: f.day(a.targetPayPeriod.startDate), to: f.day(a.targetPayPeriod.endDate) })}</p>}
            <div className="erp-table-scroll">
              <table className="erp-table" aria-label={t('adj.col.dates')}>
                <thead><tr><th scope="col">{t('adj.day.col.date')}</th><th scope="col">{t('adj.day.col.locked')}</th><th scope="col">{t('adj.day.col.corrected')}</th></tr></thead>
                <tbody>{a.days.map((d) => <tr key={d.date}><td>{f.day(d.date)}</td><td>{summary(d.old)}</td><td>{d.new ? summary(d.new) : ''}</td></tr>)}</tbody>
              </table>
            </div>
            <section className="erp-stack" aria-label={t('adj.detail.history')}>
              <h2 className="erp-section-title">{t('adj.detail.history')}</h2>
              <ul className="erp-stack">{a.history.map((h) => <li key={`${h.status}-${h.at}`}>{t(`adj.status.${h.status}`)} · {f.at(h.at)}{h.comment ? ` · ${h.comment}` : ''}</li>)}</ul>
            </section>
          </>
        )}
      </div>
    </Dialog>
  );
}

interface DayRow { date: CalendarDate | null; result: DayResult; worked: number | null; late: number | null; overtime: number | null }

function NewAdjustmentDialog({ company, onClose }: { company: string; onClose: () => void }) {
  const { t, server } = useText();
  const f = useDay();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const [employment, setEmployment] = useState('');
  const [kind, setKind] = useState<AdjustmentKind>('attendance');
  const [leave, setLeave] = useState('');
  const [rows, setRows] = useState<DayRow[]>([{ date: null, result: 'present', worked: null, late: null, overtime: null }]);
  const [reason, setReason] = useState('');
  const [impact, setImpact] = useState<Impact | null>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const people = useQuery({ queryKey: ['hrm', 'employments', company], queryFn: () => policiesApi.employments(company), enabled: !!company });
  const leaves = useQuery({
    queryKey: ['hrm', 'approved-leave', employment],
    queryFn: () => requestJson<{ items: { id: string; requestNo: string; startDate: string; endDate: string }[] }>('GET', `/api/hrm/leave-requests?employmentId=${encodeURIComponent(employment)}&status=approved&limit=100`),
    enabled: kind === 'leave_cancellation' && !!employment,
  });
  const days = (): AdjustedDayInput[] => rows.flatMap((r) => (r.date ? [{ ...r, date: r.date }] : [])).map((r) => ({
    date: r.date.toString(),
    ...(kind === 'overtime' ? { overtimeMinutes: r.overtime ?? 0 } : { result: r.result, ...(r.worked === null ? {} : { workedMinutes: r.worked }), ...(r.late === null ? {} : { lateMinutes: r.late }) }),
  }));
  const body = () => ({ employmentId: employment, kind, ...(kind === 'leave_cancellation' ? { leaveRequestId: leave } : {}), days: kind === 'leave_cancellation' ? [] : days(), reason: reason.trim() });
  const call = async (send: boolean) => {
    setBusy(true);
    setErrors({});
    setSummary(null);
    try {
      if (send) {
        await periodsApi.submit(body());
        await queryClient.invalidateQueries({ queryKey: ['hrm'] });
        toast.success(t('adj.form.done'));
        onClose();
      } else {
        setImpact(await periodsApi.impact(body()));
      }
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  const change = (i: number, patch: Partial<DayRow>) => { setRows(rows.map((r, n) => (n === i ? { ...r, ...patch } : r))); setImpact(null); };
  return (
    <Dialog title={t('adj.form.title')} isOpen onOpenChange={(o) => { if (!o) onClose(); }}
      actions={() => (<>
        <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
        <Button variant="secondary" isPending={busy} onPress={() => { void call(false); }}>{t('adj.form.impact')}</Button>
        <Button variant="primary" isPending={busy} isDisabled={!impact?.canSubmit} onPress={() => { void call(true); }}>{t('adj.form.submit')}</Button>
      </>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <SelectField label={t('adj.form.employee')} isRequired options={(people.data ?? []).map((e) => ({ id: e.id, label: e.employeeNo }))} value={employment}
          onChange={(v) => { setEmployment(v); setImpact(null); }} error={errors.employmentId ?? null} />
        <SelectField label={t('adj.form.kind')} isRequired options={KINDS.map((k) => ({ id: k, label: t(`adj.kind.${k}`) }))} value={kind}
          onChange={(v) => { setKind(v as AdjustmentKind); setImpact(null); }} error={errors.kind ?? null} />
        {kind === 'leave_cancellation' && (leaves.isSuccess && leaves.data.items.length === 0
          ? <Banner tone="info">{t('adj.form.leave.none')}</Banner>
          : <SelectField label={t('adj.form.leave')} isRequired options={(leaves.data?.items ?? []).map((l) => ({ id: l.id, label: l.requestNo, detail: `${f.day(l.startDate)} – ${f.day(l.endDate)}` }))}
              value={leave} onChange={(v) => { setLeave(v); setImpact(null); }} error={errors.leaveRequestId ?? null} />)}
        {kind !== 'leave_cancellation' && rows.map((r, i) => (
          <fieldset key={i} className="erp-stack">
            <legend>{t('adj.form.day', { no: f.int(i + 1) })}</legend>
            <div className="erp-row erp-wrap">
              <DateField label={t('adj.form.date')} isRequired value={r.date} onChange={(v) => change(i, { date: v ? parseDate(v.toString()) : null })} error={errors[`days[${i}].date`] ?? null} />
              {kind === 'attendance' && (
                <>
                  <SelectField label={t('adj.form.result')} options={DAY_RESULTS.map((x) => ({ id: x, label: t(`adj.result.${x}` as MessageKey) }))} value={r.result}
                    onChange={(v) => change(i, { result: v as DayResult })} error={errors[`days[${i}].result`] ?? null} />
                  <NumberField label={t('adj.form.worked')} {...(r.worked === null ? {} : { value: r.worked })} minValue={0} maxValue={1440}
                    onChange={(v) => change(i, { worked: Number.isFinite(v) ? v : null })} error={errors[`days[${i}].workedMinutes`] ?? null} />
                  <NumberField label={t('adj.form.late')} {...(r.late === null ? {} : { value: r.late })} minValue={0} maxValue={1440}
                    onChange={(v) => change(i, { late: Number.isFinite(v) ? v : null })} error={errors[`days[${i}].lateMinutes`] ?? null} />
                </>
              )}
              {kind === 'overtime' && (
                <NumberField label={t('adj.form.overtime')} isRequired {...(r.overtime === null ? {} : { value: r.overtime })} minValue={0} maxValue={1440}
                  onChange={(v) => change(i, { overtime: Number.isFinite(v) ? v : null })} error={errors[`days[${i}].overtimeMinutes`] ?? null} />
              )}
              {rows.length > 1 && <Button variant="quiet" onPress={() => { setRows(rows.filter((_, n) => n !== i)); setImpact(null); }}>{t('adj.form.removeDay', { no: f.int(i + 1) })}</Button>}
            </div>
          </fieldset>
        ))}
        {kind !== 'leave_cancellation' && rows.length < 62 && (
          <Button variant="quiet" onPress={() => { setRows([...rows, { date: null, result: 'present', worked: null, late: null, overtime: null }]); setImpact(null); }}>{t('adj.form.addDay')}</Button>
        )}
        <TextAreaField label={t('adj.form.reason')} isRequired value={reason} onChange={setReason} maxLength={1000} error={errors.reason ?? null} />
        {impact && <ImpactPanel impact={impact} server={server} />}
      </div>
    </Dialog>
  );
}

function ImpactPanel({ impact, server }: { impact: Impact; server: (key: string) => string | null }) {
  const { t } = useText();
  const f = useDay();
  const summary = useSummary();
  return (
    <section className="erp-stack" aria-label={t('adj.impact.title')}>
      <h2 className="erp-section-title">{t('adj.impact.title')}</h2>
      {impact.refusal && <Banner tone="warning">{server(impact.refusal) ?? impact.refusal}</Banner>}
      <p>{t('adj.impact.modules', { modules: impact.modules.map((m) => t(`adj.impact.module.${m}` as MessageKey)).join(', ') })}</p>
      <ul className="erp-stack">
        {impact.payPeriods.map((p) => <li key={p.payPeriodId}>{t(p.role === 'target' ? 'adj.impact.period.target' : 'adj.impact.period.locked', { from: f.day(p.startDate), to: f.day(p.endDate) })}</li>)}
      </ul>
      {impact.days.length > 0 && (
        <div className="erp-table-scroll">
          <table className="erp-table" aria-label={t('adj.impact.title')}>
            <thead><tr><th scope="col">{t('adj.day.col.date')}</th><th scope="col">{t('adj.day.col.locked')}</th><th scope="col">{t('adj.day.col.corrected')}</th></tr></thead>
            <tbody>{impact.days.map((d) => <tr key={d.date}><td>{f.day(d.date)}</td><td>{summary(d.old)}</td><td>{summary(d.new)}</td></tr>)}</tbody>
          </table>
        </div>
      )}
      {impact.leave && (
        <p>{t('adj.impact.leave', {
          givenBack: f.qty(impact.leave.givenBack), type: impact.leave.leaveTypeCode, before: f.qty(impact.leave.balanceBefore), after: f.qty(impact.leave.balanceAfter),
          on: impact.leave.entryDate ? f.day(impact.leave.entryDate) : '',
        })}</p>
      )}
      {impact.overtime.length > 0 && <p>{t('adj.impact.overtime', { count: f.int(impact.overtime.length) })}</p>}
      {impact.penalties.length > 0 && <p>{t('adj.impact.penalties', { count: f.int(impact.penalties.length) })}</p>}
    </section>
  );
}
