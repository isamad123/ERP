// The approval inbox (slice S1.12d Build items 20 to 22; FR-KRN-WFL-08, FR-HRM-SSP-02 inbox part, FR-KRN-IAM-14
// maker-checker decisions; S1.12 ruling Q15; spec 13 PAT-04 master-detail, STA-06). Two tabs: workflow tasks (all
// document types, with counts, filters, sort, a detail beside the list, decisions, claim and release, bulk approval) and
// "approvals of changes" (pending maker-checker requests in plain words). Only the actions the engine allows for this
// person and task are offered (FR-XM-32: never on one's own request, nothing approved automatically); every result is the
// server's answer, shown once it has come.
import { useMemo, useState, type ReactNode } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { type CalendarDate } from '@internationalized/date';
import { AlarmClock, ArrowUpCircle, MessageCircleQuestion, UserRound } from 'lucide-react';
import { DataGrid, type GridColumn } from '@erp/engine';
import { Button, DateField, Dialog, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, StatusChip, Tabs, TextAreaField, useToast } from '@erp/ui';
import { formatDate, formatDateTime, formatDecimal, formatInteger, isolate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { nameOf, workflow, type BulkResult, type FieldValue, type HistoryEntry, type InboxFilters, type InboxItem, type TaskAction, type TaskDetail } from '../../api/workflow';
import { usePageTitle } from '../../pages/errors';
import { companyName, useShell } from '../../shell/context';
import { ChangeApprovals } from './approvals-changes';
import './approvals.css';

export const APPROVALS_PATH = '/approvals';

export default function ApprovalsScreen() {
  return (
    <Routes>
      <Route index element={<Approvals tab="tasks" />} />
      <Route path="tasks/:taskId" element={<Approvals tab="tasks" />} />
      <Route path="changes" element={<Approvals tab="changes" />} />
    </Routes>
  );
}

function Approvals({ tab }: { tab: 'tasks' | 'changes' }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const navigate = useNavigate();
  const { can } = useShell();
  usePageTitle(t('approvals.title'));
  const counts = useQuery({ queryKey: ['wf', 'counts'], queryFn: workflow.counts, refetchOnMount: 'always' });
  const tasks = counts.data?.total ?? 0;
  const changeCount = counts.data?.changeApprovals ?? 0;
  const showChanges = can('iam.maker_checker.approve') || can('iam.maker_checker_requests.read');
  const title = (key: MessageKey, count: number) => (count > 0 ? t('approvals.tab.count', { label: t(key), count: formatInteger(count, numbers) }) : t(key));
  return (
    <div className="erp-page erp-approvals">
      <PageHeader title={t('approvals.title')} description={t('approvals.description')} />
      {showChanges ? (
        <Tabs label={t('approvals.tabs')} selectedKey={tab} onSelectionChange={(id) => { void navigate(id === 'changes' ? `${APPROVALS_PATH}/changes` : APPROVALS_PATH); }}
          tabs={[
            { id: 'tasks', title: title('approvals.tab.tasks', tasks), content: tab === 'tasks' ? <TaskInbox /> : null },
            { id: 'changes', title: title('approvals.tab.changes', changeCount), content: tab === 'changes' ? <ChangeApprovals /> : null },
          ]} />
      ) : <TaskInbox />}
    </div>
  );
}

// ====================================================================== workflow tasks (Build items 20 and 21)

type Choice = 'any' | 'yes' | 'no';
const asBool = (choice: Choice) => (choice === 'any' ? undefined : choice === 'yes');

function startOfDay(date: CalendarDate | null, timeZone: string): string | undefined {
  return date ? date.toDate(timeZone).toISOString() : undefined;
}

function TaskInbox() {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const navigate = useNavigate();
  const { taskId } = useParams();
  const { me, writable } = useShell();
  const [module, setModule] = useState('all');
  const [documentType, setDocumentType] = useState('all');
  const [company, setCompany] = useState('all');
  const [requester, setRequester] = useState('all');
  const [step, setStep] = useState('all');
  const [waiting, setWaiting] = useState<Choice>('any');
  const [overdue, setOverdue] = useState<Choice>('any');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [sort, setSort] = useState<'received' | 'due'>('received');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkOpen, setBulkOpen] = useState(false);

  const filters: InboxFilters = {
    module: module === 'all' ? undefined : module,
    documentType: documentType === 'all' ? undefined : documentType,
    companyId: company === 'all' ? undefined : company,
    requesterUserId: requester === 'all' ? undefined : requester,
    step: step === 'all' ? undefined : step,
    waiting: asBool(waiting),
    overdue: asBool(overdue),
    receivedFrom: startOfDay(from, timeZone),
    // "received between" includes the last day: up to the start of the next one.
    receivedTo: startOfDay(to ? to.add({ days: 1 }) : null, timeZone),
    sort,
  };
  const counts = useQuery({ queryKey: ['wf', 'counts'], queryFn: workflow.counts, refetchOnMount: 'always' });
  const everything = useQuery({ queryKey: ['wf', 'inbox', 'all'], queryFn: () => workflow.inbox({}), refetchOnMount: 'always' });
  const inbox = useQuery({ queryKey: ['wf', 'inbox', filters], queryFn: () => workflow.inbox(filters), refetchOnMount: 'always' });
  const types = useMemo(() => new Map((counts.data?.documentTypes ?? []).map((d) => [d.documentType, d])), [counts.data]);

  // Choices come from what waits for the person (requesters and steps of their open tasks).
  const all = everything.data ?? [];
  const unique = <T,>(values: T[], key: (v: T) => string) => [...new Map(values.map((v) => [key(v), v])).values()];
  const moduleOptions = unique(counts.data?.documentTypes ?? [], (d) => d.module).map((d) => ({ id: d.module, label: t(moduleKey(d.module)) }));
  const typeOptions = (counts.data?.documentTypes ?? []).filter((d) => module === 'all' || d.module === module).map((d) => ({ id: d.documentType, label: nameOf(d.name, locale) }));
  const requesterOptions = unique(all, (i) => i.requesterUserId).map((i) => ({ id: i.requesterUserId, label: i.requesterName ?? t('approvals.unknownPerson') }));
  const stepOptions = unique(all, (i) => i.stepKey).map((i) => ({ id: i.stepKey, label: nameOf(i.stepName, locale) || i.stepKey }));
  const any = { id: 'all', label: t('approvals.filter.all') };
  const choice = [{ id: 'any', label: t('approvals.filter.all') }, { id: 'yes', label: t('approvals.filter.yes') }, { id: 'no', label: t('approvals.filter.no') }];

  const items = inbox.data ?? [];
  const bulkable = writable && items.some((i) => i.bulkApproval);
  const open = (item: InboxItem) => { void navigate(`${APPROVALS_PATH}/tasks/${item.taskId}`); };
  const reset = () => {
    setModule('all'); setDocumentType('all'); setCompany('all'); setRequester('all'); setStep('all');
    setWaiting('any'); setOverdue('any'); setFrom(null); setTo(null);
  };

  const columns: GridColumn<InboxItem>[] = [
    { id: 'title', header: t('approvals.column.request'), width: 260, cell: (i) => <bdi>{nameOf(i.title, locale)}</bdi> },
    { id: 'marks', header: t('approvals.column.marks'), width: 200, cell: (i) => <TaskMarks overdue={i.overdue} escalation={i.escalationLevel} waiting={i.waitingForInformation} actingFor={i.actingForName} queue={i.queue} /> },
    { id: 'type', header: t('approvals.column.type'), width: 180, cell: (i) => nameOf(types.get(i.documentType)?.name, locale) || i.documentType },
    { id: 'requester', header: t('approvals.column.requester'), width: 180, cell: (i) => (i.requesterName ? isolate(i.requesterName) : t('approvals.unknownPerson')) },
    { id: 'step', header: t('approvals.column.step'), width: 160, cell: (i) => nameOf(i.stepName, locale) || i.stepKey },
    { id: 'received', header: t('approvals.column.received'), width: 180, cell: (i) => formatDateTime(i.receivedAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) },
    { id: 'due', header: t('approvals.column.due'), width: 180, cell: (i) => (i.dueAt ? formatDateTime(i.dueAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : t('approvals.noDue')) },
  ];

  const total = counts.data?.total ?? 0;
  const late = counts.data?.overdue ?? 0;
  return (
    <div className="erp-approvals-tasks">
      {counts.data && (
        <ul className="erp-approvals-counts" aria-label={t('approvals.counts')}>
          <li>{t('approvals.counts.total', { count: total, shown: formatInteger(total, numbers) })}</li>
          {late > 0 && <li><StatusChip status="pending" label={t('approvals.counts.overdue', { count: late, shown: formatInteger(late, numbers) })} /></li>}
          {counts.data.documentTypes.map((d) => (
            <li key={d.documentType}>
              <Button variant={documentType === d.documentType ? 'primary' : 'secondary'} onPress={() => setDocumentType(documentType === d.documentType ? 'all' : d.documentType)}
                aria-pressed={documentType === d.documentType}>
                {t('approvals.counts.type', { type: nameOf(d.name, locale), shown: formatInteger(d.count, numbers) })}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <form className="erp-filters" role="search" aria-label={t('approvals.filters')} onSubmit={(e) => e.preventDefault()}>
        <SelectField label={t('approvals.filter.module')} value={module} onChange={(v) => { setModule(v); setDocumentType('all'); }} options={[any, ...moduleOptions]} />
        <SelectField label={t('approvals.filter.type')} value={documentType} onChange={setDocumentType} options={[any, ...typeOptions]} />
        {me.companies.length > 1 && (
          <SelectField label={t('approvals.filter.company')} value={company} onChange={setCompany}
            options={[any, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale) }))]} />
        )}
        <SelectField label={t('approvals.filter.requester')} value={requester} onChange={setRequester} options={[any, ...requesterOptions]} />
        <SelectField label={t('approvals.filter.step')} value={step} onChange={setStep} options={[any, ...stepOptions]} />
        <SelectField label={t('approvals.filter.waiting')} value={waiting} onChange={(v) => setWaiting(v as Choice)} options={choice} />
        <SelectField label={t('approvals.filter.overdue')} value={overdue} onChange={(v) => setOverdue(v as Choice)} options={choice} />
        <DateField label={t('approvals.filter.from')} value={from} onChange={(v) => setFrom(v as CalendarDate | null)} />
        <DateField label={t('approvals.filter.to')} value={to} onChange={(v) => setTo(v as CalendarDate | null)} />
        <SelectField label={t('approvals.sort')} value={sort} onChange={(v) => setSort(v as 'received' | 'due')}
          options={[{ id: 'received', label: t('approvals.sort.received') }, { id: 'due', label: t('approvals.sort.due') }]} />
        <Button variant="quiet" onPress={reset}>{t('approvals.filter.reset')}</Button>
      </form>
      <div className={taskId ? 'erp-master-detail erp-has-detail' : 'erp-master-detail'}>
        <div className="erp-master">
          <DataGrid label={t('approvals.tab.tasks')} columns={columns} rows={items} rowKey={(i) => i.taskId} rowLabel={(i) => nameOf(i.title, locale)}
            status={inbox.isPending ? 'loading' : inbox.isError ? 'error' : 'ready'}
            error={<ErrorPanel message={t('approvals.loadError')} reference={inbox.error instanceof ApiProblem ? inbox.error.reference : null}
              action={<Button variant="primary" onPress={() => { void inbox.refetch(); }}>{t('common.retry')}</Button>} />}
            empty={<EmptyState title={total === 0 ? t('approvals.empty') : t('approvals.noMatches')}>{total === 0 ? t('approvals.empty.body') : null}</EmptyState>}
            selectionMode={bulkable ? 'multiple' : 'none'} selected={selected} onSelectedChange={setSelected}
            bulkActions={() => <Button variant="primary" onPress={() => setBulkOpen(true)}>{t('approvals.bulk.open')}</Button>}
            onOpen={open} />
        </div>
        {taskId && (
          <aside className="erp-detail" aria-label={t('approvals.detail')}>
            <TaskPanel key={taskId} taskId={taskId} onDone={() => { void navigate(APPROVALS_PATH); }} />
          </aside>
        )}
      </div>
      {bulkOpen && (
        <BulkApproval items={items.filter((i) => selected.has(i.taskId))} onClose={(done) => { setBulkOpen(false); if (done) setSelected(new Set()); }} />
      )}
      {inbox.isError && inbox.error instanceof ApiProblem && inbox.error.status === 403 && <p className="erp-muted">{server(inbox.error.key)}</p>}
    </div>
  );
}

const MODULES = ['kernel', 'hrm', 'test'] as const;
function moduleKey(module: string): MessageKey {
  return (MODULES as readonly string[]).includes(module) ? (`approvals.module.${module}` as MessageKey) : 'approvals.module.other';
}

/** Overdue, escalated, waiting, "for Sara" and queue marks: text and icon, never colour alone (FR-UX-TOK-03). */
function TaskMarks({ overdue, escalation, waiting, actingFor, queue }: { overdue: boolean; escalation: number; waiting: boolean; actingFor: string | null; queue: boolean }) {
  const { t } = useText();
  const marks: ReactNode[] = [];
  if (overdue) marks.push(<span key="overdue" className="erp-chip" data-tone="danger"><AlarmClock size={14} aria-hidden="true" />{t('approvals.mark.overdue')}</span>);
  if (escalation > 0) marks.push(<span key="escalated" className="erp-chip" data-tone="warning"><ArrowUpCircle size={14} aria-hidden="true" />{t('approvals.mark.escalated', { level: escalation })}</span>);
  if (waiting) marks.push(<span key="waiting" className="erp-chip" data-tone="info"><MessageCircleQuestion size={14} aria-hidden="true" />{t('approvals.mark.waiting')}</span>);
  if (actingFor) marks.push(<span key="for" className="erp-chip" data-tone="neutral"><UserRound size={14} aria-hidden="true" />{t('approvals.mark.for', { name: isolate(actingFor) })}</span>);
  if (queue) marks.push(<span key="queue" className="erp-chip" data-tone="neutral">{t('approvals.mark.queue')}</span>);
  return marks.length > 0 ? <span className="erp-row erp-task-marks">{marks}</span> : null;
}

// ---------------------------------------------------------------------- task detail

const DECISIONS: readonly TaskAction[] = ['approve', 'reject', 'return', 'ask'];

function TaskPanel({ taskId, onDone }: { taskId: string; onDone: () => void }) {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const client = useQueryClient();
  const toast = useToast();
  const writable = useShell().writable;
  const detail = useQuery({ queryKey: ['wf', 'task', taskId], queryFn: () => workflow.task(taskId), retry: false, refetchOnMount: 'always' });
  const [action, setAction] = useState<TaskAction | null>(null);
  const [busy, setBusy] = useState<TaskAction | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (detail.isPending) return <Skeleton lines={8} />;
  if (detail.isError) {
    const gone = detail.error instanceof ApiProblem && detail.error.status === 404;
    return gone
      ? <EmptyState title={t('approvals.detail.gone')} action={<Button onPress={onDone}>{t('approvals.detail.back')}</Button>}>{t('approvals.detail.gone.body')}</EmptyState>
      : <ErrorPanel message={t('approvals.detail.loadError')} reference={detail.error instanceof ApiProblem ? detail.error.reference : null}
          action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />;
  }

  const data: TaskDetail = detail.data;
  const { context, task, request } = data;
  const allowed = writable ? context.actions : [];
  const refresh = () => client.invalidateQueries({ queryKey: ['wf'] });
  const quick = async (which: 'claim' | 'release') => {
    setBusy(which);
    setError(null);
    try {
      await (which === 'claim' ? workflow.claim(taskId) : workflow.release(taskId));
      toast.success(t(which === 'claim' ? 'approvals.claimed' : 'approvals.released'));
      await refresh();
    } catch (e) {
      setError(e instanceof ApiProblem ? server(e.key) ?? t('approvals.actionError') : t('approvals.actionError'));
      await refresh();
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="erp-stack erp-task-detail">
      <div className="erp-row erp-detail-head">
        <h2><bdi>{nameOf(request.title, locale)}</bdi></h2>
        <Button variant="quiet" onPress={onDone}>{t('approvals.detail.close')}</Button>
      </div>
      <TaskMarks overdue={!!task.dueAt && new Date(task.dueAt) <= new Date()} escalation={task.escalationLevel} waiting={!!task.infoRequestedAt}
        actingFor={context.actingForName} queue={context.queue && !task.claimedAt} />
      <dl className="erp-facts">
        <dt>{t('approvals.column.type')}</dt><dd><bdi>{nameOf(context.documentTypeName, locale)}</bdi></dd>
        <dt>{t('approvals.column.requester')}</dt><dd>{context.requesterName ? isolate(context.requesterName) : t('approvals.unknownPerson')}</dd>
        <dt>{t('approvals.column.step')}</dt><dd><bdi>{nameOf(task.stepName, locale) || task.stepKey}</bdi></dd>
        <dt>{t('approvals.detail.submitted')}</dt><dd>{formatDateTime(request.startedAt, lang, timeZone, numbers)}</dd>
        <dt>{t('approvals.column.due')}</dt><dd>{task.dueAt ? formatDateTime(task.dueAt, lang, timeZone, numbers) : t('approvals.noDue')}</dd>
      </dl>
      <section aria-labelledby="erp-task-fields" className="erp-stack">
        <h3 id="erp-task-fields">{t('approvals.detail.fields')}</h3>
        <dl className="erp-facts">
          {context.fields.map((f) => (
            <FieldRow key={f.name} field={f} />
          ))}
        </dl>
        <Link href={request.link}>{t('approvals.detail.openDocument')}</Link>
      </section>

      {error && <p role="alert" className="erp-field-error">{error}</p>}
      {allowed.length > 0 ? (
        <div className="erp-row erp-decisions" role="group" aria-label={t('approvals.detail.decide')}>
          {allowed.includes('claim') && <Button variant="secondary" isPending={busy === 'claim'} onPress={() => { void quick('claim'); }}>{t('approvals.action.claim')}</Button>}
          {DECISIONS.filter((a) => allowed.includes(a)).map((a) => (
            <Button key={a} variant={a === 'approve' ? 'primary' : a === 'reject' ? 'danger' : 'secondary'} onPress={() => { setError(null); setAction(a); }}>
              {t(`approvals.action.${a}` as MessageKey)}
            </Button>
          ))}
          {allowed.includes('release') && <Button variant="quiet" isPending={busy === 'release'} onPress={() => { void quick('release'); }}>{t('approvals.action.release')}</Button>}
        </div>
      ) : (
        <p className="erp-muted">{writable ? t('approvals.detail.noActions') : t('approvals.detail.readOnly')}</p>
      )}

      <section aria-labelledby="erp-task-history" className="erp-stack">
        <h3 id="erp-task-history">{t('approvals.detail.history')}</h3>
        <ol className="erp-history">
          {data.history.map((h) => <HistoryRow key={h.id} entry={h} />)}
        </ol>
      </section>

      {action && (
        <DecisionDialog action={action} taskId={taskId} reasonRequired={context.reasonRequired.includes(action) || action === 'ask'}
          title={nameOf(request.title, locale)}
          onClose={async (done) => {
            setAction(null);
            await refresh();
            if (done) onDone();
          }} />
      )}
    </div>
  );
}

function FieldRow({ field }: { field: FieldValue }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const raw = field.value;
  let shown: string;
  if (raw === null || raw === undefined || raw === '') shown = t('approvals.field.empty');
  else if (field.type === 'user') shown = field.displayName ? isolate(field.displayName) : t('approvals.unknownPerson');
  else if (field.type === 'company' || field.type === 'org_unit') shown = nameOf(field.displayNames, locale) || String(raw);
  else if (field.type === 'money' || field.type === 'number') shown = formatDecimal(String(raw), numbers); // exact decimal text, never a float
  else if (field.type === 'date') shown = formatDate(String(raw), lang, timeZone, numbers);
  else if (field.type === 'bool') shown = raw === true ? t('approvals.field.yes') : t('approvals.field.no');
  else shown = isolate(String(raw));
  return (
    <>
      <dt><bdi>{nameOf(field.label, locale) || field.name}</bdi></dt>
      <dd>{shown}</dd>
    </>
  );
}

const EVENTS = ['started', 'task_assigned', 'approved', 'rejected', 'returned', 'info_requested', 'info_answered', 'reassigned', 'claimed', 'released', 'escalated',
  'escalation_not_possible', 'step_skipped', 'cannot_assign', 'task_closed', 'resubmitted', 'withdrawn', 'cancelled', 'completed'] as const;

function HistoryRow({ entry }: { entry: HistoryEntry }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const event = (EVENTS as readonly string[]).includes(entry.event) ? (`approvals.event.${entry.event}` as MessageKey) : 'approvals.event.other';
  const note = ['comment', 'reason', 'question', 'answer'].map((k) => entry.details[k]).find((v): v is string => typeof v === 'string' && v.length > 0);
  const who = entry.actorName ? isolate(entry.actorName) : t('approvals.history.system');
  return (
    <li className="erp-stack">
      <strong>{t(event)}</strong>
      <span>{entry.actingForName ? t('approvals.history.byFor', { who, for: isolate(entry.actingForName) }) : entry.actorName ? <bdi>{entry.actorName}</bdi> : who}</span>
      <time className="erp-muted" dateTime={entry.at}>{formatDateTime(entry.at, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</time>
      {note && <q><bdi>{note}</bdi></q>}
    </li>
  );
}

function DecisionDialog({ action, taskId, reasonRequired, title, onClose }: {
  action: TaskAction; taskId: string; reasonRequired: boolean; title: string; onClose: (done: boolean) => void | Promise<void>;
}) {
  const { t, server } = useText();
  const toast = useToast();
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const label = action === 'ask' ? t('approvals.dialog.question') : reasonRequired ? t('approvals.dialog.reason') : t('approvals.dialog.comment');
  const submit = async () => {
    const value = text.trim();
    if (reasonRequired && !value) {
      setError(t(action === 'ask' ? 'approvals.dialog.questionNeeded' : 'approvals.dialog.reasonNeeded'));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const result = action === 'ask' ? await workflow.ask(taskId, value) : await workflow.decide(taskId, action as 'approve' | 'reject' | 'return', value || null);
      // The server's answer, not an assumption: the request moved on, or finished approved, rejected or returned (STA-06).
      toast.success(action === 'ask' ? t('approvals.done.ask') : t(resultKey(action, result.status), { title }));
      await onClose(action !== 'ask');
    } catch (e) {
      setError(e instanceof ApiProblem ? server(e.key) ?? t('approvals.actionError') : t('approvals.actionError'));
      setPending(false);
    }
  };
  return (
    <Dialog title={t(`approvals.dialog.${action}` as MessageKey, { title })} isOpen onOpenChange={(open) => { if (!open && !pending) void onClose(false); }}
      actions={() => (
        <>
          <Button variant="quiet" isDisabled={pending} onPress={() => { void onClose(false); }}>{t('common.cancel')}</Button>
          <Button variant={action === 'reject' ? 'danger' : 'primary'} isPending={pending} onPress={() => { void submit(); }}>{t(`approvals.action.${action}` as MessageKey)}</Button>
        </>
      )}>
      <TextAreaField label={label} value={text} onChange={setText} isRequired={reasonRequired} error={error} maxLength={2000} />
    </Dialog>
  );
}

function resultKey(action: TaskAction, status: string): MessageKey {
  if (action === 'approve') return status === 'approved' ? 'approvals.done.approved' : 'approvals.done.nextStep';
  if (action === 'reject') return 'approvals.done.rejected';
  return 'approvals.done.returned';
}

// ---------------------------------------------------------------------- bulk approval

function BulkApproval({ items, onClose }: { items: InboxItem[]; onClose: (done: boolean) => void }) {
  const { t, server } = useText();
  const { locale, numbers } = useLocaleSettings();
  const client = useQueryClient();
  const allowed = items.filter((i) => i.bulkApproval);
  const excluded = items.filter((i) => !i.bulkApproval);
  const [comment, setComment] = useState('');
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<BulkResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const titles = new Map(items.map((i) => [i.taskId, nameOf(i.title, locale)]));
  const run = async () => {
    setPending(true);
    setError(null);
    try {
      setResult(await workflow.bulkApprove(allowed.map((i) => i.taskId), comment.trim() || null));
      await client.invalidateQueries({ queryKey: ['wf'] });
    } catch (e) {
      setError(e instanceof ApiProblem ? server(e.key) ?? t('approvals.actionError') : t('approvals.actionError'));
    } finally {
      setPending(false);
    }
  };
  return (
    <Dialog title={t('approvals.bulk.title')} isOpen onOpenChange={(open) => { if (!open && !pending) onClose(result !== null); }}
      actions={() => result ? <Button variant="primary" onPress={() => onClose(true)}>{t('common.close')}</Button> : (
        <>
          <Button variant="quiet" isDisabled={pending} onPress={() => onClose(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={pending} isDisabled={allowed.length === 0} onPress={() => { void run(); }}>
            {t('approvals.bulk.confirm', { count: allowed.length, shown: formatInteger(allowed.length, numbers) })}
          </Button>
        </>
      )}>
      {result ? (
        <div className="erp-stack" role="status">
          <p>{t('approvals.bulk.summary', { done: formatInteger(result.done, numbers), refused: formatInteger(result.refused, numbers) })}</p>
          <ul className="erp-bulk-results">
            {result.results.map((r) => (
              <li key={r.taskId} className="erp-row">
                <StatusChip status={r.outcome === 'approved' ? 'approved' : 'rejected'} label={r.outcome === 'approved' ? t('approvals.bulk.approved') : t('approvals.bulk.refused')} />
                <bdi>{titles.get(r.taskId) ?? r.taskId}</bdi>
                {r.error && <span className="erp-muted">{server(r.error) ?? r.error}</span>}
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <>
          <p>{t('approvals.bulk.preview', { count: allowed.length, shown: formatInteger(allowed.length, numbers) })}</p>
          {excluded.length > 0 && (
            <div className="erp-stack">
              <p>{t('approvals.bulk.excluded', { count: excluded.length, shown: formatInteger(excluded.length, numbers) })}</p>
              <ul>{excluded.map((i) => <li key={i.taskId}><bdi>{nameOf(i.title, locale)}</bdi></li>)}</ul>
            </div>
          )}
          <TextAreaField label={t('approvals.dialog.comment')} value={comment} onChange={setComment} maxLength={2000} />
          {error && <p role="alert" className="erp-field-error">{error}</p>}
        </>
      )}
    </Dialog>
  );
}
