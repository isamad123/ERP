// Licence banners and the read-only switch (slice S1.12b Build item 13; FR-KRN-ENT-04, FR-KRN-TEN-09; S1.12 ruling Q17;
// spec 13 FR-UX-SHL-07, FR-UX-STA-07). Pure rules, tested without a browser.
import type { Capabilities, LicenceState, LimitUse } from '../api/kernel';

export type BannerKind = 'warning' | 'grace' | 'read_only' | 'limit';

export interface BannerSpec {
  kind: BannerKind;
  tone: 'info' | 'warning' | 'danger';
  /** Only the Warning banner and the limit warnings may be hidden, and only for the day (ruling Q17). */
  dismissible: boolean;
  limit?: LimitUse;
}

/**
 * Which banners the person sees: Warning only for licence readers (hidden for the rest of the day when they asked);
 * Grace and Read-only for everyone, never hidden; limit warnings for licence readers (the server sends them to readers
 * only, CR-057), beside the Warning banner and hidden with it. Locked is a full page, not a banner.
 */
export function licenceBanners(capabilities: Pick<Capabilities, 'state' | 'warnings'>, canReadLicence: boolean, hiddenToday: boolean): BannerSpec[] {
  const banners: BannerSpec[] = [];
  const readerNotes = canReadLicence && !hiddenToday;
  if (capabilities.state === 'warning' && readerNotes) banners.push({ kind: 'warning', tone: 'warning', dismissible: true });
  if (capabilities.state === 'grace') banners.push({ kind: 'grace', tone: 'warning', dismissible: false });
  if (capabilities.state === 'read_only') banners.push({ kind: 'read_only', tone: 'danger', dismissible: false });
  if (readerNotes) {
    for (const limit of capabilities.warnings) banners.push({ kind: 'limit', tone: 'info', dismissible: true, limit });
  }
  return banners;
}

/** Whether create, edit and decide actions are offered: not in Read-only or Locked (data stays readable). */
export function isWritable(state: LicenceState): boolean {
  return state !== 'read_only' && state !== 'locked';
}

/** The person's calendar day (yyyy-mm-dd) in their time zone: "hide for today" ends at their midnight. */
export function dayIn(timeZone: string, at: number): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(at);
}
