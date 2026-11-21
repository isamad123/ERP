// Pseudo-locale used only in tests (slice S1.12a, Build item 4): every catalogue text is accented, made about 40 percent
// longer and shown right to left, so a hard-coded string (it stays plain) or a layout that does not mirror shows at once.
// ICU syntax is kept: in an ICU message, text sits at even brace depth and argument syntax at odd depth.

const ACCENTS: Record<string, string> = {
  a: 'á', b: 'ƀ', c: 'ç', d: 'ď', e: 'é', f: 'ƒ', g: 'ğ', h: 'ĥ', i: 'í', j: 'ĵ', k: 'ķ', l: 'ľ', m: 'ɱ', n: 'ñ', o: 'ó', p: 'þ',
  q: 'ǫ', r: 'ŕ', s: 'š', t: 'ť', u: 'ú', v: 'ṽ', w: 'ŵ', x: 'ẋ', y: 'ý', z: 'ž',
  A: 'Á', B: 'Ɓ', C: 'Ç', D: 'Ď', E: 'É', F: 'Ƒ', G: 'Ğ', H: 'Ĥ', I: 'Í', J: 'Ĵ', K: 'Ķ', L: 'Ľ', M: 'Ṁ', N: 'Ñ', O: 'Ó', P: 'Þ',
  Q: 'Ǫ', R: 'Ŕ', S: 'Š', T: 'Ť', U: 'Ú', V: 'Ṽ', W: 'Ŵ', X: 'Ẋ', Y: 'Ý', Z: 'Ž',
};

/** Marks that open and close every pseudo text, so a test can tell catalogue text from anything else. */
export const PSEUDO_OPEN = '⟦';
export const PSEUDO_CLOSE = '⟧';

export function pseudoText(message: string): string {
  let depth = 0;
  let quoted = false;
  let out = '';
  let letters = 0;
  for (const ch of message) {
    if (ch === "'") quoted = !quoted;
    if (!quoted && ch === '{') depth += 1;
    if (!quoted && ch === '}') depth -= 1;
    const isText = depth % 2 === 0 && ch !== '{' && ch !== '}';
    if (isText && ACCENTS[ch]) {
      out += ACCENTS[ch];
      letters += 1;
    } else {
      out += ch;
    }
  }
  const padding = '~'.repeat(Math.max(1, Math.ceil(letters * 0.4)));
  return PSEUDO_OPEN + out + padding + PSEUDO_CLOSE;
}

export function pseudoCatalogue(messages: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(messages).map(([key, text]) => [key, pseudoText(text)]));
}
