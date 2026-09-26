// Stylelint (G3): CSS outside src/design/tokens.css takes colours, sizes, spaces and radii from the tokens only.
/** @type {import('stylelint').Config} */
export default {
  rules: {
    'color-no-hex': true,
    'color-named': 'never',
    'function-disallowed-list': [
      'rgb',
      'rgba',
      'hsl',
      'hsla',
      'hwb',
      'lab',
      'lch',
      'oklab',
      'oklch',
    ],
    'declaration-property-unit-disallowed-list': {
      '/^(margin|padding|gap|row-gap|column-gap|font-size|line-height|border-radius|letter-spacing|inset|top|right|bottom|left|width|height|min-width|max-width|min-height|max-height)/':
        ['px', 'rem', 'em'],
      '/^(transition|animation)/': ['ms', 's'],
    },
  },
  overrides: [
    {
      files: ['src/design/tokens.css'],
      rules: {
        'color-no-hex': null,
        'function-disallowed-list': null,
        'declaration-property-unit-disallowed-list': null,
      },
    },
  ],
  ignoreFiles: ['dist/**', 'coverage/**', 'node_modules/**', 'src/design-preview/**'],
};
