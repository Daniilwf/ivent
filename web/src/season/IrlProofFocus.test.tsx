import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { uploadFile } from '../api/files';
import { IrlProofSection } from './IrlProofSection';

vi.mock('../api/files', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/files')>()),
  uploadFile: vi.fn(),
}));

const effect: Schemas['ManualEffectView'] = {
  id: 'e5000000-0000-0000-0000-000000000031',
  objectId: 'karaoke',
  name: 'Караоке',
  description: 'Спой песню.',
  drawEvent: 'bad',
  source: 'event',
  irl: true,
  irlProof: null,
};
const stored: Schemas['StoredFileView'] = {
  id: 'f5000000-0000-0000-0000-000000000031',
  mediaType: 'image/webp',
  width: 40,
  height: 30,
  frames: 1,
  url: '/api/files/f5000000-0000-0000-0000-000000000031',
  thumbnailUrl: '/api/files/f5000000-0000-0000-0000-000000000031/thumbnail',
  duplicate: false,
};

function open() {
  let release: ((file: Schemas['StoredFileView']) => void) | undefined;
  vi.mocked(uploadFile)
    .mockReset()
    .mockImplementation(
      () =>
        new Promise((resolve) => {
          release = resolve;
        }),
    );
  render(
    <>
      <IrlProofSection effect={effect} maxFiles={1} pending={false} enabled onSubmit={vi.fn()} />
      <button>Продолжить ход</button>
    </>,
  );
  const picker = screen.getByTestId('irl-proof-file');
  picker.focus();
  expect(picker).toHaveFocus();
  fireEvent.change(picker, {
    target: { files: [new File(['photo'], 'photo.png', { type: 'image/png' })] },
  });
  return async () => {
    await act(async () => {
      if (!release) throw new Error('No pending upload');
      release(stored);
      await Promise.resolve();
    });
    await waitFor(() => expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument());
  };
}

it('keeps a keyboard user in the proof when the last upload removes the focused picker', async () => {
  const finish = open();
  await finish();
  expect(screen.getByRole('textbox', { name: /ссылка на видео/i })).toHaveFocus();
});

it('does not take focus back after the user left the upload for another action', async () => {
  const finish = open();
  const other = screen.getByRole('button', { name: 'Продолжить ход' });
  other.focus();
  await finish();
  expect(other).toHaveFocus();
});

const submitted: Schemas['ManualEffectView'] = {
  ...effect,
  irlProof: {
    status: 'pending',
    files: [],
    link: 'https://example.com/video',
    comment: null,
    submittedAt: '2026-11-17T12:00:00Z',
  },
};

it('keeps focus in the proof status when its own successful submission removes the focused button', async () => {
  const onSubmit = vi.fn();
  const form = (task: Schemas['ManualEffectView'], pending: boolean) => (
    <IrlProofSection effect={task} maxFiles={1} pending={pending} enabled onSubmit={onSubmit} />
  );
  const { rerender } = render(form(effect, false));
  await userEvent.type(
    screen.getByRole('textbox', { name: /ссылка на видео/i }),
    'https://example.com/video',
  );
  await userEvent.click(screen.getByRole('button', { name: 'Отправить пруф' }));
  expect(onSubmit).toHaveBeenCalledExactlyOnceWith([], 'https://example.com/video');
  rerender(form(effect, true));
  rerender(form(submitted, false));
  await screen.findByText('Пруф на проверке');
  expect(document.activeElement).not.toBe(document.body);
  expect(document.activeElement).toHaveTextContent('Пруф на проверке');
});

it('does not steal focus when a proof arrives from another open session', async () => {
  const form = (task: Schemas['ManualEffectView']) => (
    <>
      <IrlProofSection effect={task} maxFiles={1} pending={false} enabled onSubmit={vi.fn()} />
      <button>Продолжить ход</button>
    </>
  );
  const { rerender } = render(form(effect));
  const other = screen.getByRole('button', { name: 'Продолжить ход' });
  other.focus();
  rerender(form(submitted));
  await screen.findByText('Пруф на проверке');
  expect(other).toHaveFocus();
});
