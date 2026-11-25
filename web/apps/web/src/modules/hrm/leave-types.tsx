/* eslint-disable jsx-a11y/no-noninteractive-tabindex -- a scrolling table region must be reachable by keyboard (WCAG 2.1.1, R-318) */
// Leave types (slice S1.33b Build item 6; FR-HRM-LVE-01): the company's leave types with how each counts, whether it is paid, and its colour (a design token name, never a raw colour),
// filters, create and change through the server's purpose-built endpoints, and retire or bring back (a type is never deleted). Every refusal is the server's, shown in plain words.
// How a type is granted and every limit live in the leave policy ("Leave rules"), never here.
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, TextField, useToast } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { COLOUR_TOKENS, leaveTypesApi, type LeaveTypeRow } from '../../api/leave-types';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { useRefused } from '../admin/config-parts';
import { isDenied, useCompanyChoice } from './live-parts';
import { LeaveTypeForm, useLeaveTypeRefresh } from './leave-types-form';
import '../admin/config-messages';
import './leave-types-messages';
import './leave-types.css';

const ALL = 'all';

export default function LeaveTypesScreen() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  useDocumentTitle(t('lty.title'));
  const { company, setCompany, options } = useCompanyChoice();
  const allowed = can('hrm.leave_types.read');
  const canCreate = writable && can('hrm.leave_types.create');
  const canEdit = writable && can('hrm.leave_types.update');
  const [status, setStatus] = useState('active');
  const [search, setSearch] = useState('');
  const [form, setForm] = useState<LeaveTypeRow | 'new' | null>(null);
  const [retiring, setRetiring] = useState<LeaveTypeRow | null>(null);
  const list = useQuery({ queryKey: ['hrm', 'leave-types-admin', company], queryFn: ({ signal }) => leaveTypesApi.list(company, signal), enabled: allowed && !!company });

  const rows = useMemo(() => {
    const wanted = search.trim().toLowerCase();
    return (list.data ?? []).filter((r) => (status === ALL || (status === 'active') === r.isActive)
      && (wanted === '' || [r.code, ...Object.values(r.nameI18n)].some((v) => v.toLowerCase().includes(wanted))));
  }, [list.data, status, search]);

  if (!allowed) return <div className="erp-page"><PageHeader title={t('lty.title')} /><EmptyState title={t('lty.denied')} /></div>;
  const statusOptions = [{ id: 'active', label: t('lty.filter.active') }, { id: 'retired', label: t('lty.filter.retired') }, { id: ALL, label: t('lty.filter.all') }];
  return (
    <div className="erp-page">
      <PageHeader title={t('lty.title')} description={t('lty.description')}
        actions={canCreate && company ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setForm('new')}>{t('lty.new')}</Button> : undefined} />
      <div className="erp-stack">
        {!writable && <Banner tone="info">{t('lty.readOnly')}</Banner>}
        <div className="erp-ltype-filters" role="group" aria-label={t('lty.filters')}>
          {options.length > 1 && <SelectField label={t('lty.company')} options={options} value={company} onChange={setCompany} />}
          <SelectField label={t('lty.filter.status')} options={statusOptions} value={status} onChange={setStatus} />
          <TextField label={t('lty.filter.search')} type="search" value={search} onChange={setSearch} />
        </div>
        {list.isPending && list.fetchStatus !== 'idle' && <Skeleton lines={6} />}
        {list.isError && (
          <ErrorPanel title={t('lty.loadError')} message={isDenied(list.error) ? t('lty.denied') : t('lty.loadError.body')}
            action={isDenied(list.error) ? null : <Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {list.isSuccess && rows.length === 0 && (
          <EmptyState title={(list.data ?? []).length === 0 ? t('lty.empty') : t('lty.empty.filtered')}>{(list.data ?? []).length === 0 ? t('lty.empty.body') : t('lty.empty.filtered.body')}</EmptyState>
        )}
        {list.isSuccess && rows.length > 0 && (
          <div className="erp-ltype-scroll" role="region" tabIndex={0} aria-label={t('lty.table')}>
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('lty.table')}</caption>
              <thead><tr>
                <th scope="col">{t('lty.col.code')}</th><th scope="col">{t('lty.col.name')}</th><th scope="col">{t('lty.col.paid')}</th><th scope="col">{t('lty.col.counting')}</th>
                <th scope="col">{t('lty.col.unit')}</th><th scope="col">{t('lty.col.document')}</th><th scope="col">{t('lty.col.compOff')}</th><th scope="col">{t('lty.col.deduct')}</th>
                <th scope="col">{t('lty.col.colour')}</th><th scope="col">{t('lty.col.status')}</th><th scope="col"><span className="erp-visually-hidden">{t('lty.col.actions')}</span></th>
              </tr></thead>
              <tbody>
                {rows.map((r) => {
                  const name = nameOf(r.nameI18n, locale) || r.code;
                  const known = (COLOUR_TOKENS as readonly string[]).includes(r.colorToken ?? '');
                  return (
                    <tr key={r.id}>
                      <th scope="row"><span className="erp-code"><bdi>{r.code}</bdi></span></th>
                      <td>{name}</td>
                      <td>{t(r.isPaid ? 'lty.paid' : 'lty.unpaid')}</td>
                      <td>{t(`lty.counting.${r.counting}` as MessageKey)}</td>
                      <td>{t(`lty.unit.${r.unit}` as MessageKey)}</td>
                      <td>{t(r.requiresDocument ? 'lty.yes' : 'lty.no')}</td>
                      <td>{t(r.isCompOff ? 'lty.yes' : 'lty.no')}</td>
                      <td>{t(r.isUnpaidDeduction ? 'lty.yes' : 'lty.no')}</td>
                      <td>{known ? <span className="erp-ltype-chip" data-colour={r.colorToken ?? undefined}>{t(`lty.colour.${r.colorToken}` as MessageKey)}</span> : <span className="erp-muted">{t('lty.colour.none')}</span>}</td>
                      <td><StatusChip status={r.isActive ? 'active' : 'inactive'} label={t(r.isActive ? 'lty.status.active' : 'lty.status.retired')} /></td>
                      <td>
                        {canEdit && (
                          <div className="erp-ltype-actions">
                            <Button variant="secondary" aria-label={t('lty.edit.aria', { code: r.code })} onPress={() => setForm(r)}>{t('lty.edit')}</Button>
                            {r.isActive
                              ? <Button variant="quiet" aria-label={t('lty.retire.aria', { code: r.code })} onPress={() => setRetiring(r)}>{t('lty.retire')}</Button>
                              : <RestoreButton row={r} />}
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {form !== null && <LeaveTypeForm companyId={company} row={form === 'new' ? null : form} onClose={() => setForm(null)} />}
      {retiring && <RetireDialog row={retiring} onClose={() => setRetiring(null)} />}
    </div>
  );
}

/** Retire or bring back: only `isActive` and the row version are sent, so nothing else changes. */
function useSwitch(row: LeaveTypeRow) {
  const { t } = useText();
  const toast = useToast();
  const refused = useRefused();
  const refresh = useLeaveTypeRefresh();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const run = async (active: boolean, done: string): Promise<boolean> => {
    setBusy(true);
    setFailure(null);
    try {
      await leaveTypesApi.update(row.id, { isActive: active, rowVersion: row.rowVersion });
      await refresh();
      toast.success(t(done as MessageKey));
      return true;
    } catch (error) {
      setFailure(refused(error).summary);
      return false;
    } finally {
      setBusy(false);
    }
  };
  return { busy, failure, run };
}

function RestoreButton({ row }: { row: LeaveTypeRow }) {
  const { t } = useText();
  const { busy, failure, run } = useSwitch(row);
  return (
    <>
      <Button variant="secondary" isPending={busy} aria-label={t('lty.restore.aria', { code: row.code })} onPress={() => { void run(true, 'lty.restored'); }}>{t('lty.restore')}</Button>
      {failure && <span className="erp-field-error" role="alert">{failure}</span>}
    </>
  );
}

function RetireDialog({ row, onClose }: { row: LeaveTypeRow; onClose: () => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { busy, failure, run } = useSwitch(row);
  return (
    <Dialog title={t('lty.retire.title', { name: nameOf(row.nameI18n, locale) || row.code })} role="alertdialog" isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
        <Button variant="danger" isPending={busy} onPress={() => { void run(false, 'lty.retired').then((ok) => { if (ok) onClose(); }); }}>{t('lty.retire.confirm')}</Button></>)}>
      <div className="erp-stack">
        <p>{t('lty.retire.body')}</p>
        {failure && <Banner tone="danger">{failure}</Banner>}
      </div>
    </Dialog>
  );
}
