import { UploadError, type StoredFile } from '../api/files';
import { ru } from '../i18n/ru';

export type ProofShot = Pick<StoredFile, 'id' | 'thumbnailUrl'>;

export const proofPictureTypes = 'image/png,image/jpeg,image/webp,image/gif';

export function proofUploadError(error: unknown): string {
  const code = error instanceof UploadError ? error.code : null;
  return (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) || ru.upload.failed;
}
