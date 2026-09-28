import { api, type Schemas } from '../api/client';

type Status = Schemas['SiteStatusView'];

// Which copy of the site this is does not change while a page is open (H9, D-220): the strip and the admin screen ask
// the site once and share the answer. A failed or unanswered request is not kept, so the next asker tries again.
let asked: Promise<Status | null> | null = null;

/** The site's status once per page: `environment` and `testTools` for the strip and the test tools; null — no answer */
export function siteEnvironment(): Promise<Status | null> {
  asked ??= api
    .GET('/api/status')
    .then(({ data }) => data ?? null)
    .catch(() => null)
    .then((status) => {
      if (status === null) asked = null;
      return status;
    });
  return asked;
}

/** Tests only: the next asker asks the site again */
export function forgetSiteEnvironment() {
  asked = null;
}
