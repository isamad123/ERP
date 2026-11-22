// Number and date fields (slice S1.12a Build item 3; moved into their own module in S1.12c so that a screen that does not
// use them, such as the shell, does not load their date and number machinery).
import { DateField as AriaDateField, DateInput, DateSegment, Input, NumberField as AriaNumberField, type DateValue } from 'react-aria-components';
import { FieldLabel, Messages, type FieldBase } from './Fields';

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
