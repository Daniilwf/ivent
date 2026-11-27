import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createRef } from 'react';
import { UploadError } from '../api/files';
import { GalleryUploadDialog } from './GalleryUploadDialog';
import {
  listProofCandidates,
  publishMoment,
  publishProofMoment,
  type GalleryMoment,
} from './galleryApi';

vi.mock('./galleryApi', () => ({
  listProofCandidates: vi.fn(),
  publishMoment: vi.fn(),
  publishProofMoment: vi.fn(),
}));

test('a repeated submission after a lost reply keeps both moment and command ids', async () => {
  const moment: GalleryMoment = {
    id: 'moment',
    playerId: 'player',
    playerName: 'Лиса',
    caption: 'Момент',
    createdAt: '2026-11-20T10:00:00Z',
    imageUrl: '/test-image',
    thumbnailUrl: '/test-thumbnail',
    canWithdraw: false,
  };
  const publish = vi.mocked(publishMoment);
  publish
    .mockRejectedValueOnce(new TypeError('Connection dropped'))
    .mockRejectedValueOnce(new UploadError('file.tooOften'))
    .mockResolvedValueOnce(moment);
  const onPublished = vi.fn();
  const user = userEvent.setup();

  render(
    <GalleryUploadDialog
      seasonId="season"
      onPublished={onPublished}
      close={vi.fn()}
      returnFocus={createRef<HTMLButtonElement>()}
    />,
  );

  const file = new File(['picture'], 'moment.png', { type: 'image/png' });
  await user.upload(screen.getByLabelText('Выбрать скриншот'), file);
  fireEvent.change(screen.getByLabelText('Подпись'), { target: { value: '  Момент  ' } });
  const send = screen.getByRole('button', { name: 'Опубликовать' });
  await user.click(send);
  expect(await screen.findByRole('alert')).toHaveTextContent('Не удалось опубликовать момент');

  await user.click(send);
  expect(await screen.findByRole('alert')).toHaveTextContent('Слишком много загрузок подряд');
  fireEvent.change(screen.getByLabelText('Подпись'), { target: { value: 'Момент   ' } });

  await user.click(send);
  expect(publish).toHaveBeenCalledTimes(3);
  const [first, second, third] = publish.mock.calls;
  if (!first || !second || !third) throw new Error('Every submission must reach the API.');
  expect(first.slice(0, 4)).toEqual(['season', second[1], file, 'Момент']);
  expect(third[1]).toEqual(first[1]);
  expect(first[1].commandId).not.toBe(first[1].itemId);
  expect(onPublished).toHaveBeenCalledWith(moment);
});

test('publishes the selected approved proof image and retries with the same command', async () => {
  const candidate = {
    runId: 'run',
    fileId: 'second-file',
    title: 'Portal',
    thumbnailUrl: '/second.jpg',
  };
  const moment: GalleryMoment = {
    id: 'published-proof',
    playerId: 'player',
    playerName: 'Лиса',
    caption: 'Выбранный кадр',
    createdAt: '2026-11-20T10:00:00Z',
    imageUrl: '/test-image',
    thumbnailUrl: '/test-thumbnail',
    canWithdraw: true,
  };
  const list = vi.mocked(listProofCandidates);
  list.mockResolvedValueOnce([
    { ...candidate, runId: 'first-run', thumbnailUrl: '/first.jpg' },
    candidate,
  ]);
  const publish = vi.mocked(publishProofMoment);
  publish.mockRejectedValueOnce(new TypeError('Connection dropped')).mockResolvedValueOnce(moment);
  const onPublished = vi.fn();
  const user = userEvent.setup();

  render(
    <GalleryUploadDialog
      seasonId="season"
      close={vi.fn()}
      onPublished={onPublished}
      returnFocus={createRef<HTMLButtonElement>()}
    />,
  );

  expect(list).not.toHaveBeenCalled();
  await user.click(screen.getByRole('radio', { name: 'Из одобренного пруфа' }));
  await user.selectOptions(await screen.findByLabelText('Одобренный кадр'), 'run:second-file');
  expect(screen.getByRole('img', { name: 'Выбранный кадр пруфа' })).toHaveAttribute(
    'src',
    '/second.jpg',
  );
  await user.type(screen.getByLabelText('Подпись'), 'Выбранный кадр');
  const send = screen.getByRole('button', { name: 'Опубликовать' });
  await user.click(send);
  await screen.findByRole('alert');
  await user.click(send);

  const [first, second] = publish.mock.calls;
  if (!first || !second) throw new Error('Both submissions must reach the API.');
  expect(first.slice(0, 4)).toEqual(['season', second[1], candidate, 'Выбранный кадр']);
  expect(onPublished).toHaveBeenCalledWith(moment);
});
