// Display choices for the whole application (language, theme, density), with the localisation root. After sign-in the
// shell (S1.12b) sets the person's time zone and number style from /api/kernel/me (S1.12 rulings Q6, Q9); signed out,
// the defaults apply.
import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { I18nProvider } from 'react-aria-components';
import { DEFAULT_NUMBERS, I18nRoot, localeTag, type NumberPreferences } from '@erp/i18n';
import { readDisplay, uiLocale, writeDisplay, type Display } from './preferences';

export interface Formats {
  timeZone: string;
  numbers: NumberPreferences;
}

export const DEFAULT_FORMATS: Formats = { timeZone: 'Asia/Karachi', numbers: DEFAULT_NUMBERS };

interface DisplayValue {
  display: Display;
  setDisplay: (display: Display) => void;
  formats: Formats;
  setFormats: (formats: Formats) => void;
}

const DisplayContext = createContext<DisplayValue>({
  display: { language: 'en', theme: 'system', density: 'standard' }, setDisplay: () => undefined,
  formats: DEFAULT_FORMATS, setFormats: () => undefined,
});

export function DisplayProvider({ children }: { children: ReactNode }) {
  const [display, setState] = useState(readDisplay);
  const [formats, setFormatState] = useState(DEFAULT_FORMATS);
  const locale = uiLocale(display);
  const value = useMemo<DisplayValue>(() => ({
    display,
    setDisplay: (next: Display) => { writeDisplay(next); setState(next); },
    formats,
    setFormats: (next: Formats) => setFormatState((current) =>
      current.timeZone === next.timeZone && current.numbers.digits === next.numbers.digits && current.numbers.grouping === next.numbers.grouping ? current : next),
  }), [display, formats]);
  return (
    <DisplayContext.Provider value={value}>
      <I18nRoot locale={locale} timeZone={formats.timeZone} numbers={formats.numbers}>
        <I18nProvider locale={localeTag(locale, formats.numbers)}>{children}</I18nProvider>
      </I18nRoot>
    </DisplayContext.Provider>
  );
}

export function useDisplay() {
  return useContext(DisplayContext);
}
