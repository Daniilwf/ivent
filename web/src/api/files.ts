import { antiforgeryHeaders, rejectionCode, type Schemas } from './client';

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
 * Uploads one picture (D-108) as multipart with the antiforgery header. A new command id each time; the server stores it
 * once even if the request is repeated. The typed client builds JSON requests, so the form goes through fetch itself;
 * the answer is still the generated type.
 */
export async function uploadFile(file: File): Promise<StoredFile> {
  const form = new FormData();
  form.append('commandId', crypto.randomUUID());
  form.append('file', file, file.name);
  const response = await globalThis.fetch(`${globalThis.location.origin}/api/files`, {
    method: 'POST',
    body: form,
    credentials: 'same-origin',
    headers: antiforgeryHeaders(),
  });
  const body: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    throw new UploadError(rejectionCode(body));
  }
  return body as StoredFile;
}
