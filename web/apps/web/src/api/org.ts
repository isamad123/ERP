// Hand-written clients of the organization set-up (slice S1.14a, CR-070): company registrations and bank accounts (encrypted
// on the server, shown masked, in full or not at all by field security), the holiday calendar year copy, and the settings a
// company overrides. The generated client serves everything else (companies, locations, calendars and holidays are made by the
// engine). The server checks every request again; the screens show its answers as confirmed, never assumed (spec 13 STA-06).
import { requestJson, send, problemOf } from './client';
import type { AccessAnswer } from './access';

export const REGISTRATION_KINDS = ['ntn', 'strn', 'eobi', 'social_security', 'provident_fund', 'commercial_registration', 'other'] as const;
export type RegistrationKind = (typeof REGISTRATION_KINDS)[number];

/** What field security allows the caller to see of a value: all of it, its last four characters, or nothing. */
export type ValueAccess = 'full' | 'masked' | 'hidden';

export interface Registration {
  id: string;
  companyId: string;
  kind: RegistrationKind;
  authority: string;
  value: string | null;
  valueAccess: ValueAccess;
  validFrom: string;
  validTo: string | null;
  rowVersion: number;
}

export interface RegistrationBody {
  kind?: RegistrationKind;
  authority?: string;
  value?: string;
  validFrom?: string | null;
  validTo?: string | null;
  rowVersion?: number;
}

export interface BankAccount {
  id: string;
  companyId: string;
  bankCode: string;
  accountTitle: string;
  accountNumber: string | null;
  iban: string | null;
  accountAccess: ValueAccess;
  currencyCode: string;
  salaryFileFormat: string | null;
  rowVersion: number;
}

export interface BankAccountBody {
  bankCode?: string;
  accountTitle?: string;
  accountNumber?: string;
  iban?: string;
  clearIban?: boolean;
  currencyCode?: string;
  salaryFileFormat?: string | null;
  rowVersion?: number;
  reason?: string;
}

export interface CopyYearResult { created: number; skipped: number }

/** A visible setting with its effective value for a company; `source` is the layer the value comes from (`company` when overridden). */
export interface SettingItem { key: string; area: string | null; name: Record<string, string> | null; description: Record<string, string>; value: unknown; source: string }

/** A file attached to a record (the company logo). */
export interface AttachmentInfo { id: string; fileName: string; contentType: string; sizeBytes: number }

const company = (id: string) => `/api/org/companies/${id}`;

export const org = {
  registrations: (companyId: string, signal?: AbortSignal) =>
    requestJson<Registration[]>('GET', `${company(companyId)}/registrations`, signal ? { signal } : {}),
  addRegistration: (companyId: string, body: RegistrationBody) =>
    requestJson<AccessAnswer>('POST', `${company(companyId)}/registrations`, { body }),
  changeRegistration: (id: string, body: RegistrationBody) =>
    requestJson<AccessAnswer>('PUT', `/api/org/company-registrations/${id}`, { body }),
  bankAccounts: (companyId: string, signal?: AbortSignal) =>
    requestJson<BankAccount[]>('GET', `${company(companyId)}/bank-accounts`, signal ? { signal } : {}),
  addBankAccount: (companyId: string, body: BankAccountBody) =>
    requestJson<AccessAnswer>('POST', `${company(companyId)}/bank-accounts`, { body }),
  changeBankAccount: (id: string, body: BankAccountBody) =>
    requestJson<AccessAnswer>('PUT', `/api/org/company-bank-accounts/${id}`, { body }),
  removeBankAccount: (id: string, body: { rowVersion: number; reason?: string }) =>
    requestJson<AccessAnswer>('POST', `/api/org/company-bank-accounts/${id}/remove`, { body }),
  /** The settings the company overrides (the settings centre lists them all, S1.15; FR-ADM-ORG-07). */
  settingOverrides: async (companyId: string, signal?: AbortSignal): Promise<SettingItem[]> =>
    (await requestJson<SettingItem[]>('GET', `/api/cfg/settings?companyId=${companyId}`, signal ? { signal } : {})).filter((s) => s.source === 'company'),
  /** The company logo: a file on the company (document type `company_logo`); the company record then points to it. */
  uploadLogo: async (companyId: string, file: File): Promise<AttachmentInfo> => {
    const form = new FormData();
    form.append('file', file);
    form.append('ownerType', 'org.companies');
    form.append('ownerId', companyId);
    form.append('documentType', 'company_logo');
    const response = await send('POST', '/api/doc/attachments', { form });
    if (!response.ok) throw await problemOf(response);
    return (await response.json()) as AttachmentInfo;
  },
  /** The logo as a data address (the page's content policy allows images from itself and data only). */
  logoImage: async (fileId: string, signal?: AbortSignal): Promise<string | null> => {
    const response = await send('GET', `/api/doc/attachments/${fileId}/content`, signal ? { signal } : {});
    if (!response.ok) return null;
    const blob = await response.blob();
    return await new Promise<string | null>((resolve) => {
      const reader = new FileReader();
      reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : null);
      reader.onerror = () => resolve(null);
      reader.readAsDataURL(blob);
    });
  },
  copyYear: (calendarId: string, fromYear: number, toYear: number) =>
    requestJson<CopyYearResult>('POST', `/api/ref/calendars/${calendarId}/copy-year`, { body: { fromYear, toYear } }),
};
