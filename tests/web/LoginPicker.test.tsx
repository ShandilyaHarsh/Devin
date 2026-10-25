import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import LoginPicker from '../../platform/web/src/shell/LoginPicker';
import { json, pathOf, stubFetch } from './helpers';

const users = [
  { id: 'u-alice', displayName: 'Alice Ops', roles: ['Requester'] },
  { id: 'u-bob', displayName: 'Bob Builder', roles: ['Builder'] },
  { id: 'u-mallory', displayName: 'Mallory Outsider', roles: [] },
];

function session(authenticated: boolean, userId?: string) {
  const user = users.find((u) => u.id === userId);
  return {
    authenticated,
    user: authenticated && user ? { id: user.id, displayName: user.displayName, email: 'x@example.test', roles: user.roles } : null,
    csrfToken: authenticated ? 'csrf-after-login' : 'csrf-before-login',
    expiresAt: null,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('LoginPicker', () => {
  it('lists dev users and logs in with CSRF token', async () => {
    let loggedIn: string | undefined;
    const calls = stubFetch((c) => {
      const path = pathOf(c.url);
      if (path === '/api/auth/dev-users') return json(users);
      if (path === '/api/session') return json(session(loggedIn !== undefined, loggedIn));
      if (path === '/api/auth/login' && c.method === 'POST') {
        loggedIn = (JSON.parse(c.body ?? '{}') as { userId: string }).userId;
        return json(session(true, loggedIn));
      }
      return undefined;
    });

    render(
      <MemoryRouter initialEntries={['/login']}>
        <LoginPicker />
      </MemoryRouter>,
    );

    for (const user of users) {
      expect(await screen.findByRole('button', { name: new RegExp(user.displayName) })).toBeInTheDocument();
    }

    await userEvent.click(screen.getByRole('button', { name: /Bob Builder/ }));

    await waitFor(() => expect(calls.some((c) => pathOf(c.url) === '/api/auth/login')).toBe(true));
    const login = calls.filter((c) => pathOf(c.url) === '/api/auth/login');
    expect(login).toHaveLength(1);
    expect(login[0]!.method).toBe('POST');
    expect(JSON.parse(login[0]!.body ?? '{}')).toEqual({ userId: 'u-bob' });
    expect(login[0]!.headers.get('X-CSRF-Token')).toBeTruthy();
  });
});
