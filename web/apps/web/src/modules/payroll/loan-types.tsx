// Loan types (slice S1.41 Build item 7; FR-HRM-LON-01): the list and form made by the engine from the generated metadata of hrm.loan_types. Reads are the generated list; creating and
// changing go to the loan type service (the generated writes are closed), which checks that the policy is a loan policy of the company and the component a deduction. A type is switched off,
// never deleted. The numbers (who may borrow, how much, interest, longest repayment) are not here: they are the loan policy's, and no number has a default.
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/hrm.loan_types.json';
import schemaJson from '@erp/generated/validation/hrm.loan_types.json';
import { sendText } from '../../api/client';
import { useShell } from '../../shell/context';
import './loans-messages';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const LOAN_TYPES_PATH = '/payroll/loan-types';

export default function LoanTypesScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<TypeList />} />
        <Route path="new" element={<TypeForm />} />
        <Route path=":id" element={<TypeForm />} />
      </Routes>
    </EngineProvider>
  );
}

function useTypeTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function TypeList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany, me } = useShell();
  const { t } = useText();
  const title = useTypeTitle();
  const options = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code, detail: c.code }));
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'kind', 'companyId', 'isActive']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('hrm.loan_types.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')}
      description={t('loan.types.description')}
      onCreate={() => { void navigate(`${LOAN_TYPES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${LOAN_TYPES_PATH}/${String(row.id)}`); }}
      rowLabel={(row) => title(row)} />
  );
}

function TypeForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany, me } = useShell();
  const title = useTypeTitle();
  const creating = !id;
  const companies = me.companies.filter((c) => can('hrm.loan_types.create', c.id)).map((c) => ({ id: c.id, label: c.name.en ?? c.code, detail: c.code }));
  const all = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code, detail: c.code }));
  const inHand = companies.find((c) => c.id === activeCompany?.id);
  const defaults = { companyId: inHand?.id ?? companies[0]?.id ?? null, isActive: true, nameI18n: {}, kind: 'loan' };
  return (
    <FormPage metadata={metadata} schema={schema} id={id ?? null}
      fields={['companyId', 'code', 'nameI18n', 'kind', 'policyId', 'deductionComponentId', 'isActive']}
      defaults={defaults} companies={creating ? companies : all}
      canCreate={writable && companies.length > 0} canUpdate={writable && can('hrm.loan_types.update')} canDelete={false}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ kind: t('loan.types.help.kind'), policyId: t('loan.types.help.policy'), deductionComponentId: t('loan.types.help.component') }}
      onSaved={(row, created, how) => {
        toast.success(t(created ? 'loan.types.created' : how === 'activation' ? (row.isActive ? 'loan.types.activated' : 'loan.types.deactivated') : 'loan.types.saved', { type: title(row) }));
        if (how === 'form') void navigate(LOAN_TYPES_PATH);
      }}
      onDeleted={() => { void navigate(LOAN_TYPES_PATH); }}
      onCancel={() => { void navigate(LOAN_TYPES_PATH); }} />
  );
}
