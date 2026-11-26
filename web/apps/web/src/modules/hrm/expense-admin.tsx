// Expense categories and exchange rates (slice S1.42; FR-HRM-EXP-01, EXP-02, Q3): two lists and forms made by the engine from the generated metadata of `hrm.expense_categories` and
// `hrm.exchange_rates`. The limits by grade and the per-diem and mileage rates are the `expense` policy type (HR policies); this screen holds the categories they refer to by code.
import { useMemo } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import categoryMeta from '@erp/generated/metadata/hrm.expense_categories.json';
import categorySchema from '@erp/generated/validation/hrm.expense_categories.json';
import rateMeta from '@erp/generated/metadata/hrm.exchange_rates.json';
import rateSchema from '@erp/generated/validation/hrm.exchange_rates.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import './expenses-messages';

const api: EngineApi = { send: sendText };
export const CATEGORIES_PATH = '/payroll/expense-categories';
export const RATES_PATH = '/payroll/exchange-rates';

function useCompanyOptions(permission?: string) {
  const { me, can } = useShell();
  const { locale } = useLocaleSettings();
  return useMemo(() => me.companies.filter((c) => !permission || can(permission, c.id)).map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, can, permission, locale]);
}

// ------------------------------------------------------------------------------------------------------------------ categories

export function CategoriesScreen() {
  return <EngineProvider api={api}><Routes><Route index element={<Categories />} /><Route path="new" element={<CategoryForm />} /><Route path=":id" element={<CategoryForm />} /></Routes></EngineProvider>;
}

function Categories() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (
    <ListPage metadata={categoryMeta as TableMetadata} columns={['code', 'nameI18n', 'kind', 'requiresReceipt', 'flagsBlockSubmit', 'taxTreatment', 'reimbursementComponentId', 'isActive']}
      companies={{ activeId: activeCompany?.id ?? null, options: useCompanyOptions() }} canCreate={writable && can('hrm.expense_categories.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')}
      canExport={can('itg.exports.run')} description={t('expc.categories.description')}
      onCreate={() => { void navigate(`${CATEGORIES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${CATEGORIES_PATH}/${row.id}`); }}
      rowLabel={(row) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim()} />
  );
}

function CategoryForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const { locale } = useLocaleSettings();
  const creatable = useCompanyOptions('hrm.expense_categories.create');
  const all = useCompanyOptions();
  const title = (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  return (
    <FormPage metadata={categoryMeta as TableMetadata} schema={categorySchema as ValidationSchema} id={id ?? null}
      fields={['companyId', 'code', 'nameI18n', 'kind', 'requiresReceipt', 'flagsBlockSubmit', 'taxTreatment', 'reimbursementComponentId', 'isActive']}
      defaults={{ companyId: inHand?.id ?? creatable[0]?.id ?? null, isActive: true, nameI18n: {}, kind: 'general', requiresReceipt: false, flagsBlockSubmit: false }}
      companies={id ? all : creatable} canCreate={writable && creatable.length > 0} canUpdate={writable && can('hrm.expense_categories.update')} canDelete={writable && can('hrm.expense_categories.delete')}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ code: t('expc.categories.help.code'), kind: t('expc.categories.help.kind'), taxTreatment: t('expc.categories.help.tax'), reimbursementComponentId: t('expc.categories.help.component') }}
      deleteText={(row) => t('expc.categories.delete.body', { category: title(row) })}
      onSaved={(row, created, how) => {
        toast.success(created ? t('expc.categories.created', { category: title(row) }) : t('expc.categories.saved', { category: title(row) }));
        if (how === 'form') void navigate(created ? `${CATEGORIES_PATH}/${row.id}` : CATEGORIES_PATH, { replace: created });
      }}
      onDeleted={() => { toast.success(t('expc.categories.deleted')); void navigate(CATEGORIES_PATH); }}
      onCancel={() => { void navigate(CATEGORIES_PATH); }} />
  );
}

// ------------------------------------------------------------------------------------------------------------------ exchange rates

export function RatesScreen() {
  return <EngineProvider api={api}><Routes><Route index element={<Rates />} /><Route path="new" element={<RateForm />} /><Route path=":id" element={<RateForm />} /></Routes></EngineProvider>;
}

function Rates() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  return (
    <ListPage metadata={rateMeta as TableMetadata} columns={['fromCurrency', 'toCurrency', 'rateDate', 'rate', 'companyId']}
      companies={{ activeId: activeCompany?.id ?? null, options: useCompanyOptions() }} canCreate={writable && can('hrm.exchange_rates.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')}
      canExport={can('itg.exports.run')} description={t('expc.rates.description')}
      onCreate={() => { void navigate(`${RATES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${RATES_PATH}/${row.id}`); }}
      rowLabel={(row) => `${String(row.fromCurrency ?? '')} ${String(row.toCurrency ?? '')} ${String(row.rateDate ?? '')}`.trim()} />
  );
}

function RateForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('hrm.exchange_rates.create');
  const all = useCompanyOptions();
  const title = (row: FormValues) => `${String(row.fromCurrency ?? '')} ${String(row.toCurrency ?? '')} ${String(row.rateDate ?? '')}`.trim();
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  return (
    <FormPage metadata={rateMeta as TableMetadata} schema={rateSchema as ValidationSchema} id={id ?? null} fields={['companyId', 'fromCurrency', 'toCurrency', 'rateDate', 'rate']}
      defaults={{ companyId: inHand?.id ?? creatable[0]?.id ?? null }} companies={id ? all : creatable}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('hrm.exchange_rates.update')} canDelete={false} canHistory={can('aud.history.view')} recordTitle={title}
      help={{ fromCurrency: t('expc.rates.help.from'), toCurrency: t('expc.rates.help.to'), rate: t('expc.rates.help.rate') }}
      onSaved={(row, created, how) => {
        toast.success(created ? t('expc.rates.created', { rate: title(row) }) : t('expc.rates.saved', { rate: title(row) }));
        if (how === 'form') void navigate(created ? `${RATES_PATH}/${row.id}` : RATES_PATH, { replace: created });
      }}
      onDeleted={() => { void navigate(RATES_PATH); }}
      onCancel={() => { void navigate(RATES_PATH); }} />
  );
}
