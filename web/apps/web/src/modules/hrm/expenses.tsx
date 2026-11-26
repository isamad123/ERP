// Expense screens (slice S1.42; FR-HRM-EXP-02, EXP-03, EXP-05): My expense claims at `/expenses`, the claim form at `/expenses/new` and the claim page at `/expenses/claims/:id`
// (also the link target of the approvals inbox). The list is made by the engine from the generated metadata of `hrm.expense_claims`.
import { useMemo } from 'react';
import { Route, Routes, useNavigate } from 'react-router';
import { EngineProvider, ListPage, type EngineApi, type Row, type TableMetadata } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import metadataJson from '@erp/generated/metadata/hrm.expense_claims.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import { ClaimPage, EXPENSES_PATH, NewClaimPage } from './expense-claim';
import './expenses-messages';

const metadata = metadataJson as TableMetadata;
const api: EngineApi = { send: sendText };

export default function ExpensesScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<MyClaims />} />
        <Route path="new" element={<NewClaimPage />} />
        <Route path="claims/:id" element={<ClaimPage />} />
      </Routes>
    </EngineProvider>
  );
}

export function useCompanyOptions() {
  const { me } = useShell();
  const { locale } = useLocaleSettings();
  return useMemo(() => me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, locale]);
}

function MyClaims() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const options = useCompanyOptions();
  return (
    <ListPage metadata={metadata} columns={['claimNo', 'title', 'status', 'currencyCode', 'totalAmount', 'reimburseVia', 'paymentStatus', 'submittedAt']}
      companies={{ activeId: activeCompany?.id ?? null, options }} canCreate={writable && can('hrm.expense_claims.create')} canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')}
      description={t('exp.list.description')} onCreate={() => { void navigate(`${EXPENSES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${EXPENSES_PATH}/claims/${String(row.id)}`); }}
      rowLabel={(row) => String(row.claimNo ?? '')} />
  );
}
