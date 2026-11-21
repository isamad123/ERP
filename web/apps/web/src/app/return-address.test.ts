// The return address after sign-in (slice S1.12a, Build item 6; FR-KRN-IAM-01, review S1.12a finding 1): a path on this
// site only, read the way the browser reads an address, so no crafted link can send the person to another site.
import { describe, expect, it } from 'vitest';
import { safeReturn } from './session-context';

const origin = 'https://acme.erp.example';

describe('FR-KRN-IAM-01 return address after sign-in (S1.12a)', () => {
  it('keeps a path on this site with its query and fragment', () => {
    expect(safeReturn('/?tab=one', origin)).toBe('/?tab=one');
    expect(safeReturn('/people/42?view=a#history', origin)).toBe('/people/42?view=a#history');
    expect(safeReturn('/a b', origin)).toBe('/a%20b');
  });

  it('refuses every form that the browser would read as another site', () => {
    for (const attack of [
      '//evil.example/', '/\\evil.example/', '/\t/evil.example/', '/\n/evil.example/', '/\r\n/evil.example',
      '/.//evil.example/', '/./\\evil.example', '/%2e//evil.example', 'https://evil.example/', 'javascript:alert(1)',
      '\t//evil.example', ' /x', '', null, undefined,
    ]) {
      expect(safeReturn(attack, origin), JSON.stringify(attack)).toBe('/');
    }
  });

  it('never comes back to the sign-in screens', () => {
    expect(safeReturn('/sign-in', origin)).toBe('/');
    expect(safeReturn('/sign-in/verify', origin)).toBe('/');
    expect(safeReturn('/x/../sign-in', origin)).toBe('/');
  });
});
