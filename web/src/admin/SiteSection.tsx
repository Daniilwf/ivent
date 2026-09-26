import { Wrench } from 'lucide-react';
import { useState } from 'react';
import { api, MAINTENANCE_EVENT } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { refusal } from './actions';
import { Loading } from './common';
import { useLoad } from './useLoad';

const t = ru.admin.site;

/** Maintenance mode (D-121): the site only reads while it is on; the banner tells everyone */
export function SiteSection() {
  const loaded = useLoad(async () => (await api.GET('/api/status')).data, []);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: 'success' | 'danger'; text: string } | null>(null);

  async function turn(on: boolean, reload: () => void) {
    setBusy(true);
    try {
      const answer = await api.PUT('/api/admin/maintenance', { body: { on } });
      if (answer.data) {
        setMessage({ tone: 'success', text: answer.data.maintenance ? t.turnedOn : t.turnedOff });
        // The banner of this page asks the site again at once
        globalThis.dispatchEvent(new Event(MAINTENANCE_EVENT));
        reload();
      } else setMessage({ tone: 'danger', text: refusal(answer) });
    } catch {
      setMessage({ tone: 'danger', text: ru.admin.failed });
    } finally {
      setOpen(false);
      setBusy(false);
    }
  }

  return (
    <Loading loaded={loaded} rows={1}>
      {(status, reload) => (
        <Panel title={t.maintenance} data-testid="admin-site">
          {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
          <p className="flex items-center gap-2 font-bold" data-testid="maintenance-state">
            <Wrench size={20} aria-hidden />
            {status.maintenance ? t.on : t.off}
          </p>
          <div>
            {status.maintenance ? (
              <Button
                variant="main"
                loading={busy}
                data-testid="maintenance-off"
                onClick={() => void turn(false, reload)}
              >
                {t.turnOff}
              </Button>
            ) : (
              <ConfirmDanger
                open={open}
                onOpenChange={setOpen}
                trigger={
                  <Button variant="danger" data-testid="maintenance-on">
                    {t.turnOn}
                  </Button>
                }
                title={t.onTitle}
                consequences={t.onConsequences}
                confirm={t.turnOn}
                busy={busy}
                onConfirm={() => void turn(true, reload)}
              />
            )}
          </div>
        </Panel>
      )}
    </Loading>
  );
}
