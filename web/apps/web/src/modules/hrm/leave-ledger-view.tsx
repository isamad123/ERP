// The ledger of one employee (slice S1.33b; FR-HRM-LVE-10, -14, FR-XM-05, -06): filters (type, leave year, kind, dates), one section per leave type and year, the post and reverse forms and the
// balance check. The entries of the current leave year come from the balances answer; another year is asked for through a date inside it (no endpoint of its own is needed): the date
// of the year's last entry, so every entry of the year is included.
import { useMemo, useState } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Button, DateField, EmptyState, ErrorPanel, SelectField, Skeleton } from '@erp/ui';
import { digitsOf, formatDate, useLocaleSettings } from '@erp/i18n';
import type { LedgerEntry } from '../../api/leave';
import { leaveLedgerApi } from '../../api/leave-ledger';
import { useShell } from '../../shell/context';
import { typeNameOf, useLeaveRefusal, useLeaveTypes } from './leave-parts';
import { LineSection } from './leave-ledger-table';
import { PostDialog, ReverseDialog, type PostPrefill } from './leave-ledger-forms';
import { RecomputePanel } from './leave-ledger-recompute';
import { useLedgerText } from './leave-ledger-messages';

const KINDS = ['opening', 'accrual', 'grant', 'usage', 'usage_reversal', 'adjustment', 'carry_forward', 'lapse', 'encashment', 'comp_off_earned', 'comp_off_expired', 'transfer_in', 'transfer_out'] as const;
const CURRENT = 'current';
const ALL = 'all';

interface Target { key: string; typeId: string | undefined; asOf: string | undefined; year: number | null }

export function LedgerView({ employmentId, companyId, employeeNo }: { employmentId: string; companyId: string; employeeNo: string }) {
  const { t } = useLedgerText();
  const { can, writable } = useShell();
  const { locale, numbers, timeZone } = useLocaleSettings();
  const refusal = useLeaveRefusal();
  const types = useLeaveTypes(companyId);
  const [year, setYear] = useState(CURRENT);
  const [typeId, setTypeId] = useState(ALL);
  const [kind, setKind] = useState(ALL);
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [reversing, setReversing] = useState<{ entry: LedgerEntry; label: string } | null>(null);
  const [posting, setPosting] = useState<PostPrefill | null>(null);

  const index = useQuery({ queryKey: ['hrm', 'leave', 'ledger-index', employmentId], queryFn: () => leaveLedgerApi.allEntries(employmentId) });
  const typeFilter = typeId === ALL ? undefined : typeId;
  const targets = useMemo((): Target[] => {
    if (year === CURRENT) return [{ key: `current:${typeFilter ?? ''}`, typeId: typeFilter, asOf: undefined, year: null }];
    const wanted = Number(year);
    const last = new Map<string, string>();
    for (const e of index.data ?? []) {
      if (e.leaveYear !== wanted || (typeFilter && e.leaveTypeId !== typeFilter)) continue;
      if ((last.get(e.leaveTypeId) ?? '') < e.entryDate) last.set(e.leaveTypeId, e.entryDate);
    }
    return [...last.entries()].map(([id, asOf]) => ({ key: `${wanted}:${id}`, typeId: id, asOf, year: wanted }));
  }, [year, typeFilter, index.data]);
  const answers = useQueries({
    queries: targets.map((tg) => ({
      queryKey: ['hrm', 'leave', 'ledger-balances', employmentId, tg.key, tg.asOf ?? ''],
      queryFn: ({ signal }: { signal: AbortSignal }) => leaveLedgerApi.balances(employmentId, tg.asOf, tg.typeId, signal),
    })),
  });
  const typeById = new Map((types.data ?? []).map((x) => [x.id, x]));
  const lines = answers.flatMap((a, i) => (a.data?.balances ?? []).filter((l) => targets[i]?.year === null || (l.leaveYear === targets[i]?.year && l.leaveTypeId === targets[i]?.typeId)))
    .sort((a, b) => a.code.localeCompare(b.code) || a.leaveYear - b.leaveYear);
  const waiting = answers.some((a) => a.isPending) || (year !== CURRENT && index.isPending);
  const failed = answers.find((a) => a.isError)?.error ?? (year !== CURRENT ? index.error : null);
  const years = [...new Set((index.data ?? []).map((e) => e.leaveYear))].sort((a, b) => b - a);
  const filter = { kind: kind === ALL ? '' : kind, from: from?.toString() ?? '', to: to?.toString() ?? '' };
  const canReverse = can('hrm.leave_ledger.post') && writable;
  const asOf = answers.find((a) => a.data)?.data?.asOf;

  return (
    <div className="erp-stack">
      <div className="erp-row erp-wrap">
        {can('hrm.leave_ledger.post') && <Button variant="primary" isDisabled={!writable} onPress={() => setPosting({})}>{t('lled.post.open')}</Button>}
      </div>
      <form className="erp-row erp-wrap" role="search" aria-label={t('lled.filters')} onSubmit={(e) => e.preventDefault()}>
        <SelectField label={t('lled.filter.type')} options={[{ id: ALL, label: t('lled.filter.all') }, ...(types.data ?? []).map((x) => ({ id: x.id, label: typeNameOf(x, locale) || x.code, detail: x.code }))]} value={typeId} onChange={setTypeId} />
        <SelectField label={t('lled.filter.year')} options={[{ id: CURRENT, label: t('lled.filter.currentYear') }, ...years.map((y) => ({ id: String(y), label: digitsOf(String(y), numbers.digits) }))]} value={year} onChange={setYear} />
        <SelectField label={t('lled.filter.kind')} options={[{ id: ALL, label: t('lled.filter.all') }, ...KINDS.map((k) => ({ id: k, label: t(`lled.kind.${k}`) }))]} value={kind} onChange={setKind} />
        <DateField label={t('lled.filter.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('lled.filter.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
      </form>
      {year === CURRENT && asOf && <p className="erp-muted" role="status">{t('lled.asOf', { date: formatDate(`${asOf}T12:00:00Z`, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) })}</p>}
      {waiting && <Skeleton lines={5} />}
      {failed && (
        <ErrorPanel title={t('lled.loadError')} message={refusal(failed)}
          action={<Button variant="primary" onPress={() => { void index.refetch(); answers.forEach((a) => { void a.refetch(); }); }}>{t('lled.retry')}</Button>} />)}
      {!waiting && !failed && lines.length === 0 && <EmptyState title={t('lled.empty')}>{t('lled.empty.body', { employee: employeeNo })}</EmptyState>}
      {!failed && lines.map((line) => {
        const type = typeById.get(line.leaveTypeId);
        const name = typeNameOf(type, locale) || line.code;
        return <LineSection key={`${line.leaveTypeId}-${line.leaveYear}`} line={line} name={name} token={type?.colorToken} filter={filter} canReverse={canReverse} onReverse={(entry) => setReversing({ entry, label: name })} />;
      })}
      {can('hrm.leave_ledger.recompute') && <RecomputePanel employmentId={employmentId} types={types.data ?? []} onAdjust={setPosting} />}
      {reversing && <ReverseDialog entry={reversing.entry} label={reversing.label} onClose={() => setReversing(null)} />}
      {posting && <PostDialog employmentId={employmentId} types={types.data ?? []} prefill={Object.keys(posting).length > 0 ? posting : undefined} onClose={() => setPosting(null)} />}
    </div>
  );
}
