import { useState } from 'react';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { InventoryPanel, ItemRow, UseItemDialog } from '../season/Inventory';
import { economyFixture, itemFixture } from '../season/inventoryFixtures';

const noop = () => undefined;
const used = () => Promise.resolve({ ok: true as const });

const choiceItem = itemFixture({
  instanceId: 'i9000000-0000-0000-0000-000000000009',
  objectId: 'dirty-trick',
  name: 'Подлянка',
  description: 'Выбери игрока с большим числом очков и жанр его следующей игры.',
  rarity: 'epic',
  steps: [
    {
      kind: 'choice',
      prompt: 'Какой жанр подкинуть?',
      options: [
        { id: 'Horror', label: 'Horror' },
        { id: 'RPG', label: 'RPG' },
      ],
      objectsByTarget: null,
    },
  ],
});

/** The inventory in each of its states, the item's button in its states and the sheet of «Использовать» (4.11a) */
export function InventoryDemos() {
  const [sheet, setSheet] = useState(false);
  const t = ru.styleguide.inventory;
  return (
    <div className="grid gap-6">
      <div className="grid items-start gap-4 desk:grid-cols-2">
        <InventoryPanel
          state={{ kind: 'ready', economy: economyFixture() }}
          onRetry={noop}
          onUse={used}
        />
        <div className="grid content-start gap-4">
          <InventoryPanel
            state={{ kind: 'ready', economy: economyFixture({ items: [], coins: -2 }) }}
            onRetry={noop}
            onUse={used}
          />
          <InventoryPanel state={{ kind: 'loading' }} onRetry={noop} onUse={used} />
          <InventoryPanel state={{ kind: 'failed' }} onRetry={noop} onUse={used} />
        </div>
      </div>
      <div className="grid gap-2">
        <h3 className="font-display font-heavy">{t.buttons}</h3>
        <ul className="grid gap-3 desk:grid-cols-2">
          {['hover', 'focus', 'active'].map((force) => (
            <ItemRow
              key={force}
              item={itemFixture({ instanceId: `force-${force}`, objectId: `force-${force}` })}
              onUse={noop}
              force={force}
            />
          ))}
          <ItemRow
            item={itemFixture({
              objectId: 'lucky-die',
              name: 'Счастливый кубик',
              description: 'Следующий бросок за прохождение получает +1 кубик.',
              hostile: false,
              rarity: 'legendary',
              usable: false,
              refusal: 'item.wrongWindow',
              window: 'beforeDice',
            })}
            onUse={noop}
          />
        </ul>
      </div>
      <div className="flex flex-wrap gap-3">
        <Button
          onClick={() => {
            setSheet(true);
          }}
        >
          {t.openSheet}
        </Button>
      </div>
      {sheet ? (
        <UseItemDialog
          item={choiceItem}
          onUse={used}
          onClose={() => {
            setSheet(false);
          }}
        />
      ) : null}
    </div>
  );
}
