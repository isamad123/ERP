// What the shell remembers in this browser (S1.12 ruling Q10; CLAUDE.md: no token or personal data in browser storage):
// the active company of each person (a company id under the person's id) and the day the licence notes were hidden.
// Conveniences only: if the browser refuses storage, the default company and visible notes apply.

function storage(): Storage | null {
  try {
    // eslint-disable-next-line no-restricted-syntax -- ids and a date only (see the file comment)
    return window.localStorage;
  } catch {
    return null;
  }
}

function read(key: string): string | null {
  try {
    return storage()?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    storage()?.setItem(key, value);
  } catch {
    // Private windows may refuse storage; the choice then lasts for this page only.
  }
}

/** The remembered company when it is still one of the person's companies, else their default company. */
export function activeCompanyOf(userId: string, companies: readonly { id: string }[], defaultCompanyId: string | null): string | null {
  const remembered = read(`erp.company.${userId}`);
  if (remembered && companies.some((c) => c.id === remembered)) return remembered;
  return defaultCompanyId ?? companies[0]?.id ?? null;
}

export function rememberCompany(userId: string, companyId: string): void {
  write(`erp.company.${userId}`, companyId);
}

export function licenceNotesHiddenOn(): string | null {
  return read('erp.licence.hidden');
}

export function hideLicenceNotes(day: string): void {
  write('erp.licence.hidden', day);
}
