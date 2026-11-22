// Parts of the settings centre (slice S1.15a): the typed value editor built from a setting's definition, the layered view and
// the history list with its revert action. Tokens and the design system only; every text is a message key.
import { useMemo } from 'react';
import { Button, CheckboxField, NumberField, SelectField, StatusChip, SwitchField, TextAreaField, TextField } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import type { ChangeInfo, SettingHistoryItem, SettingItem, SettingLayerItem } from '../../api/settings';
import { companyName, useShell } from '../../shell/context';
import { hasText, rulesOf, shortValue, titleOf, type Draft, type Parsed } from './settings-model';

/** The plain-words text of a refused value or change: the server's text for its key, else a general text. */
export function useChangeText() {
  const { t, server } = useText();
  return (error: unknown): string => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return t('error.network');
      return server(error.errors[0] ?? error.key) ?? t('settings.error.generic');
    }
    return t('settings.error.generic');
  };
}

export function problemText(t: ReturnType<typeof useText>['t'], parsed: Extract<Parsed, { ok: false }>): string {
  const limit = parsed.limit ?? 0;
  switch (parsed.problem) {
    case 'required': return t('settings.problem.required');
    case 'whole': return t('settings.problem.whole');
    case 'decimal': return t('settings.problem.decimal');
    case 'min': return t('settings.problem.min', { limit });
    case 'max': return t('settings.problem.max', { limit });
    case 'length': return t('settings.problem.length', { limit });
    case 'choice': return t('settings.problem.choice');
    case 'items': return t('settings.problem.items', { limit });
    case 'json': return t('settings.problem.json');
  }
}

export function Layer({ layer }: { layer: string }) {
  const { t } = useText();
  const key = `settings.layer.${layer}` as MessageKey;
  return <StatusChip status={layer === 'tenant' || layer === 'company' ? 'active' : 'draft'} label={t(key)} />;
}

/** A value editor for the type of the setting: switch, number with its unit and range, choice, list, text or JSON. */
export function ValueEditor({ setting, draft, onChange, error, isDisabled }: { setting: SettingItem; draft: Draft; onChange: (draft: Draft) => void; error: string | null; isDisabled: boolean }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const rules = rulesOf(setting);
  const label = titleOf(setting, locale);
  const help = rules.min !== null || rules.max !== null
    ? t('settings.range', { min: rules.min ?? '–', max: rules.max ?? '–', unit: setting.unit ? unitText(t, setting.unit) : '' })
    : undefined;
  switch (setting.type) {
    case 'bool':
      return <SwitchField label={label} isSelected={draft === true} onChange={onChange} isDisabled={isDisabled} />;
    case 'int':
      return (
        <NumberField label={label} value={draft === '' ? NaN : Number(draft)} isDisabled={isDisabled} error={error}
          {...(help ? { help } : {})}
          onChange={(n) => onChange(Number.isNaN(n) ? '' : String(n))} />
      );
    case 'choice':
      return (
        <SelectField label={label} value={String(draft)} isDisabled={isDisabled} error={error}
          options={(rules.choices ?? []).map((c) => ({ id: c, label: choiceLabel(t, setting.key, c) }))} onChange={onChange} />
      );
    case 'list':
      if (rules.choices) {
        const chosen = new Set(Array.isArray(draft) ? draft : []);
        return (
          <fieldset className="erp-stack">
            <legend>{label}</legend>
            {rules.choices.map((c) => (
              <CheckboxField key={c} label={choiceLabel(t, setting.key, c)} isSelected={chosen.has(c)} isDisabled={isDisabled}
                onChange={(on) => onChange((rules.choices ?? []).filter((x) => (x === c ? on : chosen.has(x))))} />
            ))}
            {error && <p className="erp-field-error" role="alert">{error}</p>}
          </fieldset>
        );
      }
      return <TextField label={label} value={String(draft)} isDisabled={isDisabled} error={error} help={t('settings.list.help')} onChange={onChange} />;
    case 'json':
      return <TextAreaField label={label} value={String(draft)} isDisabled={isDisabled} error={error} onChange={onChange} />;
    case 'decimal':
      return <TextField label={label} value={String(draft)} inputMode="numeric" isDisabled={isDisabled} error={error} {...(help ? { help } : {})} onChange={onChange} />;
    default:
      return (rules.maxLength ?? 0) > 120
        ? <TextAreaField label={label} value={String(draft)} isDisabled={isDisabled} error={error} maxLength={rules.maxLength ?? 2000} onChange={onChange} />
        : <TextField label={label} value={String(draft)} isDisabled={isDisabled} error={error} {...(rules.maxLength ? { maxLength: rules.maxLength } : {})} onChange={onChange} />;
  }
}

/** The text of a choice: the web text when there is one, else the stored code itself. */
export function choiceLabel(t: ReturnType<typeof useText>['t'], key: string, choice: string): string {
  const id = `settings.choice.${key}.${choice}`;
  return hasText(id) ? t(id) : choice;
}

/** The unit of a number setting in words ("minutes"); a unit without a text is shown as it is written. */
export function unitText(t: ReturnType<typeof useText>['t'], unit: string): string {
  const id = `settings.unit.${unit.replaceAll(' ', '_')}`;
  return hasText(id) ? t(id) : unit;
}

export function useWords() {
  const { t } = useText();
  return useMemo(() => ({ yes: t('engine.value.yes'), no: t('engine.value.no'), notSet: t('settings.notSet') }), [t]);
}

export function who(names: Map<string, string>, id: string): string {
  return names.get(id) ?? '—';
}

export function ChangedBy({ info, names }: { info: ChangeInfo | null; names: Map<string, string> }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  if (!info) return <span className="erp-muted">{t('settings.neverChanged')}</span>;
  return <span>{t('settings.changedBy', { when: formatDateTime(info.at, locale === 'ur' ? 'ur' : 'en', timeZone, numbers), who: who(names, info.by) })}</span>;
}

/** The layered view (FR-ADM-SET-03): every value that exists for this setting, the one in force first, and why a layer is ignored. */
export function LayerTable({ setting, layers, names }: { setting: SettingItem; layers: SettingLayerItem[]; names: Map<string, string> }) {
  const { t } = useText();
  const { numbers, locale } = useLocaleSettings();
  const { me } = useShell();
  const words = useWords();
  const scope = (l: SettingLayerItem) => (l.scopeId ? companyName(me.companies.find((c) => c.id === l.scopeId) ?? null, locale) || '—' : t('settings.scope.all'));
  const rows = [{ layer: 'product' as const, scopeId: null, value: setting.default, changed: null, ignored: null }, ...layers];
  return (
    <table className="erp-table" aria-label={t('settings.layers.title')}>
      <thead><tr><th scope="col">{t('settings.layers.layer')}</th><th scope="col">{t('settings.layers.scope')}</th><th scope="col">{t('settings.layers.value')}</th><th scope="col">{t('settings.layers.changed')}</th></tr></thead>
      <tbody>
        {rows.map((l, i) => (
          <tr key={`${l.layer}-${l.scopeId ?? ''}-${i}`}>
            <td><Layer layer={l.layer} /></td>
            <td>{l.layer === 'product' ? '' : scope(l as SettingLayerItem)}</td>
            <td><bdi>{shortValue(setting, l.value, numbers, words)}</bdi>{l.ignored && <span className="erp-small erp-muted"> {t('settings.layers.ignored', { why: hasText(`settings.ignored.${l.ignored}`) ? t(`settings.ignored.${l.ignored}` as MessageKey) : l.ignored })}</span>}</td>
            <td>{l.layer === 'product' ? '' : <ChangedBy info={l.changed} names={names} />}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function HistoryList({ setting, items, names, onRevert }: { setting: SettingItem; items: SettingHistoryItem[]; names: Map<string, string>; onRevert: ((item: SettingHistoryItem) => void) | null }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { me } = useShell();
  const words = useWords();
  if (items.length === 0) return <p className="erp-muted">{t('settings.history.empty')}</p>;
  return (
    <ol className="erp-settings-history" aria-label={t('settings.history.title')}>
      {items.map((h) => (
        <li key={h.id} className="erp-stack">
          <span>
            <strong>{formatDateTime(h.at, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</strong> · {who(names, h.by)} · <Layer layer={h.layer} />
            {h.scopeId && <> · {companyName(me.companies.find((c) => c.id === h.scopeId) ?? null, locale)}</>}
          </span>
          <span><bdi>{h.oldValue === null || h.oldValue === undefined ? t('settings.history.none') : shortValue(setting, h.oldValue, numbers, words)}</bdi> → <bdi>{h.newValue === null || h.newValue === undefined ? t('settings.history.removed') : shortValue(setting, h.newValue, numbers, words)}</bdi></span>
          {h.reason && <span className="erp-small erp-muted">{h.reason}</span>}
          {onRevert && <span><Button variant="quiet" onPress={() => onRevert(h)}>{t('settings.history.revert')}</Button></span>}
        </li>
      ))}
    </ol>
  );
}
