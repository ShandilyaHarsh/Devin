import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import LoginPicker from './LoginPicker';

afterEach(() => vi.unstubAllGlobals());

it('lists dev users and logs in with the chosen userId', async () => {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) === '/api/auth/dev-users') {
      return Response.json([{ id: 'u-bob', displayName: 'Bob Builder', roles: ['Builder'] }]);
    }
    return Response.json({ authenticated: true });
  });
  vi.stubGlobal('fetch', fetchMock);
  render(<LoginPicker />);
  expect(screen.getByText(/Development identity provider/)).toBeInTheDocument();
  await userEvent.click(await screen.findByRole('button', { name: /Bob Builder/ }));
  const loginCall = fetchMock.mock.calls.find(([url]) => url === '/api/auth/login') as unknown as [string, RequestInit];
  expect(loginCall[1].method).toBe('POST');
  expect(JSON.parse(String(loginCall[1].body))).toEqual({ userId: 'u-bob' });
});
