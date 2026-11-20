// Applied before the first paint (slice S1.12a, spec 13 FR-UX-TOK-22): the browser's remembered language, theme and
// density, so the page never flashes in the wrong direction or colours. Only these display choices are kept in the
// browser; no personal data and no token (S1.12 rulings Q3, Q13).
(function () {
  var prefs = {};
  try {
    prefs = JSON.parse(window.localStorage.getItem('erp.display') || '{}') || {};
  } catch {
    prefs = {};
  }
  var root = document.documentElement;
  var testPseudo = false;
  try {
    testPseudo = window.localStorage.getItem('erp.test.pseudo') === '1';
  } catch {
    testPseudo = false;
  }
  var language = prefs.language === 'ur' || prefs.language === 'en' ? prefs.language
    : (navigator.language || '').toLowerCase().indexOf('ur') === 0 ? 'ur' : 'en';
  if (testPseudo) language = 'pseudo';
  root.lang = language === 'pseudo' ? 'en-XA' : language;
  root.dir = language === 'en' ? 'ltr' : 'rtl';
  if (prefs.theme === 'light' || prefs.theme === 'dark' || prefs.theme === 'contrast') root.setAttribute('data-theme', prefs.theme);
  if (prefs.density === 'compact' || prefs.density === 'comfortable') root.setAttribute('data-density', prefs.density);
})();
