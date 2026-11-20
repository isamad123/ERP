// Style rules of spec 13 section 24 (slice S1.12a, Build item 1), with stylelint's own rules only:
// logical properties (no left or right: layouts mirror for Urdu), token values only (no raw colours, pixel sizes,
// fonts or z-index outside the generated tokens file).
export default {
  ignoreFiles: ['**/dist/**', '**/node_modules/**', '**/src/tokens/tokens.css'],
  rules: {
    'color-no-hex': true,
    'color-named': 'never',
    'function-disallowed-list': ['rgb', 'rgba', 'hsl', 'hsla', 'hwb', 'lab', 'lch', 'oklab', 'oklch'],
    'unit-disallowed-list': [['px', 'pt', 'cm', 'mm', 'in'], { ignoreProperties: { px: ['/^--/'] } }],
    'property-disallowed-list': [
      'left', 'right', 'margin-left', 'margin-right', 'padding-left', 'padding-right', 'border-left', 'border-right',
      'border-left-width', 'border-right-width', 'border-left-color', 'border-right-color', 'border-top-left-radius',
      'border-top-right-radius', 'border-bottom-left-radius', 'border-bottom-right-radius',
    ],
    'declaration-property-value-disallowed-list': {
      'text-align': ['left', 'right'], float: ['left', 'right'], clear: ['left', 'right'],
    },
    // Fonts, layers and transitions only through tokens.
    'declaration-property-value-allowed-list': {
      'font-family': ['/^var\\(--font-/', 'inherit'],
      'z-index': ['/^var\\(--layer-/', 'auto'],
    },
    'declaration-no-important': true,
    'selector-max-id': 0,
  },
};
