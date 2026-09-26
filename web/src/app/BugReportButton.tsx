import * as Dialog from '@radix-ui/react-dialog';
import { Bug } from 'lucide-react';
import { useRef, useState, type SyntheticEvent } from 'react';
import { api, rejectionCode } from '../api/client';
import { uploadFile } from '../api/files';
import { ru } from '../i18n/ru';
import { Button, IconButton } from '../ui/Button';
import { Checkbox, TextArea } from '../ui/Field';
import { Notice } from '../ui/States';
import { bugContext } from './bugContext';
import { captureScreenshot } from './screenshot';

type Phase = 'closed' | 'capturing' | 'open' | 'sending' | 'sent';

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
  const [missingText, setMissingText] = useState(false);
  const [screenshot, setScreenshot] = useState<Blob | null>(null);
  const commandId = useRef('');
  const button = useRef<HTMLButtonElement>(null);

  async function open() {
    setPhase('capturing');
    const picture = await captureScreenshot();
    commandId.current = crypto.randomUUID();
    setScreenshot(picture);
    setText('');
    setAttach(picture !== null);
    setError(null);
    setMissingText(false);
    setPhase('open');
  }

  async function send(event: SyntheticEvent) {
    event.preventDefault();
    if (!text.trim()) {
      setMissingText(true);
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
          page: globalThis.location.pathname,
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

  const close = () => {
    setPhase('closed');
  };
  const dialogOpen = phase === 'open' || phase === 'sending' || phase === 'sent';

  return (
    <>
      <IconButton
        ref={button}
        label={ru.bugReport.open}
        data-testid="bug-report"
        loading={phase === 'capturing'}
        onClick={() => void open()}
      >
        <Bug size={20} />
      </IconButton>
      <Dialog.Root
        open={dialogOpen}
        onOpenChange={(next) => {
          if (!next && phase !== 'sending') close();
        }}
      >
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-20 bg-ink/40" />
          <Dialog.Content
            onCloseAutoFocus={(event) => {
              // The dialog opens from state, not from a trigger: focus goes back to the button by hand
              event.preventDefault();
              button.current?.focus();
            }}
            data-testid="bug-report-dialog"
            className="fixed inset-x-4 top-1/2 z-20 mx-auto grid max-h-dvh max-w-120 -translate-y-1/2 gap-4 overflow-auto rounded-lg border-3 border-ink bg-card p-5 shadow-lift"
          >
            <Dialog.Title className="font-display text-xl font-heavy">
              {ru.bugReport.title}
            </Dialog.Title>
            {phase === 'sent' ? (
              <>
                <Dialog.Description asChild>
                  <div>
                    <Notice tone="success">{ru.bugReport.sent}</Notice>
                  </div>
                </Dialog.Description>
                <Button variant="main" onClick={close}>
                  {ru.bugReport.done}
                </Button>
              </>
            ) : (
              <form onSubmit={(event) => void send(event)} className="grid gap-4">
                <Dialog.Description className="text-sm text-ink-soft">
                  {ru.bugReport.context}
                </Dialog.Description>
                <TextArea
                  label={ru.bugReport.what}
                  data-testid="bug-report-text"
                  value={text}
                  maxLength={4000}
                  error={missingText ? ru.bugReport.textRequired : undefined}
                  onChange={(event) => {
                    setText(event.target.value);
                    setMissingText(false);
                  }}
                />
                {screenshot ? (
                  <Checkbox
                    label={ru.bugReport.attach}
                    data-testid="bug-report-attach"
                    checked={attach}
                    onChange={(event) => {
                      setAttach(event.target.checked);
                    }}
                  />
                ) : (
                  <Notice tone="info">{ru.bugReport.noScreenshot}</Notice>
                )}
                {error ? <Notice tone="danger">{error}</Notice> : null}
                <div className="flex flex-wrap justify-end gap-3">
                  <Button onClick={close} disabled={phase === 'sending'}>
                    {ru.bugReport.cancel}
                  </Button>
                  <Button
                    type="submit"
                    variant="main"
                    data-testid="bug-report-send"
                    loading={phase === 'sending'}
                  >
                    {ru.bugReport.send}
                  </Button>
                </div>
              </form>
            )}
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  );
}
