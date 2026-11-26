// The `expense` policy type (slice S1.42; FR-HRM-EXP-01) on the policy form: its rules are a list of rows (category code, optional grade code, what the figure limits, the amount) made from the type's schema,
// the two choices of "what the figure limits" have words in the person's language, rows are added and removed, and an amount typed stays the number typed.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { I18nRoot } from '@erp/i18n';
import type { JsonObject, ParamSchema } from '../../api/policies';
import { ParameterForm } from './policy-form';

const SCHEMA: ParamSchema = {
  type: 'object', additionalProperties: false, required: ['rules'],
  properties: {
    rules: {
      type: 'array', minItems: 1, maxItems: 500, title: 'hrm.policy.param.expense.rules',
      items: {
        type: 'object', additionalProperties: false, required: ['categoryCode', 'basis', 'amount'],
        properties: {
          categoryCode: { type: 'string', minLength: 1, maxLength: 40, title: 'hrm.policy.param.expense.category_code' },
          gradeCode: { type: 'string', minLength: 1, maxLength: 40, title: 'hrm.policy.param.expense.grade_code' },
          basis: { type: 'string', enum: ['per_line', 'per_unit'], title: 'hrm.policy.param.expense.basis' },
          amount: { type: 'number', minimum: 0, title: 'hrm.policy.param.expense.amount' },
        },
      },
    },
  },
};

function Harness({ start, onValue }: { start: JsonObject; onValue: (v: JsonObject) => void }) {
  const [value, setValue] = useState<JsonObject>(start);
  return <ParameterForm schema={SCHEMA} value={value} onChange={(v) => { setValue(v); onValue(v); }} />;
}

function open(start: JsonObject, locale: 'en' | 'ur' = 'en') {
  const seen = vi.fn();
  render(<I18nRoot locale={locale}><Harness start={start} onValue={seen} /></I18nRoot>);
  return seen;
}

afterEach(() => cleanup());

describe('FR-HRM-EXP-01 the expense policy on the policy form', () => {
  it('shows each rule as a row with its four fields named by the server texts', async () => {
    open({ rules: [{ categoryCode: 'TRAVEL', basis: 'per_line', amount: 5000 }, { categoryCode: 'PD', gradeCode: 'G3', basis: 'per_unit', amount: 3500.5 }] });
    expect(await screen.findByText('Limits and rates')).toBeTruthy();
    const rows = screen.getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    const second = within(rows[1] as HTMLElement);
    expect((await second.findByRole('textbox', { name: /Expense category/ })).getAttribute('value')).toBe('PD');
    expect(second.getByRole('textbox', { name: /Grade \(code\)/ }).getAttribute('value')).toBe('G3');
    expect(second.getByRole('textbox', { name: /Amount/ }).getAttribute('value')).toBe('3500.5');
    expect(second.getByRole('button', { name: /What the figure limits/ }).textContent).toContain('A rate per day or kilometre');
    expect(within(rows[0] as HTMLElement).getByRole('button', { name: /What the figure limits/ }).textContent).toContain('A limit for a whole line');
  });

  it('adds a row, edits an amount and removes a row, and the value follows', async () => {
    const seen = open({ rules: [{ categoryCode: 'TRAVEL', basis: 'per_line', amount: 5000 }] });
    const user = userEvent.setup();
    await screen.findByText('Limits and rates');
    await user.click(screen.getByRole('button', { name: 'Add a row' }));
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    const first = within(screen.getAllByRole('listitem')[0] as HTMLElement);
    const amount = first.getByRole('textbox', { name: /Amount/ });
    await user.clear(amount);
    await user.type(amount, '7500');
    await waitFor(() => expect(((seen.mock.calls.at(-1)?.[0] as JsonObject).rules as { amount: number }[])[0]?.amount).toBe(7500));
    await user.click(screen.getByRole('button', { name: 'Remove row 2' }));
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
  });

  it('shows the choices in Urdu', async () => {
    open({ rules: [{ categoryCode: 'KM', basis: 'per_unit', amount: 40 }] }, 'ur');
    await waitFor(() => expect(document.body.textContent).toContain('فی دن یا فی کلومیٹر شرح'));
  });
});
