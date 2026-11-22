// How a stored value is shown in lists, forms and history (slice S1.12c): codes isolated and read left to right inside
// Urdu text, two-language names in the person's language, dates and times in their time zone, numbers in their digits,
// decimals as their exact digits, hidden and masked values as the server sends them (FR-KRN-IAM-13, GHR-06).
import type { ReactNode } from 'react';
import { StatusChip } from '@erp/ui';
import { formatDate, formatDateTime, formatDecimal, formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import type { FieldMetadata } from './metadata';
import type { LabelFn } from './labels';

/** The name in the language, else English, else any language given. */
export function nameIn(value: unknown, locale: string): string {
  if (!value || typeof value !== 'object') return typeof value === 'string' ? value : '';
  const names = value as Record<string, unknown>;
  const pick = names[locale === 'pseudo' ? 'en' : locale] ?? names.en ?? Object.values(names).find((v) => typeof v === 'string' && v.length > 0);
  return typeof pick === 'string' ? pick : '';
}

/** A value of one field as text and markup. `undefined`: the server left the field out (hidden by field security). */
export function Value({ field, value, label }: { field: FieldMetadata; value: unknown; label: LabelFn }): ReactNode {
  const { t } = useText();
  const { locale: uiLocale, timeZone, numbers } = useLocaleSettings();
  const locale = uiLocale === 'ur' ? 'ur' : 'en';
  if (value === undefined) return <span className="erp-muted">{t('engine.value.hidden')}</span>;
  if (value === null || value === '') return <span className="erp-muted" aria-label={t('engine.value.none')}>–</span>;
  // A masked value arrives as text such as "****1234" whatever the field's kind (S1.06 field shaping).
  if (typeof value === 'string' && /^\*{2,}/.test(value) && field.kind !== 'Code' && field.kind !== 'Text') {
    return <span className="erp-code"><bdi>{value}</bdi></span>;
  }
  switch (field.kind) {
    case 'Code':
    case 'FixedText':
      return <span className="erp-code"><bdi>{String(value)}</bdi></span>;
    case 'I18n':
      return <bdi>{nameIn(value, uiLocale)}</bdi>;
    case 'Bool':
      return field.field === 'isActive'
        ? <StatusChip status={value ? 'active' : 'inactive'} />
        : <span>{value ? t('engine.value.yes') : t('engine.value.no')}</span>;
    case 'Enum': {
      const option = field.options?.find((o) => o.value === value);
      return <span>{option ? label(option.labelKey) : String(value)}</span>;
    }
    case 'Date':
      // A business date has no time zone: shown as stored.
      return <span>{formatDate(String(value), locale, 'UTC', numbers)}</span>;
    case 'Timestamp':
      return <span>{formatDateTime(String(value), locale, timeZone, numbers)}</span>;
    case 'Int4':
    case 'BigInteger':
      return <span>{formatInteger(Number(value), numbers)}</span>;
    case 'Money':
    case 'Qty':
    case 'Rate':
    case 'Pct':
    case 'Numeric':
      return <span>{formatDecimal(String(value), numbers)}</span>;
    case 'Json':
      return <span className="erp-muted">{t('engine.value.structured')}</span>;
    default:
      return <bdi>{String(value)}</bdi>;
  }
}
