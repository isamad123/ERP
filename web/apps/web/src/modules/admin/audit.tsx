// The audit search screen (slice S1.13b Build item 11; FR-KRN-AUD-04, ruling Q9): one plain search over the change log
// (person, table, record, dates, action, source) and the security events, with the existing audit export. Values the viewer
// may not see arrive hidden from the server. There is no chain verification button here (operator command, S1.07).
import { useMemo, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Download } from 'lucide-react';
import { Button, DateField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, TextField, Tabs, useToast, type Option } from '@erp/ui';
import { EngineProvider, LazyExportButton, type EngineApi, useDocumentTitle } from '@erp/engine';
import { formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, sendText } from '../../api/client';
import { access, downloadChangeExport, type AuditChange, type ChangeFilters } from '../../api/access';
import type { SecurityEvent } from '../../api/users';
import { useShell } from '../../shell/context';
import { PersonPicker, useProblemText } from './users-parts';
import './reports-messages';
import './users.css';

const exportApi: EngineApi = { send: sendText };
export const AUDIT_PATH = '/admin/audit';
const PAGE = 50;
const ACTIONS = ['create', 'update', 'delete', 'status'] as const;
const SOURCES = ['ui', 'api', 'import', 'job', 'system'] as const;
const SEVERITIES = ['info', 'warning', 'critical'] as const;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export default function AuditScreen() {
  const { t } = useText();
  const { can } = useShell();
  const [tab, setTab] = useState(can('aud.changes.view') ? 'changes' : 'events');
  useDocumentTitle(t('audit.title'));
  const tabs = [
    ...(can('aud.changes.view') ? [{ id: 'changes', title: t('audit.tab.changes'), content: tab === 'changes' ? <Changes /> : null }] : []),
    ...(can('aud.security_events.view') ? [{ id: 'events', title: t('audit.tab.events'), content: tab === 'events' ? <Events /> : null }] : []),
  ];
  return (
    <div className="erp-page">
      <PageHeader title={t('audit.title')} description={t('audit.description')} />
      {tabs.length === 0 ? <ErrorPanel message={t('audit.denied')} /> : <Tabs label={t('audit.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />}
    </div>
  );
}

const dayStart = (d: CalendarDate | null) => (d ? `${d.toString()}T00:00:00Z` : undefined);
const dayAfter = (d: CalendarDate | null) => (d ? `${d.add({ days: 1 }).toString()}T00:00:00Z` : undefined);

// ---------------------------------------------------------------------- change log

function Changes() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can, activeCompany } = useShell();
  const toast = useToast();
  const problemText = useProblemText();
  const [search] = useSearchParams();
  const [person, setPerson] = useState<Option | null>(null);
  const [table, setTable] = useState(search.get('entityType') ?? '');
  const [record, setRecord] = useState(search.get('entityId') ?? '');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [action, setAction] = useState('any');
  const [source, setSource] = useState('any');
  const [applied, setApplied] = useState<ChangeFilters>({ entityType: search.get('entityType') ?? undefined, entityId: search.get('entityId') ?? undefined });
  const [exporting, setExporting] = useState(false);
  const recordInvalid = record.trim() !== '' && !UUID.test(record.trim());

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (recordInvalid) return;
    setApplied({
      userId: person?.id, entityType: table.trim() || undefined, entityId: record.trim() || undefined,
      from: dayStart(from), to: dayAfter(to), action: action === 'any' ? undefined : action, source: source === 'any' ? undefined : source,
    });
  };
  const reset = () => { setPerson(null); setTable(''); setRecord(''); setFrom(null); setTo(null); setAction('any'); setSource('any'); setApplied({}); };

  const list = useInfiniteQuery({
    queryKey: ['aud', 'changes', applied],
    queryFn: ({ pageParam, signal }) => access.changes({ ...applied, cursor: pageParam, limit: PAGE }, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? null,
    placeholderData: keepPreviousData,
    refetchOnMount: 'always',
  });
  const rows = useMemo<AuditChange[]>(() => list.data?.pages.flatMap((p) => p.items) ?? [], [list.data]);
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const filtered = Object.values(applied).some((v) => v !== undefined);
  const when = (value: string) => formatDateTime(value, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
  const exportNow = async () => {
    setExporting(true);
    try { await downloadChangeExport(applied); toast.success(t('audit.exported')); } catch (e) { toast.error(problemText(e)); } finally { setExporting(false); }
  };

  return (
    <div className="erp-stack">
      <form className="erp-filters" role="search" aria-label={t('audit.filters')} onSubmit={submit}>
        <PersonPicker label={t('audit.user')} value={person} onChange={setPerson} />
        <TextField label={t('audit.table')} help={t('audit.table.help')} value={table} onChange={setTable} isCode />
        <TextField label={t('audit.record')} help={t('audit.record.help')} value={record} onChange={setRecord} isCode error={recordInvalid ? t('audit.record.invalid') : null} />
        <DateField label={t('audit.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('audit.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
        <SelectField label={t('audit.action')} value={action} onChange={setAction}
          options={[{ id: 'any', label: t('users.any') }, ...ACTIONS.map((a) => ({ id: a, label: t(`rep.action.${a}` as MessageKey) }))]} />
        <SelectField label={t('audit.source')} value={source} onChange={setSource}
          options={[{ id: 'any', label: t('users.any') }, ...SOURCES.map((s) => ({ id: s, label: t(`audit.source.${s}` as MessageKey) }))]} />
        <Button type="submit" variant="primary">{t('audit.search')}</Button>
        <Button variant="quiet" onPress={reset}>{t('rep.clear')}</Button>
        {can('aud.export') && <Button variant="secondary" icon={<Download size={16} aria-hidden="true" />} isPending={exporting} onPress={() => { void exportNow(); }}>{t('audit.export')}</Button>}
        {can('aud.export') && can('itg.exports.run') && (
          <EngineProvider api={exportApi}><LazyExportButton source="aud.change_log" title={t('audit.tab.changes')} companyId={activeCompany?.id ?? null}
            query={{ userId: applied.userId, entityType: applied.entityType, entityId: applied.entityId, from: applied.from, to: applied.to, action: applied.action, source: applied.source }}
            columns={[
              { field: 'occurredAt', label: t('rep.col.when') }, { field: 'actorName', label: t('rep.col.who') }, { field: 'entityType', label: t('audit.table') }, { field: 'entityId', label: t('audit.record') },
              { field: 'action', label: t('rep.col.action') }, { field: 'source', label: t('audit.source') }, { field: 'changes', label: t('rep.col.fields') },
            ]} /></EngineProvider>
        )}
      </form>
      {list.isPending ? <Skeleton lines={6} /> : list.isError && rows.length === 0 ? (
        <ErrorPanel message={problem?.status === 403 ? t('audit.denied') : t('audit.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      ) : rows.length === 0 ? <EmptyState title={filtered ? t('audit.noMatches') : t('audit.empty')}>{filtered ? t('audit.noMatches.body') : t('audit.empty.body')}</EmptyState> : (
        <>
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('audit.tab.changes')}</caption>
            <thead><tr>
              <th scope="col">{t('rep.col.when')}</th><th scope="col">{t('rep.col.who')}</th><th scope="col">{t('audit.table')}</th><th scope="col">{t('audit.record')}</th>
              <th scope="col">{t('rep.col.action')}</th><th scope="col">{t('audit.source')}</th><th scope="col">{t('rep.col.fields')}</th>
            </tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <th scope="row">{when(r.occurredAt)}</th>
                  <td><bdi>{r.actorName ?? t('rep.system')}</bdi></td>
                  <td><bdi className="erp-code">{r.entityType}</bdi></td>
                  <td><bdi className="erp-code erp-small">{r.entityId}</bdi></td>
                  <td>{ACTIONS.includes(r.action as (typeof ACTIONS)[number]) ? t(`rep.action.${r.action}` as MessageKey) : r.action}</td>
                  <td>{r.source && SOURCES.includes(r.source as (typeof SOURCES)[number]) ? t(`audit.source.${r.source}` as MessageKey) : <span className="erp-muted">{t('rep.none')}</span>}</td>
                  <td><bdi className="erp-small">{r.changes.map((c) => (c.hidden ? `${c.field} (${t('audit.hidden')})` : c.field)).join(', ')}</bdi></td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="erp-small erp-muted">{t('rep.shown', { count: formatInteger(rows.length, numbers) })}</p>
          {list.hasNextPage && <div><Button variant="secondary" isPending={list.isFetchingNextPage} onPress={() => { void list.fetchNextPage(); }}>{t('rep.more')}</Button></div>}
        </>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------- security events

function Events() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const [type, setType] = useState('');
  const [severity, setSeverity] = useState('any');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [applied, setApplied] = useState<Record<string, string | undefined>>({});
  const submit = (event: FormEvent) => {
    event.preventDefault();
    setApplied({ eventType: type.trim() || undefined, severity: severity === 'any' ? undefined : severity, from: dayStart(from), to: dayAfter(to) });
  };
  const list = useInfiniteQuery({
    queryKey: ['aud', 'security-events', applied],
    queryFn: ({ pageParam, signal }) => access.securityEvents({ ...applied, cursor: pageParam, limit: PAGE }, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
    refetchOnMount: 'always',
  });
  const rows = useMemo<SecurityEvent[]>(() => list.data?.pages.flatMap((p) => p.items) ?? [], [list.data]);
  const problem = list.error instanceof ApiProblem ? list.error : null;
  return (
    <div className="erp-stack">
      <form className="erp-filters" role="search" aria-label={t('audit.filters')} onSubmit={submit}>
        <TextField label={t('audit.eventType')} value={type} onChange={setType} isCode />
        <SelectField label={t('audit.severity')} value={severity} onChange={setSeverity}
          options={[{ id: 'any', label: t('users.any') }, ...SEVERITIES.map((s) => ({ id: s, label: t(`audit.severity.${s}` as MessageKey) }))]} />
        <DateField label={t('audit.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('audit.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
        <Button type="submit" variant="primary">{t('audit.search')}</Button>
      </form>
      {list.isPending ? <Skeleton lines={6} /> : list.isError && rows.length === 0 ? (
        <ErrorPanel message={problem?.status === 403 ? t('audit.denied') : t('audit.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      ) : rows.length === 0 ? <EmptyState title={t('audit.events.empty')} /> : (
        <>
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('audit.tab.events')}</caption>
            <thead><tr>
              <th scope="col">{t('rep.col.when')}</th><th scope="col">{t('audit.eventType')}</th><th scope="col">{t('audit.severity')}</th>
              <th scope="col">{t('rep.col.who')}</th><th scope="col">{t('audit.subject')}</th>
            </tr></thead>
            <tbody>
              {rows.map((e) => (
                <tr key={e.id}>
                  <th scope="row">{formatDateTime(e.occurredAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</th>
                  <td><bdi className="erp-code">{e.eventType}</bdi></td>
                  <td>{(SEVERITIES as readonly string[]).includes(e.severity) ? t(`audit.severity.${e.severity}` as MessageKey) : e.severity}</td>
                  <td><bdi>{e.actor ?? t('rep.system')}</bdi></td>
                  <td><bdi>{e.subject ?? ''}</bdi></td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.hasNextPage && <div><Button variant="secondary" isPending={list.isFetchingNextPage} onPress={() => { void list.fetchNextPage(); }}>{t('rep.more')}</Button></div>}
        </>
      )}
    </div>
  );
}
