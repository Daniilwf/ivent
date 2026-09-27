import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { BugReportButton } from './BugReportButton';
import { clearBugContext, recordAction, startBugContext } from './bugContext';
import { json } from '../test/fakeServer';

// A9, D-121: «Сообщить о баге» — a screenshot of the page, a description, and the context sent along by itself.

const toBlob = vi.fn<() => Promise<Blob | null>>();
vi.mock('html-to-image', () => ({ toBlob: () => toBlob() }));

type Sent = { url: string; method: string; body: unknown };

const stored = {
  id: '50000000-0000-0000-0000-000000000001',
  mediaType: 'image/webp',
  width: 390,
  height: 844,
  frames: 1,
  url: '/api/files/5',
  thumbnailUrl: '/api/files/5/thumbnail',
  duplicate: false,
};

function serve(report: () => Response, screenshot: () => Response = () => json(200, stored)) {
  const sent: Sent[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: Request | string, init?: RequestInit) => {
      const request = typeof input === 'string' ? null : input;
      const url = request?.url ?? (input as string);
      const method = request?.method ?? init?.method ?? 'GET';
      const body: unknown = request
        ? await request
            .clone()
            .json()
            .catch(() => null)
        : init?.body;
      sent.push({ url, method, body });
      return url.endsWith('/api/bug-reports/screenshot') ? screenshot() : report();
    }),
  );
  return sent;
}

beforeEach(() => {
  clearBugContext();
  toBlob.mockResolvedValue(new Blob(['png'], { type: 'image/png' }));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function describeBug(text: string) {
  const user = userEvent.setup();
  render(<BugReportButton />);
  await user.click(screen.getByTestId('bug-report'));
  await user.type(await screen.findByTestId('bug-report-text'), text);
  return user;
}

describe('BugReportButton', () => {
  it('sends the screenshot, then the report with the page and the context', async () => {
    const sent = serve(() => json(200, { id: 'r1' }));
    recordAction('click button[data-testid=roll] «Бросить»');

    const user = await describeBug('Кнопка не нажимается');
    await user.click(screen.getByTestId('bug-report-send'));

    expect(await screen.findByText(ru.bugReport.sent)).toBeInTheDocument();
    expect(sent.map((s) => s.url.replace(globalThis.location.origin, ''))).toEqual([
      '/api/bug-reports/screenshot',
      '/api/bug-reports',
    ]);
    const report = sent[1]?.body as {
      commandId: string;
      page: string;
      text: string;
      screenshotFileId: string | null;
      context: { actions: { text: string }[]; viewport: string };
    };
    expect(report.text).toBe('Кнопка не нажимается');
    expect(report.page).toBe(globalThis.location.pathname);
    expect(report.screenshotFileId).toBe(stored.id);
    expect(report.commandId).toMatch(/^[0-9a-f-]{36}$/);
    expect(report.context.actions.map((a) => a.text)).toContain(
      'click button[data-testid=roll] «Бросить»',
    );
    expect(report.context.viewport).toMatch(/^\d+x\d+$/);
  });

  it('sends no screenshot when the user unticks it', async () => {
    const sent = serve(() => json(200, { id: 'r1' }));

    const user = await describeBug('Опечатка');
    await user.click(screen.getByTestId('bug-report-attach'));
    await user.click(screen.getByTestId('bug-report-send'));

    await screen.findByText(ru.bugReport.sent);
    expect(sent).toHaveLength(1);
    expect((sent[0]?.body as { screenshotFileId: string | null }).screenshotFileId).toBeNull();
  });

  it('reports without a picture when the browser cannot draw the page or the upload fails', async () => {
    toBlob.mockRejectedValueOnce(new Error('tainted canvas'));
    const sent = serve(() => json(200, { id: 'r1' }));

    const user = await describeBug('Без скрина');
    expect(screen.getByText(ru.bugReport.noScreenshot)).toBeInTheDocument();
    await user.click(screen.getByTestId('bug-report-send'));
    await screen.findByText(ru.bugReport.sent);
    expect(sent).toHaveLength(1);

    const failing = serve(
      () => json(200, { id: 'r2' }),
      () => json(422, { code: 'file.broken' }),
    );
    await user.click(screen.getByRole('button', { name: ru.bugReport.done }));
    await user.click(screen.getByTestId('bug-report'));
    await user.type(await screen.findByTestId('bug-report-text'), 'Скрин не принят');
    await user.click(screen.getByTestId('bug-report-send'));
    await screen.findByText(ru.bugReport.sent);
    expect((failing[1]?.body as { screenshotFileId: string | null }).screenshotFileId).toBeNull();
  });

  it('asks for a description before sending', async () => {
    const sent = serve(() => json(200, { id: 'r1' }));
    const user = userEvent.setup();
    render(<BugReportButton />);
    await user.click(screen.getByTestId('bug-report'));

    await user.click(await screen.findByTestId('bug-report-send'));

    expect(screen.getByRole('alert')).toHaveTextContent(ru.bugReport.textRequired);
    expect(sent).toHaveLength(0);
  });

  it('sends one report with one screenshot however often it is retried in a dialog (D-68)', async () => {
    // As in the app: every click is recorded, «Отправить» included
    const stop = startBugContext();
    try {
      recordAction('кнопка «Завершить прохождение»');
      let answer: () => Response = () => {
        throw new TypeError('Failed to fetch');
      };
      const sent = serve(() => answer());

      const user = await describeBug('Во время обслуживания');
      await user.click(screen.getByTestId('bug-report-send'));
      expect(await screen.findByRole('alert')).toHaveTextContent(ru.bugReport.failed);

      answer = () => json(503, { code: 'site.maintenance' });
      await user.click(screen.getByTestId('bug-report-send'));
      expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection['site.maintenance']);

      answer = () => json(429, {});
      await user.click(screen.getByTestId('bug-report-send'));
      expect(await screen.findByRole('alert')).toHaveTextContent(ru.bugReport.tooOften);

      answer = () => json(200, { id: 'r1' });
      await user.click(screen.getByTestId('bug-report-send'));
      await screen.findByText(ru.bugReport.sent);

      type Report = {
        commandId: string;
        screenshotFileId: string | null;
        context: { actions: { text: string }[] };
      };
      const reports = sent
        .filter((s) => s.url.endsWith('/api/bug-reports'))
        .map((s) => s.body as Report);
      expect(sent.filter((s) => s.url.endsWith('/api/bug-reports/screenshot'))).toHaveLength(1);
      expect(reports).toHaveLength(4);
      expect(new Set(reports.map((r) => r.commandId)).size).toBe(1);
      expect(new Set(reports.map((r) => r.screenshotFileId))).toEqual(new Set([stored.id]));
      expect(new Set(reports.map((r) => JSON.stringify(r.context))).size).toBe(1);
      expect(reports[0]?.context.actions.map((x) => x.text)).toContain(
        'кнопка «Завершить прохождение»',
      );
    } finally {
      stop();
    }
  });

  it('makes a new report of a new dialog', async () => {
    const sent = serve(() => json(200, { id: 'r1' }));
    const user = await describeBug('Первый');
    await user.click(screen.getByTestId('bug-report-send'));
    await screen.findByText(ru.bugReport.sent);
    await user.click(screen.getByRole('button', { name: ru.bugReport.done }));
    await user.click(screen.getByTestId('bug-report'));
    await user.type(await screen.findByTestId('bug-report-text'), 'Второй');
    await user.click(screen.getByTestId('bug-report-send'));
    await screen.findByText(ru.bugReport.sent);

    const ids = sent
      .filter((s) => s.url.endsWith('/api/bug-reports'))
      .map((s) => (s.body as { commandId: string }).commandId);
    expect(ids).toHaveLength(2);
    expect(ids[1]).not.toBe(ids[0]);
  });
});
