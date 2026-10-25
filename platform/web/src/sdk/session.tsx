import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, setCsrfToken } from './api';
import type { SessionInfo } from './types';

export interface SessionContextValue {
  session: SessionInfo | null;
  loading: boolean;
  refresh: () => Promise<SessionInfo | null>;
  login: (userId: string) => Promise<SessionInfo | null>;
  logout: () => Promise<void>;
}

const SessionContext = createContext<SessionContextValue | null>(null);

function fetchSession(): Promise<SessionInfo | null> {
  return api.get<SessionInfo>('/api/session').catch(() => null);
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionInfo | null>(null);
  const [loading, setLoading] = useState(true);

  const apply = useCallback((info: SessionInfo | null): SessionInfo | null => {
    setCsrfToken(info?.csrfToken ?? '');
    setSession(info);
    setLoading(false);
    return info;
  }, []);

  const refresh = useCallback(
    (): Promise<SessionInfo | null> =>
      fetchSession().then(apply),
    [apply],
  );

  const login = useCallback(
    async (userId: string): Promise<SessionInfo | null> => {
      await api.post<SessionInfo>('/api/auth/login', { userId });
      // The session id rotates on login; re-fetch to get the CSRF token bound to the new session.
      return refresh();
    },
    [refresh],
  );

  const logout = useCallback(async (): Promise<void> => {
    try {
      await api.post<void>('/api/auth/logout');
    } finally {
      await refresh();
    }
  }, [refresh]);

  useEffect(() => {
    let cancelled = false;
    void fetchSession().then((info) => {
      if (!cancelled) apply(info);
    });
    return () => {
      cancelled = true;
    };
  }, [apply]);

  const value = useMemo(() => ({ session, loading, refresh, login, logout }), [session, loading, refresh, login, logout]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const ctx = useContext(SessionContext);
  if (!ctx) {
    throw new Error('useSession must be used inside <SessionProvider>');
  }
  return ctx;
}

/** Like useSession, but returns null outside a SessionProvider (lets shell components render standalone). */
export function useOptionalSession(): SessionContextValue | null {
  return useContext(SessionContext);
}
