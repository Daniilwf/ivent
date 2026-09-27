import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { AccountsSection } from './AccountsSection';
import { adminUser, answer, fakeServer } from '../test/fakeServer';

// H8: accounts (D8, D-106): create with a temporary password shown once, change the name and the role, reset the
// password and delete after a confirmation, restore; the admin cannot delete themselves from here.

const t = ru.admin.accounts;

const account = (id: string, login: string, name: string, extra: object = {}) => ({
  id,
  login,
  name,
  role: 'player' as const,
  mustChangePassword: false,
  isDeleted: false,
  createdAt: '2026-09-01T00:00:00Z',
  ...extra,
});

const accounts = [
  account(adminUser.id, 'admin', 'Админ', { role: 'admin' }),
  account('u1', 'vasya', 'Вася', { mustChangePassword: true }),
  account('u2', 'old', 'Старый', { isDeleted: true }),
];

function open(routes: Record<string, unknown> = {}) {
  const server = fakeServer({ 'GET /api/admin/accounts': accounts, ...routes });
  render(<AccountsSection me={adminUser.id} />);
  return server;
}

const actionOk = (password: string | null) => ({
  duplicate: false,
  account: account('u9', 'sova', 'Сова'),
  temporaryPassword: password,
});

describe('The accounts', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists the accounts with roles and marks', async () => {
    open();

    const vasya = await screen.findByTestId('account-vasya');
    expect(vasya).toHaveTextContent(t.roles.player);
    expect(vasya).toHaveTextContent(t.mustChange);
    expect(screen.getByTestId('account-old')).toHaveTextContent(t.deleted);
    expect(within(screen.getByTestId('account-old')).getByTestId('account-restore')).toBeVisible();
  });

  it('creates an account and shows its temporary password once', async () => {
    const server = open({ 'POST /api/admin/accounts': actionOk('kotik-42-lampa') });

    await userEvent.click(await screen.findByTestId('account-create'));
    expect(screen.getAllByText(t.required)).toHaveLength(2);

    await userEvent.type(screen.getByTestId('account-login'), 'sova');
    await userEvent.type(screen.getByTestId('account-name'), 'Сова');
    await userEvent.selectOptions(screen.getByTestId('account-role'), 'spectator');
    await userEvent.click(screen.getByTestId('account-create'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/accounts')[0]?.body).toMatchObject({
        login: 'sova',
        name: 'Сова',
        role: 'spectator',
      });
    });
    const secret = await screen.findByTestId('temporary-password');
    expect(secret).toHaveTextContent('kotik-42-lampa');
    await userEvent.click(within(secret).getByRole('button', { name: t.hidePassword }));
    expect(screen.queryByTestId('temporary-password')).toBeNull();
  });

  it('says a taken login', async () => {
    open({
      'POST /api/admin/accounts': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'account.loginTaken',
      }),
    });

    await userEvent.type(await screen.findByTestId('account-login'), 'vasya');
    await userEvent.type(screen.getByTestId('account-name'), 'Вася');
    await userEvent.click(screen.getByTestId('account-create'));

    expect(await screen.findByText(ru.rejection['account.loginTaken'])).toBeInTheDocument();
  });

  it('changes the name and the role', async () => {
    const server = open({ 'POST /api/admin/accounts/*': actionOk(null) });

    const vasya = await screen.findByTestId('account-vasya');
    await userEvent.click(within(vasya).getByTestId('account-edit'));
    await userEvent.clear(within(vasya).getByTestId('account-edit-name'));
    await userEvent.type(within(vasya).getByTestId('account-edit-name'), 'Василий');
    await userEvent.selectOptions(within(vasya).getByTestId('account-edit-role'), 'spectator');
    await userEvent.click(within(vasya).getByTestId('account-save'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/accounts/u1')[0]?.body).toMatchObject({
        name: 'Василий',
        role: 'spectator',
      });
    });
  });

  it('resets a password after a confirmation and shows the new one', async () => {
    const server = open({
      'POST /api/admin/accounts/*/reset-password': actionOk('novyi-parol-7'),
    });

    await userEvent.click(
      within(await screen.findByTestId('account-vasya')).getByTestId('account-reset'),
    );
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(t.resetConsequences[0])).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.resetConfirm }));

    expect(await screen.findByTestId('temporary-password')).toHaveTextContent('novyi-parol-7');
    expect(server.sent('POST', '/u1/reset-password')).toHaveLength(1);
  });

  it('deletes after a confirmation and restores; my own account has no delete', async () => {
    const server = open({
      'POST /api/admin/accounts/*/delete': actionOk(null),
      'POST /api/admin/accounts/*/restore': actionOk(null),
    });

    expect(
      within(await screen.findByTestId('account-admin')).queryByTestId('account-delete'),
    ).toBeNull();
    await userEvent.click(
      within(screen.getByTestId('account-vasya')).getByTestId('account-delete'),
    );
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: t.deleteConfirm }),
    );
    await waitFor(() => {
      expect(server.sent('POST', '/u1/delete')).toHaveLength(1);
    });

    await userEvent.click(within(screen.getByTestId('account-old')).getByTestId('account-restore'));
    await waitFor(() => {
      expect(server.sent('POST', '/u2/restore')).toHaveLength(1);
    });
  });
});
