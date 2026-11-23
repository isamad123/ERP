// Parts shared by the policy screens (slice S1.23a Build item 6): the company in hand (from the address, else the person's default), the
// type catalogue, the sections of the area, a date field typed as yyyy-mm-dd, and rules in words.
import { useQuery } from '@tanstack/react-query';
import { NavLink, useSearchParams } from 'react-router';
import { parseDate } from '@internationalized/date';
import { DateField, SelectField, type Option } from '@erp/ui';
import { formatDate, isolate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { policiesApi, type Condition, type ConditionEvaluation, type PolicyType, type RuleNames } from '../../api/policies';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { conditionWords, type WordContext } from './policies-model';
import './policies-messages';

export const POLICIES_PATH = '/hr/policies';
export const ASSIGNMENTS_PATH = `${POLICIES_PATH}/assignments`;
export const RESOLUTION_PATH = '/hr/policy-resolution';

/** Search parameters of the address, changed in place (back and forward keep working). */
export function useParamsState() {
  const [params, setParams] = useSearchParams();
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });
  return { params, patch };
}

/** The company the screen works in: `?company=`, else the person's default company, else their first. */
export function useCompany(): string {
  const { me } = useShell();
  const { params } = useParamsState();
  return params.get('company') ?? me.defaultCompanyId ?? me.companies[0]?.id ?? '';
}

export function CompanyPicker({ onChange }: { onChange?: () => void }) {
  const { t } = useText();
  const { me } = useShell();
  const { locale } = useLocaleSettings();
  const companyId = useCompany();
  const { patch } = useParamsState();
  if (me.companies.length < 2) return null;
  const options: Option[] = me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }));
  return <SelectField label={t('pol.company')} options={options} value={companyId} onChange={(id) => { patch({ company: id }); onChange?.(); }} />;
}

export function useTypes() {
  return useQuery({ queryKey: ['hrm', 'policy-types'], queryFn: policiesApi.types, staleTime: 300_000 });
}

/** The name of a type in the person's language (the catalogue's names; the code when unknown). */
export function useTypeName() {
  const { locale } = useLocaleSettings();
  const types = useTypes();
  return (code: string) => {
    const type = types.data?.items.find((x: PolicyType) => x.code === code);
    return type ? nameOf(type.name, locale) || code : code;
  };
}

export function Sections() {
  const { t } = useText();
  const [params] = useSearchParams();
  const company = params.get('company');
  const suffix = company ? `?company=${encodeURIComponent(company)}` : '';
  const link = ({ isActive }: { isActive: boolean }) => `erp-pol-section-link${isActive ? ' erp-pol-section-link-current' : ''}`;
  return (
    <nav className="erp-pol-sections" aria-label={t('pol.sections')}>
      <NavLink end to={`${POLICIES_PATH}${suffix}`} className={link}>{t('pol.section.policies')}</NavLink>
      <NavLink to={`${ASSIGNMENTS_PATH}${suffix}`} className={link}>{t('pol.section.assignments')}</NavLink>
      <NavLink to={`${RESOLUTION_PATH}${suffix}`} className={link}>{t('pol.section.resolution')}</NavLink>
    </nav>
  );
}

/** A date as the server takes it (yyyy-mm-dd); empty is null. */
export function DateInput({ label, value, onChange, help, error, isRequired }: { label: string; value: string | null; onChange: (value: string | null) => void; help?: string; error?: string | null; isRequired?: boolean }) {
  let parsed = null;
  try { parsed = value ? parseDate(value) : null; } catch { parsed = null; }
  return <DateField label={label} value={parsed} onChange={(v) => onChange(v ? v.toString() : null)} error={error ?? null} isRequired={isRequired ?? false} {...(help ? { help } : {})} />;
}

/** The labels of the employee details rules use. */
export const attributeKey = (attribute: string): MessageKey => `pol.attr.${attribute}` as MessageKey;

/** A condition in plain words ("Grade is one of G5, G6"; "Unit is Finance and its sub-units"). */
export function useConditionText() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (c: Pick<Condition, 'attribute' | 'field' | 'operator' | 'values' | 'subUnits'> | ConditionEvaluation, names: RuleNames) => {
    const ctx: WordContext = { names, locale, yes: t('pol.value.yes'), no: t('pol.value.no') };
    const words = conditionWords({ ...c, field: c.field ?? undefined } as Condition, ctx);
    const attribute = c.attribute === 'custom' ? words.field : t(attributeKey(c.attribute));
    const values = words.subUnits ? t('pol.cond.subUnits', { values: isolate(words.values) }) : isolate(words.values);
    return t(words.key, { attribute, values });
  };
}

/** The employee's value of a condition in words (or "none"). */
export function useActualText() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (c: ConditionEvaluation, names: RuleNames) => {
    if (c.hidden) return t('pol.value.hidden');
    if (c.actual.length === 0) return t('pol.value.none');
    const ctx: WordContext = { names, locale, yes: t('pol.value.yes'), no: t('pol.value.no') };
    return conditionWords({ attribute: c.attribute, field: c.field ?? undefined, operator: 'equals', values: c.actual, subUnits: false } as Condition, ctx).values;
  };
}

/** A business date (yyyy-mm-dd) in words; read as a calendar day, never shifted by a time zone. */
export function useDay() {
  const { locale, numbers } = useLocaleSettings();
  return (d: string | null | undefined) => (d ? formatDate(`${d}T00:00:00Z`, locale === 'ur' ? 'ur' : 'en', 'UTC', numbers) : '');
}
