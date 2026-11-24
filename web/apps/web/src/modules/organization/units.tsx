// Organization units, unit types and hierarchies (slice S1.14b Build items 7 and 8; FR-KRN-ORG-03, FR-ADM-ORG-03, FR-ADM-ORG-04):
// lists and forms made by the engine from the generated metadata; a unit has its code, names, type, company and site and can be
// deactivated. Where a unit sits is not on its form: that is the structure of a hierarchy and is changed on the org chart, from a
// date (the move service), so history is kept. Renaming the standard terms (for example Department to Wing) is done in the labels
// editor of the settings centre (S1.15b, risk R-089). The server checks every request again (permissions, scopes, rules).
import './organization.css';
import { useMemo, type ReactNode } from 'react';
import { NavLink, Route, Routes, useNavigate, useParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { EngineProvider, FormPage, ListPage, nameIn, type EngineApi, type FieldEditorProps, type FormValues, type Row, type TableMetadata, type ValidationSchema } from '@erp/engine';
import { locations as locationApi, orgUnitTypes as typeApi } from '@erp/generated/api/org';
import { Link, SelectField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import unitMeta from '@erp/generated/metadata/org.org_units.json';
import unitSchema from '@erp/generated/validation/org.org_units.json';
import typeMeta from '@erp/generated/metadata/org.org_unit_types.json';
import typeSchema from '@erp/generated/validation/org.org_unit_types.json';
import hierarchyMeta from '@erp/generated/metadata/org.org_hierarchies.json';
import hierarchySchema from '@erp/generated/validation/org.org_hierarchies.json';
import { sendText } from '../../api/client';
import { companyName, useShell } from '../../shell/context';

const api: EngineApi = { send: sendText };
export const UNITS_PATH = '/organization/org-units';
export const UNIT_TYPES_PATH = `${UNITS_PATH}/types`;
export const HIERARCHIES_PATH = `${UNITS_PATH}/hierarchies`;
/** The labels editor of the settings centre (S1.15b), filtered to the table's texts (R-210). */
export const LABELS_LINK = '/settings/labels?table=org.org_units';

export default function UnitsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<UnitList />} />
        <Route path="new" element={<UnitForm />} />
        <Route path="types" element={<TypeList />} />
        <Route path="types/new" element={<TypeForm />} />
        <Route path="types/:id" element={<TypeForm />} />
        <Route path="hierarchies" element={<HierarchyList />} />
        <Route path="hierarchies/new" element={<HierarchyForm />} />
        <Route path="hierarchies/:id" element={<HierarchyForm />} />
        <Route path=":id" element={<UnitForm />} />
      </Routes>
    </EngineProvider>
  );
}

/** The three lists of this screen and the way to the chart. */
function Sections({ children }: { children: ReactNode }) {
  const { t } = useText();
  const { can } = useShell();
  const link = ({ isActive }: { isActive: boolean }) => `erp-section-link${isActive ? ' erp-section-link-current' : ''}`;
  return (
    <>
      <nav className="erp-section-nav" aria-label={t('units.sections')}>
        <NavLink end to={UNITS_PATH} className={link}>{t('units.section.units')}</NavLink>
        {can('org.org_unit_types.read') && <NavLink to={UNIT_TYPES_PATH} className={link}>{t('units.section.types')}</NavLink>}
        {can('org.org_hierarchies.read') && <NavLink to={HIERARCHIES_PATH} className={link}>{t('units.section.hierarchies')}</NavLink>}
        {can('org.org_unit_links.read') && <NavLink to="/organization/org-chart" className={link}>{t('units.section.chart')}</NavLink>}
      </nav>
      {children}
    </>
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

/** Unit types by id, for the lists and the pickers. */
export function useUnitTypes() {
  const { locale } = useLocaleSettings();
  const list = useQuery({ queryKey: ['org', 'unit-types'], queryFn: () => typeApi.list({ limit: 200, sort: 'code' }), staleTime: 60_000 });
  return useMemo(() => {
    const items = (list.data?.items ?? []).map((u) => ({ id: u.id, code: u.code, name: u.nameI18n[locale] ?? u.nameI18n.en ?? u.code, isActive: u.isActive }));
    return { items, name: new Map(items.map((i) => [i.id, i.name])), isPending: list.isPending };
  }, [list.data, list.isPending, locale]);
}

function useSites() {
  const { locale } = useLocaleSettings();
  const list = useQuery({ queryKey: ['org', 'sites', 'all'], queryFn: () => locationApi.list({ limit: 200, sort: 'code' }), staleTime: 60_000 });
  return useMemo(() => (list.data?.items ?? []).map((l) => ({ id: l.id, companyId: l.companyId, name: l.nameI18n[locale] ?? l.nameI18n.en ?? l.code, code: l.code })), [list.data, locale]);
}

// ------------------------------------------------------------------------------------------------------------- units

const unitMetadata = unitMeta as TableMetadata;
const unitValidation = unitSchema as ValidationSchema;

function UnitList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany } = useShell();
  const { t } = useText();
  const options = useCompanyOptions();
  const title = useTitle();
  const types = useUnitTypes();
  const sites = useSites();
  const siteName = new Map(sites.map((s) => [s.id, s.name]));
  return (
    <Sections>
      <ListPage metadata={unitMetadata} columns={['code', 'nameI18n', 'unitTypeId', 'companyId', 'locationId', 'isActive']}
        companies={{ activeId: activeCompany?.id ?? null, options }}
        cells={{
          unitTypeId: (row) => <bdi>{types.name.get(String(row.unitTypeId)) ?? '—'}</bdi>,
          locationId: (row) => <bdi>{row.locationId ? siteName.get(String(row.locationId)) ?? '—' : '—'}</bdi>,
        }}
        canCreate={writable && can('org.org_units.create', activeCompany?.id)} canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')} description={t('units.description')}
        onCreate={() => { void navigate(`${UNITS_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${UNITS_PATH}/${row.id}`); }} rowLabel={(row) => title(row)} />
    </Sections>
  );
}

function UnitTypeSelect({ label, value, onChange, readOnly, error, help }: FieldEditorProps) {
  const types = useUnitTypes();
  const options = useMemo<Option[]>(() => types.items.filter((i) => i.isActive || i.id === value).map((i) => ({ id: i.id, label: i.name, detail: i.code })), [types.items, value]);
  return <SelectField label={label} isRequired isDisabled={readOnly} options={options} value={typeof value === 'string' ? value : null} onChange={onChange} error={error} {...(help ? { help } : {})} />;
}

function SiteSelect({ label, value, onChange, readOnly, error, help, values }: FieldEditorProps) {
  const { t } = useText();
  const sites = useSites();
  const none = '__none';
  const companyId = typeof values.companyId === 'string' ? values.companyId : null;
  const options = useMemo<Option[]>(() => [{ id: none, label: t('units.site.none') },
    ...sites.filter((s) => s.companyId === companyId).map((s): Option => ({ id: s.id, label: s.name, detail: s.code }))], [sites, companyId, t]);
  return <SelectField label={label} isDisabled={readOnly} options={options} value={typeof value === 'string' ? value : none} onChange={(id) => onChange(id === none ? null : id)} error={error} {...(help ? { help } : {})} />;
}

function UnitForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable, activeCompany } = useShell();
  const creatable = useCompanyOptions('org.org_units.create');
  const all = useCompanyOptions();
  const title = useTitle();
  const creating = !id;
  const inHand = creatable.find((c) => c.id === activeCompany?.id);
  const defaults = { companyId: inHand?.id ?? creatable[0]?.id ?? null, isActive: true, nameI18n: {}, locationId: null };
  return (
    <FormPage metadata={unitMetadata} schema={unitValidation} id={id ?? null} fields={['companyId', 'code', 'nameI18n', 'unitTypeId', 'locationId', 'isActive']} defaults={defaults}
      editors={{ unitTypeId: UnitTypeSelect, locationId: SiteSelect }} companies={creating ? creatable : all}
      canCreate={writable && creatable.length > 0} canUpdate={writable && can('org.org_units.update')} canDelete={writable && can('org.org_units.delete')}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ code: t('units.help.code'), locationId: t('units.help.site') }}
      deleteText={(row) => t('units.delete.body', { unit: title(row) })}
      onSaved={(row, created, how) => {
        toast.success(created ? t('units.created', { unit: title(row) }) : how === 'activation'
          ? t(row.isActive ? 'units.activated' : 'units.deactivated', { unit: title(row) }) : t('units.saved', { unit: title(row) }));
        if (how === 'form') void navigate(created ? `${UNITS_PATH}/${row.id}` : UNITS_PATH, { replace: created });
      }}
      onDeleted={() => { toast.success(t('units.deleted')); void navigate(UNITS_PATH); }}
      onCancel={() => { void navigate(UNITS_PATH); }} />
  );
}

// ------------------------------------------------------------------------------------------------------- unit types

const typeMetadata = typeMeta as TableMetadata;
const typeValidation = typeSchema as ValidationSchema;

function TypeList() {
  const navigate = useNavigate();
  const { can, writable } = useShell();
  const { t } = useText();
  const title = useTitle();
  return (
    <Sections>
      <ListPage metadata={typeMetadata} columns={['code', 'nameI18n', 'levelHint', 'isActive']} canCreate={writable && can('org.org_unit_types.create')}
        canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')} description={t('unitTypes.description')}
        onCreate={() => { void navigate(`${UNIT_TYPES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${UNIT_TYPES_PATH}/${row.id}`); }} rowLabel={(row) => title(row)} />
      <p className="erp-page erp-help"><Link href={LABELS_LINK}>{t('unitTypes.rename.link')}</Link> {t('unitTypes.rename.help')}</p>
    </Sections>
  );
}

function TypeForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable } = useShell();
  const title = useTitle();
  return (
    <FormPage metadata={typeMetadata} schema={typeValidation} id={id ?? null} fields={['code', 'nameI18n', 'levelHint', 'isActive']} defaults={{ isActive: true, nameI18n: {} }}
      canCreate={writable && can('org.org_unit_types.create')} canUpdate={writable && can('org.org_unit_types.update')} canDelete={false}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ levelHint: t('unitTypes.help.level') }}
      onSaved={(row, created, how) => {
        toast.success(created ? t('unitTypes.created', { type: title(row) }) : how === 'activation'
          ? t(row.isActive ? 'unitTypes.activated' : 'unitTypes.deactivated', { type: title(row) }) : t('unitTypes.saved', { type: title(row) }));
        if (how === 'form') void navigate(created ? `${UNIT_TYPES_PATH}/${row.id}` : UNIT_TYPES_PATH, { replace: created });
      }}
      onDeleted={() => { void navigate(UNIT_TYPES_PATH); }} onCancel={() => { void navigate(UNIT_TYPES_PATH); }} />
  );
}

// -------------------------------------------------------------------------------------------------------- hierarchies

const hierarchyMetadata = hierarchyMeta as TableMetadata;
const hierarchyValidation = hierarchySchema as ValidationSchema;

function HierarchyList() {
  const navigate = useNavigate();
  const { can, writable } = useShell();
  const { t } = useText();
  const title = useTitle();
  return (
    <Sections>
      <ListPage metadata={hierarchyMetadata} columns={['code', 'nameI18n', 'kind', 'isActive']} canCreate={writable && can('org.org_hierarchies.create')}
        canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')} description={t('hierarchies.description')}
        onCreate={() => { void navigate(`${HIERARCHIES_PATH}/new`); }} onOpen={(row: Row) => { void navigate(`${HIERARCHIES_PATH}/${row.id}`); }} rowLabel={(row) => title(row)} />
    </Sections>
  );
}

function HierarchyForm() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const { t } = useText();
  const { can, writable } = useShell();
  const title = useTitle();
  return (
    <FormPage metadata={hierarchyMetadata} schema={hierarchyValidation} id={id ?? null} fields={['code', 'nameI18n', 'kind', 'isActive']} defaults={{ isActive: true, nameI18n: {}, kind: 'reporting' }}
      canCreate={writable && can('org.org_hierarchies.create')} canUpdate={writable && can('org.org_hierarchies.update')} canDelete={false}
      canHistory={can('aud.history.view')} recordTitle={title}
      help={{ kind: t('hierarchies.help.kind') }}
      onSaved={(row, created, how) => {
        toast.success(created ? t('hierarchies.created', { hierarchy: title(row) }) : how === 'activation'
          ? t(row.isActive ? 'hierarchies.activated' : 'hierarchies.deactivated', { hierarchy: title(row) }) : t('hierarchies.saved', { hierarchy: title(row) }));
        if (how === 'form') void navigate(created ? `${HIERARCHIES_PATH}/${row.id}` : HIERARCHIES_PATH, { replace: created });
      }}
      onDeleted={() => { void navigate(HIERARCHIES_PATH); }} onCancel={() => { void navigate(HIERARCHIES_PATH); }} />
  );
}
