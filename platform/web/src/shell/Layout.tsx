import type { ReactNode } from 'react';
import { Link as RouterLink, useLocation, useNavigate } from 'react-router';
import {
  Avatar,
  Badge,
  Button,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Tab,
  TabList,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { api, useSession } from '@platform/sdk';
import { useCatalog } from './useCatalog';

const useStyles = makeStyles({
  root: { minHeight: '100vh', display: 'flex', flexDirection: 'column', backgroundColor: tokens.colorNeutralBackground2 },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow2,
  },
  brand: {
    fontWeight: tokens.fontWeightBold,
    fontSize: tokens.fontSizeBase500,
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  nav: { flexGrow: 1 },
  user: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  userText: { display: 'flex', flexDirection: 'column' },
  roles: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap' },
  main: { flexGrow: 1, backgroundColor: tokens.colorNeutralBackground1 },
});

export function Layout({ children }: { children: ReactNode }) {
  const styles = useStyles();
  const { session } = useSession();
  const { entries } = useCatalog();
  const navigate = useNavigate();
  const location = useLocation();
  const user = session?.user;
  const selected = location.pathname.startsWith('/audit') ? 'audit' : location.pathname === '/' ? 'catalog' : 'app';

  const signOut = async () => {
    try {
      // Not useSession().logout(): re-rendering as anonymous would redirect with the old page in router state.
      await api.post<void>('/api/auth/logout');
    } finally {
      // Full reload drops every piece of in-memory app state (incl. any revealed PII) from the previous user.
      // Clear router state first so the next user is not sent to the previous user's page.
      window.history.replaceState(null, '', '/login');
      window.location.reload();
    }
  };

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <RouterLink to="/" className={styles.brand}>
          Barebone
        </RouterLink>
        <TabList className={styles.nav} selectedValue={selected} appearance="subtle-circular" size="small">
          <Tab value="catalog" onClick={() => navigate('/')}>
            Catalog
          </Tab>
          <Tab value="audit" onClick={() => navigate('/audit')}>
            Audit
          </Tab>
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button appearance="subtle" size="small">
                Apps ▾
              </Button>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {entries && entries.length > 0 ? (
                  entries.map((entry) => (
                    <MenuItem key={entry.slug} onClick={() => navigate(`/apps/${encodeURIComponent(entry.slug)}`)}>
                      {entry.displayName}
                    </MenuItem>
                  ))
                ) : (
                  <MenuItem disabled>No apps registered</MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        </TabList>
        {user ? (
          <div className={styles.user} aria-label="Current user">
            <Avatar name={user.displayName} size={32} color="colorful" />
            <div className={styles.userText}>
              <Text weight="semibold">{user.displayName}</Text>
              <span className={styles.roles}>
                {user.roles.length ? (
                  user.roles.map((role) => (
                    <Badge key={role} size="small" appearance="filled" color="informative">
                      {role}
                    </Badge>
                  ))
                ) : (
                  <Badge size="small" appearance="outline">
                    no roles
                  </Badge>
                )}
              </span>
            </div>
            <Button size="small" onClick={() => void signOut()}>
              Sign out
            </Button>
          </div>
        ) : null}
      </header>
      <main className={styles.main}>{children}</main>
    </div>
  );
}
