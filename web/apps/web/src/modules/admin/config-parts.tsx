// Small parts shared by the labels editor, the option lists and the custom field definitions (slice S1.15b): turning a refused
// request into texts beside the fields, and a two-language name input.
import { useText } from '@erp/i18n';
import { TextField } from '@erp/ui';
import { ApiProblem } from '../../api/client';
import type { Names } from '../../api/workflow';

export interface RefusedSave {
  /** Texts by field name (the server's field names, e.g. `code`, `name.en`, `validation.maxLength`). */
  fields: Record<string, string>;
  /** A text for the whole request when no field is named (or as a headline over the fields). */
  summary: string;
}

/** The plain-words text of a refused request: each field's text in the person's language, else the server's text for the key. */
export function useRefused(): (error: unknown) => RefusedSave {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return { fields: {}, summary: t('error.network') };
      if (error.status === 403) return { fields: {}, summary: t('config.error.denied') };
      if (error.status === 409 && error.key === 'errors.data.concurrency_conflict') return { fields: {}, summary: t('config.error.stale') };
      const fields = Object.fromEntries(Object.entries(error.fieldErrors).map(([name, keys]) => [name, server(keys[0] ?? '') ?? t('config.error.field')]));
      const named = Object.keys(fields).length > 0;
      return { fields, summary: named ? t('config.error.fix') : (server(error.errors[0] ?? error.key) ?? t('config.error.generic')) };
    }
    return { fields: {}, summary: t('config.error.generic') };
  };
}

/** English and Urdu text side by side (English first); Urdu reads right to left. */
export function NamePair({ label, value, onChange, errors, isReadOnly, required = true, maxLength = 200 }: {
  label: string; value: Names; onChange: (value: Names) => void; errors?: { en?: string | undefined; ur?: string | undefined }; isReadOnly?: boolean; required?: boolean; maxLength?: number;
}) {
  const { t } = useText();
  return (
    <fieldset className="erp-fieldset">
      <legend className="erp-label">{label}{required && <span className="erp-required" aria-hidden="true" title={t('common.required')}>*</span>}</legend>
      <div className="erp-i18n-pair">
        <TextField label={t('config.inEnglish', { label })} value={value.en ?? ''} onChange={(v) => onChange({ ...value, en: v })} isReadOnly={isReadOnly ?? false} maxLength={maxLength} isRequired={required} error={errors?.en ?? null} />
        <div lang="ur" dir="rtl">
          <TextField label={t('config.inUrdu', { label })} value={value.ur ?? ''} onChange={(v) => onChange({ ...value, ur: v })} isReadOnly={isReadOnly ?? false} maxLength={maxLength} error={errors?.ur ?? null} />
        </div>
      </div>
    </fieldset>
  );
}

/** A name with empty languages left out. */
export const cleanNames = (names: Names): Names => Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim().length > 0).map(([k, v]) => [k, v.trim()]));
