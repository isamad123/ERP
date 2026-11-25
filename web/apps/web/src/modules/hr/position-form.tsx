// One position (slice S1.22b Build item 10; FR-HRM-POS-02): create it, change it from a date (a new row starts that day, the earlier rows
// stay as history), freeze or reopen it, close it, and see who holds it. The server checks every request again (permissions, scope,
// cycles in the reporting line, budgets, holders at closing) and the screen says what it answered.
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, NumberField, PageHeader, SelectField, Skeleton, StatusChip, TextField, useToast, type Option, ScrollRegion } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDate, formatDecimal, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, requestJson } from '../../api/client';
import { positionsApi, type PositionRow, type PositionWrite } from '../../api/positions';
import { useProblemText } from '../admin/users-parts';
import { useShell } from '../../shell/context';
import { ANY, todayIso, useCompanyChoice, useGradeOptions, useLocationOptions, useUnitOptions } from './positions-parts';

const STATUS_CHIP: Record<string, 'draft' | 'info' | 'approved' | 'on_hold' | 'blocked'> = { planned: 'draft', open: 'info', filled: 'approved', frozen: 'on_hold', closed: 'blocked' };
const POSITIONS = '/hr/positions';

export default function PositionForm() {
  const { id } = useParams();
  const { t } = useText();
  useDocumentTitle(t('pos.positions.title'));
  const known = useQuery({ queryKey: ['hrm', 'position', id], enabled: Boolean(id), queryFn: () => requestJson<PositionRow>('GET', `/api/hrm/positions/${id}`) });
  if (!id) return <Editor row={null} history={[]} />;
  if (known.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (known.isError) {
    const gone = known.error instanceof ApiProblem && known.error.status === 404;
    return <div className="erp-page"><ErrorPanel message={gone ? t('pos.form.notFound') : t('pos.loadError')} /></div>;
  }
  return <WithHistory row={known.data} />;
}

function WithHistory({ row }: { row: PositionRow }) {
  const history = useQuery({ queryKey: ['hrm', 'position', 'history', row.companyId, row.positionCode], queryFn: () => positionsApi.history(row.companyId, row.positionCode) });
  if (history.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  const rows = (history.data?.items ?? [row]).filter((r) => r.positionCode.toLowerCase() === row.positionCode.toLowerCase()).sort((a, b) => a.validFrom.localeCompare(b.validFrom));
  return <Editor row={rows[rows.length - 1] ?? row} history={rows} />;
}

function Editor({ row, history }: { row: PositionRow | null; history: PositionRow[] }) {
  const { t } = useText();
  const { locale, numbers, timeZone } = useLocaleSettings();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const { can, writable } = useShell();
  const creating = row === null;
  const { companyId: chosenCompany, setCompanyId, options: companies } = useCompanyChoice(creating ? 'hrm.positions.create' : 'hrm.positions.read');
  const companyId = row?.companyId ?? chosenCompany;
  const units = useUnitOptions(companyId);
  const grades = useGradeOptions(companyId);
  const locations = useLocationOptions(companyId);
  const others = useQuery({ queryKey: ['hrm', 'positions', 'choices', companyId], enabled: companyId !== null, queryFn: () => positionsApi.list(companyId as string, null) });
  const reportOptions: Option[] = [{ id: ANY, label: t('pos.form.none') }, ...(others.data?.items ?? []).filter((p) => p.positionCode.toLowerCase() !== row?.positionCode.toLowerCase()).map((p) => ({ id: p.id, label: p.title, detail: p.positionCode }))];
  const [code, setCode] = useState(row?.positionCode ?? '');
  const [title, setTitle] = useState(row?.title ?? '');
  const [unit, setUnit] = useState(row?.orgUnitId ?? '');
  const [grade, setGrade] = useState(row?.gradeId ?? ANY);
  const [reportsTo, setReportsTo] = useState(row?.reportsToPositionId ?? ANY);
  const [location, setLocation] = useState(row?.locationId ?? ANY);
  const [fte, setFte] = useState(row ? Number(row.budgetedFte) : 1);
  const [date, setDate] = useState(creating ? todayIso() : todayIso());
  const [closing, setClosing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const day = (iso: string) => formatDate(`${iso}T12:00:00Z`, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
  const parsed = (() => { try { return parseDate(date); } catch { return null; } })();
  const canWrite = writable && (creating ? true : can('hrm.positions.update', companyId ?? undefined));
  const frozen = row?.status === 'frozen';
  const closed = row?.status === 'closed';

  const holders = useQuery({ queryKey: ['hrm', 'position', 'holders', row?.id, todayIso()], enabled: row !== null, queryFn: () => positionsApi.holders(row?.id as string, null) });
  const refresh = async () => { await queryClient.invalidateQueries({ queryKey: ['hrm'] }); };
  const fail = (e: unknown) => { setError(e instanceof ApiProblem ? problemText(e) : t('pos.form.failed')); };
  const clearable = (value: string) => (value === ANY ? null : value);

  const save = useMutation({
    mutationFn: async (status?: 'open' | 'frozen') => {
      if (creating) {
        const body: PositionWrite = { companyId: companyId as string, positionCode: code.trim(), title: title.trim(), orgUnitId: unit, budgetedFte: fte, validFrom: date };
        if (grade !== ANY) body.gradeId = grade;
        if (reportsTo !== ANY) body.reportsToPositionId = reportsTo;
        if (location !== ANY) body.locationId = location;
        return positionsApi.create(body);
      }
      const prior = row;
      const body: PositionWrite = { effectiveDate: date, rowVersion: prior.rowVersion };
      if (title.trim() !== prior.title) body.title = title.trim();
      if (unit !== prior.orgUnitId) body.orgUnitId = unit;
      if (fte !== Number(prior.budgetedFte)) body.budgetedFte = fte;
      const clear: string[] = [];
      const optional: [string, string, string | null][] = [['gradeId', grade, prior.gradeId], ['reportsToPositionId', reportsTo, prior.reportsToPositionId], ['locationId', location, prior.locationId]];
      for (const [key, value, before] of optional) {
        if (clearable(value) === before) continue;
        if (value === ANY) clear.push(key); else (body as Record<string, unknown>)[key] = value;
      }
      if (clear.length > 0) body.clear = clear;
      if (status) body.status = status;
      return positionsApi.change(prior.id, body);
    },
    onSuccess: async (saved) => {
      await refresh();
      toast.success(t(creating ? 'pos.form.created' : 'pos.form.saved', { code: saved.positionCode }));
      void navigate(creating ? `${POSITIONS}/${saved.id}` : POSITIONS, { replace: creating });
    },
    onError: fail,
  });
  const close = useMutation({
    mutationFn: () => positionsApi.close((row as PositionRow).id, { effectiveDate: date, rowVersion: (row as PositionRow).rowVersion }),
    onSuccess: async (saved) => { await refresh(); setClosing(false); toast.success(t('pos.form.closed', { code: saved.positionCode })); void navigate(POSITIONS); },
    onError: (e) => { setClosing(false); fail(e); },
  });
  const ready = Boolean(companyId) && title.trim() !== '' && unit !== '' && parsed !== null && (!creating || code.trim() !== '');
  const num = (n: number) => formatDecimal(String(n), numbers);

  if (companyId === null) return <div className="erp-page"><EmptyState title={t('pos.noCompany')}>{null}</EmptyState></div>;
  return (
    <div className="erp-page">
      <PageHeader title={creating ? t('pos.positions.new') : `${row.positionCode} · ${row.title}`} description={t('pos.positions.description')}
        breadcrumbs={[{ label: t('pos.positions.title'), href: POSITIONS }, { label: creating ? t('pos.positions.new') : row.positionCode }]}
        actions={row ? <StatusChip status={STATUS_CHIP[holders.data?.status ?? row.status] ?? 'draft'} label={t(`pos.status.${holders.data?.status ?? row.status}` as MessageKey)} /> : undefined} />
      {error && <Banner tone="danger" onDismiss={() => setError(null)}>{error}</Banner>}
      <form className="erp-pos-form" onSubmit={(e) => { e.preventDefault(); setError(null); save.mutate(undefined); }}>
        {creating && companies.length > 1 && <SelectField label={t('pos.company')} options={companies} value={companyId} onChange={setCompanyId} />}
        {creating && <TextField label={t('pos.form.code')} help={t('pos.form.code.help')} value={code} onChange={setCode} isCode isRequired maxLength={40} />}
        <TextField label={t('pos.form.title')} value={title} onChange={setTitle} isRequired maxLength={200} isDisabled={!canWrite || closed} />
        <SelectField label={t('pos.form.unit')} options={units.options} value={unit || null} onChange={setUnit} isRequired isDisabled={!canWrite || closed} />
        <SelectField label={t('pos.form.grade')} options={[{ id: ANY, label: t('pos.form.none') }, ...grades.options]} value={grade} onChange={setGrade} isDisabled={!canWrite || closed} />
        <SelectField label={t('pos.form.reportsTo')} options={reportOptions} value={reportsTo} onChange={setReportsTo} isDisabled={!canWrite || closed} />
        <SelectField label={t('pos.form.location')} options={[{ id: ANY, label: t('pos.form.none') }, ...locations.options]} value={location} onChange={setLocation} isDisabled={!canWrite || closed} />
        <NumberField label={t('pos.form.fte')} value={fte} minValue={0} onChange={(v) => setFte(Number.isFinite(v) ? v : 0)} isDisabled={!canWrite || closed} />
        <DateField label={t(creating ? 'pos.form.from' : 'pos.form.effective')} value={parsed} onChange={(d) => { if (d) setDate(d.toString()); }} isDisabled={!canWrite || closed} />
        {canWrite && !closed && (
          <div className="erp-row erp-wrap">
            <Button type="submit" variant="primary" isPending={save.isPending} isDisabled={!ready}>{t(creating ? 'pos.form.save' : 'pos.form.change')}</Button>
            {!creating && <Button variant="secondary" isDisabled={!ready || save.isPending} onPress={() => { setError(null); save.mutate(frozen ? 'open' : 'frozen'); }}>{t(frozen ? 'pos.form.unfreeze' : 'pos.form.freeze')}</Button>}
            {!creating && can('hrm.positions.close', companyId) && <Button variant="danger" isDisabled={!ready} onPress={() => setClosing(true)}>{t('pos.form.close')}</Button>}
            <Button variant="quiet" onPress={() => { void navigate(POSITIONS); }}>{t('common.cancel')}</Button>
          </div>
        )}
      </form>
      {row && (
        <section className="erp-section" aria-labelledby="pos-holders">
          <h2 id="pos-holders" className="erp-h2">{t('pos.holders.title')}</h2>
          {holders.isPending && <Skeleton lines={2} />}
          {holders.isError && <p className="erp-muted">{t('pos.loadError')}</p>}
          {holders.data && (
            <>
              <p>{t('pos.holders.summary', { budget: num(holders.data.budgetedFte), filled: num(holders.data.filledFte), vacant: num(holders.data.vacantFte), over: num(holders.data.overFte) })}</p>
              {holders.data.holders.length === 0 ? <p className="erp-muted">{t('pos.holders.none')}</p> : (
                <ul className="erp-plain-list">{holders.data.holders.map((h) => <li key={h.employmentId}><bdi className="erp-code">{h.employeeNo}</bdi> · {num(h.fte)}{h.isActing ? <span className="erp-muted"> ({t('pos.holders.acting')})</span> : null}</li>)}</ul>
              )}
            </>
          )}
        </section>
      )}
      {history.length > 0 && (
        <section className="erp-section" aria-labelledby="pos-history">
          <h2 id="pos-history" className="erp-h2">{t('pos.history.title')}</h2>
          <ScrollRegion className="erp-pos-scroll" label={t('pos.history.title')}>
            <table className="erp-pos-table" aria-label={t('pos.history.title')}>
              <thead><tr><th scope="col">{t('pos.positions.col.from')}</th><th scope="col">{t('pos.positions.col.title')}</th><th scope="col" className="erp-num">{t('pos.positions.col.fte')}</th><th scope="col">{t('pos.positions.col.status')}</th></tr></thead>
              <tbody>
                {history.map((h) => (
                  <tr key={h.id}>
                    <td>{h.validTo ? t('pos.history.range', { from: day(h.validFrom), to: day(h.validTo) }) : t('pos.history.open', { from: day(h.validFrom) })}</td>
                    <td><bdi>{h.title}</bdi></td><td className="erp-num">{num(Number(h.budgetedFte))}</td><td>{t(`pos.status.${h.status}` as MessageKey)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </ScrollRegion>
        </section>
      )}
      {closing && (
        <Dialog title={t('pos.form.close')} isOpen onOpenChange={(open) => { if (!open) setClosing(false); }}
          actions={() => (
            <>
              <Button variant="secondary" onPress={() => setClosing(false)}>{t('common.cancel')}</Button>
              <Button variant="danger" isPending={close.isPending} onPress={() => { setError(null); close.mutate(); }}>{t('pos.form.close.confirm')}</Button>
            </>
          )}>
          <p>{t('pos.form.close.body')}</p>
          <p><strong>{day(date)}</strong></p>
        </Dialog>
      )}
    </div>
  );
}
