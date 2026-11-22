// The Urdu label check of slice S1.12c Build item 15 (S1.12 ruling Q11): every screen that is in a menu and shows a
// table's fields has an Urdu label for the table, each field it can show and each option. A missing label fails this
// test and the failure lists them; English comes from the generator, Urdu is kept by hand (R-062).
import { describe, expect, it } from 'vitest';
import { LABELS, labelKeysOf } from '@erp/engine';
import { kernelScreens } from './kernel';
import { hrmScreens } from './hrm';

const metadata = import.meta.glob<{ default: Parameters<typeof labelKeysOf>[0] & { table: string } }>('../../../../generated/metadata/*.json', { eager: true });
const byTable = new Map(Object.values(metadata).map((m) => [m.default.table, m.default]));

describe('FR-KRN-LOC-01 Urdu labels of every menu screen', () => {
  it('lists no missing Urdu label', () => {
    const missing: string[] = [];
    for (const screen of [...kernelScreens, ...hrmScreens].filter((s) => s.group && s.component)) {
      for (const table of screen.tables ?? []) {
        const meta = byTable.get(table);
        if (!meta) {
          missing.push(`${screen.id}: no metadata for ${table}`);
          continue;
        }
        for (const key of labelKeysOf(meta)) {
          if (!LABELS.ur[key]) missing.push(`${screen.id}: ${key} (English "${LABELS.en[key] ?? '?'}")`);
        }
      }
    }
    expect(missing).toEqual([]);
  });

  it('knows the branches screen shows org.branches (the check is not empty)', () => {
    expect(kernelScreens.find((s) => s.id === 'branches')?.tables).toEqual(['org.branches']);
    expect(LABELS.en['entities.org.branches']).toBe('Branches');
    expect(LABELS.en['fields.org.branches.company_id']).toBe('Company');
  });
});
