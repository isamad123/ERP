// Localisation of the web application (slice S1.12a, Build item 4; rulings Q5, Q6, Q22). English and Urdu in 1.0, Urdu
// right to left (D-42: no Arabic or Hijri in 1.0). Web texts are ICU messages (react-intl); server texts (problem titles
// and field errors are keys) are looked up as plain text, because they are written for the server, not in ICU syntax.
import { createContext, useContext, useMemo, useSyncExternalStore, type ReactNode } from 'react';
import { IntlProvider, useIntl } from 'react-intl';
import en from '../messages/en.json';
import ur from '../messages/ur.json';
// Texts of the settings, feature-flag and sign-in-page screens (S1.15a), numbering and approval routes (S1.15c), HR policies (S1.23a) live in their own files, loaded with those screens
// (`registerMessages`), so they do not weigh on the shell's script budget (spec 13 FR-UX-PRF-01). Only the types come from here.
import type configEn from '../messages/config.en.json';
import type numberingEn from '../messages/numbering.en.json';
import type routesEn from '../messages/routes.en.json';
import type importsEn from '../messages/imports.en.json';
import type attendanceEn from '../messages/attendance.en.json';
import type setupEn from '../messages/setup.en.json';
import type payEn from '../messages/pay.en.json';
import type notifyEn from '../messages/notify.en.json';
import type doctemplatesEn from '../messages/doctemplates.en.json';
import type policiesEn from '../messages/policies.en.json';
import type packsEn from '../messages/packs.en.json';
import serverEn from './generated/server.en.json';
import serverUr from './generated/server.ur.json';
import { pseudoCatalogue, pseudoText } from './pseudo';
import { DEFAULT_NUMBERS, directionOf, type Locale, type NumberPreferences } from './format';

export * from './format';
export { pseudoText, pseudoCatalogue, PSEUDO_OPEN, PSEUDO_CLOSE } from './pseudo';

/** Languages a person can choose; `pseudo` exists only in tests (it is never offered on a screen). */
export const LANGUAGES: readonly Locale[] = ['en', 'ur'];
export type UiLocale = Locale | 'pseudo';

type Catalogue = Record<string, string>;

const WEB: Record<Locale, Catalogue> = { en: strip(en), ur: strip(ur) };
// Platform server texts ship with the shell; the business modules' texts arrive as their own chunk right after start-up (below).
const SERVER: Record<Locale, Catalogue> = { en: { ...serverEn }, ur: { ...serverUr } };

function strip(catalogue: Record<string, string>): Catalogue {
  return Object.fromEntries(Object.entries(catalogue).filter(([key]) => !key.startsWith('_')));
}

export type MessageKey = keyof typeof en | keyof typeof configEn | keyof typeof numberingEn | keyof typeof routesEn | keyof typeof notifyEn | keyof typeof doctemplatesEn | keyof typeof payEn | keyof typeof setupEn | keyof typeof attendanceEn | keyof typeof policiesEn | keyof typeof packsEn | keyof typeof importsEn;

let registryVersion = 0;
const listeners = new Set<() => void>();

/** Adds the texts of a lazily loaded screen to the catalogues; mounted roots pick them up at once. */
export function registerMessages(extra: Record<Locale, Record<string, string>>): void {
  for (const locale of LANGUAGES) Object.assign(WEB[locale], strip(extra[locale]));
  registryVersion += 1;
  for (const listener of listeners) listener();
}

const subscribe = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; };
const version = () => registryVersion;

/**
 * The business modules' server texts (S1.20c: HR answers), loaded as their own chunk as soon as the application starts, so
 * module growth never weighs on the shell's script budget (spec 13 FR-UX-PRF-01, R-213). Mounted roots pick them up at once;
 * a module answer can only come after the person acts, by which time they are here. `serverTextsReady` resolves once loaded.
 */
export const serverTextsReady: Promise<void> = Promise.all([import('./generated/modules.en.json'), import('./generated/modules.ur.json')])
  .then(([moduleEn, moduleUr]) => {
    Object.assign(SERVER.en, moduleEn.default);
    Object.assign(SERVER.ur, moduleUr.default);
    registryVersion += 1;
    for (const listener of listeners) listener();
  });

export function webMessages(locale: UiLocale, overrides: Readonly<Record<string, string>> = NO_OVERRIDES): Catalogue {
  const base = locale === 'pseudo' ? pseudoCatalogue(WEB.en) : WEB[locale];
  return withOverrides(base, locale === 'pseudo' ? NO_OVERRIDES : overrides);
}

/** Label overrides of the customer and company (cfg.labels, slice S1.12c Build item 15) for the current language: key → text. */
export type LabelOverrides = Readonly<Record<string, string>>;
const NO_OVERRIDES: LabelOverrides = Object.freeze({});

/**
 * The web catalogue with the customer's label overrides (FR-KRN-ORG-08, FR-KRN-MET-22): an override replaces a web text
 * that has no placeholders (menu names, titles, field labels), written as plain ICU text (quotes and braces escaped); a
 * text with placeholders keeps the product's wording, so an override can never break a message.
 */
export function withOverrides(base: Catalogue, overrides: LabelOverrides): Catalogue {
  const keys = Object.keys(overrides);
  if (keys.length === 0) return base;
  const merged = { ...base };
  for (const key of keys) {
    const current = base[key];
    const text = overrides[key];
    if (current === undefined || text === undefined || /[{}]/.test(current.replace(/'[^']*'/g, ''))) continue;
    merged[key] = icuLiteral(text);
  }
  return merged;
}

/** Plain text as an ICU message: apostrophes doubled, braces, # and the like quoted. */
export function icuLiteral(text: string): string {
  return text.replace(/'/g, "''").replace(/[{}#<>|]+/g, (special) => `'${special}'`);
}

/** A server text in the language, English when the Urdu catalogue lacks it, else null. */
export function serverText(key: string, locale: UiLocale, values?: Record<string, string | number>): string | null {
  const base = locale === 'pseudo' ? SERVER.en[key] : (SERVER[locale][key] ?? SERVER.en[key]);
  if (base === undefined) return null;
  const text = values ? base.replace(/\{(\w+)\}/g, (all, name: string) => (name in values ? String(values[name]) : all)) : base;
  return locale === 'pseudo' ? pseudoText(text) : text;
}

interface LocaleSettings {
  locale: UiLocale;
  timeZone: string;
  numbers: NumberPreferences;
  /** The customer's label overrides for this language (empty signed out and in the pseudo-locale). */
  overrides: LabelOverrides;
}

const LocaleContext = createContext<LocaleSettings>({ locale: 'en', timeZone: 'Asia/Karachi', numbers: DEFAULT_NUMBERS, overrides: NO_OVERRIDES });

/** The Intl locale tag for react-intl and React Aria (Pakistan; Western digits unless chosen otherwise, Q6). */
export function localeTag(locale: UiLocale, numbers: NumberPreferences = DEFAULT_NUMBERS): string {
  const base = locale === 'ur' ? 'ur-PK' : locale === 'pseudo' ? 'en-XA' : 'en-PK';
  return `${base}-u-nu-${numbers.digits === 'western' ? 'latn' : 'arabext'}`;
}

export function I18nRoot({ locale, timeZone = 'Asia/Karachi', numbers = DEFAULT_NUMBERS, overrides = NO_OVERRIDES, children }: { locale: UiLocale; timeZone?: string; numbers?: NumberPreferences; overrides?: LabelOverrides; children: ReactNode }) {
  const effective = locale === 'pseudo' ? NO_OVERRIDES : overrides;
  const settings = useMemo(() => ({ locale, timeZone, numbers, overrides: effective }), [locale, timeZone, numbers, effective]);
  const registered = useSyncExternalStore(subscribe, version, version);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const messages = useMemo(() => webMessages(locale, effective), [locale, effective, registered]);
  return (
    <LocaleContext.Provider value={settings}>
      <IntlProvider locale={localeTag(locale, numbers)} messages={messages} defaultLocale="en-PK" timeZone={timeZone}
        onError={(e) => { if (e.code !== 'MISSING_TRANSLATION' || locale !== 'pseudo') throw e; }}>
        {children}
      </IntlProvider>
    </LocaleContext.Provider>
  );
}

export function useLocaleSettings(): LocaleSettings & { direction: 'ltr' | 'rtl'; tag: string } {
  const settings = useContext(LocaleContext);
  return { ...settings, direction: directionOf(settings.locale), tag: localeTag(settings.locale, settings.numbers) };
}

/** `t(key, values)` for web texts and `server(key)` for server keys, in the current language. */
export function useText() {
  const intl = useIntl();
  const { locale } = useContext(LocaleContext);
  return useMemo(() => ({
    t: (key: MessageKey, values?: Record<string, string | number | Date>) => intl.formatMessage({ id: key }, values),
    server: (key: string, values?: Record<string, string | number>) => serverText(key, locale, values),
  }), [intl, locale]);
}
