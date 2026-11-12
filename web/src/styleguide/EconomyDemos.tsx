import { useState } from 'react';
import { ru } from '../i18n/ru';
import { BetDialog, BetsPanel, BetRunRow } from '../season/Bets';
import { LotRow, ShopPanel } from '../season/Shop';
import { betsFixture, economyFixture, fixtureNow, shopFixture } from '../season/inventoryFixtures';
import { Button } from '../ui/Button';

const noop = () => undefined;
const done = () => Promise.resolve({ ok: true as const });
const shopOf = (shop: ReturnType<typeof shopFixture>) => ({
  kind: 'ready' as const,
  economy: economyFixture({ shop }),
});

/** The shop in each of its states and the lot's button in its states (4.11c); the clock stands still */
export function ShopDemos() {
  const t = ru.styleguide.shop;
  const lot = shopFixture().lots[0];
  return (
    <div className="grid gap-6">
      <div className="grid items-start gap-4 desk:grid-cols-2">
        <ShopPanel
          state={shopOf(shopFixture())}
          onRetry={noop}
          onRoll={done}
          onBuy={done}
          now={fixtureNow}
        />
        <div className="grid content-start gap-4">
          <ShopPanel
            state={shopOf(
              shopFixture({
                payment: 'freeRoll',
                price: 0,
                freeRolls: 1,
                expiresAt: null,
                lots: [],
              }),
            )}
            onRetry={noop}
            onRoll={done}
            onBuy={done}
            now={fixtureNow}
          />
          <ShopPanel
            state={shopOf(
              shopFixture({
                refusal: 'coins.inDebt',
                expiresAt: new Date(fixtureNow.getTime() - 1000).toISOString(),
              }),
            )}
            onRetry={noop}
            onRoll={done}
            onBuy={done}
            now={fixtureNow}
          />
          <ShopPanel state={{ kind: 'loading' }} onRetry={noop} onRoll={done} onBuy={done} />
          <ShopPanel state={{ kind: 'failed' }} onRetry={noop} onRoll={done} onBuy={done} />
        </div>
      </div>
      {lot ? (
        <div className="grid gap-2">
          <h3 className="font-display font-heavy">{t.buttons}</h3>
          <div className="grid gap-4 desk:grid-cols-2">
            {['hover', 'focus', 'active'].map((force) => (
              <ul key={force}>
                <LotRow lot={lot} busy={false} disabled={false} onBuy={noop} force={force} />
              </ul>
            ))}
            <ul>
              <LotRow lot={lot} busy disabled onBuy={noop} />
            </ul>
          </div>
        </div>
      ) : null}
    </div>
  );
}

/** The bets in each of their states, the «Поставить» button in its states and the sheet of a bet (4.11c) */
export function BetsDemos() {
  const [sheet, setSheet] = useState(false);
  const t = ru.styleguide.bets;
  const bets = betsFixture();
  const run = bets.runs[0];
  return (
    <div className="grid gap-6">
      <div className="grid items-start gap-4 desk:grid-cols-2">
        <BetsPanel
          state={{ kind: 'ready', economy: economyFixture({ bets }) }}
          onRetry={noop}
          onBet={done}
        />
        <div className="grid content-start gap-4">
          <BetsPanel
            state={{
              kind: 'ready',
              economy: economyFixture({ bets: betsFixture({ runs: [], bets: [] }) }),
            }}
            onRetry={noop}
            onBet={done}
          />
          <BetsPanel
            state={{
              kind: 'ready',
              economy: economyFixture({
                bets: betsFixture({ refusal: 'bet.tooMany', runs: [], openBets: 3 }),
              }),
            }}
            onRetry={noop}
            onBet={done}
          />
          <BetsPanel state={{ kind: 'loading' }} onRetry={noop} onBet={done} />
          <BetsPanel state={{ kind: 'failed' }} onRetry={noop} onBet={done} />
        </div>
      </div>
      {run ? (
        <>
          <div className="grid gap-4 desk:grid-cols-3">
            {['hover', 'focus', 'active'].map((force) => (
              <ul key={force}>
                <BetRunRow run={run} onBet={noop} force={force} />
              </ul>
            ))}
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
            <BetDialog
              run={run}
              stakeLimit={bets.stakeLimit}
              onBet={done}
              onClose={() => {
                setSheet(false);
              }}
            />
          ) : null}
        </>
      ) : null}
    </div>
  );
}
