// The labels editor (slice S1.15b Build item 5; FR-ADM-STU-25, -05, FR-KRN-MET-22, FR-KRN-ORG-08): every text a customer may
// rename, searchable, English and Urdu side by side, with the product's text, the customer's and one company's change, where
// the text appears, and "reset to product text". A change shows on every screen within a minute (the shell asks again), and at
// once on the screen that saved it. Text with placeholders is not offered (it keeps the product's wording).
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, SwitchField, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { labelsApi, type LabelOverride } from '../../api/config';
import { companyName, useShell } from '../../shell/context';
import { kernelScreens } from '../kernel';
import { hrmScreens } from '../hrm';
import { catalogue, declaredTables, matchesEntry, overridesByKey, screensOf, viewOf, type CatalogueEntry } from './labels-model';
import './config-messages';
import './settings.css';
import './configure.css';

const screens = [...kernelScreens, ...hrmScreens];
const PAGE = 50;
export const LABEL_MAX = 200;
const WHOLE = 'all';

export default function LabelsScreen() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me, can, writable } = useShell();
  useDocumentTitle(t('labels.title'));
  const [params, setParams] = useSearchParams();
  const table = params.get('table') ?? '';
  const company = params.get('company') ?? '';
  const [search, setSearch] = useState(params.get('q') ?? '');
  const [changedOnly, setChangedOnly] = useState(false);
  const [shown, setShown] = useState(PAGE);
  const [editing, setEditing] = useState<CatalogueEntry | null>(null);
  const list = useQuery({ queryKey: ['cfg', 'label-overrides'], queryFn: labelsApi.list, refetchOnMount: 'always' });
  const canWrite = writable && can('cfg.labels.update');
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });

  const overrides = useMemo(() => overridesByKey(list.data?.items ?? []), [list.data]);
  const companyId = company && me.companies.some((c) => c.id === company) ? company : null;
  const entries = useMemo(() => catalogue(), []);
  const rows = useMemo(() => entries.flatMap((entry) => {
    if (table && entry.table !== table) return [];
    const view = viewOf(entry, overrides, companyId);
    if (changedOnly && !view.changedBy.customer && !view.changedBy.company) return [];
    return matchesEntry(entry, view, search) ? [{ entry, view }] : [];
  }), [entries, overrides, companyId, table, search, changedOnly]);
  const tables = useMemo(() => declaredTables(screens), []);
  const tableOptions: Option[] = [{ id: WHOLE, label: t('labels.table.all') }, ...tables.map((tb) => ({ id: tb, label: tableName(tb, entries, locale) }))];
  const companyOptions: Option[] = [{ id: WHOLE, label: t('labels.layer.customer') }, ...me.companies.map((c) => ({ id: c.id, label: t('labels.layer.company', { company: companyName(c, locale) }) }))];
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const visible = rows.slice(0, shown);
  return (
    <div className="erp-page">
      <PageHeader title={t('labels.title')} description={t('labels.description')} />
      <div className="erp-settings-tools" role="search" aria-label={t('labels.search.label')}>
        <TextField label={t('labels.search.label')} help={t('labels.search.help')} value={search} onChange={(v) => { setSearch(v); setShown(PAGE); patch({ q: v.trim() || null }); }} />
        <SelectField label={t('labels.table.label')} options={tableOptions} value={tableOptions.some((o) => o.id === table) ? table : WHOLE}
          onChange={(v) => { setShown(PAGE); patch({ table: v === WHOLE ? null : v }); }} />
        {me.companies.length > 1 && (
          <SelectField label={t('labels.layer.label')} help={t('labels.layer.help')} options={companyOptions} value={companyId ?? WHOLE}
            onChange={(v) => patch({ company: v === WHOLE ? null : v })} />
        )}
        <SwitchField label={t('labels.changedOnly')} isSelected={changedOnly} onChange={(on) => { setChangedOnly(on); setShown(PAGE); }} />
      </div>
      {!canWrite && list.isSuccess && <Banner tone="info">{t('labels.readOnly')}</Banner>}
      {list.isPending && <Skeleton lines={6} />}
      {list.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('labels.denied') : t('labels.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {list.isSuccess && rows.length === 0 && (
        <EmptyState title={search.trim() || table || changedOnly ? t('labels.noMatch') : t('labels.empty')}>{search.trim() || table || changedOnly ? t('labels.noMatch.body') : t('labels.empty.body')}</EmptyState>
      )}
      {list.isSuccess && rows.length > 0 && (
        <>
          <p className="erp-small erp-muted" role="status">{t('labels.count', { count: rows.length })}</p>
          <div className="erp-table-scroll">
          <table className="erp-table erp-labels-table" aria-label={t('labels.title')}>
            <thead>
              <tr>
                <th scope="col">{t('labels.col.text')}</th>
                <th scope="col">{t('labels.col.en')}</th>
                <th scope="col">{t('labels.col.ur')}</th>
                <th scope="col">{t('labels.col.where')}</th>
                <th scope="col"><span className="erp-visually-hidden">{t('labels.col.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {visible.map(({ entry, view }) => {
                const name = view.effective.en || entry.key;
                return (
                  <tr key={entry.key}>
                    <th scope="row">
                      <span className="erp-stack">
                        <bdi>{name}</bdi>
                        <span className="erp-small erp-muted erp-code"><bdi>{entry.key}</bdi></span>
                        <span className="erp-row erp-wrap">
                          {view.changedBy.customer && <StatusChip status="active" label={t('labels.changed.customer')} />}
                          {view.changedBy.company && <StatusChip status="active" label={t('labels.changed.company')} />}
                        </span>
                      </span>
                    </th>
                    <td lang="en"><bdi>{view.effective.en}</bdi></td>
                    <td lang="ur" dir="rtl"><bdi>{view.effective.ur || <span className="erp-muted">–</span>}</bdi></td>
                    <td className="erp-small">{whereText(entry, t, locale)}</td>
                    <td>
                      <Button variant="secondary" aria-label={t(canWrite ? 'labels.edit.aria' : 'labels.view.aria', { text: name })} onPress={() => setEditing(entry)}>
                        {canWrite ? t('labels.edit') : t('labels.view')}
                      </Button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          </div>
          {rows.length > shown && <Button variant="secondary" onPress={() => setShown((n) => n + PAGE)}>{t('labels.more', { shown: visible.length, total: rows.length })}</Button>}
        </>
      )}
      {editing && (
        <LabelDrawer entry={editing} overrides={list.data?.items ?? []} companyId={companyId} canWrite={canWrite} onClose={() => setEditing(null)} />
      )}
    </div>
  );
}

function tableName(table: string, entries: CatalogueEntry[], locale: string): string {
  const entity = entries.find((e) => e.key === `entities.${table}`);
  return (locale === 'ur' && entity?.ur) || entity?.en || table;
}

/** Where a text appears: the screens that show its table, the menu, or the area of a web text. */
export function whereText(entry: CatalogueEntry, t: ReturnType<typeof useText>['t'], locale: string): string {
  if (entry.table) {
    const found = screensOf(entry.table, screens).map((s) => t(s.labelKey));
    return found.length > 0 ? found.join(', ') : t('labels.where.table', { table: tableName(entry.table, catalogue(), locale) });
  }
  if (entry.key.startsWith('nav.')) return t('labels.where.menu');
  const area = entry.key.split('.')[0] ?? entry.key;
  return t('labels.where.area', { area });
}

function LabelDrawer({ entry, overrides, companyId, canWrite, onClose }: { entry: CatalogueEntry; overrides: readonly LabelOverride[]; companyId: string | null; canWrite: boolean; onClose: () => void }) {
  const { t, server } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const view = viewOf(entry, overridesByKey(overrides), companyId);
  const [en, setEn] = useState(view.effective.en);
  const [ur, setUr] = useState(view.effective.ur);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState<'save' | 'reset' | null>(null);
  const layerName = companyId ? t('labels.layer.company', { company: companyName(me.companies.find((c) => c.id === companyId) ?? null, locale) }) : t('labels.layer.customer');
  const hasOwn = view.own.en !== undefined || view.own.ur !== undefined;
  const dirty = en.trim() !== view.effective.en || ur.trim() !== view.effective.ur;

  const run = async (kind: 'save' | 'reset') => {
    setErrors({});
    setSummary(null);
    if (kind === 'save' && (en.trim() === '' || en.length > LABEL_MAX || ur.length > LABEL_MAX)) {
      setErrors({ en: en.trim() === '' ? t('labels.error.required') : t('labels.error.tooLong', { max: LABEL_MAX }), ...(ur.length > LABEL_MAX ? { ur: t('labels.error.tooLong', { max: LABEL_MAX }) } : {}) });
      return;
    }
    setBusy(kind);
    // A text equal to the one below this layer is no change: it is sent as "no override" (null).
    const texts = kind === 'reset' ? { en: null, ur: null } : { en: en.trim() === view.base.en ? null : en.trim(), ur: ur.trim() === view.base.ur || ur.trim() === '' ? null : ur.trim() };
    try {
      await labelsApi.save({ key: entry.key, layer: companyId ? 'company' : 'tenant', ...(companyId ? { companyId } : {}), texts });
      await queryClient.invalidateQueries({ queryKey: ['cfg', 'label-overrides'] });
      await queryClient.invalidateQueries({ queryKey: ['kernel', 'labels'] });
      toast.success(kind === 'reset' ? t('labels.reset.done') : t('labels.saved'));
      onClose();
    } catch (error) {
      if (error instanceof ApiProblem) {
        const found: Record<string, string> = {};
        for (const [field, keys] of Object.entries(error.fieldErrors)) {
          const name = field.replace(/^texts\./, '');
          found[name] = server(keys[0] ?? '') ?? t('labels.error.generic');
        }
        setErrors(found);
        setSummary(Object.keys(found).length === 0 ? (server(error.key) ?? t('labels.error.generic')) : t('labels.error.fix'));
      } else {
        setSummary(t('labels.error.generic'));
      }
    } finally {
      setBusy(null);
    }
  };

  return (
    <Dialog drawer isOpen title={t('labels.drawer.title')} onOpenChange={(open) => { if (!open) onClose(); }}>
      {() => (
        <form className="erp-stack" onSubmit={(e) => { e.preventDefault(); void run('save'); }} aria-label={t('labels.drawer.title')}>
          <p className="erp-small erp-muted"><span className="erp-code"><bdi>{entry.key}</bdi></span> · {whereText(entry, t, locale)}</p>
          <p className="erp-small erp-muted">{t('labels.drawer.scope', { layer: layerName })}</p>
          {summary && <Banner tone="danger">{summary}</Banner>}
          <div className="erp-i18n-pair">
            <div className="erp-stack">
              <TextField label={t('labels.field.en')} value={en} onChange={setEn} isReadOnly={!canWrite} maxLength={LABEL_MAX} error={errors.en ?? null}
                help={t('labels.product.en', { text: view.base.en })} />
            </div>
            <div className="erp-stack" lang="ur" dir="rtl">
              <TextField label={t('labels.field.ur')} value={ur} onChange={setUr} isReadOnly={!canWrite} maxLength={LABEL_MAX} error={errors.ur ?? null}
                help={t('labels.product.ur', { text: view.base.ur || '–' })} />
            </div>
          </div>
          {canWrite && (
            <div className="erp-row erp-wrap">
              <Button type="submit" variant="primary" isPending={busy === 'save'} isDisabled={busy !== null || !dirty}>{t('labels.save')}</Button>
              {hasOwn && <Button variant="secondary" isPending={busy === 'reset'} isDisabled={busy !== null} onPress={() => { void run('reset'); }}>{t('labels.reset')}</Button>}
            </div>
          )}
          {hasOwn && canWrite && <p className="erp-small erp-muted">{t('labels.reset.help')}</p>}
          <p className="erp-small erp-muted">{t('labels.note.refresh')}</p>
        </form>
      )}
    </Dialog>
  );
}
