// Form controls (spec 13 sections 7 and 9; slice S1.12a Build item 3): text, password (password managers and paste
// allowed, FR-UX-SEC-07), text area, number, date, select, combobox with server lookup, checkbox, switch and one-time
// code. Each has a visible label, help text, required marker and an error message tied to the control for screen readers.
import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Button as AriaButton, Checkbox as AriaCheckbox, Radio, RadioGroup, ComboBox as AriaComboBox, DateField as AriaDateField, DateInput, DateSegment, FieldError,
  Input, Label, ListBox, ListBoxItem, NumberField as AriaNumberField, Popover, Select as AriaSelect, SelectValue, Switch as AriaSwitch, Text,
  TextArea, TextField as AriaTextField, type DateValue, type Key,
} from 'react-aria-components';
import { AlertCircle, Check, ChevronDown, Eye, EyeOff } from 'lucide-react';
import { useText } from '@erp/i18n';

interface FieldBase {
  label: string;
  help?: ReactNode;
  /** A server or validation message shown under the field and announced with it. */
  error?: string | null;
  isRequired?: boolean;
  isDisabled?: boolean;
  isReadOnly?: boolean;
  name?: string;
}

function FieldLabel({ label, isRequired }: { label: string; isRequired?: boolean | undefined }) {
  const { t } = useText();
  return (
    <Label className="erp-label">
      {label}
      {isRequired && <span className="erp-required" aria-hidden="true" title={t('common.required')}>*</span>}
    </Label>
  );
}

function Messages({ help, error }: { help?: ReactNode; error?: string | null | undefined }) {
  return (
    <>
      {help && <Text slot="description" className="erp-help">{help}</Text>}
      <FieldError className="erp-field-error">
        {({ validationErrors }) => (
          <>
            <AlertCircle size={16} aria-hidden="true" />
            <span>{error ?? validationErrors.join(' ')}</span>
          </>
        )}
      </FieldError>
    </>
  );
}

export interface TextFieldProps extends FieldBase {
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  type?: 'text' | 'email' | 'search' | 'tel' | 'url';
  autoComplete?: string;
  inputMode?: 'text' | 'numeric' | 'email' | 'tel' | 'search' | 'url';
  maxLength?: number;
  /** Codes and identifiers read left to right inside Urdu text too. */
  isCode?: boolean;
}

export function TextField({ label, help, error, isCode, autoComplete, inputMode, maxLength, type = 'text', ...props }: TextFieldProps) {
  return (
    <AriaTextField {...props} type={type} className="erp-field" isInvalid={!!error} validationBehavior="aria" autoComplete={autoComplete}>
      <FieldLabel label={label} isRequired={props.isRequired} />
      <Input className={isCode ? 'erp-input erp-code' : 'erp-input'} inputMode={inputMode} maxLength={maxLength} />
      <Messages help={help} error={error} />
    </AriaTextField>
  );
}

export interface PasswordFieldProps extends FieldBase {
  value?: string;
  onChange?: (value: string) => void;
  /** `current-password` at sign-in, `new-password` when choosing one, so password managers fill or suggest. */
  autoComplete: 'current-password' | 'new-password';
}

/** Password input with show and hide; paste and password managers are allowed (FR-UX-SEC-07). */
export function PasswordField({ label, help, error, autoComplete, ...props }: PasswordFieldProps) {
  const { t } = useText();
  const [visible, setVisible] = useState(false);
  return (
    <AriaTextField {...props} type={visible ? 'text' : 'password'} className="erp-field" isInvalid={!!error} validationBehavior="aria" autoComplete={autoComplete}>
      <FieldLabel label={label} isRequired={props.isRequired} />
      <div className="erp-input-group">
        <Input className="erp-input" spellCheck={false} autoCapitalize="off" />
        <AriaButton className="erp-button erp-icon-button" data-variant="secondary" aria-label={visible ? t('ui.password.hide') : t('ui.password.show')}
          aria-pressed={visible} onPress={() => setVisible((v) => !v)}>
          {visible ? <EyeOff size={18} aria-hidden="true" /> : <Eye size={18} aria-hidden="true" />}
        </AriaButton>
      </div>
      <Messages help={help} error={error} />
    </AriaTextField>
  );
}

export function TextAreaField({ label, help, error, ...props }: FieldBase & { value?: string; onChange?: (value: string) => void; maxLength?: number }) {
  return (
    <AriaTextField {...props} className="erp-field" isInvalid={!!error} validationBehavior="aria">
      <FieldLabel label={label} isRequired={props.isRequired} />
      <TextArea className="erp-input" />
      <Messages help={help} error={error} />
    </AriaTextField>
  );
}

/** Whole numbers and counts. Money is never entered here: the money input keeps exact decimals (S1.12c, FR-UX-CMP-04). */
export function NumberField({ label, help, error, ...props }: FieldBase & { value?: number; onChange?: (value: number) => void; minValue?: number; maxValue?: number }) {
  return (
    <AriaNumberField {...props} className="erp-field" isInvalid={!!error} validationBehavior="aria" formatOptions={{ maximumFractionDigits: 0 }}>
      <FieldLabel label={label} isRequired={props.isRequired} />
      <Input className="erp-input" />
      <Messages help={help} error={error} />
    </AriaNumberField>
  );
}

/** A date typed by segments (day, month, year) in the person's language; Gregorian only in 1.0 (D-42). */
export function DateField({ label, help, error, ...props }: FieldBase & { value?: DateValue | null; onChange?: (value: DateValue | null) => void }) {
  return (
    <AriaDateField {...props} className="erp-field" isInvalid={!!error} validationBehavior="aria">
      <FieldLabel label={label} isRequired={props.isRequired} />
      <DateInput className="erp-date-input">{(segment) => <DateSegment segment={segment} className="erp-date-segment" />}</DateInput>
      <Messages help={help} error={error} />
    </AriaDateField>
  );
}

export interface Option {
  id: string;
  label: string;
  /** Second line: code, status or other context (FR-UX-CMP-06). */
  detail?: string;
  isDisabled?: boolean;
}

export function SelectField({ label, help, error, options, value, onChange, placeholder, ...props }: FieldBase & { options: Option[]; value?: string | null; onChange?: (id: string) => void; placeholder?: string }) {
  const { t } = useText();
  return (
    <AriaSelect {...props} className="erp-field" isInvalid={!!error} validationBehavior="aria" placeholder={placeholder ?? t('ui.select.placeholder')}
      {...(value === undefined ? {} : { selectedKey: value })} onSelectionChange={(key: Key | null) => { if (key !== null) onChange?.(String(key)); }}
      disabledKeys={options.filter((o) => o.isDisabled).map((o) => o.id)}>
      <FieldLabel label={label} isRequired={props.isRequired} />
      <AriaButton className="erp-select-button">
        <SelectValue />
        <ChevronDown size={16} aria-hidden="true" />
      </AriaButton>
      <Messages help={help} error={error} />
      <Popover className="erp-popover">
        <ListBox className="erp-listbox" items={options}>
          {(option) => <ListBoxItem id={option.id} className="erp-option" textValue={option.label}>{option.label}</ListBoxItem>}
        </ListBox>
      </Popover>
    </AriaSelect>
  );
}

/**
 * A lookup that asks the server as the person types (debounced 250 ms; FR-UX-CMP-06): code or name in any language,
 * with a second line of context. `search` gets the text and an AbortSignal; an older answer never replaces a newer one.
 */
export function ComboBoxField({ label, help, error, search, value, onChange, ...props }: FieldBase & {
  search: (text: string, signal: AbortSignal) => Promise<Option[]>;
  value?: Option | null;
  onChange?: (option: Option | null) => void;
}) {
  const { t } = useText();
  const [text, setText] = useState(value?.label ?? '');
  const [items, setItems] = useState<Option[]>(value ? [value] : []);
  const [loading, setLoading] = useState(false);
  const latest = useRef<AbortController | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    latest.current?.abort();
    latest.current = controller;
    const timer = window.setTimeout(() => {
      setLoading(true);
      search(text, controller.signal)
        .then((found) => { if (!controller.signal.aborted) setItems(found); })
        .catch(() => { if (!controller.signal.aborted) setItems([]); })
        .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    }, 250);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [text, search]);
  return (
    <AriaComboBox {...props} className="erp-field" isInvalid={!!error} validationBehavior="aria" items={items} inputValue={text} onInputChange={setText}
      allowsEmptyCollection selectedKey={value?.id ?? null}
      onSelectionChange={(key: Key | null) => onChange?.(items.find((i) => i.id === key) ?? null)} disabledKeys={items.filter((o) => o.isDisabled).map((o) => o.id)}>
      <FieldLabel label={label} isRequired={props.isRequired} />
      <div className="erp-input-group">
        <Input className="erp-input" />
        <AriaButton className="erp-button erp-icon-button" data-variant="secondary" aria-label={t('ui.combobox.showOptions')}>
          <ChevronDown size={16} aria-hidden="true" />
        </AriaButton>
      </div>
      <Messages help={help} error={error} />
      <Popover className="erp-popover">
        <ListBox className="erp-listbox" renderEmptyState={() => <div className="erp-combobox-empty">{loading ? t('ui.combobox.searching') : t('ui.combobox.noResults')}</div>}>
          {(option: Option) => (
            <ListBoxItem id={option.id} className="erp-option" textValue={option.label}>
              <span className="erp-stack">
                <span>{option.label}</span>
                {option.detail && <span className="erp-small erp-muted erp-code">{option.detail}</span>}
              </span>
            </ListBoxItem>
          )}
        </ListBox>
      </Popover>
    </AriaComboBox>
  );
}

export function CheckboxField({ label, help, isSelected, onChange, isDisabled }: { label: string; help?: ReactNode; isSelected?: boolean; onChange?: (selected: boolean) => void; isDisabled?: boolean }) {
  return (
    <AriaCheckbox className="erp-checkbox" {...(isSelected === undefined ? {} : { isSelected })} onChange={onChange} isDisabled={isDisabled}>
      {({ isSelected: selected }) => (
        <>
          <span className="erp-checkbox-box" aria-hidden="true">{selected && <Check size={14} />}</span>
          <span className="erp-stack">
            <span>{label}</span>
            {help && <span className="erp-help">{help}</span>}
          </span>
        </>
      )}
    </AriaCheckbox>
  );
}

export function SwitchField({ label, isSelected, onChange, isDisabled }: { label: string; isSelected?: boolean; onChange?: (selected: boolean) => void; isDisabled?: boolean }) {
  return (
    <AriaSwitch className="erp-switch" {...(isSelected === undefined ? {} : { isSelected })} onChange={onChange} isDisabled={isDisabled}>
      <span className="erp-switch-track" aria-hidden="true"><span className="erp-switch-thumb" /></span>
      <span>{label}</span>
    </AriaSwitch>
  );
}

/**
 * A one-time code (authenticator or e-mail): one input, digits only, read left to right, filled by the phone's
 * suggestion (`one-time-code`) or by paste. Spaces and dashes in a pasted code are removed.
 */
export function OneTimeCodeField({ label, help, error, value, onChange, length = 6, ...props }: FieldBase & { value: string; onChange: (value: string) => void; length?: number }) {
  return (
    <AriaTextField {...props} className="erp-field" isInvalid={!!error} validationBehavior="aria" value={value}
      onChange={(text) => onChange(text.replace(/[\s-]/g, '').replace(/[^0-9]/g, '').slice(0, length))} autoComplete="one-time-code">
      <FieldLabel label={label} isRequired={props.isRequired} />
      <Input className="erp-input erp-otp" inputMode="numeric" pattern="[0-9]*" maxLength={length + 4} />
      <Messages help={help} error={error} />
    </AriaTextField>
  );
}

/** A choice of one among a few (shown all at once); each option may carry a help line. */
export function RadioGroupField({ label, options, value, onChange, error, isRequired }: { label: string; options: Option[]; value: string; onChange: (id: string) => void; error?: string | null; isRequired?: boolean }) {
  return (
    <RadioGroup className="erp-field erp-radio-group" value={value} onChange={onChange} isInvalid={!!error} isRequired={isRequired} validationBehavior="aria">
      <FieldLabel label={label} isRequired={isRequired} />
      {options.map((option) => (
        <Radio key={option.id} value={option.id} className="erp-radio" isDisabled={option.isDisabled}>
          <span className="erp-radio-dot" aria-hidden="true" />
          <span className="erp-stack">
            <span>{option.label}</span>
            {option.detail && <span className="erp-help">{option.detail}</span>}
          </span>
        </Radio>
      ))}
      <Messages error={error} />
    </RadioGroup>
  );
}
