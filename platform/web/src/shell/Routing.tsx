import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router';
import { Spinner } from '@fluentui/react-components';
import { useSession } from '@platform/sdk';
import AppGate from './AppGate';
import AuditPage from './AuditPage';
import CatalogPage from './CatalogPage';
import { Layout } from './Layout';
import LoginPicker from './LoginPicker';

function safeReturnPath(value: unknown): string {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/login')
    ? value
    : '/';
}

function RequireAuth({ children }: { children: ReactNode }) {
  const { session, loading } = useSession();
  const location = useLocation();
  if (loading && !session) return <Spinner label="Loading session" />;
  if (!session?.authenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }
  return <Layout>{children}</Layout>;
}

function LoginRoute() {
  const { session, loading } = useSession();
  const location = useLocation();
  if (loading && !session) return <Spinner label="Loading session" />;
  if (session?.authenticated) {
    const from = (location.state as { from?: unknown } | null)?.from;
    return <Navigate to={safeReturnPath(from)} replace />;
  }
  return <LoginPicker />;
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginRoute />} />
      <Route path="/" element={<RequireAuth><CatalogPage /></RequireAuth>} />
      <Route path="/audit" element={<RequireAuth><AuditPage /></RequireAuth>} />
      <Route path="/apps/:slug/*" element={<RequireAuth><AppGate /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
