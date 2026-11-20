import { antiforgeryHeaders, api, noteResponse, rejectionCode, type Schemas } from '../api/client';
import { maxUploadBytes, UploadError } from '../api/files';
import { siteEnvironment } from '../app/siteStatus';
import { answerOf, type Answer } from '../app/useLoaded';

export type Gallery = Schemas['GalleryView'];
export type GalleryMoment = Schemas['GalleryMomentView'];
export type GalleryAnswer = Answer<Gallery> | { kind: 'disabled' };

export async function readGallery(
  seasonId: string,
  playerId: string,
  pages: number,
  signal: AbortSignal,
  itemId?: string,
): Promise<GalleryAnswer> {
  let query: { playerId?: string; before?: string; itemId?: string } = {
    ...(playerId ? { playerId } : {}),
    ...(itemId ? { itemId } : {}),
  };
  const items: GalleryMoment[] = [];
  for (let page = 0; page < pages; page++) {
    const result = await api.GET('/api/seasons/{seasonId}/gallery', {
      params: { path: { seasonId }, query },
      signal,
    });
    if (rejectionCode(result.error) === 'feature.disabled') return { kind: 'disabled' };
    if (result.response.status === 403) return { kind: 'notFound' };
    const answer = answerOf(result);
    if (answer.kind !== 'ready') return answer;
    items.push(...answer.value.items);
    if (!answer.value.nextBefore || page === pages - 1)
      return { kind: 'ready', value: { ...answer.value, items } };
    query = { ...query, before: answer.value.nextBefore };
  }
  return { kind: 'failed' };
}

export async function publishMoment(
  seasonId: string,
  ids: { commandId: string; itemId: string },
  file: File,
  caption: string,
  signal: AbortSignal,
): Promise<GalleryMoment> {
  const path = `/api/seasons/${seasonId}/gallery`;
  const limits = (await siteEnvironment())?.uploads;
  if (limits && file.size > maxUploadBytes(path, file.type, limits))
    throw new UploadError('file.tooLarge');

  const form = new FormData();
  form.set('commandId', ids.commandId);
  form.set('itemId', ids.itemId);
  form.set('caption', caption);
  form.set('file', file);
  const response = await globalThis.fetch(path, {
    method: 'POST',
    body: form,
    credentials: 'same-origin',
    headers: antiforgeryHeaders(),
    signal,
  });
  noteResponse('POST', path, response.status);
  if (response.ok) return (await response.json()) as GalleryMoment;

  const body: unknown = await response.json().catch(() => null);
  const code = rejectionCode(body);
  if (code) throw new UploadError(code);
  if (response.status === 413) throw new UploadError('file.tooLarge');
  if (response.status === 429) throw new UploadError('file.tooOften');
  if (response.status === 403 || response.status === 404)
    throw new UploadError('gallery.notParticipant');
  throw new UploadError(null);
}
