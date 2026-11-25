// Attendance requests (slice S1.30a Build items 1 to 4; FR-HRM-ATT-11, ATT-14, GHR-09): the requests of the signed-in person, of the people they may see (team,
// company) and the two forms. A request waits for the approval of the line manager (or HR) in the shared approvals inbox; its maker can withdraw it while it waits.
// A request made by someone else for a person says so. Proof files (S1.30b) are listed under a request and can be deleted while it waits.
import { Fragment, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { dutyRequests, regularizationRequests, type DutyRequest, type RegularizationRequest } from '@erp/generated/api/hrm';
import { Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, Tabs, useToast } from '@erp/ui';
import { formatDate, formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { attendanceRequestsApi } from '../../api/attendance-requests';
import { useShell } from '../../shell/context';
import { CorrectionDialog, DutyDialog, useRefusal } from './requests-forms';
import { ProofList } from './proof-files';
import { PROOF_OWNER } from '../../api/attendance-requests';
import './requests-messages';

type Status = 'submitted' | 'approved' | 'rejected' | 'cancelled';
const CHIP: Record<Status, 'pending' | 'approved' | 'rejected' | 'inactive'> = { submitted: 'pending', approved: 'approved', rejected: 'rejected', cancelled: 'inactive' };

export default function RequestsScreen() {
  const { t } = useText();
  const { can, me, activeCompany } = useShell();
  useDocumentTitle(t('req.title'));
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.companies[0]?.id ?? '');
  const seeCorrections = can('hrm.regularization_requests.read');
  const seeDuty = can('hrm.duty_requests.read');
  const [tab, setTab] = useState(seeCorrections ? 'corrections' : 'duty');
  if (!seeCorrections && !seeDuty) return <div className="erp-page"><PageHeader title={t('req.title')} /><EmptyState title={t('req.denied')} /></div>;
  const options = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code }));
  return (
    <div className="erp-page">
      <PageHeader title={t('req.title')} description={t('req.description')} />
      <div className="erp-stack">
        {options.length > 1 && <SelectField label={t('req.company')} options={options} value={company} onChange={setCompany} />}
        <Tabs label={t('req.tab.label')} selectedKey={tab} onSelectionChange={setTab} tabs={[
          ...(seeCorrections ? [{ id: 'corrections', title: t('req.tab.corrections'), content: <Corrections company={company} /> }] : []),
          ...(seeDuty ? [{ id: 'duty', title: t('req.tab.duty'), content: <Duty company={company} /> }] : []),
        ]} />
      </div>
    </div>
  );
}

function useWithdraw(): (instanceId: string | null) => void {
  const { t } = useText();
  const toast = useToast();
  const refusal = useRefusal();
  const queryClient = useQueryClient();
  const withdraw = useMutation({
    mutationFn: (instanceId: string) => attendanceRequestsApi.withdraw(instanceId, t('req.withdraw.reason')),
    onSuccess: async () => { toast.success(t('req.withdrawn')); await queryClient.invalidateQueries({ queryKey: ['hrm', 'attendance-requests'] }); },
    onError: (e) => toast.error(refusal(e)),
  });
  return (instanceId) => { if (instanceId) withdraw.mutate(instanceId); };
}

function Chip({ status }: { status: Status }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status]} label={t(`req.status.${status}` as MessageKey)} />;
}

function Corrections({ company }: { company: string }) {
  const { t } = useText();
  const { writable, can, me } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const refusal = useRefusal();
  const withdraw = useWithdraw();
  const [open, setOpen] = useState(false);
  const [filesOf, setFilesOf] = useState<string | null>(null);
  const query = useQuery({ queryKey: ['hrm', 'attendance-requests', 'regularization', company], queryFn: () => regularizationRequests.list({ companyId: company, limit: 100 }), enabled: !!company });
  const clock = (v: string | null) => (v ? formatDateTime(v, lang, timeZone, numbers) : '');
  return (
    <div className="erp-stack">
      {can('hrm.regularization_requests.create') && <div><Button variant="primary" isDisabled={!writable} onPress={() => setOpen(true)}>{t('req.new.correction')}</Button></div>}
      {query.isPending && <Skeleton />}
      {query.isError && <ErrorPanel title={t('req.loadError')} message={refusal(query.error)} />}
      {query.data && (query.data.items.length === 0 ? <EmptyState title={t('req.empty')}>{t('req.empty.body')}</EmptyState> : (
        <table className="erp-pos-table">
          <thead><tr><th scope="col">{t('req.col.day')}</th><th scope="col">{t('req.col.times')}</th><th scope="col">{t('req.col.reason')}</th><th scope="col">{t('req.col.by')}</th><th scope="col">{t('req.col.status')}</th><th scope="col">{t('req.col.actions')}</th></tr></thead>
          <tbody>{query.data.items.map((r: RegularizationRequest) => (
            <Fragment key={r.id}>
            <tr>
              <td><bdi>{formatDate(`${r.workDate}T12:00:00Z`, lang, timeZone, numbers)}</bdi></td>
              <td><bdi>{[r.requestedIn ? t('req.times.in', { time: clock(r.requestedIn) }) : '', r.requestedOut ? t('req.times.out', { time: clock(r.requestedOut) }) : ''].filter(Boolean).join(' · ')}</bdi></td>
              <td><bdi>{r.reason}</bdi></td>
              <td>{r.createdBy === me.userId ? t('req.by.me') : t('req.by.other')}</td>
              <td><Chip status={r.status} /></td>
              <td>
                <Button variant="quiet" aria-expanded={filesOf === r.id} onPress={() => setFilesOf(filesOf === r.id ? null : r.id)}>{filesOf === r.id ? t('req.proof.hide') : t('req.proof.files')}</Button>
                {r.status === 'submitted' && r.createdBy === me.userId && <Button variant="quiet" isDisabled={!writable} onPress={() => withdraw(r.workflowInstanceId)}>{t('req.withdraw')}</Button>}
              </td>
            </tr>
            {filesOf === r.id && <tr><td colSpan={6}><ProofList owner={PROOF_OWNER.correction} ownerId={r.id} canDelete={r.status === 'submitted' && writable} /></td></tr>}
            </Fragment>))}</tbody>
        </table>
      ))}
      {open && <CorrectionDialog company={company} isOpen={open} onOpenChange={setOpen} />}
    </div>
  );
}

function Duty({ company }: { company: string }) {
  const { t } = useText();
  const { writable, can, me } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const refusal = useRefusal();
  const withdraw = useWithdraw();
  const [open, setOpen] = useState(false);
  const [filesOf, setFilesOf] = useState<string | null>(null);
  const query = useQuery({ queryKey: ['hrm', 'attendance-requests', 'duty', company], queryFn: () => dutyRequests.list({ companyId: company, limit: 100 }), enabled: !!company });
  return (
    <div className="erp-stack">
      {can('hrm.duty_requests.create') && <div><Button variant="primary" isDisabled={!writable} onPress={() => setOpen(true)}>{t('req.new.duty')}</Button></div>}
      {query.isPending && <Skeleton />}
      {query.isError && <ErrorPanel title={t('req.loadError')} message={refusal(query.error)} />}
      {query.data && (query.data.items.length === 0 ? <EmptyState title={t('req.empty')}>{t('req.empty.body')}</EmptyState> : (
        <table className="erp-pos-table">
          <thead><tr><th scope="col">{t('req.col.kind')}</th><th scope="col">{t('req.col.period')}</th><th scope="col">{t('req.col.purpose')}</th><th scope="col">{t('req.col.by')}</th><th scope="col">{t('req.col.status')}</th><th scope="col">{t('req.col.actions')}</th></tr></thead>
          <tbody>{query.data.items.map((r: DutyRequest) => (
            <Fragment key={r.id}>
            <tr>
              <td>{t(`req.kind.${r.kind}` as MessageKey)}</td>
              <td><bdi>{t('req.period', { from: formatDateTime(r.startAt, lang, timeZone, numbers), to: formatDateTime(r.endAt, lang, timeZone, numbers) })}</bdi></td>
              <td><bdi>{r.purpose}</bdi></td>
              <td>{r.createdBy === me.userId ? t('req.by.me') : t('req.by.other')}</td>
              <td><Chip status={r.status} /></td>
              <td>
                <Button variant="quiet" aria-expanded={filesOf === r.id} onPress={() => setFilesOf(filesOf === r.id ? null : r.id)}>{filesOf === r.id ? t('req.proof.hide') : t('req.proof.files')}</Button>
                {r.status === 'submitted' && r.createdBy === me.userId && <Button variant="quiet" isDisabled={!writable} onPress={() => withdraw(r.workflowInstanceId)}>{t('req.withdraw')}</Button>}
              </td>
            </tr>
            {filesOf === r.id && <tr><td colSpan={6}><ProofList owner={PROOF_OWNER.duty} ownerId={r.id} canDelete={r.status === 'submitted' && writable} /></td></tr>}
            </Fragment>))}</tbody>
        </table>
      ))}
      {open && <DutyDialog company={company} isOpen={open} onOpenChange={setOpen} />}
    </div>
  );
}
