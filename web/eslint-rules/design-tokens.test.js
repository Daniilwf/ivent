import { RuleTester } from 'eslint';
import { describe, it } from 'vitest';
import plugin from './design-tokens.js';

RuleTester.describe = describe;
RuleTester.it = it;

const tester = new RuleTester({
  languageOptions: { parserOptions: { ecmaFeatures: { jsx: true } } },
});

const rule = plugin.rules['tokens-only'];

tester.run('design/tokens-only', rule, {
  valid: [
    'const a = <div className="bg-card p-4 text-ink rounded-lg shadow-press" />',
    'const a = <div className="data-[state=open]:bg-page is-hover:bg-action-strong" />',
    'const a = <div className="transition duration-(--duration-fast) w-1/2 -mt-2" />',
    'const a = <a href="#main-world" />',
    'const a = cx("px-5", on && "bg-me", "text-on-color")',
    'const a = { fill: "var(--color-token-3)" }',
    'const a = <div className="grid-cols-[auto_minmax(0,1fr)_auto]" />',
  ],
  invalid: [
    { code: 'const a = <div className="p-[13px]" />', errors: 1 },
    { code: 'const a = <div className="bg-[#ff0000]" />', errors: 2 },
    { code: 'const a = <div className="p-2.5" />', errors: 1 },
    { code: 'const a = <div className="duration-300" />', errors: 1 },
    { code: 'const a = <div className="text-red-500 hover:bg-white" />', errors: 2 },
    { code: 'const a = <div className={`gap-4 ${x ? "mt-[3px]" : ""}`} />', errors: 1 },
    { code: 'const a = cx("m-1.5")', errors: 1 },
    { code: 'const a = <div className="grid-cols-[120px_1fr]" />', errors: 1 },
    { code: 'const a = { color: "#fff" }', errors: 1 },
    { code: 'const a = <rect fill="rgb(0, 0, 0)" />', errors: 1 },
  ],
});
