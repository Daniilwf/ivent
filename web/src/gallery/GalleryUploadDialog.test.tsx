import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createRef } from 'react';
import { UploadError } from '../api/files';
import { GalleryUploadDialog } from './GalleryUploadDialog';
import { publishMoment, type GalleryMoment } from './galleryApi';

vi.mock('./galleryApi', () => ({ publishMoment: vi.fn() }));

test('a repeated submission after a lost reply keeps both moment and command ids', async () => {
  const moment: GalleryMoment = {
    id: 'moment',
    playerId: 'player',
    playerName: 'Лиса',
    caption: 'Момент',
    createdAt: '2026-11-20T10:00:00Z',
    imageUrl: '/test-image',
    thumbnailUrl: '/test-thumbnail',
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
