import {
  bugContext,
  clearBugContext,
  describe as describeElement,
  MAX_ACTIONS,
  MAX_ERRORS,
  recordRequest,
  startBugContext,
} from './bugContext';

// D-121: the context of a bug report — the last actions and the browser's errors, never what was typed into fields.

let stop: () => void = () => undefined;

beforeEach(() => {
  clearBugContext();
  stop = startBugContext();
});

afterEach(() => {
  stop();
  document.body.innerHTML = '';
});

describe('bug report context', () => {
  it('keeps a click with the element, its test id and its label', () => {
    document.body.innerHTML = '<button data-testid="roll"><span>Бросить</span></button>';

    document.querySelector('span')?.click();

    expect(bugContext().actions?.map((a) => a.text)).toEqual([
      'click button[data-testid=roll] «Бросить»',
    ]);
  });

  it('never keeps what was typed into a field', () => {
    document.body.innerHTML = '<input name="password" data-testid="pw" value="secret-123" />';

    document.querySelector('input')?.click();

    const texts = bugContext().actions?.map((a) => a.text) ?? [];
    expect(texts).toEqual(['click input[data-testid=pw] «password»']);
    expect(texts.join(' ')).not.toContain('secret-123');
  });

  it('keeps requests without ids, with their answers', () => {
    recordRequest(
      'POST',
      'http://localhost/api/seasons/30000000-0000-0000-0000-0000000000aa/roll',
      409,
    );

    expect(bugContext().actions?.[0]?.text).toBe('POST /api/seasons/…/roll → 409');
  });

  it('keeps the browser errors, unhandled rejections and console errors', () => {
    const quiet = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    stop();
    stop = startBugContext();

    globalThis.dispatchEvent(
      new ErrorEvent('error', {
        error: new TypeError('x is undefined'),
        message: 'x is undefined',
      }),
    );
    const rejected = new Event('unhandledrejection') as PromiseRejectionEvent;
    Object.defineProperty(rejected, 'reason', { value: new Error('network down') });
    globalThis.dispatchEvent(rejected);
    console.error('render failed', { code: 1 });

    expect(bugContext().errors?.map((e) => e.text)).toEqual([
      'TypeError: x is undefined',
      'Unhandled: Error: network down',
      'render failed {"code":1}',
    ]);
    quiet.mockRestore();
  });

  it('keeps only the last actions and errors', () => {
    for (let i = 0; i < MAX_ACTIONS + 5; i++)
      recordRequest('GET', `http://localhost/api/x${i}`, 200);

    const actions = bugContext().actions ?? [];
    expect(actions).toHaveLength(MAX_ACTIONS);
    expect(actions[0]?.text).toBe('GET /api/x5 → 200');
    expect(MAX_ERRORS).toBeLessThanOrEqual(20);
  });

  it('sends the window size and the browser', () => {
    const context = bugContext();

    expect(context.viewport).toMatch(/^\d+x\d+$/);
    expect(context.userAgent).toBeTruthy();
  });

  it('stops keeping when stopped', () => {
    stop();
    document.body.innerHTML = '<button>Ещё</button>';

    document.querySelector('button')?.click();

    expect(bugContext().actions).toEqual([]);
    expect(describeElement(document.querySelector('button') as Element)).toBe('click button «Ещё»');
  });
});
