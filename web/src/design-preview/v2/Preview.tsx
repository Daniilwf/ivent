import '@fontsource/unbounded/cyrillic-800.css';
import '@fontsource/unbounded/latin-800.css';
import '@fontsource/unbounded/cyrillic-600.css';
import '@fontsource/unbounded/latin-600.css';
import '@fontsource/onest/cyrillic-400.css';
import '@fontsource/onest/cyrillic-600.css';
import '@fontsource/onest/cyrillic-700.css';
import '@fontsource/onest/latin-400.css';
import '@fontsource/onest/latin-600.css';
import '@fontsource/onest/latin-700.css';
import './preview.css';
import './moments.css';
import { useEffect, useState } from 'react';
import { ru } from '../../i18n/ru';
import { useContent, type Content } from './content';
import { MainScreen } from './MainScreen';
import { DiceMoment, FinishMoment, MoveMoment, WheelMoment } from './Moments';

const t = ru.designPreview;

// The previews of the second round (G2), one page each, chosen by a plain anchor: design-preview.html#wheel. The anchor
// works in the site and in a published copy of the page alike. The chosen palette «Кухонный стол» is the default; «-felt» at the end shows «Зелёное сукно».

function useAnchor() {
  const read = () => globalThis.location.hash.replace(/^#/, '');
  const [anchor, setAnchor] = useState(read);
  useEffect(() => {
    const change = () => {
      setAnchor(read());
      globalThis.scrollTo(0, 0);
    };
    globalThis.addEventListener('hashchange', change);
    return () => {
      globalThis.removeEventListener('hashchange', change);
    };
  }, []);
  return anchor;
}

function Index({ content }: { content: Content }) {
  const links: [string, string][] = [
    ['main-world', t.index.mainWorld],
    ['main-board', t.index.mainBoard],
    ['palettes', t.index.palettes],
  ];
  const moments: [string, string][] = [
    ['wheel', t.index.wheel],
    ['dice', t.index.dice],
    ['move', t.index.move],
    ['finish', t.index.finish],
  ];
  return (
    <div className="proto index">
      <header className="proto-head">
        <h1>{t.index.title}</h1>
        <p>{t.index.lead}</p>
        {content.real ? null : <p className="hint">{t.index.noLocal}</p>}
      </header>
      <nav className="index-grid" aria-label={t.index.screens}>
        <h2>{t.index.screens}</h2>
        {links.map(([href, label]) => (
          <a key={href} className="index-link" href={`#${href}`}>
            {label}
          </a>
        ))}
        <h2>{t.index.moments}</h2>
        {moments.map(([href, label]) => (
          <a key={href} className="index-link" href={`#${href}`}>
            {label}
          </a>
        ))}
      </nav>
    </div>
  );
}

function Palettes() {
  // Each palette in a phone of its own: the frames are the same page at 390 px, so the phone layout applies inside
  const frames: [string, string, string][] = [
    ['main-world', t.palette.kitchen, t.palette.kitchenWhy],
    ['main-world-felt', t.palette.felt, t.palette.feltWhy],
  ];
  return (
    <div className="proto palettes">
      <header className="proto-head">
        <a className="back" href="#">
          {t.back}
        </a>
        <h1>{t.index.palettes}</h1>
      </header>
      <div className="phones">
        {frames.map(([page, name, why]) => (
          <figure key={page} className="phone">
            <figcaption>
              <strong>{name}</strong>
              <span>{why}</span>
            </figcaption>
            <iframe
              title={name}
              src={`${globalThis.location.pathname}#${page}`}
              width={390}
              height={760}
            />
          </figure>
        ))}
      </div>
    </div>
  );
}

export function Preview() {
  const content = useContent();
  const anchor = useAnchor();
  const felt = anchor.endsWith('-felt');
  const page = anchor.replace(/-felt$/, '');
  useEffect(() => {
    document.title = `${t.index.title}${page ? `: ${page}` : ''}`;
  }, [page]);
  if (!content) return <div className={`dp ${felt ? 'pal-felt' : 'pal-kitchen'}`} />;
  const screen = (() => {
    switch (page) {
      case 'main-world':
        return (
          <MainScreen content={content} variant="world" compact={globalThis.innerWidth < 900} />
        );
      case 'main-board':
        return (
          <MainScreen content={content} variant="board" compact={globalThis.innerWidth < 900} />
        );
      case 'palettes':
        return <Palettes />;
      case 'wheel':
        return <WheelMoment content={content} />;
      case 'dice':
        return <DiceMoment />;
      case 'move':
        return <MoveMoment content={content} />;
      case 'finish':
        return <FinishMoment content={content} />;
      default:
        return <Index content={content} />;
    }
  })();
  return <div className={`dp ${felt ? 'pal-felt' : 'pal-kitchen'}`}>{screen}</div>;
}
