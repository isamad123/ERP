// The payroll calendar (slice S1.34a Build items 2 and 3; FR-HRM-PAY-01, FR-HRM-PAY-38): every period of the person's pay groups with
// its cut-off and payment dates and the days left, generating the periods of a tax year, changing the dates of a period that has not
// opened, and the readiness checklist of a period (items whose slice does not exist yet show as "not available", never as passed;
// who is missing is listed). The server checks every request again (permissions, data scope); dates and counts are shown as the
// server confirmed them, never assumed (spec 13 STA-06).
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { EngineProvider, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, NumberField, PageHeader, SelectField, Skeleton, StatusChip, useToast, type Option, type Status } from '@erp/ui';
import { formatDate, formatInteger, intlLocale, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, sendText } from '../../api/client';
import { payApi, WEEKDAYS, type CalendarPeriod, type GroupOption, type PeriodStatus, type ReadinessStatus, type Weekday } from '../../api/pay';
import { useRefused } from '../admin/config-parts';
import { useShell } from '../../shell/context';
import './pay-messages';

const api: EngineApi = { send: sendText };
const ALL = 'all';

const PERIOD_CHIP: Record<PeriodStatus, Status> = { future: 'draft', open: 'info', processing: 'pending', closed: 'approved' };
const READINESS_CHIP: Record<ReadinessStatus, Status> = { passed: 'approved', failed: 'blocked', not_available: 'on_hold' };

export default function CalendarScreen() {
  return <EngineProvider api={api}><Calendar /></EngineProvider>;
}

function useDay() {
  const { locale, timeZone, numbers } = useLocaleSettings();
  return (day: string) => formatDate(`${day}T12:00:00Z`, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
}

function Calendar() {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  useDocumentTitle(t('pay.calendar.title'));
  const [group, setGroup] = useState(ALL);
  const [year, setYear] = useState<number | null>(null);
  const [generating, setGenerating] = useState(false);
  const [changing, setChanging] = useState<CalendarPeriod | null>(null);
  const [checking, setChecking] = useState<CalendarPeriod | null>(null);
  const day = useDay();
  const groups = useQuery({ queryKey: ['hrm', 'pay', 'groups'], queryFn: () => payApi.groups() });
  const calendar = useQuery({ queryKey: ['hrm', 'pay', 'calendar', group, year], queryFn: () => payApi.calendar(group === ALL ? null : group, year), refetchOnMount: 'always' });
  const problem = calendar.error instanceof ApiProblem ? calendar.error : null;
  const options: Option[] = [{ id: ALL, label: t('pay.calendar.group.all') }, ...(groups.data?.items ?? []).map((g) => ({ id: g.id, label: g.code, detail: g.isActive ? t(`pay.frequency.${g.frequency}`) : t('pay.group.off') }))];
  const canGenerate = writable && can('hrm.pay_periods.generate');
  const canChange = writable && can('hrm.pay_periods.update');
  const canCheck = can('hrm.pay_readiness.read');
  const left = (p: CalendarPeriod) => (p.status === 'closed' ? null : p.daysToCutOff === 0 ? t('pay.calendar.cutoff.today') : p.daysToCutOff > 0
    ? t('pay.calendar.cutoff.in', { days: formatInteger(p.daysToCutOff, numbers) }) : t('pay.calendar.cutoff.passed', { days: formatInteger(-p.daysToCutOff, numbers) }));
  return (
    <div className="erp-page">
      <PageHeader title={t('pay.calendar.title')} description={t('pay.calendar.description')}
        actions={canGenerate ? <Button variant="primary" onPress={() => setGenerating(true)}>{t('pay.calendar.generate')}</Button> : undefined} />
      <div className="erp-row erp-wrap">
        <SelectField label={t('pay.calendar.group')} options={options} value={group} onChange={setGroup} />
        <NumberField label={t('pay.calendar.year')} help={t('pay.calendar.year.help')} {...(year === null ? {} : { value: year })} minValue={2000} maxValue={2100}
          onChange={(v) => setYear(Number.isFinite(v) ? v : null)} />
      </div>
      {calendar.isPending && <Skeleton lines={5} />}
      {calendar.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('pay.calendar.denied') : t('pay.calendar.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void calendar.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {calendar.isSuccess && calendar.data.items.length === 0 && (
        <EmptyState title={t('pay.calendar.empty')}>{canGenerate ? t('pay.calendar.empty.generate') : t('pay.calendar.empty.ask')}</EmptyState>
      )}
      {calendar.isSuccess && calendar.data.items.length > 0 && (
        <>
          {calendar.data.more && <Banner tone="info">{t('pay.calendar.more')}</Banner>}
          <div className="erp-table-scroll">
            <table className="erp-table" aria-label={t('pay.calendar.title')}>
              <thead><tr>
                <th scope="col">{t('pay.calendar.col.group')}</th><th scope="col">{t('pay.calendar.col.period')}</th><th scope="col">{t('pay.calendar.col.dates')}</th>
                <th scope="col">{t('pay.calendar.col.cutoff')}</th><th scope="col">{t('pay.calendar.col.payment')}</th><th scope="col">{t('pay.calendar.col.status')}</th>
                <th scope="col"><span className="erp-visually-hidden">{t('pay.calendar.col.actions')}</span></th>
              </tr></thead>
              <tbody>
                {calendar.data.items.map((p) => (
                  <tr key={p.id}>
                    <td><span className="erp-code">{p.payGroupCode ?? ''}</span></td>
                    <td>{t('pay.calendar.periodName', { year: new Intl.NumberFormat(intlLocale(locale === 'ur' ? 'ur' : 'en', numbers), { useGrouping: false }).format(p.taxYear), no: formatInteger(p.periodNo, numbers) })}</td>
                    <td>{day(p.startDate)} – {day(p.endDate)}</td>
                    <td><span className="erp-stack"><span>{day(p.cutOffDate)}</span><span className="erp-small erp-muted">{left(p)}</span></span></td>
                    <td>{day(p.paymentDate)}</td>
                    <td><StatusChip status={PERIOD_CHIP[p.status]} label={t(`pay.period.${p.status}`)} /></td>
                    <td>
                      <span className="erp-row erp-wrap">
                        {canCheck && <Button variant="quiet" aria-label={t('pay.calendar.readiness.aria', { period: p.periodNo, group: p.payGroupCode ?? '' })} onPress={() => setChecking(p)}>{t('pay.calendar.readiness')}</Button>}
                        {canChange && p.status === 'future' && <Button variant="quiet" aria-label={t('pay.calendar.change.aria', { period: p.periodNo, group: p.payGroupCode ?? '' })} onPress={() => setChanging(p)}>{t('pay.calendar.change')}</Button>}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
      {generating && <GenerateDialog groups={groups.data?.items ?? []} group={group === ALL ? null : group} year={year} onClose={() => setGenerating(false)} />}
      {changing && <ChangeDialog period={changing} onClose={() => setChanging(null)} />}
      {checking && <ReadinessDialog period={checking} onClose={() => setChecking(null)} locale={locale} />}
    </div>
  );
}

function GenerateDialog({ groups, group, year, onClose }: { groups: GroupOption[]; group: string | null; year: number | null; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const active = groups.filter((g) => g.isActive);
  const [id, setId] = useState<string>(group ?? active[0]?.id ?? '');
  const [taxYear, setTaxYear] = useState<number | null>(year);
  const [weekday, setWeekday] = useState<Weekday>('monday');
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const chosen = active.find((g) => g.id === id);
  const weekly = chosen?.frequency === 'weekly' || chosen?.frequency === 'bi_weekly';
  const submit = async () => {
    if (!chosen || taxYear === null) { setSummary(t('pay.generate.error.fix')); setErrors(taxYear === null ? { taxYear: t('pay.generate.year.required') } : {}); return; }
    setBusy(true);
    setErrors({});
    setSummary(null);
    try {
      const done = await payApi.generate(chosen.id, { taxYear, ...(weekly ? { weekStartsOn: weekday } : {}) });
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'pay'] });
      toast.success(t('pay.generate.done', { count: done.count, group: chosen.code }));
      onClose();
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={t('pay.generate.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button><Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('pay.generate.submit')}</Button></>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        {active.length === 0 ? <Banner tone="info">{t('pay.generate.noGroups')}</Banner> : (
          <SelectField label={t('pay.calendar.group')} isRequired options={active.map((g) => ({ id: g.id, label: g.code, detail: t(`pay.frequency.${g.frequency}`) }))} value={id} onChange={setId} error={errors.payGroupId ?? null} />
        )}
        <NumberField label={t('pay.calendar.year')} help={t('pay.generate.year.help')} isRequired {...(taxYear === null ? {} : { value: taxYear })} minValue={2000} maxValue={2100}
          onChange={(v) => setTaxYear(Number.isFinite(v) ? v : null)} error={errors.taxYear ?? null} />
        {weekly && (
          <SelectField label={t('pay.generate.weekday')} help={t('pay.generate.weekday.help')} options={WEEKDAYS.map((w) => ({ id: w, label: t(`pay.weekday.${w}` as MessageKey) }))}
            value={weekday} onChange={(w) => setWeekday(w as Weekday)} error={errors.weekStartsOn ?? null} />
        )}
        <p className="erp-small erp-muted">{t('pay.generate.explain')}</p>
      </div>
    </Dialog>
  );
}

function ChangeDialog({ period, onClose }: { period: CalendarPeriod; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const [cutOff, setCutOff] = useState<CalendarDate | null>(parseDate(period.cutOffDate));
  const [payment, setPayment] = useState<CalendarDate | null>(parseDate(period.paymentDate));
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const submit = async () => {
    if (!cutOff || !payment) { setSummary(t('pay.change.error.fix')); return; }
    setBusy(true);
    setErrors({});
    setSummary(null);
    try {
      await payApi.change(period.id, { cutOffDate: cutOff.toString(), paymentDate: payment.toString(), rowVersion: period.rowVersion });
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'pay'] });
      toast.success(t('pay.change.done'));
      onClose();
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={t('pay.change.title', { period: period.periodNo, group: period.payGroupCode ?? '' })} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button><Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('pay.change.submit')}</Button></>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <p className="erp-small erp-muted">{t('pay.change.explain')}</p>
        <DateField label={t('pay.calendar.col.cutoff')} isRequired value={cutOff} onChange={(v) => setCutOff(v ? parseDate(v.toString()) : null)} error={errors.cutOffDate ?? null} />
        <DateField label={t('pay.calendar.col.payment')} isRequired value={payment} onChange={(v) => setPayment(v ? parseDate(v.toString()) : null)} error={errors.paymentDate ?? null} />
      </div>
    </Dialog>
  );
}

function ReadinessDialog({ period, onClose, locale }: { period: CalendarPeriod; onClose: () => void; locale: string }) {
  const { t, server } = useText();
  const { numbers } = useLocaleSettings();
  const check = useQuery({ queryKey: ['hrm', 'pay', 'readiness', period.id], queryFn: () => payApi.readiness(period.id), refetchOnMount: 'always' });
  const problem = check.error instanceof ApiProblem ? check.error : null;
  return (
    <Dialog title={t('pay.readiness.title', { period: period.periodNo, group: period.payGroupCode ?? '' })} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => <Button variant="secondary" onPress={onClose}>{t('common.close')}</Button>}>
      <div className="erp-stack" lang={locale}>
        {check.isPending && <Skeleton lines={4} />}
        {check.isError && (
          <ErrorPanel message={problem?.status === 403 ? t('pay.readiness.denied') : t('pay.readiness.loadError')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void check.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {check.isSuccess && (
          <>
            <Banner tone={check.data.ready ? 'success' : 'warning'}>{check.data.ready ? t('pay.readiness.ready') : t('pay.readiness.notReady')}</Banner>
            <ul className="erp-stack" aria-label={t('pay.readiness.title', { period: period.periodNo, group: period.payGroupCode ?? '' })}>
              {check.data.items.map((item) => (
                <li key={item.code} className="erp-stack">
                  <span className="erp-row erp-wrap">
                    <StatusChip status={READINESS_CHIP[item.status]} label={t(`pay.readiness.status.${item.status}`)} />
                    <span>{server(item.labelKey) ?? item.code}</span>
                  </span>
                  {item.status === 'not_available' && <span className="erp-small erp-muted">{t('pay.readiness.notAvailable', { slice: item.slice })}</span>}
                  {item.status === 'failed' && (
                    <span className="erp-small">
                      {t('pay.readiness.missing', { count: formatInteger(item.count, numbers) })}
                      {item.employees.length > 0 && <> {item.employees.map((e) => e.employeeNo).join(', ')}{item.count > item.employees.length ? ' …' : ''}</>}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </Dialog>
  );
}
