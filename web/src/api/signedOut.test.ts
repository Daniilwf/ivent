import { noteResponse, SIGNED_OUT_EVENT } from './client';

// D-466: a session ended on another device or by the admin answers 401; the page hears it at once and shows the
// sign-in form. A wrong password at the sign-in is a 401 too, but not a sign-out.

describe('The sign-out signal', () => {
  it('fires on a 401 of any request but the sign-in', () => {
    const heard = vi.fn();
    globalThis.addEventListener(SIGNED_OUT_EVENT, heard);
    try {
      noteResponse('GET', `${globalThis.location.origin}/api/seasons/current`, 401);
      expect(heard).toHaveBeenCalledTimes(1);

      noteResponse('POST', `${globalThis.location.origin}/api/auth/login`, 401);
      noteResponse('GET', `${globalThis.location.origin}/api/seasons/current`, 200);
      noteResponse('GET', `${globalThis.location.origin}/api/seasons/current`, 403);
      expect(heard).toHaveBeenCalledTimes(1);
    } finally {
      globalThis.removeEventListener(SIGNED_OUT_EVENT, heard);
    }
  });
});
