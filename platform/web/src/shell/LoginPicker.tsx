import { useEffect, useState } from 'react';
import {
  Avatar,
  Badge,
  Button,
  Caption1,
  Card,
  MessageBar,
  MessageBarBody,
  Spinner,
  Subtitle1,
  Text,
  Title1,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ApiError, api, useOptionalSession } from '@platform/sdk';

interface DevUser {
  id: string;
  displayName: string;
  roles: string[];
}

const useStyles = makeStyles({
  root: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
    padding: tokens.spacingHorizontalXXL,
  },
  card: { width: '560px', maxWidth: '100%', padding: tokens.spacingHorizontalXXL, gap: tokens.spacingVerticalL },
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  user: { justifyContent: 'flex-start', height: 'auto', padding: tokens.spacingVerticalS, textAlign: 'left' },
  userText: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginLeft: tokens.spacingHorizontalM },
  roles: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap' },
});

export default function LoginPicker() {
  const styles = useStyles();
  const session = useOptionalSession();
  const [users, setUsers] = useState<DevUser[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .get<DevUser[]>('/api/auth/dev-users')
      .then((list) => {
        if (!cancelled) setUsers(list);
      })
      .catch(() => {
        if (!cancelled) setError('Could not load the development directory.');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const signIn = async (userId: string) => {
    setBusyId(userId);
    setError(null);
    try {
      if (session) {
        await session.login(userId);
      } else {
        await api.post('/api/auth/login', { userId });
      }
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? 'Too many sign-in attempts. Try again shortly.' : 'Sign-in failed.');
    } finally {
      setBusyId(null);
    }
  };

  return (
    <main className={styles.root}>
      <Card className={styles.card}>
        <Title1 as="h1">Barebone</Title1>
        <Subtitle1>Sign in as a development user</Subtitle1>
        <MessageBar intent="info">
          <MessageBarBody>Development identity provider — Entra ID in production</MessageBarBody>
        </MessageBar>
        {error ? (
          <MessageBar intent="error" role="alert">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        ) : null}
        {users === null && !error ? <Spinner label="Loading users" /> : null}
        <div className={styles.list}>
          {users?.map((user) => (
            <Button
              key={user.id}
              className={styles.user}
              appearance="subtle"
              disabled={busyId !== null}
              aria-label={`Sign in as ${user.displayName}${user.roles.length ? ` (${user.roles.join(', ')})` : ' (no roles)'}`}
              onClick={() => void signIn(user.id)}
            >
              <Avatar name={user.displayName} color="colorful" />
              <span className={styles.userText}>
                <Text weight="semibold">{user.displayName}</Text>
                <span className={styles.roles}>
                  {user.roles.length ? (
                    user.roles.map((role) => (
                      <Badge key={role} appearance="tint" size="small">
                        {role}
                      </Badge>
                    ))
                  ) : (
                    <Caption1>no roles</Caption1>
                  )}
                </span>
              </span>
              {busyId === user.id ? <Spinner size="tiny" /> : null}
            </Button>
          ))}
        </div>
      </Card>
    </main>
  );
}
