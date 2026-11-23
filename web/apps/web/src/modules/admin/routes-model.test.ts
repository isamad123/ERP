// The route editor's model (slice S1.15c; FR-KRN-WFL-01 to -03, S1.15 ruling Q9): operators by field type, conditions as sentences, moving and tidying steps.
import { describe, expect, it } from 'vitest';
import type { ConditionJson, RouteField } from '../../api/routes';
import { addTo, between, describe as sentenceOf, isBetween, move, newStep, operatorsFor, retarget, stepOfProblem, tidy, updateAt, type SentenceContext } from './routes-model';

const field = (name: string, type: string, choices: string[] = []): RouteField => ({ name, type, label: { en: name[0]!.toUpperCase() + name.slice(1) }, choices });
const fields = [field('amount', 'money'), field('grade', 'choice', ['a', 'b']), field('urgent', 'bool'), field('start', 'date'), field('company', 'company')];
const words: Record<string, string> = { 'routes.op.eq': 'is', 'routes.op.gt': 'is more than', 'routes.op.in': 'is one of', 'routes.op.empty': 'is empty', 'routes.and': 'and', 'routes.or': 'or', 'routes.yes': 'yes', 'routes.no': 'no' };
const ctx: SentenceContext = {
  fields, locale: 'en', companyName: (id) => (id === 'c1' ? 'First company' : ''),
  say: (key, values) => (key === 'routes.sentence.test' ? `${values?.field} ${values?.op} ${values?.value}`
    : key === 'routes.sentence.between' ? `${values?.field} is between ${values?.low} and ${values?.high}` : (words[key] ?? key)),
};

describe('FR-KRN-WFL-02 operators by field type (S1.15 Q9)', () => {
  it('offers ordered comparisons only for numbers, money and dates', () => {
    expect(operatorsFor('money')).toEqual(['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'empty', 'not_empty']);
    expect(operatorsFor('date')).toContain('gt');
    expect(operatorsFor('choice')).toEqual(['eq', 'ne', 'in', 'not_in', 'empty', 'not_empty']);
    expect(operatorsFor('bool')).toEqual(['eq', 'ne', 'empty', 'not_empty']);
    expect(operatorsFor('text')).not.toContain('gt');
  });

  it('resets the value when the field or the kind of comparison changes', () => {
    const test: ConditionJson = { field: 'amount', op: 'gt', value: '50000' };
    expect(retarget(test, fields, { op: 'ge' })).toEqual({ field: 'amount', op: 'ge', value: '50000' });
    expect(retarget(test, fields, { op: 'in' })).toEqual({ field: 'amount', op: 'in', value: [] });
    expect(retarget(test, fields, { op: 'empty' })).toEqual({ field: 'amount', op: 'empty' });
    expect(retarget(test, fields, { field: 'grade' })).toEqual({ field: 'grade', op: 'eq', value: 'a' });
    expect(retarget(test, fields, { field: 'urgent' })).toEqual({ field: 'urgent', op: 'eq', value: true });
  });
});

describe('FR-KRN-WFL-03 conditions as plain sentences', () => {
  it('writes a test, a range, groups and lists in words', () => {
    expect(sentenceOf({ field: 'amount', op: 'gt', value: '50000' }, ctx)).toBe('Amount is more than 50000');
    expect(sentenceOf(between(fields[0]!), ctx)).toBe('Amount is between … and …');
    expect(sentenceOf({ all: [{ field: 'amount', op: 'ge', value: '10' }, { field: 'amount', op: 'le', value: '50' }] }, ctx)).toBe('Amount is between 10 and 50');
    expect(sentenceOf({ any: [{ field: 'grade', op: 'in', value: ['a', 'b'] }, { all: [{ field: 'urgent', op: 'eq', value: true }, { field: 'amount', op: 'gt', value: '1' }] }] }, ctx))
      .toBe('Grade is one of a, b or (Urgent is yes and Amount is more than 1)');
    expect(sentenceOf({ field: 'company', op: 'eq', value: 'c1' }, ctx)).toBe('Company is First company');
    expect(sentenceOf({ field: 'amount', op: 'empty' }, ctx)).toBe('Amount is empty');
  });

  it('recognizes the range shape the editor makes', () => {
    expect(isBetween(between(fields[0]!))).toBe(true);
    expect(isBetween({ all: [{ field: 'amount', op: 'ge', value: '1' }, { field: 'start', op: 'le', value: '2' }] })).toBe(false);
  });
});

describe('FR-KRN-WFL-01 changing a route\'s conditions and steps', () => {
  const root: ConditionJson = { all: [{ field: 'amount', op: 'gt', value: '1' }, { any: [{ field: 'grade', op: 'eq', value: 'a' }] }] };

  it('changes, adds and removes a test at a path without touching the original', () => {
    const changed = updateAt(root, [1, 0], (c) => ({ ...c, value: 'b' }));
    expect(changed?.all?.[1]?.any?.[0]?.value).toBe('b');
    expect(root.all?.[1]?.any?.[0]?.value).toBe('a');
    expect(addTo(root, [1], { field: 'urgent', op: 'eq', value: true }).all?.[1]?.any).toHaveLength(2);
    expect(updateAt(root, [0], () => null)?.all).toHaveLength(1);
  });

  it('moves steps up and down and names a new step with a free code', () => {
    expect(move(['a', 'b', 'c'], 2, -1)).toEqual(['a', 'c', 'b']);
    expect(move(['a', 'b'], 0, -1)).toEqual(['a', 'b']);
    const steps = [{ ...newStep([]), key: 'step_2' }];
    expect(newStep(steps).key).toBe('step_3');
  });

  it('writes a route in the server\'s canonical shape, without the empty parts the editor leaves behind', () => {
    const step = { key: 'a', name: { en: 'A', ur: 'ا' }, condition: null, approver: { kind: 'role', role: 'hr' }, escalation: null, fallback: null, time_limit_working_days: null };
    expect(tidy({ steps: [step] })).toEqual({ steps: [{ key: 'a', name: { en: 'A', ur: 'ا' }, approver: { kind: 'role', role: 'hr' }, bulk_approval: false }] });
  });

  it('finds the step a problem names', () => {
    expect(stepOfProblem('steps[2].approver')).toBe(2);
    expect(stepOfProblem('steps')).toBeNull();
    expect(stepOfProblem('route')).toBeNull();
  });
});
