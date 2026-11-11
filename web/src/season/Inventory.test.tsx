import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { InventorySection } from './Inventory';
import { economyFixture, itemFixture } from './inventoryFixtures';

// My inventory on the season screen (4.11a, D-440, D-441): every object with its words, «Использовать» only in its
// moment, the sheet offers only the targets the server gave and asks each answer; the press sends one command with the
// target and the answers; a refusal is said in Russian in the sheet; loading, empty and error states.

const petya = '10000000-0000-0000-0000-000000000002';
const kolya = '10000000-0000-0000-0000-000000000003';

function renderSection(version = 1) {
  const onSignedOut = vi.fn();
  const view = render(
    <InventorySection seasonId={seasonId} version={version} onSignedOut={onSignedOut} />,
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
    rerender(<InventorySection seasonId={seasonId} version={2} onSignedOut={onSignedOut} />);

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
