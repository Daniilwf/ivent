import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { UploadError, uploadFile } from '../api/files';
import { ru } from '../i18n/ru';
import { IrlProofSection } from './IrlProofSection';

vi.mock('../api/files', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/files')>();
  return { ...actual, uploadFile: vi.fn() };
});

const upload = vi.mocked(uploadFile);
const videoLink = 'https://example.com/watch?v=karaoke';
const description = 'Спой песню на выбор остальных и скинь видео в галерею.';

function task(overrides: Partial<Schemas['ManualEffectView']> = {}): Schemas['ManualEffectView'] {
  return {
    id: 'e5000000-0000-0000-0000-000000000001',
    drawEvent: 'bad',
    source: 'drop',
    objectId: 'karaoke',
    irl: true,
    name: 'Караоке',
    description,
    irlProof: null,
    ...overrides,
  };
}

function shot(number: number): Schemas['StoredFileView'] {
  const id = '50000000-0000-0000-0000-' + String(number).padStart(12, '0');
  return {
    id,
    mediaType: 'image/webp',
    width: 40,
    height: 30,
    frames: 1,
    url: '/api/files/' + id,
    thumbnailUrl: '/api/files/' + id + '/thumbnail',
    duplicate: false,
  };
}

function fileLink(number: number): Schemas['FileLinkView'] {
  const file = shot(number);
  return { id: file.id, url: file.url, thumbnailUrl: file.thumbnailUrl };
}

function proof(overrides: Partial<Schemas['IrlProofView']> = {}): Schemas['IrlProofView'] {
  return {
    status: 'pending',
    files: [fileLink(1)],
    link: videoLink,
    comment: null,
    submittedAt: '2026-11-17T16:00:00Z',
    ...overrides,
  };
}

const picture = (name = 'karaoke.png') =>
  new File([new Uint8Array([137, 80, 78, 71])], name, { type: 'image/png' });
const linkField = () => screen.getByRole('textbox', { name: /ссылка/i });
const submit = () => screen.getByRole('button', { name: 'Отправить пруф' });

function fileField() {
  const field = screen.getByTestId('irl-proof-file');
  if (!(field instanceof HTMLInputElement)) throw new Error('The photo picker must be an input.');
  return field;
}

function renderTask(effect = task(), maxFiles = 5) {
  const onSubmit = vi.fn();
  const rendered = render(
    <IrlProofSection
      effect={effect}
      maxFiles={maxFiles}
      pending={false}
      enabled
      onSubmit={onSubmit}
    />,
  );
  return { ...rendered, onSubmit };
}

describe('the proof of an IRL task', () => {
  beforeEach(() => {
    upload.mockReset();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the task name and description as text, with one explicit proof action', () => {
    const { container } = renderTask(
      task({ name: 'Караоке <img src=x>', description: description + ' <svg onload=alert(1)>' }),
    );

    expect(screen.getByText('Караоке <img src=x>')).toBeVisible();
    expect(screen.getByText(description + ' <svg onload=alert(1)>')).toBeVisible();
    expect(container.querySelector('img, svg[onload]')).toBeNull();
    expect(submit()).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Применено' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Не применимо' })).not.toBeInTheDocument();
  });

  it('sends a trimmed HTTPS link without uploading or automatically submitting while typing', async () => {
    const { onSubmit } = renderTask();
    await userEvent.type(linkField(), '  ' + videoLink + '  ');

    expect(onSubmit).not.toHaveBeenCalled();
    expect(submit()).toBeEnabled();
    await userEvent.click(submit());

    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([], videoLink);
    expect(upload).not.toHaveBeenCalled();
  });

  it('does not invent a proof from an empty or whitespace-only link', async () => {
    const { onSubmit } = renderTask();
    await userEvent.type(linkField(), '   ');
    await userEvent.click(submit());

    expect(onSubmit).not.toHaveBeenCalled();
    expect(submit()).toBeDisabled();
    expect(upload).not.toHaveBeenCalled();
  });

  it('allows a 500-character HTTPS link at the boundary', async () => {
    const link = 'https://example.com/' + 'a'.repeat(480);
    expect(link).toHaveLength(500);
    const { onSubmit } = renderTask();
    fireEvent.change(linkField(), { target: { value: link } });

    await userEvent.click(submit());

    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([], link);
  });

  it.each([
    ['HTTP', 'http://example.com/video'],
    ['script', 'javascript:alert(1)'],
    ['relative address', '/video/karaoke'],
    ['credentials', 'https://name:password@example.com/video'],
    ['backslash', 'https://example.com/a\\b'],
    ['internal space', 'https://example.com/a b'],
    ['control character', 'https://example.com/a\u0001b'],
    ['format character', 'https://example.com/a\u200bb'],
    ['over 500 characters', 'https://example.com/' + 'a'.repeat(481)],
  ])('refuses a link with %s and explains the invalid field', async (_name, link) => {
    const { onSubmit } = renderTask();
    const input = linkField();
    fireEvent.change(input, { target: { value: link } });

    await userEvent.click(submit());

    expect(onSubmit).not.toHaveBeenCalled();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription();
  });

  it('offers a link-only proof when the current season allows zero photos', async () => {
    const { onSubmit } = renderTask(task(), 0);

    expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
    await userEvent.type(linkField(), videoLink);
    await userEvent.click(submit());

    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([], videoLink);
    expect(upload).not.toHaveBeenCalled();
  });

  it('uploads an image with the shared uploader and submits only its stored id', async () => {
    upload.mockResolvedValueOnce(shot(1));
    const { onSubmit } = renderTask();
    const selected = picture();
    const input = fileField();
    expect(input.accept).toMatch(/image\//);
    expect(input.accept).not.toMatch(/video|audio/);

    await userEvent.upload(input, selected);

    const image = await screen.findByRole('img');
    expect(image).toHaveAccessibleName();
    expect(image).toHaveAttribute('src', shot(1).thumbnailUrl);
    expect(upload).toHaveBeenCalledTimes(1);
    expect(upload.mock.calls[0]?.[0]).toBe(selected);
    expect(onSubmit).not.toHaveBeenCalled();
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(1).id], null);
  });

  it('does not offer a direct video upload', async () => {
    const { onSubmit } = renderTask();
    const video = new File(['video'], 'karaoke.mp4', { type: 'video/mp4' });

    await userEvent.upload(fileField(), video);

    expect(upload).not.toHaveBeenCalled();
    expect(onSubmit).not.toHaveBeenCalled();
    expect(submit()).toBeDisabled();
  });

  it('removes one uploaded photo and keeps the other id and link in the draft', async () => {
    upload.mockResolvedValueOnce(shot(1)).mockResolvedValueOnce(shot(2));
    const { onSubmit } = renderTask();
    await userEvent.type(linkField(), videoLink);
    await userEvent.upload(fileField(), picture('first.png'));
    await screen.findByRole('img');
    await userEvent.upload(fileField(), picture('second.png'));
    await vi.waitFor(() => {
      expect(screen.getAllByRole('img')).toHaveLength(2);
    });

    await userEvent.click(screen.getByRole('button', { name: 'Убрать фото 1' }));
    await userEvent.click(submit());

    expect(screen.getAllByRole('img')).toHaveLength(1);
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(2).id], videoLink);
  });

  it('enforces the current photo cap without hard-coding five', async () => {
    upload.mockResolvedValueOnce(shot(1)).mockResolvedValueOnce(shot(2));
    const { onSubmit } = renderTask(task(), 2);
    await userEvent.upload(fileField(), picture('first.png'));
    await screen.findByRole('img');
    await userEvent.upload(fileField(), picture('second.png'));
    await vi.waitFor(() => {
      expect(screen.getAllByRole('img')).toHaveLength(2);
    });

    const next = screen.queryByTestId('irl-proof-file');
    if (next) expect(next).toBeDisabled();
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(1).id, shot(2).id], null);
  });

  it('does not add a duplicate stored id when the shared uploader finds the same picture', async () => {
    upload.mockResolvedValueOnce(shot(1)).mockResolvedValueOnce({ ...shot(1), duplicate: true });
    const { onSubmit } = renderTask(task(), 2);
    await userEvent.upload(fileField(), picture('first.png'));
    await screen.findByRole('img');
    await userEvent.upload(fileField(), picture('same-content.png'));
    await vi.waitFor(() => {
      expect(upload).toHaveBeenCalledTimes(2);
    });

    expect(screen.getAllByRole('img')).toHaveLength(1);
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(1).id], null);
  });

  it('keeps photos on a lower live cap, requires removing the excess, then submits the remaining id', async () => {
    upload.mockResolvedValueOnce(shot(1)).mockResolvedValueOnce(shot(2));
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect, 2);
    await userEvent.upload(fileField(), picture('first.png'));
    await screen.findByRole('img');
    await userEvent.upload(fileField(), picture('second.png'));
    await vi.waitFor(() => {
      expect(screen.getAllByRole('img')).toHaveLength(2);
    });

    rerender(
      <IrlProofSection effect={effect} maxFiles={1} pending={false} enabled onSubmit={onSubmit} />,
    );

    expect(screen.getAllByRole('img')).toHaveLength(2);
    expect(submit()).toBeDisabled();
    await userEvent.click(submit());
    expect(onSubmit).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Убрать фото 1' }));
    expect(submit()).toBeEnabled();
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(2).id], null);
  });

  it('keeps an uploaded photo and the unsent link after an upload refusal', async () => {
    upload.mockResolvedValueOnce(shot(1)).mockRejectedValueOnce(new UploadError('file.tooLarge'));
    const { onSubmit } = renderTask();
    await userEvent.type(linkField(), videoLink);
    await userEvent.upload(fileField(), picture('first.png'));
    await screen.findByRole('img');
    await userEvent.upload(fileField(), picture('too-large.png'));

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.upload.errors['file.tooLarge']);
    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getAllByRole('img')).toHaveLength(1);
    expect(onSubmit).not.toHaveBeenCalled();
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(1).id], videoLink);
  });

  it('does not expose an unexpected upload exception and keeps the link for retry', async () => {
    upload.mockRejectedValueOnce(new Error('private-server-detail'));
    renderTask();
    await userEvent.type(linkField(), videoLink);
    await userEvent.upload(fileField(), picture());

    const error = await screen.findByRole('alert');
    expect(error).not.toHaveTextContent('private-server-detail');
    expect(error.textContent).toMatch(/[а-яё]/i);
    expect(linkField()).toHaveValue(videoLink);
  });

  it('blocks submit and another upload while an image upload is in flight', async () => {
    let finish: ((file: Schemas['StoredFileView']) => void) | undefined;
    upload.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const { onSubmit } = renderTask();
    await userEvent.type(linkField(), videoLink);
    await userEvent.upload(fileField(), picture());

    expect(submit()).toBeDisabled();
    const next = screen.queryByTestId('irl-proof-file');
    if (next) expect(next).toBeDisabled();
    await userEvent.click(submit());
    expect(onSubmit).not.toHaveBeenCalled();
    await act(async () => {
      finish?.(shot(1));
      await Promise.resolve();
    });
    await screen.findByRole('img');
    expect(linkField()).toHaveValue(videoLink);
    expect(submit()).toBeEnabled();
  });

  it('preserves an unsent draft through a same-effect refresh and pending request', async () => {
    upload.mockResolvedValueOnce(shot(1));
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    await userEvent.type(linkField(), videoLink);
    await userEvent.upload(fileField(), picture());
    await screen.findByRole('img');

    rerender(
      <IrlProofSection effect={{ ...effect }} maxFiles={5} pending enabled onSubmit={onSubmit} />,
    );

    expect(linkField()).toHaveValue(videoLink);
    expect(linkField()).toBeDisabled();
    expect(submit()).toBeDisabled();
    await userEvent.dblClick(submit());
    expect(onSubmit).not.toHaveBeenCalled();
    rerender(
      <IrlProofSection
        effect={{ ...effect }}
        maxFiles={5}
        pending={false}
        enabled
        onSubmit={onSubmit}
      />,
    );
    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getAllByRole('img')).toHaveLength(1);
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([shot(1).id], videoLink);
  });

  it('shows a pending proof read-only without replacing or manually resolving it', () => {
    const { onSubmit } = renderTask(task({ irlProof: proof() }));

    expect(screen.getByText('Пруф на проверке')).toBeVisible();
    expect(screen.getByText(description)).toBeVisible();
    expect(screen.getByRole('img')).toHaveAttribute('src', fileLink(1).thumbnailUrl);
    const video = screen.getByRole('link', { name: /example\.com/ });
    expect(video).toHaveAttribute('href', videoLink);
    expect(video).toHaveAttribute('target', '_blank');
    expect(video.getAttribute('rel')?.split(/\s+/)).toEqual(
      expect.arrayContaining(['noopener', 'noreferrer']),
    );
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: /отправить|заменить|применено|не применимо/i }),
    ).not.toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it.each([
    'javascript:alert(1)',
    'http://example.com/video',
    'https://user:password@example.com/video',
  ])('never turns an invalid stored proof link into a navigable anchor: %s', (link) => {
    const { container } = renderTask(task({ irlProof: proof({ files: [], link }) }));

    expect(
      [...container.querySelectorAll('a')].some((anchor) => anchor.getAttribute('href') === link),
    ).toBe(false);
    expect(screen.getByText('Пруф на проверке')).toBeVisible();
  });

  it('shows a video link without embedding or fetching it', () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    const { container } = renderTask(task({ irlProof: proof({ files: [] }) }));

    expect(screen.getByRole('link', { name: /example\.com/ })).toBeVisible();
    expect(container.querySelector('iframe, video, audio, object, embed')).toBeNull();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('keeps a rejected proof available for correction and resubmission with its comment', async () => {
    const { onSubmit } = renderTask(
      task({
        irlProof: proof({ status: 'rejected', comment: 'Не слышно песню. <img src=x>' }),
      }),
    );

    expect(screen.getByText('Пруф отклонён')).toBeVisible();
    expect(screen.getByText('Не слышно песню. <img src=x>')).toBeVisible();
    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getAllByRole('img')).toHaveLength(1);
    await userEvent.clear(linkField());
    await userEvent.type(linkField(), 'https://example.com/new-video');
    await userEvent.click(submit());

    expect(onSubmit).toHaveBeenCalledExactlyOnceWith(
      [fileLink(1).id],
      'https://example.com/new-video',
    );
  });

  it('retains a draft through a pause without offering an upload or write during the pause', async () => {
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    await userEvent.type(linkField(), videoLink);

    rerender(
      <IrlProofSection
        effect={{ ...effect }}
        maxFiles={5}
        pending={false}
        enabled={false}
        onSubmit={onSubmit}
      />,
    );

    expect(screen.getByText('Караоке')).toBeVisible();
    expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Отправить пруф' })).not.toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
    rerender(
      <IrlProofSection
        effect={{ ...effect }}
        maxFiles={5}
        pending={false}
        enabled
        onSubmit={onSubmit}
      />,
    );
    expect(linkField()).toHaveValue(videoLink);
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenCalledExactlyOnceWith([], videoLink);
  });

  it.each([null, proof({ status: 'rejected', comment: 'Попробуй ещё раз' })])(
    'keeps a disabled task readable without any upload or submission control',
    (irlProof) => {
      const onSubmit = vi.fn();
      render(
        <IrlProofSection
          effect={task({ irlProof })}
          maxFiles={5}
          pending={false}
          enabled={false}
          onSubmit={onSubmit}
        />,
      );

      expect(screen.getByText('Караоке')).toBeVisible();
      expect(screen.getByText(description)).toBeVisible();
      expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Отправить пруф' })).not.toBeInTheDocument();
      expect(onSubmit).not.toHaveBeenCalled();
    },
  );

  it('prefills an untouched draft from a proof that arrives pending and is later rejected', () => {
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    expect(linkField()).toHaveValue('');
    rerender(
      <IrlProofSection
        effect={{ ...effect, irlProof: proof() }}
        maxFiles={5}
        pending={false}
        enabled
        onSubmit={onSubmit}
      />,
    );
    expect(screen.getByText('Пруф на проверке')).toBeVisible();
    rerender(
      <IrlProofSection
        effect={{ ...effect, irlProof: proof({ status: 'rejected', comment: 'Нужно погромче' }) }}
        maxFiles={5}
        pending={false}
        enabled
        onSubmit={onSubmit}
      />,
    );

    expect(screen.getByText('Пруф отклонён')).toBeVisible();
    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getByRole('img')).toHaveAttribute('src', fileLink(1).thumbnailUrl);
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('does not replace a dirty local draft with another submitted proof after its rejection', async () => {
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    const localLink = 'https://example.com/my-unsent-video';
    await userEvent.type(linkField(), localLink);
    rerender(
      <IrlProofSection
        effect={{ ...effect, irlProof: proof() }}
        maxFiles={5}
        pending={false}
        enabled
        onSubmit={onSubmit}
      />,
    );
    expect(screen.getByText('Пруф на проверке')).toBeVisible();
    rerender(
      <IrlProofSection
        effect={{ ...effect, irlProof: proof({ status: 'rejected', comment: 'Новое видео' }) }}
        maxFiles={5}
        pending={false}
        enabled
        onSubmit={onSubmit}
      />,
    );

    expect(screen.getByText('Пруф отклонён')).toBeVisible();
    expect(linkField()).toHaveValue(localLink);
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it.each([false, true])(
    'adopts another rejected proof after its own draft was acknowledged (equal timestamps: %s)',
    async (equalTimestamps) => {
      upload.mockResolvedValueOnce(shot(1));
      const effect = task({
        irlProof: equalTimestamps ? proof({ status: 'rejected', files: [], link: null }) : null,
      });
      const { rerender, onSubmit } = renderTask(effect);
      const receive = (irlProof: Schemas['IrlProofView']) => {
        rerender(
          <IrlProofSection
            effect={{ ...effect, irlProof }}
            maxFiles={5}
            pending={false}
            enabled
            onSubmit={onSubmit}
          />,
        );
      };

      fireEvent.change(linkField(), { target: { value: '  ' + videoLink + '  ' } });
      await userEvent.upload(fileField(), picture());
      await screen.findByRole('img');
      await userEvent.click(submit());
      expect(onSubmit).toHaveBeenCalledExactlyOnceWith([fileLink(1).id], videoLink);

      receive(proof());
      receive(proof({ status: 'rejected', comment: 'First refusal' }));
      const replacement = proof({
        files: [fileLink(2)],
        link: 'https://example.com/corrected-video',
        submittedAt: equalTimestamps ? proof().submittedAt : '2026-11-17T16:01:00Z',
      });
      receive(replacement);
      receive({ ...replacement, status: 'rejected', comment: 'Second refusal' });

      expect(linkField()).toHaveValue(replacement.link);
      expect(screen.getByRole('img')).toHaveAttribute('src', fileLink(2).thumbnailUrl);
      await userEvent.click(submit());
      expect(onSubmit).toHaveBeenLastCalledWith([fileLink(2).id], replacement.link);
    },
  );

  it('preserves the draft after a failed submission and a later refusal from another tab', async () => {
    upload.mockResolvedValueOnce(shot(1));
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    const receive = (irlProof: Schemas['IrlProofView'] | null, pending = false) => {
      rerender(
        <IrlProofSection
          effect={{ ...effect, irlProof }}
          maxFiles={5}
          pending={pending}
          enabled
          onSubmit={onSubmit}
        />,
      );
    };
    fireEvent.change(linkField(), { target: { value: videoLink } });
    await userEvent.upload(fileField(), picture());
    await screen.findByRole('img');
    await userEvent.click(submit());

    receive(null, true);
    receive(null);
    const replacement = proof({ files: [fileLink(2)], link: 'https://example.com/another-tab' });
    receive(replacement);
    receive({ ...replacement, status: 'rejected' });

    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getByRole('img')).toHaveAttribute('src', fileLink(1).thumbnailUrl);
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenLastCalledWith([fileLink(1).id], videoLink);
  });

  it('does not clear newer local edits when a late acknowledgement matches the submitted draft', async () => {
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    const receive = (irlProof: Schemas['IrlProofView']) => {
      rerender(
        <IrlProofSection
          effect={{ ...effect, irlProof }}
          maxFiles={5}
          pending={false}
          enabled
          onSubmit={onSubmit}
        />,
      );
    };
    fireEvent.change(linkField(), { target: { value: videoLink } });
    await userEvent.click(submit());
    const newerLink = 'https://example.com/not-yet-submitted';
    fireEvent.change(linkField(), { target: { value: newerLink } });
    receive(proof({ files: [] }));
    receive(proof({ files: [], status: 'rejected' }));
    const replacement = proof({
      files: [],
      link: 'https://example.com/another-tab',
      submittedAt: '2026-11-17T16:01:00Z',
    });
    receive(replacement);
    receive({ ...replacement, status: 'rejected' });

    expect(linkField()).toHaveValue(newerLink);
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenLastCalledWith([], newerLink);
  });

  it('does not acknowledge a submission when the incoming proof has different photos', async () => {
    upload.mockResolvedValueOnce(shot(1));
    const effect = task();
    const { rerender, onSubmit } = renderTask(effect);
    fireEvent.change(linkField(), { target: { value: videoLink } });
    await userEvent.upload(fileField(), picture());
    await screen.findByRole('img');
    await userEvent.click(submit());
    for (const status of ['pending', 'rejected'] as const)
      rerender(
        <IrlProofSection
          effect={{ ...effect, irlProof: proof({ status, files: [fileLink(2)] }) }}
          maxFiles={5}
          pending={false}
          enabled
          onSubmit={onSubmit}
        />,
      );

    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getByRole('img')).toHaveAttribute('src', fileLink(1).thumbnailUrl);
    await userEvent.click(submit());
    expect(onSubmit).toHaveBeenLastCalledWith([fileLink(1).id], videoLink);
  });
});
