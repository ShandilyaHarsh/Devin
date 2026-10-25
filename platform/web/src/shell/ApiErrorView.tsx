import { useEffect } from 'react';
import { Spinner, Text } from '@fluentui/react-components';
import { AccessDenied, ApiError, useOptionalSession } from '@platform/sdk';

/** 401 => re-check the session (RequireAuth then redirects to /login); 403 => AccessDenied; else a generic error. */
export function ApiErrorView({ error }: { error: unknown }) {
  const session = useOptionalSession();
  const unauthenticated = error instanceof ApiError && error.status === 401;

  useEffect(() => {
    if (unauthenticated && session) {
      void session.refresh();
    }
  }, [unauthenticated, session]);

  if (unauthenticated) {
    return <Spinner label="Session expired, returning to sign-in" />;
  }
  if (error instanceof ApiError && error.status === 403) {
    return <AccessDenied reason={error.problem.reason ?? error.problem.title} />;
  }
  const correlationId = error instanceof ApiError ? error.problem.correlationId : undefined;
  return (
    <Text role="alert">
      Something went wrong{error instanceof ApiError && error.problem.title ? `: ${error.problem.title}` : ''}.
      {correlationId ? ` Correlation id: ${correlationId}` : ''}
    </Text>
  );
}
