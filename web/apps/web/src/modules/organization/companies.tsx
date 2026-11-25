// Companies (slice S1.14a Build item 1; FR-ADM-ORG-01, FR-KRN-ORG-01, FR-ADM-ORG-07, FR-KRN-IAM-18): the legal entities of the
// customer. The list and the form come from the engine (metadata of org.companies); a company's record adds its registrations
// and bank accounts (hand-written endpoints: encrypted values, field security, maker-checker on bank accounts), the settings it
// overrides with a link into the settings centre, and its letterhead template. A new company has its first branch and its
// default numbering schemes at once; at the licence's company limit the plain refusal is shown. The server decides everything.
import './organization.css';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/org.companies.json';
import schemaJson from '@erp/generated/validation/org.companies.json';
import { sendText } from '../../api/client';
import { useShell } from '../../shell/context';
import { logoEditor } from './logo-editor';
import { BankAccountsPanel, calendarEditor, languageEditor, LetterheadLink, RegistrationsPanel, SettingOverridesPanel } from './org-parts';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const COMPANIES_PATH = '/organization/companies';
const LIMIT_DETAIL = 'errors.licence.limit_reached.companies';

export default function CompaniesScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<CompanyList />} />
        <Route path="new" element={<CompanyRecord />} />
        <Route path=":id" element={<CompanyRecord />} />
      </Routes>
    </EngineProvider>
  );
}

function useCompanyTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function CompanyList() {
  const navigate = useNavigate();
  const { can, writable } = useShell();
  const { t } = useText();
  const title = useCompanyTitle();
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'legalName', 'baseCurrencyCode', 'status']}
      canCreate={writable && can('org.companies.create')} canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')} description={t('companies.description')}
      onCreate={() => { void navigate(`${COMPANIES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${COMPANIES_PATH}/${row.id}`); }}
      rowLabel={(row) => title(row)} />
  );
}

const FIELDS = ['code', 'nameI18n', 'legalName', 'countryCode', 'baseCurrencyCode', 'defaultLanguage', 'holidayCalendarId', 'logoFileId', 'costCenterModel', 'status'];

function CompanyRecord() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable } = useShell();
  const title = useCompanyTitle();
  const defaults = { status: 'active', costCenterModel: 'none', defaultLanguage: 'en', countryCode: 'PK', baseCurrencyCode: 'PKR', nameI18n: {}, isActive: true };
  const editors = { defaultLanguage: languageEditor, holidayCalendarId: calendarEditor, logoFileId: logoEditor(id ?? null) };
  return (
    <>
      <FormPage metadata={metadata} schema={schema} id={id ?? null} fields={FIELDS} defaults={defaults} editors={editors}
        canCreate={writable && can('org.companies.create')} canUpdate={writable && can('org.companies.update', id)} canDelete={false}
        canHistory={can('aud.history.view')} recordTitle={title}
        help={{ code: t('companies.help.code'), baseCurrencyCode: t('companies.help.currency'), status: t('companies.help.status') }}
        ruleTexts={{ [LIMIT_DETAIL]: t('companies.limit') }}
        onSaved={(row, created, how) => {
          toast.success(created ? t('companies.created', { company: title(row) }) : t('companies.saved', { company: title(row) }));
          if (how === 'form' && created) void navigate(`${COMPANIES_PATH}/${row.id}`, { replace: true });
        }}
        onDeleted={() => { void navigate(COMPANIES_PATH); }} onCancel={() => { void navigate(COMPANIES_PATH); }} />
      {id && (
        <div className="erp-page erp-company-parts">
          <LetterheadLink companyId={id} />
          {can('org.company_registrations.read', id) && <RegistrationsPanel companyId={id} canCreate={writable && can('org.company_registrations.create', id)} canUpdate={writable && can('org.company_registrations.update', id)} />}
          {can('org.company_bank_accounts.read', id) && <BankAccountsPanel companyId={id} canCreate={writable && can('org.company_bank_accounts.create', id)}
            canUpdate={writable && can('org.company_bank_accounts.update', id)} canDelete={writable && can('org.company_bank_accounts.delete', id)} />}
          {can('cfg.setting_values.read', id) && <SettingOverridesPanel companyId={id} />}
        </div>
      )}
    </>
  );
}
