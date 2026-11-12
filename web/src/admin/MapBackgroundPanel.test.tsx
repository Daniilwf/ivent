import { onDesktop } from '../test/desk';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, json, seasonId } from '../test/fakeServer';
import { MapBackgroundPanel } from './MapBackgroundPanel';
import type { Draft } from './mapDraft';
import { draftOf, normalized, sameMap, setBackground } from './mapDraft';
import { MapSection } from './MapSection';

// The map's background in the editor (SPEC «Редактор»: загрузка фона; D-277, D-278): none, uploading, failed, there;
// replaced and removed in the draft; sent with the check and the publication as its file and size.

const t = ru.admin.map.background;

const stored = {
  id: 'b0000000-0000-0000-0000-000000000001',
  mediaType: 'image/webp',
  width: 1600,
  height: 900,
  frames: 1,
  url: '/api/files/b0000000-0000-0000-0000-000000000001',
  thumbnailUrl: '/api/files/b0000000-0000-0000-0000-000000000001/thumbnail',
  duplicate: false,
};

const picture = () =>
  new File([new Uint8Array([137, 80, 78, 71])], 'map.png', { type: 'image/png' });

type Upload = { url: string; body: FormData };

/** The uploads go through fetch with a URL (a form, not the typed client): answered here, the rest by the fake server */
function serveUploads(answers: Response[]) {
  const uploads: Upload[] = [];
  const api = globalThis.fetch;
  vi.stubGlobal(
    'fetch',
    vi.fn((input: Request | string, init?: RequestInit) => {
      if (typeof input !== 'string') return api(input);
      uploads.push({ url: input, body: init?.body as FormData });
      return Promise.resolve(answers.shift() ?? json(500, {}));
    }),
  );
  return uploads;
}

const published: Schemas['MapGraphView'] = {
  cells: [
    { id: 'start', type: 'start', x: 100, y: 400 },
    { id: 'c1', type: 'empty', x: 600, y: 400 },
    { id: 'finish', type: 'finish', x: 1100, y: 400 },
  ],
  edges: [
    { from: 'start', to: 'c1', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'c1', to: 'finish', isDefaultForward: true, isPrimaryBackward: true },
  ],
  zones: [],
};

const view = (map: Schemas['MapGraphView'] = published): Schemas['AdminMapView'] => ({
  mode: 'graph',
  status: 'active',
  map,
  players: [],
  version: 0,
  events: false,
  shop: false,
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('the background panel', () => {
  it('says there is no background and uploads one into the draft', async () => {
    fakeServer({});
    const uploads = serveUploads([json(200, stored)]);
    const onChange = vi.fn();
    render(<MapBackgroundPanel seasonId={seasonId} background={null} onChange={onChange} />);

    expect(screen.getByTestId('map-background-none')).toHaveTextContent(t.none);
    // One explanation per state: without a picture, what to upload; placing cells comes once there is one
    expect(screen.getByText(t.formats)).toBeInTheDocument();
    expect(screen.queryByText(t.placement)).toBeNull();
    expect(screen.queryByTestId('map-background-remove')).toBeNull();
    await userEvent.upload(screen.getByTestId('map-background-file'), picture());

    expect(uploads[0]?.url).toMatch(new RegExp(`/api/admin/seasons/${seasonId}/map/background$`));
    expect(uploads[0]?.body.get('file')).toBeInstanceOf(File);
    expect(uploads[0]?.body.get('commandId')).toBeTruthy();
    expect(onChange).toHaveBeenCalledWith({
      fileId: stored.id,
      width: 1600,
      height: 900,
      url: stored.url,
    });
  });

  it('shows the picture with its size, replaces it and removes it', async () => {
    fakeServer({});
    serveUploads([
      json(200, { ...stored, id: 'b2', width: 800, height: 600, url: '/api/files/b2' }),
    ]);
    const onChange = vi.fn();
    render(
      <MapBackgroundPanel
        seasonId={seasonId}
        background={{ fileId: stored.id, width: 1600, height: 900 }}
        onChange={onChange}
        pending
      />,
    );

    expect(screen.getByTestId('map-background-preview')).toHaveAttribute(
      'src',
      stored.thumbnailUrl,
    );
    expect(screen.getByTestId('map-background-size')).toHaveTextContent(t.size(1600, 900));
    expect(screen.getByText(t.placement)).toBeInTheDocument();
    expect(screen.queryByText(t.formats)).toBeNull();
    expect(screen.getByText(t.draftOnly)).toBeInTheDocument();
    expect(screen.getByText(t.replace)).toBeInTheDocument();

    await userEvent.upload(screen.getByTestId('map-background-file'), picture());
    expect(onChange).toHaveBeenLastCalledWith({
      fileId: 'b2',
      width: 800,
      height: 600,
      url: '/api/files/b2',
    });

    // The keyboard reaches «Убрать фон» like any button
    screen.getByTestId('map-background-remove').focus();
    await userEvent.keyboard('{Enter}');
    expect(onChange).toHaveBeenLastCalledWith(null);
  });

  it('says why an upload was refused and keeps the background it had', async () => {
    fakeServer({});
    serveUploads([json(413, { title: 'x', status: 413, detail: null, code: 'file.tooLarge' })]);
    const onChange = vi.fn();
    render(<MapBackgroundPanel seasonId={seasonId} background={null} onChange={onChange} />);

    await userEvent.upload(screen.getByTestId('map-background-file'), picture());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.upload.errors['file.tooLarge']);
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByTestId('map-background-none')).toBeInTheDocument();
  });

  it('says an animation is not a background', async () => {
    fakeServer({});
    serveUploads([json(422, { title: 'x', status: 422, detail: null, code: 'file.notStill' })]);
    render(<MapBackgroundPanel seasonId={seasonId} background={null} onChange={vi.fn()} />);

    await userEvent.upload(screen.getByTestId('map-background-file'), picture());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.upload.errors['file.notStill']);
  });

  it('shows the upload while it runs and takes no second file', () => {
    render(
      <MapBackgroundPanel
        seasonId={seasonId}
        background={null}
        onChange={vi.fn()}
        state={{ kind: 'uploading' }}
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent(t.uploading);
    expect(screen.getByTestId('map-background-file')).toBeDisabled();
    expect(screen.getByTestId('map-background')).toHaveAttribute('aria-busy', 'true');
  });
});

describe('the background in the draft', () => {
  const draft = draftOf(published);

  it('is its file and size: the link stays out of the map sent to the server', () => {
    const withPicture = setBackground(draft, {
      fileId: 'f',
      width: 10,
      height: 20,
      url: '/api/files/f',
    });
    expect(normalized(withPicture).background).toEqual({ fileId: 'f', width: 10, height: 20 });
    expect(normalized(draft)).not.toHaveProperty('background');
    expect(sameMap(withPicture, draft)).toBe(false);
    expect(sameMap(setBackground(withPicture, null), draft)).toBe(true);
    // The server's map with the picture and the draft of it are the same map
    expect(
      sameMap(
        draftOf({ ...published, background: { fileId: 'f', width: 10, height: 20 } }),
        withPicture,
      ),
    ).toBe(true);
  });
});

describe('the editor with a background', () => {
  it('sends a new background with the check and the publication, and draws it on the canvas', async () => {
    onDesktop();
    const server = fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': (call) => ({
        body: {
          canPublish: !sameMap(call.body as Draft, published as Draft),
          problems: [],
          warnings: [],
        },
      }),
      'POST /api/admin/seasons/*/map/publish': { duplicate: false, events: [] },
    });
    serveUploads([json(200, stored)]);
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(ru.admin.map.check.unchanged);

    await userEvent.upload(screen.getByTestId('map-background-file'), picture());

    expect(await screen.findByTestId('map-background-size')).toHaveTextContent(t.size(1600, 900));
    expect(screen.getByTestId('map-changed')).toBeInTheDocument();
    expect(screen.getByTestId('canvas-background')).toHaveAttribute('src', stored.url);
    expect(await screen.findByText(ru.admin.map.check.ok)).toBeInTheDocument();
    const checked = server.sent('POST', '/map/check').at(-1)?.body as Schemas['MapGraphView'];
    expect(checked.background).toEqual({ fileId: stored.id, width: 1600, height: 900 });

    await userEvent.type(screen.getByTestId('map-publish-comment'), 'Фон карты');
    await userEvent.click(screen.getByTestId('map-publish-button'));
    await userEvent.click(
      within(await screen.findByTestId('map-publish-confirm')).getByRole('button', {
        name: ru.admin.map.publish.confirm,
      }),
    );
    const sent = server.sent('POST', '/map/publish')[0]?.body as { map: Schemas['MapGraphView'] };
    expect(sent.map.background).toEqual({ fileId: stored.id, width: 1600, height: 900 });
  });

  it('shows the published background and removes it in the draft', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view({
        ...published,
        background: { fileId: stored.id, width: 1600, height: 900, url: stored.url },
      }),
      'POST /api/admin/seasons/*/map/check': { canPublish: true, problems: [], warnings: [] },
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(await screen.findByTestId('map-background-preview')).toBeInTheDocument();
    expect(screen.queryByText(t.draftOnly)).toBeNull();
    await userEvent.click(screen.getByTestId('map-background-remove'));

    expect(screen.getByTestId('map-background-none')).toBeInTheDocument();
    expect(screen.getByText(t.draftOnly)).toBeInTheDocument();
    expect(screen.queryByTestId('canvas-background')).toBeNull();
    expect(screen.getByTestId('map-changed')).toBeInTheDocument();
  });

  it('shows the published background on a phone under the cells', async () => {
    fakeServer({
      'GET /api/admin/seasons/*/map': view({
        ...published,
        background: { fileId: stored.id, width: 1600, height: 900, url: stored.url },
      }),
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    await screen.findByTestId('map-phone');
    expect(document.querySelector('image[data-map-background]')?.getAttribute('href')).toBe(
      stored.url,
    );
  });
});
