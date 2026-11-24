// Shifts (slice S1.28b Build item 9; FR-HRM-ATT-06): the list and form are made by the engine from the generated metadata of hrm.shifts. Reads are
// the generated list; creating and changing go to the shift service (the generated writes are closed): a change of the times, the type or the
// segments of a shift in use becomes a new version from the date in "Valid from" and the old version is kept. The weekly off pattern, the grace
// minutes and the least rest are not on the shift: they come from the shift policy.
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/hrm.shifts.json';
import schemaJson from '@erp/generated/validation/hrm.shifts.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import './rosters-messages';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const SHIFTS_PATH = '/time/shifts';

export default function ShiftsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<ShiftList />} />
        <Route path="new" element={<ShiftForm />} />
        <Route path=":id" element={<ShiftForm />} />
      </Routes>
    </EngineProvider>
  );
}

function useShiftTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function useCompanyOptions(permission?: string) {
  const { me, can } = useShell();
  const { locale } = useLocaleSettings();
  return me.companies.filter((c) => !permission || can(permission, c.id)).map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }));
}

function ShiftList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const options = useCompanyOptions();
  const title = useShiftTitle();
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'shiftType', 'startTime', 'endTime', 'validFrom', 'companyId']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('hrm.shifts.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')}
      description={t('ros.shifts.description')}
      onCreate={() => { void navigate(`${SHIFTS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${SHIFTS_PATH}/${row.id}`); }}
      rowLabel={(row) => title(row)} />
  );
}

function ShiftForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('hrm.shifts.create');
  const all = useCompanyOptions();
  const title = useShiftTitle();
  const creating = !id;
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  const defaults = { companyId: inHand?.id ?? creatable[0]?.id ?? null, isActive: true, crossesMidnight: false, nameI18n: {}, segments: [] };
  return (
    <FormPage metadata={metadata} schema={schema} id={id ?? null}
      fields={['companyId', 'code', 'nameI18n', 'shiftType', 'startTime', 'endTime', 'coreStart', 'coreEnd', 'crossesMidnight', 'minFullDayMinutes', 'validFrom', 'isActive']}
      defaults={defaults} companies={creating ? creatable : all}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('hrm.shifts.update')} canDelete={false}
      canHistory={can('aud.history.view')} recordTitle={title} help={{ validFrom: t('ros.shifts.help.validFrom') }}
      onSaved={(row, created, how) => {
        toast.success(t(created ? 'ros.shifts.created' : 'ros.shifts.saved', { shift: title(row) }));
        if (how === 'form') void navigate(created ? `${SHIFTS_PATH}/${row.id}` : SHIFTS_PATH, { replace: created });
      }}
      onDeleted={() => { void navigate(SHIFTS_PATH); }} onCancel={() => { void navigate(SHIFTS_PATH); }} />
  );
}
