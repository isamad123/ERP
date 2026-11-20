// Display choices for the whole application (language, theme, density), with the localisation root.
import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { I18nProvider } from 'react-aria-components';
import { I18nRoot, localeTag } from '@erp/i18n';
import { readDisplay, uiLocale, writeDisplay, type Display } from './preferences';

const DisplayContext = createContext<{ display: Display; setDisplay: (display: Display) => void }>({
  display: { language: 'en', theme: 'system', density: 'standard' }, setDisplay: () => undefined,
});

export function DisplayProvider({ children }: { children: ReactNode }) {
  const [display, setState] = useState(readDisplay);
  const locale = uiLocale(display);
  const value = useMemo(() => ({ display, setDisplay: (next: Display) => { writeDisplay(next); setState(next); } }), [display]);
  return (
    <DisplayContext.Provider value={value}>
      <I18nRoot locale={locale}>
        <I18nProvider locale={localeTag(locale)}>{children}</I18nProvider>
      </I18nRoot>
    </DisplayContext.Provider>
  );
}

export function useDisplay() {
  return useContext(DisplayContext);
}
