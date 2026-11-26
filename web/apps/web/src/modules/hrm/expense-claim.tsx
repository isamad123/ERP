// The expense claim page (slice S1.42; FR-HRM-EXP-02, EXP-03, EXP-05, GHR-06, FR-XM-32): `/expenses/new` and `/expenses/claims/:id`, the link target of the approvals inbox. A draft is edited
// (header, lines, receipts) and saved, submitted or cancelled; a returned draft says so; a submitted claim shows its status and can be withdrawn; an approved or paid claim is read-only with
// its payment. Amounts the person may not see are shown as "hidden", never as zero. The server checks everything again; its words are shown as they come.
import { useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, TextField, useToast } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { expensesApi, type ClaimView, type NewClaimContext, type ReimburseVia } from '../../api/expenses';
import { ApiProblem } from '../../api/client';
import { useShell } from '../../shell/context';
import { FIELD_OF, checkLines, claimBody, draftOf, emptyLine, serverProblems, type LineDraft, type LineErrors } from './expense-model';
import { LinesEditor, LinesTable } from './expense-lines';
import { ClaimStatusChip, Money, PaymentChip, useCategories, useExpenseRefusal } from './expense-parts';
import { ReceiptsPanel, useReceipts } from './expense-receipts';
import './expenses.css';
import './expenses-messages';

export const EXPENSES_PATH = '/expenses';

const contextKey = (employmentId?: string) => ['hrm', 'expenses', 'context', employmentId ?? ''];

function useBack() {
  const { t } = useText();
  const navigate = useNavigate();
  return <Button variant="quiet" onPress={() => { void navigate(EXPENSES_PATH); }}>{t('expc.back')}</Button>;
}

/** `/expenses/new`: a claim for the signed-in person's own employment (or `?employment=<id>` for someone in scope). */
export function NewClaimPage() {
  const { t } = useText();
  const { can } = useShell();
  const [params] = useSearchParams();
  const employmentId = params.get('employment') ?? undefined;
  const refusal = useExpenseRefusal();
  const back = useBack();
  useDocumentTitle(t('expc.new.title'));
  const context = useQuery({ queryKey: contextKey(employmentId), queryFn: ({ signal }) => expensesApi.context(employmentId, signal), enabled: can('hrm.expense_claims.create') });
  const heading = <PageHeader title={t('expc.new.title')} description={t('expc.new.description')} actions={back} />;
  if (!can('hrm.expense_claims.create')) return <div className="erp-page">{heading}<EmptyState title={t('expc.denied')}>{t('expc.denied.body')}</EmptyState></div>;
  if (context.isPending) return <div className="erp-page">{heading}<Skeleton lines={6} /></div>;
  if (context.isError) return <div className="erp-page">{heading}<ErrorPanel title={t('expc.loadError')} message={refusal(context.error)} action={<Button onPress={() => { void context.refetch(); }}>{t('expc.retry')}</Button>} /></div>;
  if (!context.data.employmentId) return <div className="erp-page">{heading}<EmptyState title={t('expc.noEmployment')}>{t('expc.noEmployment.body')}</EmptyState></div>;
  return <div className="erp-page">{heading}<ClaimEditor context={context.data} view={null} /></div>;
}

/** `/expenses/claims/:id`: a draft is edited, anything else is read. */
export function ClaimPage() {
  const { id = '' } = useParams();
  const { t } = useText();
  const refusal = useExpenseRefusal();
  const location = useLocation();
  const notice = (location.state as { notice?: string } | null)?.notice;
  const back = useBack();
  const view = useQuery({ queryKey: ['hrm', 'expenses', 'claim', id], queryFn: ({ signal }) => expensesApi.detail(id, signal), enabled: !!id });
  const context = useQuery({ queryKey: contextKey(view.data?.employmentId), queryFn: ({ signal }) => expensesApi.context(view.data?.employmentId, signal), enabled: !!view.data && view.data.canEdit && view.data.status === 'draft' });
  useDocumentTitle(view.data ? t('expc.claim.title', { no: view.data.claimNo }) : t('expc.claim.titleLoading'));
  const heading = (title: string) => <PageHeader title={title} description={t('expc.claim.description')} actions={back} />;
  if (view.isPending) return <div className="erp-page">{heading(t('expc.claim.titleLoading'))}<Skeleton lines={8} /></div>;
  if (view.isError) {
    const status = view.error instanceof ApiProblem ? view.error.status : 0;
    return (
      <div className="erp-page">{heading(t('expc.claim.titleLoading'))}
        {status === 404 ? <EmptyState title={t('expc.gone')} action={back}>{t('expc.gone.body')}</EmptyState>
          : status === 403 ? <EmptyState title={t('expc.denied')}>{t('expc.denied.body')}</EmptyState>
            : <ErrorPanel title={t('expc.loadError')} message={refusal(view.error)} action={<Button onPress={() => { void view.refetch(); }}>{t('expc.retry')}</Button>} />}
      </div>);
  }
  const v = view.data;
  const editing = v.canEdit && v.status === 'draft';
  return (
    <div className="erp-page">
      {heading(t('expc.claim.title', { no: v.claimNo }))}
      <div className="erp-stack">
        {notice && <Banner tone="warning" title={t('expc.submitFailed')}>{notice}</Banner>}
        {editing && context.isPending && <Skeleton lines={6} />}
        {editing && context.isError && <ErrorPanel title={t('expc.loadError')} message={refusal(context.error)} action={<Button onPress={() => { void context.refetch(); }}>{t('expc.retry')}</Button>} />}
        {editing && context.data && <ClaimEditor context={context.data} view={v} />}
        {!editing && <ClaimReadOnly view={v} />}
      </div>
    </div>
  );
}

function useInvalidate() {
  const queryClient = useQueryClient();
  return async () => {
    await queryClient.invalidateQueries({ queryKey: ['hrm', 'expenses'] });
    await queryClient.invalidateQueries({ queryKey: ['engine', 'hrm.expense_claims'] });
    await queryClient.invalidateQueries({ queryKey: ['wf'] });
  };
}

function ClaimEditor({ context, view }: { context: NewClaimContext; view: ClaimView | null }) {
  const { t, server } = useText();
  const { writable } = useShell();
  const navigate = useNavigate();
  const toast = useToast();
  const refusal = useExpenseRefusal();
  const invalidate = useInvalidate();
  const currency = view?.currencyCode ?? context.currencyCode ?? '';
  const [title, setTitle] = useState(view?.title ?? '');
  const [via, setVia] = useState<ReimburseVia>(view?.reimburseVia ?? 'payroll');
  const [lines, setLines] = useState<LineDraft[]>(() => (view ? view.lines.map(draftOf) : [emptyLine(currency, context.today)]));
  const [tried, setTried] = useState(false);
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [cancelOpen, setCancelOpen] = useState(false);
  const categories = useCategories(context.companyId);
  const receipts = useReceipts(view?.id ?? null);
  const categoryMap = new Map((categories.data ?? []).map((c) => [c.id, c]));
  const first = useRef(true);

  // After a save the server's lines (new ids, flags, converted amounts) replace the form's rows.
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    if (view) setLines(view.lines.map(draftOf));
  }, [view?.rowVersion]); // eslint-disable-line react-hooks/exhaustive-deps

  const local = tried ? checkLines(lines, categoryMap) : new Map<string, LineErrors>();
  const served = problem ? serverProblems(problem.fieldErrors) : null;
  const errors = new Map<string, Partial<Record<keyof LineErrors | 'receipt' | 'line', string>>>();
  for (const [key, e] of local) errors.set(key, Object.fromEntries(Object.entries(e).map(([k, v]) => [k, t(v as MessageKey)])));
  served?.byLine.forEach((members, index) => {
    const row = lines[index];
    if (!row) return;
    const merged = { ...(errors.get(row.key) ?? {}) } as Record<string, string>;
    for (const [member, key] of Object.entries(members)) merged[FIELD_OF[member] ?? 'line'] = server(key) ?? t('expc.error.generic');
    errors.set(row.key, merged);
  });
  const generalErrors = served ? served.general.map((k) => server(k) ?? t('expc.error.generic')) : [];

  const header = { title, reimburseVia: via, ...(view || !context.onBehalf || !context.employmentId ? {} : { employmentId: context.employmentId }) };
  const save = async (): Promise<ClaimView> => {
    const body = claimBody(header, lines, categoryMap, view ? { rowVersion: view.rowVersion } : {});
    return (view ? await expensesApi.update(view.id, body) : await expensesApi.create(body)) as ClaimView;
  };
  const fail = (e: unknown) => {
    setProblem(e instanceof ApiProblem && Object.keys(e.fieldErrors).length > 0 ? e : null);
    setMessage(refusal(e));
  };
  const saveDraft = useMutation({
    mutationFn: save,
    onSuccess: async (saved) => {
      setProblem(null); setMessage(null);
      toast.success(t('expc.saved'));
      await invalidate();
      if (!view) void navigate(`${EXPENSES_PATH}/claims/${saved.id}`, { replace: true });
    },
    onError: fail,
  });
  const submit = useMutation({
    mutationFn: async () => {
      const saved = await save();
      try {
        return await expensesApi.submit(saved.id, saved.rowVersion);
      } catch (e) {
        throw Object.assign(e instanceof Error ? e : new Error('submit'), { savedId: saved.id });
      }
    },
    onSuccess: async (done) => {
      setProblem(null); setMessage(null);
      toast.success(done.flagged > 0 ? t('expc.submitted.flagged', { count: done.flagged }) : t('expc.submitted'));
      await invalidate();
      void navigate(`${EXPENSES_PATH}/claims/${done.id}`, { replace: true });
    },
    onError: async (e) => {
      const savedId = (e as { savedId?: string }).savedId;
      await invalidate();
      // The draft is saved even when the submit is refused: show it (with the flags the server worked out) and say why.
      if (savedId && !view) void navigate(`${EXPENSES_PATH}/claims/${savedId}`, { replace: true, state: { notice: refusal(e) } });
      else fail(e);
    },
  });
  const cancel = useMutation({
    mutationFn: () => expensesApi.cancel((view as ClaimView).id, (view as ClaimView).rowVersion),
    onSuccess: async () => { setCancelOpen(false); toast.success(t('expc.cancelled')); await invalidate(); void navigate(EXPENSES_PATH); },
    onError: (e) => { setCancelOpen(false); fail(e); },
  });

  const ready = lines.length > 0 && checkLines(lines, categoryMap).size === 0;
  const trySubmit = () => {
    setTried(true);
    if (!ready) { setMessage(t('expc.fix')); return; }
    setMessage(null);
    submit.mutate();
  };
  const blocked = view?.lines.filter((l) => l.blocksSubmit) ?? [];

  return (
    <div className="erp-stack">
      {view?.returned && <Banner tone="warning" title={t('expc.returned.title')}>{t('expc.returned.body')}</Banner>}
      {(message || generalErrors.length > 0) && (
        <Banner tone="danger" title={message ?? t('expc.error.generic')}>
          {generalErrors.length > 0 ? <ul>{generalErrors.map((g) => <li key={g}>{g}</li>)}</ul> : null}
        </Banner>)}
      {blocked.length > 0 && <Banner tone="warning" title={t('expc.blocked.title')}>{t('expc.blocked.body', { count: blocked.length })}</Banner>}
      <div className="erp-exp-head">
        {view ? <dl className="erp-facts"><dt>{t('expc.facts.claimNo')}</dt><dd><bdi>{view.claimNo}</bdi></dd><dt>{t('expc.facts.status')}</dt><dd><ClaimStatusChip status={view.status} /></dd></dl> : null}
        <p><strong>{t('expc.facts.employee')}:</strong> <bdi>{context.employeeNo ?? view?.employeeNo ?? ''}</bdi>{context.onBehalf && <> {t('expc.onBehalf')}</>}</p>
        <TextField label={t('expc.field.title')} help={t('expc.field.title.help')} maxLength={120} value={title} onChange={setTitle} />
        <SelectField label={t('expc.field.via')} isRequired value={via} onChange={(v) => setVia(v as ReimburseVia)}
          options={[{ id: 'payroll', label: t('expc.via.payroll') }, { id: 'payment_batch', label: t('expc.via.payment_batch') }]} help={t(`expc.via.${via}.help` as MessageKey)} />
        <p className="erp-small erp-muted">{t('expc.field.currency', { currency })}</p>
      </div>
      {categories.isPending && <Skeleton lines={2} />}
      {categories.isError && <ErrorPanel title={t('expc.categories.error')} message={refusal(categories.error)} action={<Button onPress={() => { void categories.refetch(); }}>{t('expc.retry')}</Button>} />}
      {categories.data && categories.data.length === 0 && <EmptyState title={t('expc.categories.empty')}>{t('expc.categories.empty.body')}</EmptyState>}
      {categories.data && categories.data.length > 0 && (
        <LinesEditor lines={lines} onChange={setLines} categories={categories.data.filter((c) => c.isActive)} claimCurrency={currency} today={context.today}
          saved={new Map((view?.lines ?? []).map((l) => [l.id, l]))} receipts={receipts.data ?? []} errors={errors} />)}
      <ReceiptsPanel claimId={view?.id ?? null} canAdd={!!view} />
      <div className="erp-row erp-wrap">
        <Button variant="secondary" isDisabled={!writable || lines.length === 0} isPending={saveDraft.isPending} onPress={() => { setTried(false); saveDraft.mutate(); }}>{t('expc.save')}</Button>
        <Button variant="primary" isDisabled={!writable || lines.length === 0} isPending={submit.isPending} onPress={trySubmit}>{t('expc.submit')}</Button>
        {view ? <Button variant="danger" isDisabled={!writable} onPress={() => setCancelOpen(true)}>{t('expc.cancel')}</Button>
          : <Button variant="quiet" onPress={() => { void navigate(EXPENSES_PATH); }}>{t('expc.discard')}</Button>}
      </div>
      {cancelOpen && (
        <Dialog title={t('expc.cancel.title')} isOpen onOpenChange={setCancelOpen}
          actions={(close) => <>
            <Button variant="quiet" onPress={close}>{t('expc.cancel.keep')}</Button>
            <Button variant="danger" isPending={cancel.isPending} onPress={() => cancel.mutate()}>{t('expc.cancel.confirm')}</Button>
          </>}>
          <p>{t('expc.cancel.body')}</p>
        </Dialog>)}
    </div>
  );
}

/** A claim that is waiting, decided, paid or cancelled: facts, totals, lines, receipts, and the withdraw action while it waits. */
export function ClaimReadOnly({ view }: { view: ClaimView }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const toast = useToast();
  const refusal = useExpenseRefusal();
  const invalidate = useInvalidate();
  const withdraw = useMutation({
    mutationFn: () => expensesApi.withdraw(view.instanceId as string, t('expc.withdraw.reason')),
    onSuccess: async () => { toast.success(t('expc.withdrawn')); await invalidate(); },
    onError: (e) => toast.error(refusal(e)),
  });
  const showDecision = (view.status !== 'draft' && view.status !== 'submitted') || view.lines.some((l) => l.decidedAt !== null);
  const when = (v: string | null) => (v ? formatDateTime(v, lang, timeZone, numbers) : t('expc.facts.none'));
  return (
    <div className="erp-stack">
      <div className="erp-row erp-wrap">
        <ClaimStatusChip status={view.status} />
        {view.status === 'submitted' && !!view.instanceId && can('hrm.expense_claims.create') && (
          <Button variant="quiet" isDisabled={!writable} isPending={withdraw.isPending} onPress={() => withdraw.mutate()}>{t('expc.withdraw')}</Button>)}
        {view.status === 'submitted' && view.instanceId && <Link href="/approvals">{t('expc.history')}</Link>}
      </div>
      {view.status === 'submitted' && <p className="erp-muted" role="status">{t(view.needsFinance ? 'expc.waiting.finance' : 'expc.waiting')}</p>}
      <dl className="erp-facts">
        <dt>{t('expc.facts.employee')}</dt><dd><bdi>{view.employeeNo ?? ''}</bdi></dd>
        {view.title && <><dt>{t('expc.field.title')}</dt><dd><bdi>{view.title}</bdi></dd></>}
        <dt>{t('expc.field.via')}</dt><dd>{t(`expc.via.${view.reimburseVia}` as MessageKey)}</dd>
        <dt>{t('expc.facts.submitted')}</dt><dd>{when(view.submittedAt)}</dd>
        <dt>{t('expc.facts.total')}</dt><dd><Money value={view.totalAmount} currency={view.currencyCode} /></dd>
        {(view.status === 'approved' || view.status === 'reimbursed' || view.approvedTotal !== null) && <><dt>{t('expc.facts.approvedTotal')}</dt><dd><Money value={view.approvedTotal} currency={view.currencyCode} /></dd></>}
        {(view.status === 'approved' || view.status === 'reimbursed') && (
          <>
            <dt>{t('expc.facts.payment')}</dt><dd><PaymentChip status={view.paymentStatus} /></dd>
            {view.paymentReference && <><dt>{t('expc.facts.reference')}</dt><dd><bdi>{view.paymentReference}</bdi></dd></>}
            {view.payrollInput && <><dt>{t('expc.facts.payrollInput')}</dt><dd><bdi>{view.payrollInput}</bdi></dd></>}
            {view.reimbursedAt && <><dt>{t('expc.facts.reimbursed')}</dt><dd>{when(view.reimbursedAt)}</dd></>}
          </>)}
      </dl>
      {view.amountsHidden && <p className="erp-muted" role="note">{t('expc.hidden.note')}</p>}
      <LinesTable claimId={view.id} lines={view.lines} currency={view.currencyCode} showDecision={showDecision} />
      <ReceiptsPanel claimId={view.id} canAdd={false} />
    </div>
  );
}
