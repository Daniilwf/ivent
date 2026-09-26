import { KeyRound, UserCog, UserPlus } from 'lucide-react';
import { useCallback, useState, type SyntheticEvent } from 'react';
import { api, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { Field, Select } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { newCommandId, refusal } from './actions';
import { Loading } from './common';
import { answerOf, useLoaded } from '../app/useLoaded';

const t = ru.admin.accounts;

type Account = Schemas['AccountView'];
type Role = Schemas['Role'];
type Message = { tone: 'success' | 'danger'; text: string } | null;
type Secret = { login: string; password: string } | null;

const roles: Role[] = ['player', 'admin', 'spectator'];

/** Accounts live between seasons (SPEC «Аккаунты»): create, rename, change the role, reset the password, delete */
export function AccountsSection({ me }: { me: string }) {
  const loaded = useLoaded(
    useCallback(async () => answerOf(await api.GET('/api/admin/accounts')), []),
  );
  const [message, setMessage] = useState<Message>(null);
  const [secret, setSecret] = useState<Secret>(null);

  return (
    <Loading loaded={loaded}>
      {(accounts, reload) => {
        const done = (text: string, password?: { login: string; password: string | null }) => {
          setMessage({ tone: 'success', text });
          setSecret(
            password?.password ? { login: password.login, password: password.password } : null,
          );
          reload();
        };
        const failed = (text: string) => {
          setMessage({ tone: 'danger', text });
        };
        return (
          <div className="grid gap-6" data-testid="admin-accounts">
            {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
            {secret ? (
              <div
                className="grid gap-2 rounded-lg border-2 border-ink bg-card p-4"
                data-testid="temporary-password"
              >
                <p className="flex items-start gap-2">
                  <KeyRound size={20} aria-hidden className="shrink-0" />
                  {t.tempPassword(secret.login)}
                </p>
                <p className="font-display text-xl font-heavy break-all select-all tabular-nums">
                  {secret.password}
                </p>
                <div>
                  <Button
                    variant="link"
                    onClick={() => {
                      setSecret(null);
                    }}
                  >
                    {t.hidePassword}
                  </Button>
                </div>
              </div>
            ) : null}
            <CreateAccount onDone={done} onFailed={failed} />
            {accounts.length === 0 ? (
              <EmptyState
                level={2}
                icon={<UserCog size={28} aria-hidden />}
                title={t.emptyTitle}
                text={t.emptyText}
              />
            ) : (
              <ul className="grid gap-3">
                {accounts.map((a) => (
                  <li key={a.id}>
                    <AccountRow account={a} me={a.id === me} onDone={done} onFailed={failed} />
                  </li>
                ))}
              </ul>
            )}
          </div>
        );
      }}
    </Loading>
  );
}

type Done = (text: string, password?: { login: string; password: string | null }) => void;

function CreateAccount({ onDone, onFailed }: { onDone: Done; onFailed: (text: string) => void }) {
  const [login, setLogin] = useState('');
  const [name, setName] = useState('');
  const [role, setRole] = useState<Role>('player');
  const [errors, setErrors] = useState<{ login?: string | undefined; name?: string | undefined }>(
    {},
  );
  const [busy, setBusy] = useState(false);

  async function submit(e: SyntheticEvent) {
    e.preventDefault();
    const found = {
      login: login.trim() === '' ? t.required : undefined,
      name: name.trim() === '' ? t.required : undefined,
    };
    setErrors(found);
    if (found.login || found.name) return;
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/accounts', {
        body: { commandId: newCommandId(), login: login.trim(), name: name.trim(), role },
      });
      if (answer.data) {
        setLogin('');
        setName('');
        onDone(t.created(answer.data.account.login), {
          login: answer.data.account.login,
          password: answer.data.temporaryPassword,
        });
      } else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title={t.create}>
      <form
        className="grid gap-3 desk:grid-cols-3 desk:items-start"
        onSubmit={(e) => void submit(e)}
        noValidate
      >
        <Field
          label={ru.login.login}
          hint={t.loginHint}
          value={login}
          autoCapitalize="off"
          autoComplete="off"
          maxLength={32}
          error={errors.login}
          data-testid="account-login"
          onChange={(e) => {
            setLogin(e.target.value);
          }}
        />
        <Field
          label={t.name}
          hint={t.nameHint}
          value={name}
          maxLength={64}
          error={errors.name}
          data-testid="account-name"
          onChange={(e) => {
            setName(e.target.value);
          }}
        />
        <Select
          label={t.role}
          value={role}
          data-testid="account-role"
          onChange={(e) => {
            setRole(e.target.value as Role);
          }}
        >
          {roles.map((r) => (
            <option key={r} value={r}>
              {t.roles[r]}
            </option>
          ))}
        </Select>
        <div className="desk:col-span-3">
          <Button
            type="submit"
            icon={<UserPlus size={20} aria-hidden />}
            loading={busy}
            data-testid="account-create"
          >
            {t.create}
          </Button>
        </div>
      </form>
    </Panel>
  );
}

function AccountRow({
  account,
  me,
  onDone,
  onFailed,
}: {
  account: Account;
  me: boolean;
  onDone: Done;
  onFailed: (text: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(account.name);
  const [role, setRole] = useState<Role>(account.role);
  const [nameError, setNameError] = useState<string>();
  const [busy, setBusy] = useState<'save' | 'reset' | 'delete' | 'restore' | null>(null);
  const [confirming, setConfirming] = useState<'reset' | 'delete' | null>(null);

  async function save(e: SyntheticEvent) {
    e.preventDefault();
    if (name.trim() === '') {
      setNameError(t.required);
      return;
    }
    setNameError(undefined);
    setBusy('save');
    try {
      const answer = await api.POST('/api/admin/accounts/{userId}', {
        params: { path: { userId: account.id } },
        body: { commandId: newCommandId(), name: name.trim(), role },
      });
      if (answer.data) {
        setEditing(false);
        onDone(t.saved(answer.data.account.name));
      } else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  async function act(action: 'reset' | 'delete' | 'restore') {
    setBusy(action);
    try {
      const params = { path: { userId: account.id } };
      const body = { commandId: newCommandId() };
      const answer =
        action === 'reset'
          ? await api.POST('/api/admin/accounts/{userId}/reset-password', { params, body })
          : action === 'delete'
            ? await api.POST('/api/admin/accounts/{userId}/delete', { params, body })
            : await api.POST('/api/admin/accounts/{userId}/restore', { params, body });
      setConfirming(null);
      if (!answer.data) {
        onFailed(refusal(answer));
        return;
      }
      if (action === 'reset')
        onDone(t.resetOk(account.name), {
          login: account.login,
          password: answer.data.temporaryPassword,
        });
      else onDone(action === 'delete' ? t.deletedOk(account.name) : t.restored(account.name));
    } catch {
      setConfirming(null);
      onFailed(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  return (
    <article
      className="grid gap-3 rounded-lg bg-card p-4 wrap-anywhere"
      data-testid={`account-${account.login}`}
    >
      <div className="grid gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="font-display text-lg font-heavy">{account.name}</h2>
          <span className="rounded-full bg-muted px-2 text-xs font-bold">
            {t.roles[account.role]}
          </span>
          {account.isDeleted ? (
            <span className="rounded-full bg-danger-soft px-2 text-xs font-bold text-ink">
              {t.deleted}
            </span>
          ) : null}
        </div>
        <p className="text-sm text-ink-soft">
          {account.login}
          {account.mustChangePassword ? `, ${t.mustChange}` : ''}
        </p>
      </div>
      {account.isDeleted ? (
        <div>
          <Button
            loading={busy === 'restore'}
            data-testid="account-restore"
            onClick={() => void act('restore')}
          >
            {t.restore}
          </Button>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-3">
          <Button
            variant="link"
            aria-expanded={editing}
            data-testid="account-edit"
            onClick={() => {
              setEditing(!editing);
            }}
          >
            {ru.admin.edit}
          </Button>
          <ConfirmDanger
            open={confirming === 'reset'}
            onOpenChange={(open) => {
              setConfirming(open ? 'reset' : null);
            }}
            trigger={
              <Button variant="dangerLink" data-testid="account-reset">
                {t.reset}
              </Button>
            }
            title={t.resetTitle(account.name)}
            consequences={t.resetConsequences}
            confirm={t.resetConfirm}
            busy={busy === 'reset'}
            onConfirm={() => void act('reset')}
          />
          {me ? null : (
            <ConfirmDanger
              open={confirming === 'delete'}
              onOpenChange={(open) => {
                setConfirming(open ? 'delete' : null);
              }}
              trigger={
                <Button variant="dangerLink" data-testid="account-delete">
                  {t.delete}
                </Button>
              }
              title={t.deleteTitle(account.name)}
              consequences={t.deleteConsequences}
              confirm={t.deleteConfirm}
              busy={busy === 'delete'}
              onConfirm={() => void act('delete')}
            />
          )}
        </div>
      )}
      {editing ? (
        <form
          className="grid gap-3 rounded-md border-2 border-muted p-3 desk:grid-cols-2 desk:items-start"
          onSubmit={(e) => void save(e)}
          noValidate
        >
          <Field
            label={t.name}
            value={name}
            maxLength={64}
            error={nameError}
            data-testid="account-edit-name"
            onChange={(e) => {
              setName(e.target.value);
            }}
          />
          <Select
            label={t.role}
            value={role}
            data-testid="account-edit-role"
            onChange={(e) => {
              setRole(e.target.value as Role);
            }}
          >
            {roles.map((r) => (
              <option key={r} value={r}>
                {t.roles[r]}
              </option>
            ))}
          </Select>
          <div className="desk:col-span-2">
            <Button type="submit" loading={busy === 'save'} data-testid="account-save">
              {ru.admin.save}
            </Button>
          </div>
        </form>
      ) : null}
    </article>
  );
}
