// Locations (slice S1.14a Build item 4; FR-ADM-ORG-02, FR-KRN-ORG-02): the sites of a company. List and form come from the
// engine; the address (lines, city, province code, postal code, country), the time zone (a picker of IANA names) and the
// opening hours by weekday have editors of their own. Latitude and longitude are typed as numbers and checked for their range
// by the database (ruling Q3, D-36): no map is drawn and nothing is fetched from outside. The server decides everything.
import './organization.css';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useMemo } from 'react';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { useToast } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/org.locations.json';
import schemaJson from '@erp/generated/validation/org.locations.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';
import { addressEditor, branchEditor, hoursEditor, timeZoneEditor } from './location-editors';
import { calendarEditor } from './org-parts';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const api: EngineApi = { send: sendText };
export const LOCATIONS_PATH = '/organization/locations';

export default function LocationsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<LocationList />} />
        <Route path="new" element={<LocationForm />} />
        <Route path=":id" element={<LocationForm />} />
      </Routes>
    </EngineProvider>
  );
}

function useTitle() {
  const { locale } = useLocaleSettings();
  return (row: FormValues) => `${String(row.code ?? '')} ${nameIn(row.nameI18n, locale)}`.trim();
}

function useCompanyOptions(permission?: string) {
  const { me, can } = useShell();
  const { locale } = useLocaleSettings();
  return useMemo(() => me.companies.filter((c) => !permission || can(permission, c.id)).map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code })), [me.companies, can, permission, locale]);
}

function LocationList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const options = useCompanyOptions();
  const title = useTitle();
  return (
    <ListPage metadata={metadata} columns={['code', 'nameI18n', 'companyId', 'regionCode', 'timeZone', 'isActive']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('org.locations.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')} description={t('locations.description')}
      onCreate={() => { void navigate(`${LOCATIONS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${LOCATIONS_PATH}/${row.id}`); }} rowLabel={(row) => title(row)} />
  );
}

const FIELDS = ['companyId', 'code', 'nameI18n', 'branchId', 'address', 'regionCode', 'latitude', 'longitude', 'timeZone', 'holidayCalendarId', 'operatingHours', 'isActive'];

function LocationForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('org.locations.create');
  const all = useCompanyOptions();
  const title = useTitle();
  const creating = !id;
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  const defaults = { companyId: inHand?.id ?? creatable[0]?.id ?? null, isActive: true, nameI18n: {}, timeZone: 'Asia/Karachi', address: { country: 'PK' }, operatingHours: null };
  const editors = { address: addressEditor, branchId: branchEditor, timeZone: timeZoneEditor, operatingHours: hoursEditor, holidayCalendarId: calendarEditor };
  return (
    <FormPage metadata={metadata} schema={schema} id={id ?? null} fields={FIELDS} defaults={defaults} editors={editors} companies={creating ? creatable : all}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('org.locations.update')} canDelete={writable && can('org.locations.delete')}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ regionCode: t('locations.help.region'), latitude: t('locations.help.latitude'), longitude: t('locations.help.longitude'), branchId: t('locations.help.branch') }}
      deleteText={(row) => t('locations.delete.body', { location: title(row) })}
      onSaved={(row, created, how) => {
        toast.success(created ? t('locations.created', { location: title(row) }) : how === 'activation'
          ? t(row.isActive ? 'locations.activated' : 'locations.deactivated', { location: title(row) }) : t('locations.saved', { location: title(row) }));
        if (how === 'form') void navigate(created ? `${LOCATIONS_PATH}/${row.id}` : LOCATIONS_PATH, { replace: created });
      }}
      onDeleted={() => { toast.success(t('locations.deleted')); void navigate(LOCATIONS_PATH); }}
      onCancel={() => { void navigate(LOCATIONS_PATH); }} />
  );
}
