import { useEffect, useState } from 'react';
import { NavLink, Route, Routes, useParams } from 'react-router';
import { Spinner, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import { AccessDenied, api, StageBadge, type Stage } from '@platform/sdk';
import { findAppModule } from '../apps';
import { ApiErrorView } from './ApiErrorView';

interface AppAccess {
  slug: string;
  stage: Stage;
  allowed: boolean;
  reason: string | null;
}

const SLUG = /^[a-z][a-z0-9-]{2,40}$/;

const useStyles = makeStyles({
  root: { display: 'flex', minHeight: 'calc(100vh - 56px)' },
  side: {
    width: '220px',
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalL,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  link: {
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected, fontWeight: tokens.fontWeightSemibold },
  content: { flexGrow: 1, minWidth: 0 },
  padded: { padding: tokens.spacingHorizontalXXL },
});

export default function AppGate() {
  const styles = useStyles();
  const { slug = '' } = useParams();
  const validSlug = SLUG.test(slug);
  const [result, setResult] = useState<{ slug: string; access?: AppAccess; error?: unknown } | null>(null);

  useEffect(() => {
    if (!validSlug) return;
    let cancelled = false;
    api
      .get<AppAccess>(`/api/platform/apps/${encodeURIComponent(slug)}/access`)
      .then((result) => {
        if (!cancelled) setResult({ slug, access: result });
      })
      .catch((err: unknown) => {
        if (!cancelled) setResult({ slug, error: err });
      });
    return () => {
      cancelled = true;
    };
  }, [slug, validSlug]);

  if (!validSlug) {
    return (
      <div className={styles.padded}>
        <AccessDenied reason="invalid-app-slug" />
      </div>
    );
  }
  const current = result?.slug === slug ? result : null;
  const access = current?.access;
  if (current?.error) {
    return (
      <div className={styles.padded}>
        <ApiErrorView error={current.error} />
      </div>
    );
  }
  if (!access) {
    return <Spinner className={styles.padded} label="Checking access" />;
  }
  if (!access.allowed) {
    return (
      <div className={styles.padded}>
        <AccessDenied reason={access.reason ?? undefined} />
      </div>
    );
  }

  const module = findAppModule(slug);
  if (!module) {
    return (
      <div className={styles.padded}>
        <Text>This app is registered but has no web module in this build.</Text>
      </div>
    );
  }

  const base = `/apps/${slug}`;
  return (
    <div className={styles.root}>
      <nav className={styles.side} aria-label={`${module.displayName} navigation`}>
        <Title3 as="h2">{module.displayName}</Title3>
        <div>
          <StageBadge stage={access.stage} />
        </div>
        {module.routes
          .filter((r) => r.navLabel)
          .map((r) => (
            <NavLink
              key={r.path}
              to={r.path ? `${base}/${r.path}` : base}
              end={r.path === ''}
              className={({ isActive }) => (isActive ? `${styles.link} ${styles.active}` : styles.link)}
            >
              {r.navLabel}
            </NavLink>
          ))}
      </nav>
      <div className={styles.content}>
        <Routes>
          {module.routes.map((r) =>
            r.path === '' ? <Route key="(index)" index element={r.element} /> : <Route key={r.path} path={r.path} element={r.element} />,
          )}
          <Route path="*" element={<div className={styles.padded}><Text>Page not found.</Text></div>} />
        </Routes>
      </div>
    </div>
  );
}
