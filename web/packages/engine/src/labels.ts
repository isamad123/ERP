// Labels of tables, fields and options (slice S1.12c Build item 15, S1.12 ruling Q11; FR-KRN-ORG-08, FR-KRN-MET-22):
// the customer's and company's overrides (cfg.labels, GET /api/kernel/labels) first, then the module's hand-kept Urdu
// labels (Urdu only), then the English labels the generator writes from the data design. The pseudo-locale (tests) shows
// the English label in pseudo text, so a label that bypasses this lookup stands out.
import { useMemo } from 'react';
import { pseudoText, useLocaleSettings } from '@erp/i18n';
import kernelEn from '@erp/generated/labels/kernel.en.json';
import hrmEn from '@erp/generated/labels/hrm.en.json';
import kernelUr from '@erp/i18n/labels/kernel.ur.json';
import hrmUr from '@erp/i18n/labels/hrm.ur.json';

type Catalogue = Readonly<Record<string, string>>;

const strip = (c: Record<string, string>): Catalogue => Object.fromEntries(Object.entries(c).filter(([k]) => !k.startsWith('_')));

/** Labels of every module: English from the generator, Urdu kept by hand (R-062: for a native speaker's review). */
export const LABELS: { en: Catalogue; ur: Catalogue } = {
  en: { ...strip(kernelEn), ...strip(hrmEn) },
  ur: { ...strip(kernelUr), ...strip(hrmUr) },
};

export type LabelFn = (key: string) => string;

/** One label in a language: override, then Urdu (for Urdu), then English, else the key itself (never blank). */
export function labelText(key: string, locale: string, overrides: Readonly<Record<string, string>>, labels = LABELS): string {
  if (locale === 'pseudo') return pseudoText(labels.en[key] ?? key);
  const override = overrides[key];
  if (override) return override;
  if (locale === 'ur') {
    const ur = labels.ur[key];
    if (ur) return ur;
  }
  return labels.en[key] ?? key;
}

/** The label lookup for the current language and the customer's overrides. */
export function useLabel(): LabelFn {
  const { locale, overrides } = useLocaleSettings();
  return useMemo(() => (key: string) => labelText(key, locale, overrides), [locale, overrides]);
}

/** Label keys the screens of a table show: the table, its form and list fields and their options. */
export function labelKeysOf(metadata: { labelKey: string; fields: { labelKey: string; system: boolean; options?: { labelKey: string }[] }[] }): string[] {
  return [
    metadata.labelKey,
    `${metadata.labelKey}.one`,
    ...metadata.fields.filter((f) => !f.system).flatMap((f) => [f.labelKey, ...(f.options ?? []).map((o) => o.labelKey)]),
  ];
}
