import { useState, type ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';

/** Cards shown at first and added by «Показать ещё» */
export const pageSize = 60;

/**
 * A long list of games shown page by page (the pool page, the admin's pool; D-202): hundreds of cards at once slow a
 * phone down. The list grows by a page with «Показать ещё N»; a new `key` (another filter or search) starts from the
 * first page again.
 */
export function usePaging<T>(
  items: readonly T[],
  key: string,
): { page: readonly T[]; more: ReactNode } {
  const [showing, setShowing] = useState({ key: '', count: pageSize });
  const visible = showing.key === key ? showing.count : pageSize;
  const left = items.length - visible;
  return {
    page: items.slice(0, visible),
    more:
      left > 0 ? (
        <Button
          className="justify-self-center"
          data-testid="show-more"
          onClick={() => {
            setShowing({ key, count: visible + pageSize });
          }}
        >
          {ru.pool.more(Math.min(pageSize, left), left)}
        </Button>
      ) : null,
  };
}
