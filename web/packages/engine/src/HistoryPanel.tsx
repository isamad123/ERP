// A record's change history (slice S1.12c Build item 19; FR-KRN-AUD-07, FR-ADM-OPS-03, S1.12 ruling Q14), from
// GET /api/aud/history/{table}/{id}: who changed it and when, and for each field the value before and after as the
// server allows. Values the person may not see come back marked "hidden", values of a person whose data was erased
// "erased", and encrypted columns "redacted"; the panel says so instead of showing anything. Newest first.
import { useQuery } from '@tanstack/react-query';
import { Button, EmptyState, ErrorPanel, Skeleton } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText } from '@erp/i18n';
import { isProblem, useEngineApi } from './data';
import type { FieldMetadata, TableMetadata } from './metadata';
import { useLabel } from './labels';
import { Value } from './values';

export interface HistoryChange {
  field: string;
  before?: unknown;
  after?: unknown;
  hidden?: boolean;
  redacted?: boolean;
  erased?: boolean;
}

export interface HistoryEntry {
  id: string;
  occurredAt: string;
  actorUserId: string | null;
  actorKind: string;
  actorName: string | null;
  action: string;
  source: string | null;
  changes: HistoryChange[];
}

/** The history of one record (null id: not asked for). */
export function useHistory(table: string, id: string | null) {
  const api = useEngineApi();
  return useQuery({
    queryKey: ['engine', table, 'history', id],
    queryFn: async ({ signal }) => (id === null ? [] : JSON.parse(await api.send('GET', `/api/aud/history/${encodeURIComponent(table)}/${encodeURIComponent(id)}`, undefined, signal)) as HistoryEntry[]),
    enabled: id !== null,
  });
}

/** Columns the panel never lists: bookkeeping the person did not change (version, audit stamps). */
const BOOKKEEPING = new Set(['row_version', 'updated_at', 'updated_by', 'created_at', 'created_by', 'id']);

export function HistoryPanel({ metadata, id }: { metadata: TableMetadata; id: string }) {
  const { t } = useText();
  const label = useLabel();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const history = useHistory(metadata.table, id);
  const byColumn = new Map<string, FieldMetadata>(metadata.fields.map((f) => [f.column, f]));
  if (history.isPending) return <Skeleton lines={5} />;
  if (history.isError) {
    const problem = isProblem(history.error) ? history.error : null;
    return <ErrorPanel message={t('engine.history.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void history.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  if (history.data.length === 0) return <EmptyState title={t('engine.history.empty')} />;
  return (
    <ol className="erp-history" aria-label={t('engine.history.list')}>
      {history.data.map((entry) => {
        const who = entry.actorName ?? (entry.actorKind === 'user' ? t('engine.history.someone') : t('engine.history.system'));
        const when = formatDateTime(entry.occurredAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
        const changes = entry.changes.filter((c) => !BOOKKEEPING.has(c.field) && c.field !== 'event');
        return (
          <li key={entry.id} className="erp-history-entry">
            <p className="erp-history-head">
              <strong>{t(`engine.history.action.${entry.action === 'insert' || entry.action === 'create' ? 'create' : entry.action === 'delete' ? 'delete' : 'update'}`)}</strong>
              <span className="erp-muted">{t('engine.history.byWhen', { who, when })}</span>
            </p>
            {changes.length > 0 && (
              <dl className="erp-history-changes">
                {changes.map((change) => {
                  const field = byColumn.get(change.field);
                  const name = field ? label(field.labelKey) : change.field;
                  const mark = change.hidden ? t('engine.history.hidden') : change.erased ? t('engine.history.erased') : change.redacted ? t('engine.history.redacted') : null;
                  return (
                    <div key={change.field} className="erp-history-change">
                      <dt>{name}</dt>
                      <dd>
                        {mark ? <span className="erp-muted">{mark}</span> : (
                          <span className="erp-row">
                            {'before' in change && <span><span className="erp-visually-hidden">{t('engine.history.before')}</span>{field ? <Value field={field} value={change.before ?? null} label={label} /> : String(change.before)}</span>}
                            {'before' in change && 'after' in change && <span aria-hidden="true" className="erp-mirror">→</span>}
                            {'after' in change && <span><span className="erp-visually-hidden">{t('engine.history.after')}</span>{field ? <Value field={field} value={change.after ?? null} label={label} /> : String(change.after)}</span>}
                          </span>
                        )}
                      </dd>
                    </div>
                  );
                })}
              </dl>
            )}
          </li>
        );
      })}
    </ol>
  );
}
