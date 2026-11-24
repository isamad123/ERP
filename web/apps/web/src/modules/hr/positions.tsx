// Grades, headcount budgets and positions (slice S1.22b Build item 10; FR-HRM-POS-01, -02, -06). Grades and budgets are made by the
// engine from the generated metadata (their writes are the generated ones). Positions are listed by the engine (read only) and written
// through the position service: a new position, a change from a date (a new row, the history stays), freeze or reopen, and close.
// The server checks every request again (permissions, data scope, cycles, budgets); the screen shows what it answered.
import { useMemo } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import gradeMeta from '@erp/generated/metadata/hrm.grades.json';
import gradeSchema from '@erp/generated/validation/hrm.grades.json';
import budgetMeta from '@erp/generated/metadata/hrm.headcount_budgets.json';
import budgetSchema from '@erp/generated/validation/hrm.headcount_budgets.json';
import positionMeta from '@erp/generated/metadata/hrm.positions.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import PositionForm from './position-form';
import './positions-messages';
import './positions.css';

const api: EngineApi = { send: sendText };
export const POSITIONS_PATH = '/hr/positions';
export const GRADES_PATH = '/hr/grades';
export const BUDGETS_PATH = '/hr/headcount';

function useCompanyOptions(permission?: string) {
  const { me, can } = useShell();
  const { locale } = useLocaleSettings();
  return useMemo(() => me.companies.filter((c) => !permission || can(permission, c.id)).map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, can, permission, locale]);
}

// ------------------------------------------------------------------------------------------------------------------ grades

export function GradesScreen() {
  return <EngineProvider api={api}><Routes><Route index element={<Grades />} /><Route path="new" element={<GradeForm />} /><Route path=":id" element={<GradeForm />} /></Routes></EngineProvider>;
}

function Grades() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (
    <ListPage metadata={gradeMeta as TableMetadata} columns={['code', 'nameI18n', 'rank', 'companyId']} companies={{ activeId: activeCompany?.id ?? null, options: useCompanyOptions() }}
      canCreate={writable && can('hrm.grades.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')} description={t('pos.grades.description')}
      onCreate={() => { void navigate(`${GRADES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${GRADES_PATH}/${row.id}`); }}
      rowLabel={(row) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim()} />
  );
}

function GradeForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { can, writable, activeCompany } = useShell();
  const { locale } = useLocaleSettings();
  const creatable = useCompanyOptions('hrm.grades.create');
  const all = useCompanyOptions();
  const title = (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
  return (
    <FormPage metadata={gradeMeta as TableMetadata} schema={gradeSchema as ValidationSchema} id={id ?? null} fields={['companyId', 'code', 'nameI18n', 'rank', 'defaults', 'isActive']}
      defaults={{ companyId: creatable.find((c) => c.id === activeCompany?.id)?.id ?? creatable[0]?.id ?? null, isActive: true, nameI18n: {}, defaults: {} }} companies={id ? all : creatable}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('hrm.grades.update')} canDelete={writable && can('hrm.grades.delete')} canHistory={can('aud.history.view')} recordTitle={title}
      onSaved={(row, created, how) => { toast.success(title(row)); if (how === 'form') void navigate(created ? `${GRADES_PATH}/${row.id}` : GRADES_PATH, { replace: created }); }}
      onDeleted={() => { void navigate(GRADES_PATH); }} onCancel={() => { void navigate(GRADES_PATH); }} />
  );
}

// ------------------------------------------------------------------------------------------------------------------ budgets

export function BudgetsScreen() {
  return <EngineProvider api={api}><Routes><Route index element={<Budgets />} /><Route path="new" element={<BudgetForm />} /><Route path=":id" element={<BudgetForm />} /></Routes></EngineProvider>;
}

function Budgets() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  return (
    <ListPage metadata={budgetMeta as TableMetadata} columns={['orgUnitId', 'gradeId', 'sanctionedCount', 'validFrom', 'validTo', 'companyId']} companies={{ activeId: activeCompany?.id ?? null, options: useCompanyOptions() }}
      canCreate={writable && can('hrm.headcount_budgets.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')} description={t('pos.budgets.description')}
      onCreate={() => { void navigate(`${BUDGETS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${BUDGETS_PATH}/${row.id}`); }} rowLabel={(row) => String(row.sanctionedCount ?? '')} />
  );
}

function BudgetForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('hrm.headcount_budgets.create');
  const all = useCompanyOptions();
  return (
    <FormPage metadata={budgetMeta as TableMetadata} schema={budgetSchema as ValidationSchema} id={id ?? null} fields={['companyId', 'orgUnitId', 'gradeId', 'sanctionedCount', 'validFrom', 'validTo']}
      defaults={{ companyId: creatable.find((c) => c.id === activeCompany?.id)?.id ?? creatable[0]?.id ?? null }} companies={id ? all : creatable}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('hrm.headcount_budgets.update')} canDelete={writable && can('hrm.headcount_budgets.delete')} canHistory={can('aud.history.view')}
      recordTitle={(row) => String(row.sanctionedCount ?? '')}
      onSaved={(row, created, how) => { toast.success(String(row.sanctionedCount ?? '')); if (how === 'form') void navigate(created ? `${BUDGETS_PATH}/${row.id}` : BUDGETS_PATH, { replace: created }); }}
      onDeleted={() => { void navigate(BUDGETS_PATH); }} onCancel={() => { void navigate(BUDGETS_PATH); }} />
  );
}

// ------------------------------------------------------------------------------------------------------------------ positions

export default function PositionsScreen() {
  return <EngineProvider api={api}><Routes><Route index element={<PositionList />} /><Route path="new" element={<PositionForm />} /><Route path=":id" element={<PositionForm />} /></Routes></EngineProvider>;
}

function PositionList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  return (
    <ListPage metadata={positionMeta as TableMetadata} columns={['positionCode', 'title', 'orgUnitId', 'gradeId', 'budgetedFte', 'status', 'validFrom', 'companyId']} companies={{ activeId: activeCompany?.id ?? null, options: useCompanyOptions() }}
      canCreate={writable && can('hrm.positions.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')} description={t('pos.positions.description')}
      onCreate={() => { void navigate(`${POSITIONS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${POSITIONS_PATH}/${row.id}`); }} rowLabel={(row) => `${String(row.positionCode ?? '')} ${String(row.title ?? '')}`.trim()} />
  );
}
