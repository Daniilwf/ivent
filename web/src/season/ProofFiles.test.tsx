import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { refreshCsrf, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { ProofSection } from './ProofForm';
import { json } from '../test/fakeServer';

// D-116: screenshots in the proof — uploaded one by one to /api/files, shown as thumbnails, removable, sent as ids;
// a screenshot alone is a proof; refusals of the upload are said in Russian.

const shot = (n: number) => ({
  id: `50000000-0000-0000-0000-00000000000${String(n)}`,
  mediaType: 'image/webp',
  width: 40,
  height: 30,
  frames: 1,
  url: `/api/files/5000000${String(n)}`,
  thumbnailUrl: `/api/files/5000000${String(n)}/thumbnail`,
  duplicate: false,
});

type Sent = { url: string; method: string | undefined; body: FormData };

function serveUploads(answers: Response[]) {
  const requests: Sent[] = [];
  const fetch = vi.fn((url: string, init?: RequestInit) => {
    requests.push({ url, method: init?.method, body: init?.body as FormData });
    return Promise.resolve(answers.shift() ?? json(500, {}));
  });
  vi.stubGlobal('fetch', fetch);
  return requests;
}

const picture = (name = 'credits.png') =>
  new File([new Uint8Array([137, 80, 78, 71])], name, { type: 'image/png' });

function renderForm(proof: Schemas['ProofView'] | null = null) {
  const onSubmit = vi.fn();
  render(<ProofSection proof={proof} witnesses={[]} pending={false} onSubmit={onSubmit} />);
  return onSubmit;
}

describe('Screenshots in the proof', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('links the proof to what counts as a completion (D-207)', () => {
    renderForm();

    expect(
      within(screen.getByTestId('proof-form')).getByRole('link', {
        name: ru.rules.sections.completion,
      }),
    ).toHaveAttribute('href', '/rules#rules-completion');
  });

  it('uploads a screenshot, shows its thumbnail and sends it alone as the proof', async () => {
    const requests = serveUploads([json(200, shot(1))]);
    const onSubmit = renderForm();

    await userEvent.upload(screen.getByTestId('proof-file'), picture());

    const shots = await screen.findByTestId('proof-shots');
    expect(within(shots).getByRole('img', { name: ru.proof.shotAlt(1) })).toHaveAttribute(
      'src',
      shot(1).thumbnailUrl,
    );
    expect(requests).toHaveLength(1);
    const [upload] = requests;
    expect(upload?.method).toBe('POST');
    expect(upload?.url).toMatch(/\/api\/files$/);
    const form = upload?.body;
    expect(form?.get('commandId')).toMatch(/^[0-9a-f-]{36}$/);
    expect((form?.get('file') as File | null)?.name).toBe('credits.png');

    await userEvent.click(screen.getByTestId('proof-submit'));

    expect(onSubmit).toHaveBeenCalledWith([], null, null, [shot(1).id]);
  });

  it('removes a screenshot before sending', async () => {
    serveUploads([json(200, shot(1)), json(200, shot(2))]);
    const onSubmit = renderForm();
    await userEvent.upload(screen.getByTestId('proof-file'), picture('a.png'));
    await screen.findByRole('img', { name: ru.proof.shotAlt(1) });
    await userEvent.upload(screen.getByTestId('proof-file'), picture('b.png'));
    await screen.findByRole('img', { name: ru.proof.shotAlt(2) });

    await userEvent.click(screen.getByRole('button', { name: ru.proof.removeShot(1) }));
    await userEvent.click(screen.getByTestId('proof-submit'));

    expect(onSubmit).toHaveBeenCalledWith([], null, null, [shot(2).id]);
  });

  it('says in Russian why a file was refused and keeps nothing', async () => {
    serveUploads([json(422, { title: 'refused', status: 422, code: 'file.typeInvalid' })]);
    renderForm();

    await userEvent.upload(screen.getByTestId('proof-file'), picture('notes.png'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.upload.errors['file.typeInvalid'],
    );
    expect(screen.queryByTestId('proof-shots')).toBeNull();
  });

  it('says the daily limit in Russian', async () => {
    serveUploads([json(429, { title: 'limit', status: 429, code: 'file.dailyLimit' })]);
    renderForm();

    await userEvent.upload(screen.getByTestId('proof-file'), picture());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.upload.errors['file.dailyLimit']);
  });

  it('falls back to a general text when the server gives no code', async () => {
    serveUploads([json(500, {})]);
    renderForm();

    await userEvent.upload(screen.getByTestId('proof-file'), picture());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.upload.failed);
  });

  it('sends the antiforgery token with the upload', async () => {
    const sent: { url: string; headers: Record<string, string> }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn((input: Request | string, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.url;
        if (url.endsWith('/api/auth/antiforgery')) {
          return Promise.resolve(json(200, { headerName: 'X-CSRF-TOKEN', token: 'token-1' }));
        }
        sent.push({ url, headers: (init?.headers ?? {}) as Record<string, string> });
        return Promise.resolve(json(200, shot(1)));
      }),
    );
    await refreshCsrf();
    renderForm();

    await userEvent.upload(screen.getByTestId('proof-file'), picture());

    await screen.findByRole('img', { name: ru.proof.shotAlt(1) });
    expect(sent).toHaveLength(1);
    expect(sent[0]?.headers['X-CSRF-TOKEN']).toBe('token-1');
  });

  it('says why when the server refuses by status only', async () => {
    serveUploads([new Response('', { status: 429 })]);
    renderForm();

    await userEvent.upload(screen.getByTestId('proof-file'), picture());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.upload.errors['file.tooOften']);
  });

  it('offers no more upload after five screenshots', async () => {
    serveUploads([1, 2, 3, 4, 5].map((n) => json(200, shot(n))));
    renderForm();

    for (let n = 1; n <= 5; n++) {
      await userEvent.upload(screen.getByTestId('proof-file'), picture(`${String(n)}.png`));
      await screen.findByRole('img', { name: ru.proof.shotAlt(n) });
    }

    expect(screen.queryByTestId('proof-file')).toBeNull();
  });

  it('shows the screenshots of a sent proof as links to the pictures', () => {
    renderForm({
      status: 'pending',
      links: [],
      note: null,
      comment: null,
      files: [{ id: shot(1).id, url: shot(1).url, thumbnailUrl: shot(1).thumbnailUrl }],
    });

    const sent = screen.getByTestId('proof-files');
    const image = within(sent).getByRole('img', { name: ru.proof.shotAlt(1) });
    expect(image).toHaveAttribute('src', shot(1).thumbnailUrl);
    expect(image.closest('a')).toHaveAttribute('href', shot(1).url);
  });

  it('still asks for a link, a screenshot or a witness when nothing is given', async () => {
    const onSubmit = renderForm();

    await userEvent.click(screen.getByTestId('proof-submit'));

    expect(screen.getByRole('alert')).toHaveTextContent(ru.proof.linkRequired);
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('picks a file with a Russian button: the browser field stays hidden under it (H4)', async () => {
    serveUploads([json(200, shot(1))]);
    renderForm();

    // The button's words are the field's name; the browser's own «Choose File» is not shown
    const input = screen.getByLabelText(ru.proof.shot);
    expect(input).toBe(screen.getByTestId('proof-file'));
    expect(input).toHaveAttribute('type', 'file');
    expect(input).toHaveClass('sr-only');
    expect(input).toHaveAccessibleDescription(ru.proof.shotHint(5));
    // The keyboard reaches the picker
    for (let i = 0; i < 12 && document.activeElement !== input; i++) await userEvent.tab();
    expect(input).toHaveFocus();

    await userEvent.upload(input, picture());
    await screen.findByRole('img', { name: ru.proof.shotAlt(1) });
    expect(screen.getByTestId('proof-file')).toHaveAccessibleDescription(ru.proof.shotHint(4));
  });

  it('shows the status of a sent proof in words with an icon (H4)', () => {
    renderForm({ status: 'approved', files: [], links: [], note: null, comment: null });

    const status = screen.getByTestId('proof-status');
    expect(status).toHaveTextContent(ru.proof.status.approved);
    expect(status.querySelector('svg')).not.toBeNull();
    expect(screen.queryByTestId('proof-form')).toBeNull();
  });

  it('tells a wrong link under that link, not the others (H4)', async () => {
    const onSubmit = renderForm();
    await userEvent.click(screen.getByRole('button', { name: ru.proof.addLink }));
    const [first, second] = screen.getAllByTestId('proof-link');
    if (!first || !second) throw new Error('Two link fields are expected.');
    await userEvent.type(first, 'https://imgur.com/a/credits');
    await userEvent.type(second, 'ftp://example.org');
    await userEvent.click(screen.getByTestId('proof-submit'));

    expect(onSubmit).not.toHaveBeenCalled();
    expect(second).toHaveAttribute('aria-invalid', 'true');
    expect(second).toHaveAccessibleDescription(ru.proof.linkInvalid);
    expect(first).not.toHaveAttribute('aria-invalid');
  });
});
