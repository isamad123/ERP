// Editors of the location form (slice S1.14a Build item 4): the structured address, the branch of the chosen company, the
// time zone (a picker of IANA names, the browser knows them) and the opening hours by weekday. They keep their value valid;
// the database checks it again (time zone known, hours "HH:MM" with the opening before the closing).
import { useMemo, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { branches as branchApi } from '@erp/generated/api/org';
import type { FieldEditorProps } from '@erp/engine';
import { CheckboxField, ComboBoxField, SelectField, TextField, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';

// ------------------------------------------------------------------------------------------------------------- address

export interface Address { line1?: string; line2?: string; city?: string; postalCode?: string; country?: string }
const asAddress = (value: unknown): Address => (value && typeof value === 'object' && !Array.isArray(value) ? (value as Address) : {});

function AddressFields({ label, value, onChange, readOnly, error, help }: FieldEditorProps) {
  const { t } = useText();
  const a = asAddress(value);
  const set = (key: keyof Address) => (text: string) => onChange({ ...a, [key]: text });
  return (
    <fieldset className="erp-fieldset">
      <legend className="erp-label">{label}<span className="erp-required" aria-hidden="true">*</span></legend>
      <div className="erp-stack">
        <TextField label={t('locations.address.line1')} isRequired isReadOnly={readOnly} value={a.line1 ?? ''} onChange={set('line1')} maxLength={200} error={error} />
        <TextField label={t('locations.address.line2')} isReadOnly={readOnly} value={a.line2 ?? ''} onChange={set('line2')} maxLength={200} />
        <div className="erp-i18n-pair">
          <TextField label={t('locations.address.city')} isRequired isReadOnly={readOnly} value={a.city ?? ''} onChange={set('city')} maxLength={100} />
          <TextField label={t('locations.address.postalCode')} isCode isReadOnly={readOnly} value={a.postalCode ?? ''} onChange={set('postalCode')} maxLength={20} />
        </div>
        <TextField label={t('locations.address.country')} isCode isRequired isReadOnly={readOnly} value={a.country ?? ''} onChange={set('country')} maxLength={2} help={t('locations.address.country.help')} />
      </div>
      {help && <span className="erp-help">{help}</span>}
    </fieldset>
  );
}
export const addressEditor = (props: FieldEditorProps): ReactNode => <AddressFields {...props} />;

// ------------------------------------------------------------------------------------------------------------- branch

function BranchSelect({ label, value, onChange, readOnly, error, help, values }: FieldEditorProps) {
  const { locale } = useLocaleSettings();
  const companyId = typeof values.companyId === 'string' ? values.companyId : null;
  const list = useQuery({ queryKey: ['org', 'branches', companyId], queryFn: () => branchApi.list({ companyId: companyId ?? '', limit: 200, sort: 'code' }), enabled: companyId !== null, staleTime: 60_000 });
  const options = useMemo<Option[]>(() => (list.data?.items ?? []).map((b) => ({ id: b.id, label: b.nameI18n[locale] ?? b.nameI18n.en ?? b.code, detail: b.code })), [list.data, locale]);
  return <SelectField label={label} isRequired isDisabled={readOnly} options={options} value={typeof value === 'string' ? value : null} onChange={onChange} error={error} {...(help ? { help } : {})} />;
}
export const branchEditor = (props: FieldEditorProps): ReactNode => <BranchSelect {...props} />;

// ------------------------------------------------------------------------------------------------------------- time zone

/** The IANA names the browser knows (Chromium and Firefox list them; elsewhere a short list keeps the screen usable). */
export function zoneNames(): string[] {
  const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const names = typeof supported === 'function' ? supported('timeZone') : [];
  return names.length > 0 ? names : ['Asia/Karachi', 'Asia/Riyadh', 'Asia/Dubai', 'Europe/London', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'UTC'];
}

function ZoneSelect({ label, value, onChange, readOnly, error, help }: FieldEditorProps) {
  const options = useMemo<Option[]>(() => zoneNames().map((z) => ({ id: z, label: z })), []);
  const current = typeof value === 'string' && value ? value : null;
  const search = useMemo(() => async (text: string): Promise<Option[]> => options.filter((o) => o.label.toLowerCase().includes(text.trim().toLowerCase())).slice(0, 50), [options]);
  return <ComboBoxField label={label} isRequired isReadOnly={readOnly} value={current ? { id: current, label: current } : null} onChange={(o) => onChange(o ? o.id : null)} search={search} error={error} {...(help ? { help } : {})} />;
}
export const timeZoneEditor = (props: FieldEditorProps): ReactNode => <ZoneSelect {...props} />;

// ------------------------------------------------------------------------------------------------------------- opening hours

export const WEEKDAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const;
export type Weekday = (typeof WEEKDAYS)[number];
export type Hours = Partial<Record<Weekday, { closed: true } | { open: string; close: string }>>;
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

const asHours = (value: unknown): Hours => (value && typeof value === 'object' && !Array.isArray(value) ? (value as Hours) : {});

/** The problem with a day's times, or null: both in HH:MM and the opening before the closing. */
export function dayProblem(day: Hours[Weekday]): 'format' | 'order' | null {
  if (!day || 'closed' in day) return null;
  if (!TIME.test(day.open) || !TIME.test(day.close)) return 'format';
  return day.open >= day.close ? 'order' : null;
}

function HoursFields({ label, value, onChange, readOnly, error, help }: FieldEditorProps) {
  const { t } = useText();
  const hours = asHours(value);
  const change = (day: Weekday, next: Hours[Weekday] | undefined) => {
    const copy: Hours = Object.fromEntries(Object.entries(hours).filter(([key]) => key !== day || next !== undefined));
    if (next !== undefined) copy[day] = next;
    onChange(Object.keys(copy).length === 0 ? null : copy);
  };
  return (
    <fieldset className="erp-fieldset">
      <legend className="erp-label">{label}</legend>
      <p className="erp-help">{t('locations.hours.help')}</p>
      <table className="erp-hours">
        <thead><tr><th scope="col">{t('locations.hours.day')}</th><th scope="col">{t('locations.hours.closed')}</th><th scope="col">{t('locations.hours.open')}</th><th scope="col">{t('locations.hours.close')}</th></tr></thead>
        <tbody>
          {WEEKDAYS.map((day) => {
            const entry = hours[day];
            const closed = !!entry && 'closed' in entry;
            const problem = dayProblem(entry);
            const dayName = t(`locations.day.${day}`);
            return (
              <tr key={day}>
                <th scope="row">{dayName}</th>
                <td><CheckboxField label={t('locations.hours.closedOn', { day: dayName })} isSelected={closed} isDisabled={readOnly} onChange={(on) => change(day, on ? { closed: true } : { open: '09:00', close: '17:00' })} /></td>
                <td>
                  <TextField label={t('locations.hours.openOn', { day: dayName })} isCode inputMode="numeric" isReadOnly={readOnly || closed} value={entry && 'open' in entry ? entry.open : ''} maxLength={5}
                    onChange={(text) => change(day, { open: text, close: entry && 'close' in entry ? entry.close : '' })} error={problem ? t(`locations.hours.${problem}`) : null} />
                </td>
                <td>
                  <TextField label={t('locations.hours.closeOn', { day: dayName })} isCode inputMode="numeric" isReadOnly={readOnly || closed} value={entry && 'close' in entry ? entry.close : ''} maxLength={5}
                    onChange={(text) => change(day, { open: entry && 'open' in entry ? entry.open : '', close: text })} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {error && <span className="erp-field-error" role="alert">{error}</span>}
      {help && <span className="erp-help">{help}</span>}
    </fieldset>
  );
}
export const hoursEditor = (props: FieldEditorProps): ReactNode => <HoursFields {...props} />;
