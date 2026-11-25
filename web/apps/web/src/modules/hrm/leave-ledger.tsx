// The leave ledger of an employee, for HR (slice S1.33b Build item 7; FR-HRM-LVE-10, -14, FR-XM-05, -06): pick a company and an employee (by employee number; the server applies the data
// scope), then read every leave entry with its reason, source and running balance, reverse one, post an opening balance, grant or adjustment, and check the balances against the policies.
// Deep link: `?employment=<id>`. Entries are never edited: a correction is a reversal.
import { useCallback, useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { employments } from '@erp/generated/api/hrm';
import { useDocumentTitle } from '@erp/engine';
import { Banner, ComboBoxField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, type Option } from '@erp/ui';
import { requestJson } from '../../api/client';
import { useShell } from '../../shell/context';
import { useCompanyChoice } from './live-parts';
import { useLeaveRefusal } from './leave-parts';
import { LedgerView } from './leave-ledger-view';
import { useLedgerText } from './leave-ledger-messages';

interface Found { id: string; employeeNo: string; status: string; companyId: string }
const SEARCH_LIMIT = 20;

export default function LeaveLedgerScreen() {
  const { t } = useLedgerText();
  const { can } = useShell();
  const refusal = useLeaveRefusal();
  useDocumentTitle(t('lled.title'));
  const [params, setParams] = useSearchParams();
  const linked = params.get('employment') ?? '';
  const { company, setCompany, options } = useCompanyChoice();
  const [picked, setPicked] = useState<{ id: string; employeeNo: string; companyId: string } | null>(null);

  const link = useQuery({ queryKey: ['hrm', 'leave', 'ledger-link', linked], queryFn: () => employments.get(linked), enabled: !!linked && can('hrm.leave_ledger.read'), retry: false });
  useEffect(() => {
    if (link.data && picked?.id !== link.data.id) { setPicked({ id: link.data.id, employeeNo: link.data.employeeNo, companyId: link.data.companyId }); setCompany(link.data.companyId); }
  }, [link.data, picked?.id, setCompany]);

  const search = useCallback(async (text: string, signal: AbortSignal): Promise<Option[]> => {
    const query = `companyId=${encodeURIComponent(company)}&limit=${SEARCH_LIMIT}&sort=employeeNo${text.trim() ? `&search=${encodeURIComponent(text.trim())}` : ''}`;
    const answer = await requestJson<{ items: Found[] }>('GET', `/api/hrm/employments?${query}`, { signal });
    const needle = text.trim().toLowerCase();
    return answer.items.filter((e) => !needle || e.employeeNo.toLowerCase().includes(needle)).map((e) => ({ id: e.id, label: e.employeeNo, detail: e.status }));
  }, [company]);

  if (!can('hrm.leave_ledger.read')) return <div className="erp-page"><PageHeader title={t('lled.title')} /><ErrorPanel title={t('lled.denied')} message={t('lled.denied.body')} /></div>;
  const choose = (option: Option | null) => {
    const next = new URLSearchParams(params);
    if (option) { setPicked({ id: option.id, employeeNo: option.label, companyId: company }); next.set('employment', option.id); } else { setPicked(null); next.delete('employment'); }
    setParams(next, { replace: true });
  };
  return (
    <div className="erp-page">
      <PageHeader title={t('lled.title')} description={t('lled.description')} />
      <div className="erp-stack">
        <div className="erp-row erp-wrap">
          {options.length > 1 && <SelectField label={t('lled.company')} options={options} value={company} onChange={(id) => { setCompany(id); setPicked(null); }} />}
          <ComboBoxField label={t('lled.employee')} help={t('lled.employee.help')} search={search} value={picked ? { id: picked.id, label: picked.employeeNo } : null} onChange={choose} />
        </div>
        {linked && link.isPending && <Skeleton lines={2} />}
        {linked && link.isError && <ErrorPanel title={t('lled.loadError')} message={refusal(link.error)} />}
        {!picked && !(linked && (link.isPending || link.isError)) && <EmptyState title={t('lled.pick')}>{t('lled.pick.body')}</EmptyState>}
        {picked && <>
          <Banner tone="info">{t('lled.never')}</Banner>
          <LedgerView key={picked.id} employmentId={picked.id} companyId={picked.companyId} employeeNo={picked.employeeNo} />
        </>}
      </div>
    </div>
  );
}
