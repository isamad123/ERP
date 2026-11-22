// The public branding read of the sign-in screens and the top bar (slice S1.15a, FR-ADM-SEC-08): kept apart from the settings
// client so the shell loads only this small read, not the settings centre's API.
import { requestJson } from './client';

export interface Branding {
  productName: string;
  logoUrl: string | null;
  banner: { en: string; ur: string };
  terms: { en: string; ur: string };
}

/** No sign-in needed: the sign-in page asks before anyone is known. */
export const readBranding = (signal?: AbortSignal) => requestJson<Branding>('GET', '/api/kernel/branding', { auth: false, ...(signal ? { signal } : {}) });
