// Licence banners, read-only switch and the active company (slice S1.12b Build items 12 and 13; FR-KRN-ENT-04,
// FR-KRN-TEN-09, FR-ADM-ORG-06; S1.12 rulings Q10 and Q17).
import { afterEach, describe, expect, it } from 'vitest';
import { dayIn, isWritable, licenceBanners } from './licence';
import { activeCompanyOf, hideLicenceNotes, licenceNotesHiddenOn, rememberCompany } from './memory';

const limit = { key: 'users.named', max: 50, used: 46, percent: 92 };

afterEach(() => window.localStorage.clear());

describe('FR-KRN-ENT-04 licence banners (ruling Q17)', () => {
  it('Warning only for licence readers, and it may be hidden for the day', () => {
    expect(licenceBanners({ state: 'warning', warnings: [] }, false, false)).toEqual([]);
    expect(licenceBanners({ state: 'warning', warnings: [] }, true, false)).toEqual([{ kind: 'warning', tone: 'warning', dismissible: true }]);
    expect(licenceBanners({ state: 'warning', warnings: [limit] }, true, true)).toEqual([]);
  });

  it('Grace and Read-only for everyone and never hidden', () => {
    for (const reader of [true, false]) {
      expect(licenceBanners({ state: 'grace', warnings: [] }, reader, true)).toEqual([{ kind: 'grace', tone: 'warning', dismissible: false }]);
      expect(licenceBanners({ state: 'read_only', warnings: [] }, reader, true)).toEqual([{ kind: 'read_only', tone: 'danger', dismissible: false }]);
    }
  });

  it('limit warnings beside the Warning banner, for licence readers only', () => {
    expect(licenceBanners({ state: 'active', warnings: [limit] }, true, false)).toEqual([{ kind: 'limit', tone: 'info', dismissible: true, limit }]);
    expect(licenceBanners({ state: 'active', warnings: [limit] }, false, false)).toEqual([]);
    expect(licenceBanners({ state: 'warning', warnings: [limit] }, true, false).map((b) => b.kind)).toEqual(['warning', 'limit']);
  });

  it('active and locked show no banner (locked is a full page)', () => {
    expect(licenceBanners({ state: 'active', warnings: [] }, true, false)).toEqual([]);
    expect(licenceBanners({ state: 'locked', warnings: [] }, true, false)).toEqual([]);
  });
});

describe('FR-KRN-TEN-09 read-only offers no create, edit or decide action', () => {
  it('is writable only in active, warning and grace', () => {
    expect(['active', 'warning', 'grace', 'read_only', 'locked'].map((s) => isWritable(s as never))).toEqual([true, true, true, false, false]);
  });

  it('"hide for today" ends at the person\'s midnight, in their time zone', () => {
    const at = Date.UTC(2026, 10, 21, 20, 0); // 21 November 20:00 UTC = 22 November 01:00 in Karachi
    expect(dayIn('UTC', at)).toBe('2026-11-21');
    expect(dayIn('Asia/Karachi', at)).toBe('2026-11-22');
    hideLicenceNotes('2026-11-22');
    expect(licenceNotesHiddenOn()).toBe('2026-11-22');
  });
});

describe('FR-ADM-ORG-06 the active company (ruling Q10)', () => {
  const companies = [{ id: 'a' }, { id: 'b' }];

  it('starts from the default company and remembers the choice per person in this browser', () => {
    expect(activeCompanyOf('u1', companies, 'b')).toBe('b');
    rememberCompany('u1', 'a');
    expect(activeCompanyOf('u1', companies, 'b')).toBe('a');
    expect(activeCompanyOf('u2', companies, 'b')).toBe('b'); // another person on the same browser
  });

  it('forgets a company the person no longer works in', () => {
    rememberCompany('u1', 'gone');
    expect(activeCompanyOf('u1', companies, null)).toBe('a');
    expect(activeCompanyOf('u1', [], null)).toBeNull();
  });
});
