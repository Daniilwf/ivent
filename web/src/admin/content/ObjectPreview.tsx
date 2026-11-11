import { ru } from '../../i18n/ru';
import { Badge, Chip } from '../../ui/Marks';
import type { Json } from './templates';

const t = ru.admin.content;

const text = (value: unknown) => (typeof value === 'string' ? value : '');
const word = (group: string, value: unknown) =>
  typeof value === 'string' ? (t.values[group]?.[value] ?? value) : null;

/**
 * An object as the players read it (D-497): the name, the description and the marks. Every text goes in as text, never
 * as markup — a name «<img onerror=…>» shows those characters. The effect itself is not retold in words: the engine
 * has no describer yet, the description is what the player reads.
 */
export function ObjectPreview({ value }: { value: Json }) {
  const name = text(value.name);
  const price = typeof value.price === 'number' ? value.price : null;
  const kind = word('kind', value.kind);
  const rarity = word('rarity', value.rarity);
  const window = word('window', value.window);
  return (
    <article
      className="grid gap-2 rounded-lg border-3 border-ink bg-card p-4 wrap-anywhere"
      data-testid="content-preview"
      aria-label={t.preview}
    >
      <div className="flex flex-wrap items-center gap-2">
        <p className="mr-auto font-display text-lg font-heavy" data-testid="content-preview-name">
          {name === '' ? t.previewEmpty : name}
        </p>
        {value.hostile === true ? <Badge tone="danger">{t.hostile}</Badge> : null}
      </div>
      <p className="whitespace-pre-line" data-testid="content-preview-description">
        {text(value.description)}
      </p>
      <div className="flex flex-wrap gap-2">
        {kind ? <Chip>{kind}</Chip> : null}
        {rarity ? <Chip>{rarity}</Chip> : null}
        {window ? <Chip>{window}</Chip> : null}
        {value.kind === 'item' ? (
          <Chip>{price === null ? t.notForSale : t.price(price)}</Chip>
        ) : null}
        {value.stackable === true ? <Chip>{t.stackable}</Chip> : null}
        {value.manual === true ? <Chip>{t.manual}</Chip> : null}
      </div>
    </article>
  );
}
