// No hard-coded user-facing text (spec 13 FR-UX-I18 rules, CLAUDE.md front-end rules): text inside JSX and literal
// strings in attributes people read or hear (aria-label, title, placeholder, alt, label, description, errorMessage)
// must come from the message catalogues. Punctuation, numbers and whitespace alone are allowed.
const READ_ATTRIBUTES = new Set(['aria-label', 'aria-description', 'aria-valuetext', 'aria-roledescription', 'title', 'placeholder', 'alt', 'label', 'description', 'errorMessage', 'heading']);
const allowed = (text) => !/\p{L}/u.test(text);

export default {
  meta: {
    type: 'problem',
    docs: { description: 'disallow literal user-facing text in JSX; use the message catalogues' },
    messages: { literal: 'User-facing text "{{text}}" must come from the message catalogue (packages/i18n).' },
    schema: [],
  },
  create(context) {
    const report = (node, text) => context.report({ node, messageId: 'literal', data: { text: text.trim().slice(0, 40) } });
    return {
      JSXText(node) {
        if (!allowed(node.value)) report(node, node.value);
      },
      JSXAttribute(node) {
        const name = typeof node.name.name === 'string' ? node.name.name : '';
        if (!READ_ATTRIBUTES.has(name) || !node.value) return;
        if (node.value.type === 'Literal' && typeof node.value.value === 'string' && !allowed(node.value.value)) report(node, node.value.value);
        if (node.value.type === 'JSXExpressionContainer' && node.value.expression.type === 'Literal'
          && typeof node.value.expression.value === 'string' && !allowed(node.value.expression.value)) report(node, node.value.expression.value);
        if (node.value.type === 'JSXExpressionContainer' && node.value.expression.type === 'TemplateLiteral'
          && node.value.expression.quasis.some((q) => !allowed(q.value.cooked ?? ''))) report(node, node.value.expression.quasis.map((q) => q.value.cooked).join('…'));
      },
      'JSXExpressionContainer > Literal'(node) {
        if (node.parent.parent?.type === 'JSXElement' && typeof node.value === 'string' && !allowed(node.value)) report(node, node.value);
      },
    };
  },
};
