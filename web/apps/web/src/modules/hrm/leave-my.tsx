/* eslint-disable jsx-a11y/no-noninteractive-tabindex -- a scrolling table region must be reachable by keyboard (WCAG 2.1.1, R-317) */
// My leave (slice S1.33a; FR-HRM-LVE-04, -06, -10 view, FR-HRM-SSP-01): the balance of each leave type with a "why" that opens the ledger entries behind the number, and the
// person's requests with their status. Buttons follow the rules of S1.32b and only appear when the server would accept them: a draft is finished or cancelled, a waiting
// request is withdrawn (through the approvals), approved leave is cancelled or changed. The answer of the server is shown as it comes.
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { leaveRequests, type LeaveRequest } from '@erp/generated/api/hrm';
import { Button, Dialog, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, TextAreaField, useToast } from '@erp/ui';
import { formatDate, useLocaleSettings, useText } from '@erp/i18n';
import { leaveApi, type BalanceLine } from '../../api/leave';
import { useShell } from '../../shell/context';
import { EntryList, LeaveStatus, TypeLabel, typeNameOf, useLeaveRefusal, useLeaveTypes, useQuantity } from './leave-parts';
import { nameOf } from '../../api/workflow';
import './leave.css';

export default function MyLeaveScreen() {
  const { t } = useText();
  const { can, me } = useShell();
  const { locale } = useLocaleSettings();
  const navigate = useNavigate();
  const refusal = useLeaveRefusal();
  useDocumentTitle(t('leave.title'));
  const [companyChoice, setCompanyChoice] = useState<string | undefined>(undefined);
  const balances = useQuery({ queryKey: ['hrm', 'leave', 'my-balances', companyChoice ?? ''], queryFn: ({ signal }) => leaveApi.myBalances(companyChoice, signal) });
  const [why, setWhy] = useState<BalanceLine | null>(null);
  const company = balances.data?.companyId ?? '';
  const employmentId = balances.data?.employmentId ?? '';
  const types = useLeaveTypes(company);
  const typeById = new Map((types.data ?? []).map((x) => [x.id, x]));
  const unitOf = (typeId: string) => typeById.get(typeId)?.unit;
  const requests = useQuery({
    queryKey: ['hrm', 'leave', 'requests', employmentId],
    queryFn: async () => (await leaveRequests.list({ employmentId, limit: 100, sort: '-startDate' })).items,
    enabled: !!employmentId,
  });
  const qty = useQuantity();

  return (
    <div className="erp-page">
      <PageHeader title={t('leave.title')} description={t('leave.description')}
        actions={can('hrm.leave_requests.create') ? <Button variant="primary" onPress={() => { void navigate('/time/leave/apply'); }}>{t('leave.apply.open')}</Button> : undefined} />
      <div className="erp-stack">
        {balances.isPending && <Skeleton lines={4} />}
        {balances.isError && <ErrorPanel title={t('leave.loadError')} message={refusal(balances.error)} action={<Button onPress={() => { void balances.refetch(); }}>{t('leave.retry')}</Button>} />}
        {balances.data?.needsCompany && (
          <SelectField label={t('leave.company')} help={t('leave.company.help')} options={balances.data.employments.map((e) => ({ id: e.companyId, label: me.companies.find((c) => c.id === e.companyId)?.name[locale] ?? me.companies.find((c) => c.id === e.companyId)?.name.en ?? e.employeeNo, detail: e.employeeNo }))}
            value={companyChoice ?? null} onChange={setCompanyChoice} />)}
        {balances.data && !balances.data.needsCompany && !balances.data.employmentId && <EmptyState title={t('leave.noEmployment')}>{t('leave.noEmployment.body')}</EmptyState>}
        {balances.data && balances.data.employmentId && (
          <section aria-labelledby="erp-leave-balances" className="erp-stack">
            <h2 id="erp-leave-balances">{t('leave.balances')}</h2>
            {balances.data.balances.length === 0 ? <EmptyState title={t('leave.balances.empty')}>{t('leave.balances.empty.body')}</EmptyState> : (
              <ul className="erp-leave-cards">
                {balances.data.balances.map((b) => (
                  <li key={b.leaveTypeId} className="erp-leave-card">
                    <TypeLabel name={nameOf(b.name, locale) || b.code} token={typeById.get(b.leaveTypeId)?.colorToken} />
                    <span className="erp-leave-balance" aria-label={t('leave.balance.aria', { name: nameOf(b.name, locale) || b.code, value: qty(b.balance, b.unit) })}>{qty(b.balance, b.unit)}</span>
                    <span className="erp-muted">{t('leave.balance.year', { year: b.leaveYear })}</span>
                    {b.noPolicy && <NoPolicy keyName={b.noPolicy.message} />}
                    <Button variant="quiet" onPress={() => setWhy(b)}>{t('leave.balance.why')}</Button>
                  </li>))}
              </ul>)}
          </section>)}

        {employmentId && (
          <section aria-labelledby="erp-leave-requests" className="erp-stack">
            <h2 id="erp-leave-requests">{t('leave.requests')}</h2>
            {requests.isPending && <Skeleton lines={4} />}
            {requests.isError && <ErrorPanel title={t('leave.loadError')} message={refusal(requests.error)} action={<Button onPress={() => { void requests.refetch(); }}>{t('leave.retry')}</Button>} />}
            {requests.data && (requests.data.length === 0 ? <EmptyState title={t('leave.requests.empty')}>{t('leave.requests.empty.body')}</EmptyState> : (
              <div className="erp-leave-scroll" role="region" tabIndex={0} aria-label={t('leave.requests')}><table className="erp-leave-table">
                <caption className="erp-visually-hidden">{t('leave.requests')}</caption>
                <thead><tr><th scope="col">{t('leave.col.no')}</th><th scope="col">{t('leave.col.type')}</th><th scope="col">{t('leave.col.dates')}</th><th scope="col">{t('leave.col.amount')}</th><th scope="col">{t('leave.col.status')}</th><th scope="col">{t('leave.col.actions')}</th></tr></thead>
                <tbody>{requests.data.map((r) => <RequestRow key={r.id} request={r} typeName={typeNameOf(typeById.get(r.leaveTypeId), locale)} token={typeById.get(r.leaveTypeId)?.colorToken} unit={unitOf(r.leaveTypeId)} />)}</tbody>
              </table></div>))}
          </section>)}
      </div>
      {why && (
        <Dialog title={t('leave.why.title', { name: nameOf(why.name, locale) || why.code })} isOpen onOpenChange={(open) => { if (!open) setWhy(null); }} actions={(close) => <Button variant="primary" onPress={close}>{t('leave.close')}</Button>}>
          <p className="erp-muted">{t('leave.why.body', { year: why.leaveYear })}</p>
          <EntryList entries={why.entries} unit={why.unit} />
        </Dialog>)}
    </div>
  );
}

function NoPolicy({ keyName }: { keyName: string }) {
  const { server, t } = useText();
  return <span className="erp-muted">{server(keyName) ?? t('leave.noPolicy')}</span>;
}

/** The buttons a request offers, from its status (S1.32b): nothing the server would refuse is shown. */
export function RequestActions({ request, onChanged }: { request: Pick<LeaveRequest, 'id' | 'status' | 'workflowInstanceId' | 'rowVersion'>; onChanged?: () => void }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useLeaveRefusal();
  const [cancelOpen, setCancelOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const done = async (message: string) => {
    toast.success(message);
    await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave'] });
    await queryClient.invalidateQueries({ queryKey: ['wf'] });
    onChanged?.();
  };
  const cancel = useMutation({
    mutationFn: async () => { const fresh = await leaveApi.details(request.id); return leaveApi.cancel(request.id, reason, fresh.rowVersion); },
    onSuccess: async (answer) => { setCancelOpen(false); setReason(''); await done(t(answer.status === 'cancel_requested' ? 'leave.cancel.asked' : 'leave.cancel.done')); },
    onError: (e) => setError(refusal(e)),
  });
  const withdraw = useMutation({
    mutationFn: () => leaveApi.withdraw(request.workflowInstanceId as string, t('leave.withdraw.reason')),
    onSuccess: async () => { await done(t('leave.withdraw.done')); },
    onError: (e) => toast.error(refusal(e)),
  });
  const { status } = request;
  const canCancel = (status === 'draft' || status === 'approved') && can('hrm.leave_requests.cancel');
  const canWithdraw = status === 'submitted' && !!request.workflowInstanceId && can('hrm.leave_requests.update');
  const canChange = status === 'approved' && can('hrm.leave_requests.update');
  const canFinish = status === 'draft' && can('hrm.leave_requests.update');
  return (
    <span className="erp-row erp-wrap">
      {canFinish && <Button variant="quiet" isDisabled={!writable} onPress={() => { void navigate(`/time/leave/apply?draft=${request.id}`); }}>{t('leave.action.finish')}</Button>}
      {canChange && <Button variant="quiet" isDisabled={!writable} onPress={() => { void navigate(`/time/leave/apply?change=${request.id}`); }}>{t('leave.action.change')}</Button>}
      {canWithdraw && <Button variant="quiet" isDisabled={!writable} isPending={withdraw.isPending} onPress={() => withdraw.mutate()}>{t('leave.action.withdraw')}</Button>}
      {canCancel && <Button variant="quiet" isDisabled={!writable} onPress={() => { setError(null); setCancelOpen(true); }}>{t('leave.action.cancel')}</Button>}
      {cancelOpen && (
        <Dialog title={t('leave.cancel.title')} isOpen onOpenChange={setCancelOpen}
          actions={(close) => <>
            <Button variant="quiet" onPress={close}>{t('leave.cancel.keep')}</Button>
            <Button variant="danger" isPending={cancel.isPending} isDisabled={reason.trim() === ''} onPress={() => cancel.mutate()}>{t('leave.cancel.confirm')}</Button>
          </>}>
          <p>{t(status === 'approved' ? 'leave.cancel.approvedBody' : 'leave.cancel.draftBody')}</p>
          <TextAreaField label={t('leave.cancel.reason')} isRequired value={reason} onChange={setReason} maxLength={500} />
          {error && <p role="alert" className="erp-field-error">{error}</p>}
        </Dialog>)}
    </span>
  );
}

function RequestRow({ request, typeName, token, unit }: { request: LeaveRequest; typeName: string; token: string | null | undefined; unit: string | undefined }) {
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const { t } = useText();
  const qty = useQuantity();
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers);
  return (
    <tr>
      <td><Link href={`/time/leave/requests/${request.id}`}><bdi>{request.requestNo}</bdi></Link></td>
      <td><TypeLabel name={typeName || t('leave.type.unknown')} token={token} /></td>
      <td><bdi>{request.startDate === request.endDate ? day(request.startDate) : t('leave.period', { from: day(request.startDate), to: day(request.endDate) })}</bdi></td>
      <td>{unit === 'hour' && request.hours !== null ? qty(request.hours, 'hour') : qty(request.days, unit)}</td>
      <td><LeaveStatus status={request.status} /></td>
      <td><RequestActions request={request} /></td>
    </tr>
  );
}
