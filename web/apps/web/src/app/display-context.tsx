// Display choices for the whole application (language, theme, density), with the localisation root. After sign-in the
// shell (S1.12b) sets the person's time zone and number style from /api/kernel/me (S1.12 rulings Q6, Q9); signed out,
// the defaults apply.
import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { I18nProvider } from 'react-aria-components';
import { DEFAULT_NUMBERS, I18nRoot, localeTag, type LabelOverrides, type NumberPreferences } from '@erp/i18n';
import { readDisplay, uiLocale, writeDisplay, type Display } from './preferences';

export interface Formats {
  timeZone: string;
  numbers: NumberPreferences;
}

export const DEFAULT_FORMATS: Formats = { timeZone: 'Asia/Karachi', numbers: DEFAULT_NUMBERS };

/** The customer's label overrides per language (GET /api/kernel/labels, slice S1.12c Build item 15). */
export type OverridesByLanguage = Readonly<Record<string, LabelOverrides>>;
const NO_OVERRIDES: OverridesByLanguage = {};

interface DisplayValue {
  display: Display;
  setDisplay: (display: Display) => void;
  formats: Formats;
  setFormats: (formats: Formats) => void;
  setOverrides: (overrides: OverridesByLanguage) => void;
}

const DisplayContext = createContext<DisplayValue>({
  display: { language: 'en', theme: 'system', density: 'standard' }, setDisplay: () => undefined,
  formats: DEFAULT_FORMATS, setFormats: () => undefined, setOverrides: () => undefined,
});

export function DisplayProvider({ children }: { children: ReactNode }) {
  const [display, setState] = useState(readDisplay);
  const [formats, setFormatState] = useState(DEFAULT_FORMATS);
  const [overrides, setOverrides] = useState<OverridesByLanguage>(NO_OVERRIDES);
  const locale = uiLocale(display);
  const value = useMemo<DisplayValue>(() => ({
    display,
    setDisplay: (next: Display) => { writeDisplay(next); setState(next); },
    formats,
    setFormats: (next: Formats) => setFormatState((current) =>
      current.timeZone === next.timeZone && current.numbers.digits === next.numbers.digits && current.numbers.grouping === next.numbers.grouping ? current : next),
    setOverrides,
  }), [display, formats]);
  return (
    <DisplayContext.Provider value={value}>
      <I18nRoot locale={locale} timeZone={formats.timeZone} numbers={formats.numbers} overrides={locale === 'pseudo' ? undefined : overrides[locale]}>
        <I18nProvider locale={localeTag(locale, formats.numbers)}>{children}</I18nProvider>
      </I18nRoot>
    </DisplayContext.Provider>
  );
}

export function useDisplay() {
  return useContext(DisplayContext);
}
