// Delegations made by administrators (slice S1.13b Build item 10; FR-ADM-ROL-09, FR-KRN-WFL-05, FR-ADM-NAV-03): who hands their
// approvals to whom, for which days, with the reason; create and revoke. A person delegates their own approvals on the
// inbox (S1.12d); this screen is for administrators acting for someone. The server refuses self-delegation and overlaps.
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Plus } from 'lucide-react';
import { DataGrid, EngineProvider, useDocumentTitle, type EngineApi, type GridColumn } from '@erp/engine';
import { Button, CheckboxField, DateField, EmptyState, ErrorPanel, Link, PageHeader, StatusChip, useToast, type Option } from '@erp/ui';
import { formatDate, useLocaleSettings, useText } from '@erp/i18n';
import { access, type DelegationRow } from '../../api/access';
import { ApiProblem, sendText } from '../../api/client';
import { users } from '../../api/users';
import { systemClock } from '../../session/clock';
import { companyName, useShell } from '../../shell/context';
import { PersonPicker, ReasonDialog } from './users-parts';

const api: EngineApi = { send: sendText };
const delegationsKey = ['iam', 'delegations', 'admin'] as const;

export default function DelegationsScreen() {
  return <EngineProvider api={api}><DelegationList /></EngineProvider>;
}

type State = 'active' | 'revoked' | 'expired';
const stateOf = (r: DelegationRow): State => (r.status === 'revoked' ? 'revoked' : new Date(r.validTo).getTime() < systemClock.now() ? 'expired' : 'active');

function DelegationList() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  useDocumentTitle(t('dlg.title'));
  const [creating, setCreating] = useState(false);
  const [revoking, setRevoking] = useState<DelegationRow | null>(null);
  const list = useQuery({ queryKey: delegationsKey, queryFn: access.delegations, refetchOnMount: 'always' });
  const canReadUsers = can('iam.users.read');
  const people = useQuery({ queryKey: ['iam', 'delegations', 'names'], queryFn: ({ signal }) => users.list({ limit: 200 }, signal), enabled: canReadUsers, staleTime: 60_000 });
  const names = useMemo(() => new Map((people.data?.items ?? []).map((p) => [p.id, p.displayName])), [people.data]);
  const who = (id: string) => names.get(id) ?? '—';
  const lang = locale === 'ur' ? 'ur' : 'en';
  const canCreate = writable && can('iam.delegations.create');
  const canRevoke = writable && can('iam.delegations.update');
  const columns = useMemo<GridColumn<DelegationRow>[]>(() => [
    { id: 'from', header: t('dlg.col.from'), width: 190, cell: (r) => <bdi>{who(r.fromUserId)}</bdi> },
    { id: 'to', header: t('dlg.col.to'), width: 190, cell: (r) => <bdi>{who(r.toUserId)}</bdi> },
    { id: 'first', header: t('dlg.col.first'), width: 150, cell: (r) => formatDate(r.validFrom, lang, timeZone, numbers) },
    { id: 'last', header: t('dlg.col.last'), width: 150, cell: (r) => formatDate(r.validTo, lang, timeZone, numbers) },
    { id: 'status', header: t('dlg.col.status'), width: 130, cell: (r) => { const s = stateOf(r); return <StatusChip status={s === 'active' ? 'active' : s === 'revoked' ? 'blocked' : 'inactive'} label={t(`dlg.status.${s}`)} />; } },
    { id: 'reason', header: t('dlg.col.reason'), width: 220, cell: (r) => <span>{r.reason ?? ''}</span> },
    { id: 'actions', header: t('dlg.col.actions'), width: 220, cell: (r) => (
      <span className="erp-row erp-wrap">
        {canRevoke && stateOf(r) === 'active' && <Button variant="quiet" onPress={() => setRevoking(r)}>{t('dlg.revoke')}</Button>}
        {can('aud.changes.view') && <Link href={`/admin/audit?entityType=iam.delegations&entityId=${r.id}`}>{t('dlg.history')}</Link>}
      </span>
    ) },
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [t, names, lang, timeZone, numbers, canRevoke]);
  const problem = list.error instanceof ApiProblem ? list.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('dlg.title')} description={t('dlg.description')}
        actions={canCreate ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setCreating(true)}>{t('dlg.create')}</Button> : undefined} />
      <DataGrid<DelegationRow> label={t('dlg.title')} columns={columns} rows={list.data ?? []} rowKey={(r) => r.id} rowLabel={(r) => `${who(r.fromUserId)} → ${who(r.toUserId)}`}
        status={list.isPending ? 'loading' : list.isError ? 'error' : 'ready'}
        error={<ErrorPanel message={problem?.status === 403 ? t('dlg.denied') : t('dlg.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />}
        empty={<EmptyState title={t('dlg.empty')} action={canCreate ? <Button variant="primary" onPress={() => setCreating(true)}>{t('dlg.create')}</Button> : undefined}>{t('dlg.empty.body')}</EmptyState>} />
      {creating && <CreateDelegation onClose={() => setCreating(false)} />}
      {revoking && <RevokeDelegation row={revoking} who={who} onClose={() => setRevoking(null)} />}
    </div>
  );
}

function CreateDelegation({ onClose }: { onClose: () => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [from, setFrom] = useState<Option | null>(null);
  const [to, setTo] = useState<Option | null>(null);
  const [first, setFirst] = useState<CalendarDate | null>(null);
  const [last, setLast] = useState<CalendarDate | null>(null);
  const [companies, setCompanies] = useState<Set<string>>(new Set());
  const toggle = (id: string, on: boolean) => setCompanies((s) => { const n = new Set(s); if (on) n.add(id); else n.delete(id); return n; });
  return (
    <ReasonDialog isOpen title={t('dlg.create')} confirmLabel={t('dlg.create')} effect={t('dlg.create.effect')} onClose={onClose} isDisabled={!from || !to || !first || !last}
      onConfirm={async (reason) => {
        if (!from || !to || !first || !last) return;
        await access.createDelegation({ fromUserId: from.id, toUserId: to.id, from: first.toString(), to: last.toString(), reason, ...(companies.size > 0 ? { companyIds: [...companies] } : {}) });
        void queryClient.invalidateQueries({ queryKey: delegationsKey });
        toast.success(t('dlg.created'));
      }}>
      <PersonPicker label={t('dlg.field.from')} help={t('dlg.field.from.help')} isRequired value={from} onChange={setFrom} {...(to ? { exclude: to.id } : {})} />
      <PersonPicker label={t('dlg.field.to')} isRequired value={to} onChange={setTo} {...(from ? { exclude: from.id } : {})} />
      <DateField label={t('dlg.field.first')} isRequired value={first} onChange={(v) => setFirst(v ? parseDate(v.toString()) : null)} />
      <DateField label={t('dlg.field.last')} isRequired value={last} onChange={(v) => setLast(v ? parseDate(v.toString()) : null)} />
      {me.companies.length > 1 && (
        <fieldset className="erp-stack">
          <legend>{t('dlg.field.companies')}</legend>
          <p className="erp-small erp-muted">{t('dlg.field.companies.help')}</p>
          {me.companies.map((c) => <CheckboxField key={c.id} label={companyName(c, locale)} isSelected={companies.has(c.id)} onChange={(on) => toggle(c.id, on)} />)}
        </fieldset>
      )}
    </ReasonDialog>
  );
}

function RevokeDelegation({ row, who, onClose }: { row: DelegationRow; who: (id: string) => string; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  return (
    <ReasonDialog isOpen danger title={t('dlg.revoke.title')} confirmLabel={t('dlg.revoke')} effect={t('dlg.revoke.effect', { from: who(row.fromUserId), to: who(row.toUserId) })} onClose={onClose}
      onConfirm={async (reason) => {
        await access.revokeDelegation(row.id, reason);
        void queryClient.invalidateQueries({ queryKey: delegationsKey });
        toast.success(t('dlg.revoked'));
      }} />
  );
}
