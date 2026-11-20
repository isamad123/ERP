// Display choices kept in this browser (language, theme, density): conveniences only, applied before first paint by
// public/boot.js. No personal data and no token is ever stored in the browser (S1.12 rulings Q3, Q13). After sign-in
// the person's own language comes from the server (S1.12b, /api/kernel/me).
import type { UiLocale } from '@erp/i18n';

export type Theme = 'system' | 'light' | 'dark' | 'contrast';
export type Density = 'compact' | 'standard' | 'comfortable';

export interface Display {
  language: 'en' | 'ur';
  theme: Theme;
  density: Density;
}

const KEY = 'erp.display';

function storage(): Storage | null {
  try {
    // eslint-disable-next-line no-restricted-syntax -- display choices only (see the file comment)
    return window.localStorage;
  } catch {
    return null;
  }
}

export function readDisplay(): Display {
  let stored: Partial<Display> = {};
  try {
    stored = JSON.parse(storage()?.getItem(KEY) ?? '{}') as Partial<Display>;
  } catch {
    stored = {};
  }
  const html = document.documentElement.lang;
  return {
    language: stored.language === 'ur' || stored.language === 'en' ? stored.language : html === 'ur' ? 'ur' : 'en',
    theme: stored.theme ?? 'system',
    density: stored.density ?? 'standard',
  };
}

export function writeDisplay(display: Display): void {
  try {
    storage()?.setItem(KEY, JSON.stringify(display));
  } catch {
    // Private windows may refuse storage; the choice then lasts for this page only.
  }
  applyDisplay(display, uiLocale(display));
}

/** The language shown: the pseudo-locale only when a test asked for it (never offered on a screen). */
export function uiLocale(display: Display): UiLocale {
  try {
    return storage()?.getItem('erp.test.pseudo') === '1' ? 'pseudo' : display.language;
  } catch {
    return display.language;
  }
}

export function applyDisplay(display: Display, locale: UiLocale): void {
  const root = document.documentElement;
  root.lang = locale === 'pseudo' ? 'en-XA' : locale;
  root.dir = locale === 'en' ? 'ltr' : 'rtl';
  if (display.theme === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', display.theme);
  if (display.density === 'standard') root.removeAttribute('data-density');
  else root.setAttribute('data-density', display.density);
}
