import { useRef, useState, type SyntheticEvent } from 'react';
import { api, rejectionCode } from '../api/client';
import { uploadFile } from '../api/files';
import { ru } from '../i18n/ru';
import { bugContext } from './bugContext';

type Phase = 'closed' | 'capturing' | 'open' | 'sending' | 'sent';

/** A picture of the page as it is now, before the form covers it; null when the browser cannot draw it. */
async function captureScreenshot(): Promise<Blob | null> {
  try {
    // Loaded on the first report only: most visits never need it
    const { toBlob } = await import('html-to-image');
    return await toBlob(document.body, { pixelRatio: 1, cacheBust: true });
  } catch {
    return null;
  }
}

/**
 * The «Сообщить о баге» button (SPEC, A9, D-121): a screenshot of the page is taken first, then the user describes what
 * happened; the page, the last actions and the browser's errors go along by themselves. A retry of the same report keeps
 * its command id, so it is stored once.
 */
export function BugReportButton() {
  const [phase, setPhase] = useState<Phase>('closed');
  const [text, setText] = useState('');
  const [attach, setAttach] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [screenshot, setScreenshot] = useState<Blob | null>(null);
  const commandId = useRef('');

  async function open() {
    setPhase('capturing');
    const picture = await captureScreenshot();
    commandId.current = crypto.randomUUID();
    setScreenshot(picture);
    setText('');
    setAttach(picture !== null);
    setError(null);
    setPhase('open');
  }

  async function send(event: SyntheticEvent) {
    event.preventDefault();
    if (!text.trim()) {
      setError(ru.bugReport.textRequired);
      return;
    }

    setPhase('sending');
    setError(null);
    try {
      let screenshotFileId: string | null = null;
      if (attach && screenshot) {
        // A report matters more than its picture: a refused screenshot leaves the report without one
        screenshotFileId = await uploadFile(
          screenshot,
          'screenshot.png',
          '/api/bug-reports/screenshot',
        )
          .then((file) => file.id)
          .catch(() => null);
      }

      const { error: refused, response } = await api.POST('/api/bug-reports', {
        body: {
          commandId: commandId.current,
          page: `${globalThis.location.pathname}${globalThis.location.search}`,
          text,
          context: bugContext(),
          screenshotFileId,
        },
      });
      if (response.ok) {
        setPhase('sent');
        return;
      }

      const code = rejectionCode(refused);
      setError(
        response.status === 503
          ? ru.rejection['site.maintenance']
          : response.status === 429
            ? ru.bugReport.tooOften
            : response.status === 400
              ? ru.rejection['bugReport.invalid']
              : ((code ? ru.rejection[code] : undefined) ?? ru.bugReport.failed),
      );
    } catch {
      setError(ru.bugReport.failed);
    }
    setPhase('open');
  }

  if (phase === 'closed' || phase === 'capturing') {
    return (
      <button
        type="button"
        data-testid="bug-report"
        disabled={phase === 'capturing'}
        onClick={() => void open()}
      >
        {ru.bugReport.open}
      </button>
    );
  }

  if (phase === 'sent') {
    return (
      <div role="dialog" aria-label={ru.bugReport.title} data-testid="bug-report-dialog">
        <p role="status">{ru.bugReport.sent}</p>
        <button
          type="button"
          onClick={() => {
            setPhase('closed');
          }}
        >
          {ru.bugReport.cancel}
        </button>
      </div>
    );
  }

  return (
    <div role="dialog" aria-label={ru.bugReport.title} data-testid="bug-report-dialog">
      <form onSubmit={(event) => void send(event)}>
        <h2>{ru.bugReport.title}</h2>
        <label>
          {ru.bugReport.what}
          <textarea
            data-testid="bug-report-text"
            value={text}
            maxLength={4000}
            onChange={(event) => {
              setText(event.target.value);
            }}
          />
        </label>
        {screenshot ? (
          <label>
            <input
              type="checkbox"
              data-testid="bug-report-attach"
              checked={attach}
              onChange={(event) => {
                setAttach(event.target.checked);
              }}
            />
            {ru.bugReport.attach}
          </label>
        ) : (
          <p>{ru.bugReport.noScreenshot}</p>
        )}
        <p>{ru.bugReport.context}</p>
        {error ? <p role="alert">{error}</p> : null}
        <button type="submit" data-testid="bug-report-send" disabled={phase === 'sending'}>
          {phase === 'sending' ? ru.bugReport.sending : ru.bugReport.send}
        </button>
        <button
          type="button"
          onClick={() => {
            setPhase('closed');
          }}
          disabled={phase === 'sending'}
        >
          {ru.bugReport.cancel}
        </button>
      </form>
    </div>
  );
}
