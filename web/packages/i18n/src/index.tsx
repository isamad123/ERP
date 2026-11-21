// Localisation of the web application (slice S1.12a, Build item 4; rulings Q5, Q6, Q22). English and Urdu in 1.0, Urdu
// right to left (D-42: no Arabic or Hijri in 1.0). Web texts are ICU messages (react-intl); server texts (problem titles
// and field errors are keys) are looked up as plain text, because they are written for the server, not in ICU syntax.
import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { IntlProvider, useIntl } from 'react-intl';
import en from '../messages/en.json';
import ur from '../messages/ur.json';
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
const SERVER: Record<Locale, Catalogue> = { en: serverEn, ur: serverUr };

function strip(catalogue: Record<string, string>): Catalogue {
  return Object.fromEntries(Object.entries(catalogue).filter(([key]) => !key.startsWith('_')));
}

export type MessageKey = keyof typeof en;

export function webMessages(locale: UiLocale): Catalogue {
  return locale === 'pseudo' ? pseudoCatalogue(WEB.en) : WEB[locale];
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
}

const LocaleContext = createContext<LocaleSettings>({ locale: 'en', timeZone: 'Asia/Karachi', numbers: DEFAULT_NUMBERS });

/** The Intl locale tag for react-intl and React Aria (Pakistan; Western digits unless chosen otherwise, Q6). */
export function localeTag(locale: UiLocale, numbers: NumberPreferences = DEFAULT_NUMBERS): string {
  const base = locale === 'ur' ? 'ur-PK' : locale === 'pseudo' ? 'en-XA' : 'en-PK';
  return `${base}-u-nu-${numbers.digits === 'western' ? 'latn' : 'arabext'}`;
}

export function I18nRoot({ locale, timeZone = 'Asia/Karachi', numbers = DEFAULT_NUMBERS, children }: { locale: UiLocale; timeZone?: string; numbers?: NumberPreferences; children: ReactNode }) {
  const settings = useMemo(() => ({ locale, timeZone, numbers }), [locale, timeZone, numbers]);
  return (
    <LocaleContext.Provider value={settings}>
      <IntlProvider locale={localeTag(locale, numbers)} messages={webMessages(locale)} defaultLocale="en-PK" timeZone={timeZone}
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
