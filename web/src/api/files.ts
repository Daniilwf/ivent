import { newCommandId } from './commands';
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

/**
 * Uploads one picture (D-108) as multipart with the antiforgery header. Each call is a new upload with a new command id
 * (a retry of the same request on the server stores it once). The typed client builds JSON requests, so the form goes
 * through fetch itself; the answer is still the generated type.
 */
export async function uploadFile(
  file: Blob,
  name = file instanceof File ? file.name : 'picture.png',
  path: '/api/files' | '/api/bug-reports/screenshot' = '/api/files',
): Promise<StoredFile> {
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
