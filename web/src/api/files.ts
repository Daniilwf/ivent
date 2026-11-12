import { newCommandId } from './commands';
import { siteEnvironment } from '../app/siteStatus';
import { antiforgeryHeaders, noteResponse, rejectionCode, type Schemas } from './client';

export type StoredFile = Schemas['StoredFileView'];

/** An upload answered with a refusal: the `file.*` code, or null when the server gave none. */
export class UploadError extends Error {
  readonly code: string | null;

  constructor(code: string | null) {
    super(code ?? 'upload failed');
    this.code = code;
  }
}

type UploadLimits = Schemas['UploadLimitsView'];

/**
 * The largest file the site takes at `path`, as the site says it is configured (`/api/status`, D-617). The server
 * checks it again; this only spares a doomed upload.
 */
export function maxUploadBytes(path: string, type: string, limits: UploadLimits): number {
  if (path === '/api/bug-reports/screenshot') return limits.bugScreenshot;
  if (path.endsWith('/map/background')) return limits.mapBackground;
  return type === 'image/gif' ? limits.gif : limits.picture;
}

/**
 * Uploads one picture (D-108) as multipart with the antiforgery header. Each call is a new upload with a new command id
 * (a retry of the same request on the server stores it once). The typed client builds JSON requests, so the form goes
 * through fetch itself; the answer is still the generated type.
 */
export async function uploadFile(
  file: Blob,
  name = file instanceof File ? file.name : 'picture.png',
  path:
    | '/api/files'
    | '/api/bug-reports/screenshot'
    | `/api/admin/seasons/${string}/map/background` = '/api/files',
): Promise<StoredFile> {
  // A body over the server's limit is cut off with no answer the page could read (K1A): it is refused here first. A
  // site that did not say its limits gets the file and answers for itself.
  const limits = (await siteEnvironment())?.uploads;
  if (limits && file.size > maxUploadBytes(path, file.type, limits))
    throw new UploadError('file.tooLarge');
  const form = new FormData();
  form.append('commandId', newCommandId());
  form.append('file', file, name);
  const url = `${globalThis.location.origin}${path}`;
  const response = await globalThis.fetch(url, {
    method: 'POST',
    body: form,
    credentials: 'same-origin',
    headers: antiforgeryHeaders(),
  });
  noteResponse('POST', url, response.status);
  const body: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    // A proxy or the rate limiter answers without a code: the status still says why
    const byStatus =
      response.status === 413 ? 'file.tooLarge' : response.status === 429 ? 'file.tooOften' : null;
    throw new UploadError(rejectionCode(body) ?? byStatus);
  }
  return body as StoredFile;
}
