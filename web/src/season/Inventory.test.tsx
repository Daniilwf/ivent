import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { EconomySection } from './Inventory';
import { economyFixture, itemFixture } from './inventoryFixtures';

// My inventory on the season screen (4.11a, D-440, D-441): every object with its words, «Использовать» only in its
// moment, the sheet offers only the targets the server gave and asks each answer; the press sends one command with the
// target and the answers; a refusal is said in Russian in the sheet; loading, empty and error states.

const petya = '10000000-0000-0000-0000-000000000002';
const kolya = '10000000-0000-0000-0000-000000000003';

function renderSection(version = 1) {
  const onSignedOut = vi.fn();
  const view = render(
    <EconomySection seasonId={seasonId} version={version} onSignedOut={onSignedOut} />,
  );
  return { ...view, onSignedOut };
}

const economyUrl = `GET /api/seasons/${seasonId}/economy`;
const useUrl = `POST /api/seasons/${seasonId}/items/*/use`;

describe('the inventory', () => {
  it('lists every object with its name, description and lifetime', async () => {
    fakeServer({ [economyUrl]: economyFixture() });

    renderSection();

    const thief = await screen.findByTestId('item-bird-thief');
    expect(within(thief).getByRole('heading', { name: 'Птичкерс' })).toBeInTheDocument();
    expect(within(thief).getByText('Укради у выбранного игрока 1d4 монеток.')).toBeInTheDocument();
    expect(within(thief).getByText(ru.inventory.hostile)).toBeInTheDocument();
    const effect = screen.getByTestId('item-curse-effect');
    expect(within(effect).getByText(ru.inventory.runsLeft(1))).toBeInTheDocument();
    expect(within(effect).getByText(ru.inventory.from('Петя'))).toBeInTheDocument();
    expect(within(effect).queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByTestId('coins')).toHaveTextContent(ru.inventory.coins(7));
    expect(screen.getByTestId('inventory-count')).toHaveTextContent(ru.inventory.count(2, 5));
  });

  it('says when an item is used instead of a button out of its moment', async () => {
    fakeServer({
      [economyUrl]: economyFixture({
        items: [
          itemFixture({
            objectId: 'lucky-die',
            name: 'Счастливый кубик',
            usable: false,
            refusal: 'item.wrongWindow',
            window: 'beforeDice',
          }),
        ],
      }),
    });

    renderSection();

    expect(await screen.findByTestId('why-lucky-die')).toHaveTextContent(
      ru.inventory.windows.beforeDice,
    );
    expect(screen.queryByTestId('use-lucky-die')).not.toBeInTheDocument();
  });

  it('is empty with a word on where items come from', async () => {
    fakeServer({ [economyUrl]: economyFixture({ items: [] }) });

    renderSection();

    expect(await screen.findByText(ru.inventory.emptyTitle)).toBeInTheDocument();
    expect(screen.getByText(ru.inventory.emptyText)).toBeInTheDocument();
  });

  it('shows the error with a retry, and the retry loads it', async () => {
    let fail = true;
    fakeServer({
      [economyUrl]: () =>
        fail ? { status: 500, body: { title: 'x' } } : { body: economyFixture() },
    });
    renderSection();
    expect(await screen.findByText(ru.inventory.loadErrorTitle)).toBeInTheDocument();

    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByTestId('item-bird-thief')).toBeInTheDocument();
  });

  it('loads again when the season moves on (a use on me shows at once)', async () => {
    const server = fakeServer({ [economyUrl]: economyFixture() });
    const { rerender, onSignedOut } = renderSection(1);
    await screen.findByTestId('item-bird-thief');

    server.calls.length = 0;
    rerender(<EconomySection seasonId={seasonId} version={2} onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(server.sent('GET', '/economy')).toHaveLength(1);
    });
  });
});

describe('«Использовать»', () => {
  it('offers only the targets the server gave and sends the target with one command', async () => {
    const server = fakeServer({
      [economyUrl]: economyFixture(),
      [useUrl]: { duplicate: false, events: [{ sequence: 9, type: 'item-used' }] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-bird-thief'));

    const sheet = await screen.findByTestId('use-item');
    expect(within(sheet).getByText(ru.inventory.useTitle('Птичкерс'))).toBeInTheDocument();
    const targets = within(sheet).getByTestId('use-targets');
    expect(within(targets).getAllByRole('radio')).toHaveLength(2);
    const confirm = within(sheet).getByTestId('use-item-confirm');
    expect(confirm).toBeDisabled();

    await userEvent.click(within(sheet).getByText('Коля'));
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);

    await waitFor(() => {
      expect(screen.queryByTestId('use-item')).not.toBeInTheDocument();
    });
    const [sent] = server.sent('POST', '/use');
    expect(sent?.path).toBe(
      `/api/seasons/${seasonId}/items/i1000000-0000-0000-0000-000000000001/use`,
    );
    expect(sent?.body).toMatchObject({ targetPlayerId: kolya, choices: [] });
    expect(typeof (sent?.body as { commandId: unknown }).commandId).toBe('string');
    expect(screen.getByRole('status')).toHaveTextContent(ru.inventory.used('Птичкерс'));
    // One live region says it: a second hidden copy would be read out twice
    expect(screen.getAllByText(ru.inventory.used('Птичкерс'))).toHaveLength(1);
  });

  it('asks each answer of the item and sends them in order', async () => {
    const trick = itemFixture({
      instanceId: 'i2000000-0000-0000-0000-000000000002',
      objectId: 'dirty-trick',
      name: 'Подлянка',
      targets: [{ playerId: petya, name: 'Петя', token: 1 }],
      steps: [
        {
          kind: 'choice',
          count: 1,
          ranked: false,
          prompt: 'Какой жанр подкинуть?',
          options: [
            { id: 'Horror', label: 'Horror' },
            { id: 'RPG', label: 'RPG' },
          ],
          objectsByTarget: null,
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [trick] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-dirty-trick'));
    const sheet = await screen.findByTestId('use-item');

    // The only target is picked already; the answer is still needed
    const confirm = within(sheet).getByTestId('use-item-confirm');
    expect(confirm).toBeDisabled();
    await userEvent.selectOptions(within(sheet).getByLabelText('Какой жанр подкинуть?'), 'RPG');
    await userEvent.click(confirm);

    await waitFor(() => {
      expect(server.sent('POST', '/use')).toHaveLength(1);
    });
    expect(server.sent('POST', '/use')[0]?.body).toMatchObject({
      targetPlayerId: petya,
      choices: ['RPG'],
    });
  });

  it('picks an object of the chosen target', async () => {
    const steal = itemFixture({
      objectId: 'pickpocket',
      name: 'Выбор вора',
      steps: [
        {
          kind: 'pickObject',
          count: 1,
          ranked: false,
          prompt: null,
          options: [],
          objectsByTarget: [
            { playerId: petya, objects: [{ id: 'aa', label: 'Апельсин' }] },
            { playerId: kolya, objects: [] },
          ],
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [steal] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-pickpocket'));
    const sheet = await screen.findByTestId('use-item');
    const pick = within(sheet).getByLabelText(ru.inventory.pickObject);
    expect(pick).toBeDisabled();

    await userEvent.click(within(sheet).getByText('Коля'));
    expect(within(sheet).getByText(ru.inventory.noObjects)).toBeInTheDocument();
    await userEvent.click(within(sheet).getByText('Петя'));
    await userEvent.selectOptions(within(sheet).getByLabelText(ru.inventory.pickObject), 'aa');
    await userEvent.click(within(sheet).getByTestId('use-item-confirm'));

    await waitFor(() => {
      expect(server.sent('POST', '/use')[0]?.body).toMatchObject({
        targetPlayerId: petya,
        choices: ['aa'],
      });
    });
  });

  it('asks no object of a target who has none, and sends no answer for it', async () => {
    const steal = itemFixture({
      objectId: 'pickpocket',
      name: 'Выбор вора',
      steps: [
        {
          kind: 'pickObject',
          count: 1,
          ranked: false,
          prompt: null,
          options: [],
          objectsByTarget: [
            { playerId: petya, objects: [{ id: 'aa', label: 'Апельсин' }] },
            { playerId: kolya, objects: [] },
          ],
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [steal] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-pickpocket'));
    const sheet = await screen.findByTestId('use-item');

    // A pick for Петя first, then Коля, who has nothing: the stale pick is not sent
    await userEvent.click(within(sheet).getByText('Петя'));
    await userEvent.selectOptions(within(sheet).getByLabelText(ru.inventory.pickObject), 'aa');
    await userEvent.click(within(sheet).getByText('Коля'));
    const confirm = within(sheet).getByTestId('use-item-confirm');
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);

    await waitFor(() => {
      expect(server.sent('POST', '/use')[0]?.body).toMatchObject({
        targetPlayerId: kolya,
        choices: [],
      });
    });
  });

  it('asks no object of my own inventory when nothing else fits', async () => {
    const swap = itemFixture({
      objectId: 'swap-own',
      name: 'Обмен',
      choosesTarget: false,
      targets: [],
      steps: [
        {
          kind: 'pickObject',
          count: 1,
          ranked: false,
          prompt: null,
          options: [],
          objectsByTarget: [{ playerId: petya, objects: [] }],
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [swap] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-swap-own'));
    const sheet = await screen.findByTestId('use-item');

    expect(within(sheet).getByText(ru.inventory.noObjects)).toBeInTheDocument();
    await userEvent.click(within(sheet).getByTestId('use-item-confirm'));

    await waitFor(() => {
      expect(server.sent('POST', '/use')[0]?.body).toMatchObject({
        targetPlayerId: null,
        choices: [],
      });
    });
  });

  it('asks the way of a push in words and sends its id', async () => {
    const pistol = itemFixture({
      objectId: 'pistol',
      name: 'Пистолет',
      targets: [{ playerId: petya, name: 'Петя', token: 1 }],
      steps: [
        {
          kind: 'direction',
          count: 1,
          ranked: false,
          prompt: null,
          options: [
            { id: 'forward', label: 'forward' },
            { id: 'back', label: 'back' },
          ],
          objectsByTarget: null,
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [pistol] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-pistol'));
    const sheet = await screen.findByTestId('use-item');

    const way = within(sheet).getByLabelText(ru.inventory.direction);
    expect(within(way).getByRole('option', { name: ru.inventory.back })).toBeInTheDocument();
    await userEvent.selectOptions(way, 'back');
    await userEvent.click(within(sheet).getByTestId('use-item-confirm'));

    await waitFor(() => {
      expect(server.sent('POST', '/use')[0]?.body).toMatchObject({
        targetPlayerId: petya,
        choices: ['back'],
      });
    });
  });

  it('asks as many different objects as the pick takes', async () => {
    const twice = itemFixture({
      objectId: 'snatch-two',
      name: 'Двойная кража',
      targets: [{ playerId: petya, name: 'Петя', token: 1 }],
      steps: [
        {
          kind: 'pickObject',
          count: 2,
          ranked: false,
          prompt: null,
          options: [],
          objectsByTarget: [
            {
              playerId: petya,
              objects: [
                { id: 'aa', label: 'Апельсин' },
                { id: 'bb', label: 'Банан' },
                { id: 'cc', label: 'Кекс' },
              ],
            },
          ],
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [twice] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-snatch-two'));
    const sheet = await screen.findByTestId('use-item');
    const confirm = within(sheet).getByTestId('use-item-confirm');

    await userEvent.selectOptions(
      within(sheet).getByLabelText(ru.inventory.pickObjectNumbered(1)),
      'bb',
    );
    expect(confirm).toBeDisabled();
    const second = within(sheet).getByLabelText(ru.inventory.pickObjectNumbered(2));
    // The object picked first is not offered again
    expect(within(second).queryByRole('option', { name: 'Банан' })).toBeNull();
    await userEvent.selectOptions(second, 'aa');
    await userEvent.click(confirm);

    await waitFor(() => {
      expect(server.sent('POST', '/use')[0]?.body).toMatchObject({ choices: ['bb', 'aa'] });
    });
  });

  it('takes a list of preference for a count known only at the use, every place optional', async () => {
    const plasma = itemFixture({
      objectId: 'plasma-gun',
      name: 'Плазмаган',
      targets: [{ playerId: petya, name: 'Петя', token: 1 }],
      steps: [
        {
          kind: 'pickObject',
          count: 1,
          ranked: true,
          prompt: null,
          options: [],
          objectsByTarget: [
            {
              playerId: petya,
              objects: [
                { id: 'aa', label: 'Апельсин' },
                { id: 'bb', label: 'Банан' },
              ],
            },
          ],
        },
      ],
    });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [plasma] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-plasma-gun'));
    const sheet = await screen.findByTestId('use-item');

    expect(within(sheet).getByText(ru.inventory.preferenceHint)).toBeInTheDocument();
    const confirm = within(sheet).getByTestId('use-item-confirm');
    expect(confirm).toBeEnabled();
    await userEvent.selectOptions(within(sheet).getByLabelText(ru.inventory.preference(2)), 'aa');
    await userEvent.click(confirm);

    await waitFor(() => {
      expect(server.sent('POST', '/use')[0]?.body).toMatchObject({ choices: ['aa'] });
    });
  });

  it('keeps the sheet open with the refusal in Russian', async () => {
    fakeServer({
      [economyUrl]: economyFixture(),
      [useUrl]: answer(409, { title: 'x', status: 409, code: 'item.invalidTarget', detail: 'no' }),
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-bird-thief'));
    const sheet = await screen.findByTestId('use-item');
    await userEvent.click(within(sheet).getByText('Петя'));

    await userEvent.click(within(sheet).getByTestId('use-item-confirm'));

    expect(await within(sheet).findByRole('alert')).toHaveTextContent(
      ru.rejection['item.invalidTarget'],
    );
    expect(screen.getByTestId('use-item')).toBeInTheDocument();
  });

  it('every refusal of the engine on items has words in the dictionary', () => {
    const codes = [
      'item.unknown',
      'item.notAnItem',
      'item.wrongWindow',
      'item.notStackable',
      'item.targetRequired',
      'item.invalidTarget',
      'item.noTarget',
      'item.invalidChoice',
      'item.hostileCap',
      'item.notApplicable',
      'inventory.frozen',
      'inventory.full',
      'feature.disabled',
      'season.deadlinePassed',
      'map.branchChoicePending',
    ];
    const missing = codes.filter(
      (code) => !(ru.rejection as Record<string, string | undefined>)[code],
    );
    expect(missing).toEqual([]);
  });
});

// Item names come in every gender (Банан, Подлянка, Зелье): no text bound to a name agrees with it
describe('texts bound to an item name', () => {
  const names = ['Банан', 'Подлянка', 'Зелье'];

  it.each(names)('read right for «%s»', async (name) => {
    const item = itemFixture({ objectId: 'x', name, rarity: 'epic', hostile: true });
    const server = fakeServer({
      [economyUrl]: economyFixture({ items: [item] }),
      [useUrl]: { duplicate: false, events: [] },
    });
    renderSection();

    const row = await screen.findByTestId('item-x');
    expect(within(row).getByText('Редкость: эпическая')).toBeInTheDocument();
    expect(within(row).getByText('Против других')).toBeInTheDocument();
    await userEvent.click(within(row).getByTestId('use-x'));
    await userEvent.click(within(await screen.findByTestId('use-item')).getByText('Коля'));
    await userEvent.click(screen.getByTestId('use-item-confirm'));

    await waitFor(() => {
      expect(server.sent('POST', '/use')).toHaveLength(1);
    });
    expect(await screen.findByRole('status')).toHaveTextContent(`Использовано: «${name}»`);
  });

  it('give every rarity as a label and a value', () => {
    expect(Object.values(ru.inventory.rarity)).toEqual([
      'Редкость: обычная',
      'Редкость: эпическая',
      'Редкость: легендарная',
    ]);
  });

  it('name what acts on its own as a ready chip', async () => {
    fakeServer({ [economyUrl]: economyFixture() });
    renderSection();

    const effect = await screen.findByTestId('item-curse-effect');
    expect(within(effect).getByText(ru.inventory.actsAloneChip('effect'))).toBeInTheDocument();
    expect(ru.inventory.actsAloneChip('effect')).toBe('Эффект: действует сам');
  });
});

describe('coins in words', () => {
  it.each([
    [1, '1 монетка'],
    [2, '2 монетки'],
    [5, '5 монеток'],
    [-1, '−1 монетка'],
    [-2, '−2 монетки'],
    [-12, '−12 монеток'],
  ])('%i reads «%s»', (n, text) => {
    expect(ru.inventory.coins(n)).toBe(text);
  });

  it('a refusal for coins says no reroll when it is the shop or a bet', () => {
    expect(ru.rejection['roll.notEnoughCoins']).not.toMatch(/реролл/i);
  });
});

// S4D-2: with 16 targets the sheet was 1552 px in a 774 px window — the press, the answers and a refusal far below.
// The title and «Закрыть» stay on top, the press and a refusal at the bottom; only the body scrolls.
describe('the «Использовать» sheet with many targets', () => {
  const many = Array.from({ length: 16 }, (_, i) => ({
    playerId: `10000000-0000-0000-0000-0000000001${String(i).padStart(2, '0')}`,
    name: `Игрок ${String(i + 1)}`,
    token: i % 8,
  }));

  async function openSheet(over?: Record<string, unknown>) {
    fakeServer({
      [economyUrl]: economyFixture({
        items: [itemFixture({ targets: many })],
      }),
      [useUrl]: over
        ? answer(409, over)
        : { duplicate: false, events: [{ sequence: 9, type: 'item-used' }] },
    });
    renderSection();
    await userEvent.click(await screen.findByTestId('use-bird-thief'));
    return screen.findByTestId('use-item');
  }

  it('keeps the title and the press out of the scrolling body', async () => {
    const sheet = await openSheet();
    const body = within(sheet).getByTestId('form-dialog-body');
    const footer = within(sheet).getByTestId('form-dialog-footer');
    expect(within(body).getByTestId('use-targets')).toBeInTheDocument();
    expect(within(footer).getByTestId('use-item-confirm')).toBeInTheDocument();
    expect(body).not.toContainElement(
      within(sheet).getByRole('heading', { name: ru.inventory.useTitle('Птичкерс') }),
    );
    expect(body).not.toContainElement(within(sheet).getByRole('button', { name: ru.ui.close }));
  });

  it('says a refusal next to the press, and the press still sends the form', async () => {
    const sheet = await openSheet({
      title: 'x',
      status: 409,
      code: 'item.invalidTarget',
      detail: 'no',
    });
    await userEvent.click(within(sheet).getByText('Игрок 16'));
    await userEvent.click(within(sheet).getByTestId('use-item-confirm'));

    const alert = await within(sheet).findByRole('alert');
    expect(within(sheet).getByTestId('form-dialog-footer')).toContainElement(alert);
  });

  it('says next to the closed press what is still to answer', async () => {
    const sheet = await openSheet();
    const footer = within(sheet).getByTestId('form-dialog-footer');
    expect(footer).toHaveTextContent(ru.inventory.missing(ru.inventory.target));
    await userEvent.click(within(sheet).getByText('Игрок 1'));
    expect(footer).not.toHaveTextContent(ru.inventory.missing(ru.inventory.target));
  });

  it('marks the chosen target with a check besides the fill', async () => {
    const sheet = await openSheet();
    await userEvent.click(within(sheet).getByText('Игрок 3'));
    const picked = within(sheet).getByTestId(`target-${many[2]?.playerId ?? ''}`);
    const other = within(sheet).getByTestId(`target-${many[3]?.playerId ?? ''}`);
    expect(picked.querySelector('.lucide-check')).not.toBeNull();
    expect(other.querySelector('.lucide-check')).toBeNull();
  });
});

// S4D-9: five items took ~2000 px — the description is two lines in the list, whole in the sheet
describe('the item row', () => {
  it('clamps the description to two lines in the list', async () => {
    fakeServer({ [economyUrl]: economyFixture() });
    renderSection();
    const row = await screen.findByTestId('item-bird-thief');
    expect(within(row).getByText(itemFixture().description)).toHaveClass('line-clamp-2');
  });
});
