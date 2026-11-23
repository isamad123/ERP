// The pure rules of the policy screens (slice S1.23a Build item 6; FR-HRM-POL-03, FR-HRM-POL-06): version differences, schema defaults,
// rules in words and the set editor.
import { describe, expect, it } from 'vitest';
import type { AssignmentRow, ParamSchema, RuleNames } from '../../api/policies';
import { addAssignment, blankCondition, conditionWords, defaultFor, diffParameters, draftOf, errorAt, errorsOf, fieldsOf, move, newestFirst, parseNumber, toInputs, valueLabel, type WordContext } from './policies-model';

const probation: ParamSchema = {
  type: 'object', additionalProperties: false, required: ['months'],
  properties: {
    months: { type: 'integer', minimum: 1, maximum: 24, title: 'hrm.policy.param.probation.months' },
    extensionMonthsMax: { type: 'integer', minimum: 0, maximum: 24 },
    reviewer: { type: 'string', enum: ['line_manager_and_hr', 'hr'] },
  },
};
const notice: ParamSchema = {
  type: 'object', required: ['bands'],
  properties: { bands: { type: 'array', minItems: 1, items: { type: 'object', required: ['serviceMonthsFrom', 'days'], properties: { serviceMonthsFrom: { type: 'integer', minimum: 0 }, days: { type: 'integer', minimum: 0 } } } } },
};

const names: RuleNames = {
  records: { g5: { code: 'G5', name: { en: 'Grade five' } }, g6: { code: 'G6', name: { en: 'G6' } }, fin: { code: 'FIN', name: { en: 'Finance', ur: 'مالیات' } } },
  customFields: [{ key: 'shift_worker', dataType: 'bool', status: 'active', label: { en: 'Shift worker' }, options: [] },
    { key: 'region', dataType: 'choice', status: 'retired', label: { en: 'Region' }, options: [{ code: 'north', label: { en: 'North' } }] }],
  employeeTypes: [{ code: 'permanent', name: { en: 'Permanent', ur: 'مستقل' } }],
};
const ctx: WordContext = { names, locale: 'en', yes: 'Yes', no: 'No' };

describe('FR-HRM-POL-06 the difference between two versions', () => {
  it('lists what was added, removed and changed, key by key, and leaves out what stayed', () => {
    const lines = diffParameters({ months: 3, reviewer: 'hr', bands: [{ days: 30 }] }, { months: 6, extensionMonthsMax: 3, bands: [{ days: 30 }, { days: 60 }] });
    expect(lines).toEqual([
      { path: 'bands[1].days', kind: 'added', before: undefined, after: 60 },
      { path: 'extensionMonthsMax', kind: 'added', before: undefined, after: 3 },
      { path: 'months', kind: 'changed', before: 3, after: 6 },
      { path: 'reviewer', kind: 'removed', before: 'hr', after: undefined },
    ]);
    expect(diffParameters({ months: 3 }, { months: 3 })).toEqual([]);
  });

  it('orders versions newest first', () => {
    const v = (version: number) => ({ id: `v${version}`, version, status: 'active' as const, parameters: {}, isOverridden: false, layer: 'tenant' as const, validFrom: null, validTo: null, inForce: false, rowVersion: 1 });
    expect(newestFirst([v(1), v(3), v(2)]).map((x) => x.version)).toEqual([3, 2, 1]);
  });
});

describe('FR-HRM-POL-02 parameters from the type schema', () => {
  it('starts with the required values only, each at its lowest value or first choice', () => {
    expect(defaultFor(probation)).toEqual({ months: 1 });
    expect(defaultFor(notice)).toEqual({ bands: [{ serviceMonthsFrom: 0, days: 0 }] });
    expect(fieldsOf(probation).map((f) => [f.key, f.required])).toEqual([['months', true], ['extensionMonthsMax', false], ['reviewer', false]]);
  });

  it('keeps typed numbers exact and finds the server error of a field by its path', () => {
    expect(parseNumber(' 12 ', true)).toBe(12);
    expect(parseNumber('1.5', true)).toBeNull();
    expect(parseNumber('1.25', false)).toBe(1.25);
    expect(parseNumber('abc', false)).toBeNull();
    expect(errorAt({ 'parameters.bands[0].days': 'too high' }, 'bands[0].days')).toBe('too high');
  });
});

describe('FR-HRM-POL-03 rules in plain words', () => {
  it('names grades, units, employee types and custom values instead of ids and codes', () => {
    expect(valueLabel('grade', null, 'g5', ctx)).toBe('Grade five (G5)');
    expect(valueLabel('grade', null, 'g6', ctx)).toBe('G6');
    expect(valueLabel('unit', null, 'fin', { ...ctx, locale: 'ur' })).toBe('مالیات (FIN)');
    expect(valueLabel('employee_type', null, 'permanent', ctx)).toBe('Permanent');
    expect(valueLabel('custom', 'shift_worker', true, ctx)).toBe('Yes');
    expect(valueLabel('custom', 'region', 'north', ctx)).toBe('North');
    expect(valueLabel('grade', null, 'unknown-id', ctx)).toBe('unknown-id');
  });

  it('gives each operator its sentence and marks a unit with its sub-units', () => {
    expect(conditionWords({ attribute: 'grade', operator: 'in', values: ['g5', 'g6'] }, ctx)).toEqual({ key: 'pol.cond.in', values: 'Grade five (G5), G6', subUnits: false, field: '' });
    expect(conditionWords({ attribute: 'unit', operator: 'equals', values: ['fin'], subUnits: true }, ctx)).toMatchObject({ key: 'pol.cond.equals', subUnits: true });
    expect(conditionWords({ attribute: 'custom', field: 'shift_worker', operator: 'not_in', values: [false] }, ctx)).toMatchObject({ key: 'pol.cond.not_in', values: 'No', field: 'Shift worker' });
  });
});

describe('FR-HRM-POL-02 the set editor', () => {
  const rows: AssignmentRow[] = [
    { id: 'a2', policyId: 'p2', policyCode: 'ALL', policyName: { en: 'All' }, precedence: 20, rule: {}, validFrom: '2026-01-01', validTo: null },
    { id: 'a1', policyId: 'p1', policyCode: 'SENIOR', policyName: { en: 'Senior' }, precedence: 10, rule: { conditions: [{ attribute: 'unit', operator: 'equals', values: ['fin'], subUnits: true }] }, validFrom: '2026-01-01', validTo: null },
  ];

  it('starts from the set in force, in order, and sends it back as the server takes it', () => {
    const draft = draftOf(rows);
    expect(draft.map((a) => a.precedence)).toEqual([10, 20]);
    expect(toInputs(draft)).toEqual([
      { policyId: 'p1', precedence: 10, rule: { conditions: [{ attribute: 'unit', operator: 'equals', values: ['fin'], subUnits: true }] } },
      { policyId: 'p2', precedence: 20, rule: { conditions: [] } },
    ]);
  });

  it('adds after the last, moves by swapping orders, and keeps custom fields and sub-units only where they belong', () => {
    let draft = addAssignment(draftOf(rows), 'p3');
    expect(draft.map((a) => a.precedence)).toEqual([10, 20, 30]);
    draft = move(draft, draft[2]!.key, -1);
    expect(draft.map((a) => a.policyId)).toEqual(['p1', 'p3', 'p2']);
    expect(draft.map((a) => a.precedence)).toEqual([10, 20, 30]);
    expect(move(draft, draft[0]!.key, -1).map((a) => a.policyId)).toEqual(['p1', 'p3', 'p2']);
    const custom = blankCondition('custom', names);
    expect(custom.field).toBe('shift_worker');
    expect(toInputs([{ key: 'x', policyId: 'p', precedence: 1, conditions: [{ ...custom, values: [true] }, { ...blankCondition('grade', names), subUnits: true, values: ['g5'] }] }])[0]!.rule.conditions)
      .toEqual([{ attribute: 'custom', field: 'shift_worker', operator: 'in', values: [true] }, { attribute: 'grade', operator: 'in', values: ['g5'] }]);
  });

  it('picks the errors of one assignment out of the server answer', () => {
    expect(errorsOf({ 'assignments[1].rule': ['k1'], 'assignments[0].precedence': ['k2'], effectiveFrom: ['k3'] }, 1)).toEqual({ rule: ['k1'] });
  });
});
