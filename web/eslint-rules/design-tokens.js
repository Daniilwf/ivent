// ESLint rule «design/tokens-only» (docs/DESIGN.md «Процесс», step 5): colours, sizes and spaces come from the tokens
// in src/design/tokens.css only. It reports, outside src/design/:
//   - colour literals (#abc, #aabbcc, rgb(), hsl(), oklch()…) in any string;
//   - in class names (className and cx() arguments): Tailwind arbitrary values (p-[13px], bg-[#fff]), half steps
//     off the 4 px grid (p-2.5), raw durations (duration-300) and Tailwind's own palette (bg-red-500), which the
//     theme switches off, so such a class would silently do nothing.
// Arbitrary variants (data-[state=open]:), token references (duration-(--duration-fast)) and grid templates without
// absolute sizes (grid-cols-[auto_1fr]) are allowed.

const colourLiteral =
  /(^|[\s(:,'"=[])#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3})(?![0-9a-z_-])|\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\(/i;

const palette =
  /^(?:text|bg|border|fill|stroke|ring|outline|from|to|via|shadow|decoration|accent|caret|divide|placeholder)-(?:red|blue|green|gray|grey|slate|zinc|neutral|stone|orange|amber|yellow|lime|emerald|teal|cyan|sky|indigo|violet|purple|fuchsia|pink|rose|black|white)(?:-\d+)?(?:\/\d+)?$/;

/** The utility part of a class: after the last variant colon that is not inside brackets or parentheses */
function utilityOf(cls) {
  let depth = 0;
  let cut = 0;
  for (let i = 0; i < cls.length; i++) {
    const ch = cls[i];
    if (ch === '[' || ch === '(') depth++;
    else if (ch === ']' || ch === ')') depth--;
    else if (ch === ':' && depth === 0) cut = i + 1;
  }
  return cls.slice(cut).replace(/^!/, '').replace(/!$/, '');
}

export function classProblem(cls) {
  const utility = utilityOf(cls);
  if (utility === '') return null;
  // Grid templates are layout, not values: allowed while they hold no absolute size (auto, 1fr, minmax(0,1fr))
  if (
    /^grid-(?:cols|rows)-\[[^\]]*\]$/.test(utility) &&
    !/\d(?:px|rem|em|vh|vw|dvh|%)/.test(utility)
  )
    return null;
  if (/\[/.test(utility)) return `«${cls}»: an arbitrary value; use a token`;
  if (/-\d+\.\d+$/.test(utility)) return `«${cls}»: a half step; spaces are whole 4 px steps`;
  if (/^duration-\d/.test(utility))
    return `«${cls}»: a raw duration; use duration-(--duration-fast|base|slow)`;
  if (palette.test(utility)) return `«${cls}»: Tailwind's own palette is off; use a colour token`;
  return null;
}

function classStrings(node) {
  if (!node) return [];
  if (node.type === 'Literal' && typeof node.value === 'string')
    return [{ node, text: node.value }];
  if (node.type === 'TemplateLiteral')
    return [
      ...node.quasis.map((q) => ({ node: q, text: q.value.cooked ?? '' })),
      ...node.expressions.flatMap((e) => classStrings(e)),
    ];
  if (node.type === 'JSXExpressionContainer') return classStrings(node.expression);
  if (node.type === 'ConditionalExpression')
    return [...classStrings(node.consequent), ...classStrings(node.alternate)];
  if (node.type === 'LogicalExpression')
    return [...classStrings(node.left), ...classStrings(node.right)];
  if (node.type === 'ArrayExpression') return node.elements.flatMap((e) => classStrings(e));
  return [];
}

const rule = {
  meta: {
    type: 'problem',
    docs: { description: 'Colours, sizes and spaces only from the design tokens' },
    schema: [],
  },
  create(context) {
    const checkClasses = (node) => {
      for (const { node: at, text } of classStrings(node)) {
        for (const cls of text.split(/\s+/).filter(Boolean)) {
          const problem = classProblem(cls);
          if (problem) context.report({ node: at, message: problem });
        }
      }
    };
    const checkColour = (node, text) => {
      if (colourLiteral.test(text))
        context.report({
          node,
          message: `A colour literal outside the tokens: «${text.slice(0, 40)}»`,
        });
    };
    return {
      Literal(node) {
        if (typeof node.value === 'string') checkColour(node, node.value);
      },
      TemplateElement(node) {
        checkColour(node, node.value.cooked ?? '');
      },
      JSXAttribute(node) {
        if (node.name.name === 'className' || node.name.name === 'class') checkClasses(node.value);
      },
      CallExpression(node) {
        if (node.callee.type === 'Identifier' && node.callee.name === 'cx')
          for (const argument of node.arguments) checkClasses(argument);
      },
    };
  },
};

export default { rules: { 'tokens-only': rule } };
