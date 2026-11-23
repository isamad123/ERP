// Pay groups (slice S1.34a Build item 1; FR-HRM-PAY-01, GHR-06): the list and form made by the engine from the generated metadata of
// hrm.pay_groups. Reads are the generated list; creating and changing go to the pay group service (the generated writes are closed),
// which checks the frequency, the cut-off and payment offsets and the periods already generated. The approval threshold is salary-class
// data: without access to it the field is not shown. An employee's pay group is never set here: it changes through an employment event.
import { useMemo } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/hrm.pay_groups.json';
import schemaJson from '@erp/generated/validation/hrm.pay_groups.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import './pay-messages';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const PAY_GROUPS_PATH = '/payroll/pay-groups';

export default function PayGroupsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<GroupList />} />
        <Route path="new" element={<GroupForm />} />
        <Route path=":id" element={<GroupForm />} />
      </Routes>
    </EngineProvider>
  );
}

function useGroupTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function useCompanyOptions(permission?: string) {
  const { me, can } = useShell();
  const { locale } = useLocaleSettings();
  return useMemo(() => me.companies.filter((c) => !permission || can(permission, c.id))
    .map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, can, permission, locale]);
}

function GroupList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const options = useCompanyOptions();
  const title = useGroupTitle();
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'frequency', 'currencyCode', 'companyId', 'isActive']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('hrm.pay_groups.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')}
      description={t('pay.groups.description')}
      onCreate={() => { void navigate(`${PAY_GROUPS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${PAY_GROUPS_PATH}/${row.id}`); }}
      rowLabel={(row) => title(row)} />
  );
}

function GroupForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('hrm.pay_groups.create');
  const all = useCompanyOptions();
  const title = useGroupTitle();
  const creating = !id;
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  const defaults = { companyId: inHand?.id ?? creatable[0]?.id ?? null, isActive: true, nameI18n: {} };
  return (
    <FormPage metadata={metadata} schema={schema} id={id ?? null}
      fields={['companyId', 'code', 'nameI18n', 'frequency', 'currencyCode', 'taxYearStartMonth', 'cutOffDay', 'paymentDay', 'companyBankAccountId', 'approvalThreshold', 'isActive']}
      defaults={defaults} companies={creating ? creatable : all}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('hrm.pay_groups.update')} canDelete={false}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{
        code: t('pay.groups.help.code'), currencyCode: t('pay.groups.help.currency'), taxYearStartMonth: t('pay.groups.help.startMonth'),
        cutOffDay: t('pay.groups.help.cutOff'), paymentDay: t('pay.groups.help.payment'), approvalThreshold: t('pay.groups.help.threshold'),
      }}
      onSaved={(row, created, how) => {
        toast.success(created ? t('pay.groups.created', { group: title(row) }) : how === 'activation'
          ? t(row.isActive ? 'pay.groups.activated' : 'pay.groups.deactivated', { group: title(row) }) : t('pay.groups.saved', { group: title(row) }));
        if (how === 'form') void navigate(created ? `${PAY_GROUPS_PATH}/${row.id}` : PAY_GROUPS_PATH, { replace: created });
      }}
      onDeleted={() => { void navigate(PAY_GROUPS_PATH); }}
      onCancel={() => { void navigate(PAY_GROUPS_PATH); }} />
  );
}
