// Branches (slice S1.12c Build item 19; FR-KRN-ORG-02, FR-KRN-AUD-07, FR-ADM-OPS-03, S1.12 rulings Q10, Q11, Q14): the
// first list and form screen, made by the engine from the generated metadata of org.branches. List with search, sort,
// filters, the company in hand or all the person's companies, saved views; create, edit, deactivate and delete; the
// database rule "every company keeps at least one branch" (SC-2) said plainly; the change history in a side panel.
// The server checks every request again (permissions, data scope, field security).
import { useMemo } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/org.branches.json';
import schemaJson from '@erp/generated/validation/org.branches.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const BRANCHES_PATH = '/organization/branches';

export default function BranchesScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<BranchList />} />
        <Route path="new" element={<BranchForm />} />
        <Route path=":id" element={<BranchForm />} />
      </Routes>
    </EngineProvider>
  );
}

function useBranchTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function useCompanyOptions(permission?: string) {
  const { me, can } = useShell();
  const { locale } = useLocaleSettings();
  return useMemo(() => me.companies.filter((c) => !permission || can(permission, c.id))
    .map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, can, permission, locale]);
}

function BranchList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const options = useCompanyOptions();
  const title = useBranchTitle();
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'companyId', 'isActive']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('org.branches.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')}
      description={t('branches.description')}
      onCreate={() => { void navigate(`${BRANCHES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${BRANCHES_PATH}/${row.id}`); }}
      rowLabel={(row) => title(row)} />
  );
}

function BranchForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('org.branches.create');
  const all = useCompanyOptions();
  const title = useBranchTitle();
  const creating = !id;
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  const defaults = { companyId: inHand?.id ?? creatable[0]?.id ?? null, isActive: true, nameI18n: {} };
  return (
    <FormPage metadata={metadata} schema={schema} id={id ?? null} fields={['companyId', 'code', 'nameI18n', 'isActive']} defaults={defaults}
      companies={creating ? creatable : all}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('org.branches.update')} canDelete={writable && can('org.branches.delete')}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ code: t('branches.help.code') }}
      ruleTexts={{ ck_branches_one_per_company: t('branches.rule.lastBranch') }}
      deleteText={(row) => t('branches.delete.body', { branch: title(row) })}
      onSaved={(row, created, how) => {
        toast.success(created ? t('branches.created', { branch: title(row) }) : how === 'activation'
          ? t(row.isActive ? 'branches.activated' : 'branches.deactivated', { branch: title(row) }) : t('branches.saved', { branch: title(row) }));
        if (how === 'form') void navigate(created ? `${BRANCHES_PATH}/${row.id}` : BRANCHES_PATH, { replace: created });
      }}
      onDeleted={() => { toast.success(t('branches.deleted')); void navigate(BRANCHES_PATH); }}
      onCancel={() => { void navigate(BRANCHES_PATH); }} />
  );
}
