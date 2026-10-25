import { useEffect, useState, type ReactNode } from 'react';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title2,
  makeStyles,
  tokens,
  type BadgeProps,
} from '@fluentui/react-components';
import { ApiError, api } from './api';
import type { AuditEvent, AuditVerifyResult, Stage } from './types';

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL, padding: tokens.spacingHorizontalXXL },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  row: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexWrap: 'wrap' },
});

export function Page({ title, children }: { title: string; children?: ReactNode }) {
  const styles = useStyles();
  return (
    <section className={styles.page} aria-label={title}>
      <Title2 as="h1">{title}</Title2>
      {children}
    </section>
  );
}

const stageColor: Record<Stage, BadgeProps['color']> = {
  'builder-only': 'warning',
  demo: 'informative',
  live: 'success',
};

export function StageBadge({ stage }: { stage: Stage }) {
  return (
    <Badge appearance="tint" color={stageColor[stage] ?? 'subtle'} title={`Stage: ${stage}`}>
      {stage}
    </Badge>
  );
}

export function AccessDenied({ reason }: { reason?: string | null }) {
  return (
    <MessageBar intent="error" role="alert">
      <MessageBarBody>
        <MessageBarTitle>Access denied</MessageBarTitle>
        {reason ? <span>{reason}</span> : null}
      </MessageBarBody>
    </MessageBar>
  );
}

export function MaskedValue({ masked, revealed }: { masked: string; revealed?: string | null }) {
  const styles = useStyles();
  const shown = revealed ?? masked;
  return (
    <span className={styles.mono} data-revealed={revealed != null ? 'true' : 'false'}>
      {shown}
    </span>
  );
}

function errorText(err: unknown): string {
  if (err instanceof ApiError) {
    return err.problem.reason ?? err.problem.title ?? `Request failed (${err.status})`;
  }
  return 'Request failed';
}

export function VerifyAuditButton() {
  const [result, setResult] = useState<AuditVerifyResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const styles = useStyles();

  const verify = async () => {
    setBusy(true);
    setError(null);
    try {
      setResult(await api.get<AuditVerifyResult>('/api/platform/audit/verify'));
    } catch (err) {
      setResult(null);
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.row}>
      <Button appearance="primary" onClick={() => void verify()} disabled={busy}>
        Verify audit chain
      </Button>
      {busy ? <Spinner size="tiny" /> : null}
      {result ? (
        <Badge appearance="filled" color={result.valid ? 'success' : 'danger'} role="status">
          {result.valid ? `Chain valid (${result.checked} rows)` : `Chain broken at row #${result.firstBrokenId}`}
        </Badge>
      ) : null}
      {result && !result.valid && result.reason ? <Text>{result.reason}</Text> : null}
      {error ? <Text role="alert">{error}</Text> : null}
    </div>
  );
}

const outcomeColor: Record<AuditEvent['outcome'], BadgeProps['color']> = {
  success: 'success',
  denied: 'danger',
  read: 'informative',
};

export function AuditViewer({ app }: { app?: string }) {
  const styles = useStyles();
  const [state, setState] = useState<{ rows: AuditEvent[] | null; error: Error | null }>({ rows: null, error: null });
  const [reloads, setReloads] = useState(0);

  useEffect(() => {
    let cancelled = false;
    const query = new URLSearchParams({ limit: '50' });
    if (app) {
      query.set('app', app);
    }
    api
      .get<AuditEvent[]>(`/api/platform/audit?${query.toString()}`)
      .then((rows) => {
        if (!cancelled) setState({ rows, error: null });
      })
      .catch((err: unknown) => {
        if (!cancelled) setState({ rows: null, error: err instanceof Error ? err : new Error('Request failed') });
      });
    return () => {
      cancelled = true;
    };
  }, [app, reloads]);

  const { rows, error } = state;
  if (error instanceof ApiError && (error.status === 403 || error.status === 401)) {
    return <AccessDenied reason={error.problem.reason ?? error.problem.title} />;
  }
  if (error) {
    return <Text role="alert">{errorText(error)}</Text>;
  }
  if (!rows) {
    return <Spinner label="Loading audit trail" />;
  }

  return (
    <div>
      <div className={styles.row}>
        <Button size="small" onClick={() => setReloads((n) => n + 1)}>
          Refresh
        </Button>
        <Text size={200}>{rows.length} most recent events</Text>
      </div>
      <Table aria-label="Audit trail" size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>#</TableHeaderCell>
            <TableHeaderCell>Time</TableHeaderCell>
            <TableHeaderCell>Actor</TableHeaderCell>
            <TableHeaderCell>Action</TableHeaderCell>
            <TableHeaderCell>Resource</TableHeaderCell>
            <TableHeaderCell>Outcome</TableHeaderCell>
            <TableHeaderCell>Hash</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.id}>
              <TableCell>{row.id}</TableCell>
              <TableCell>{new Date(row.timestamp).toLocaleString()}</TableCell>
              <TableCell>{row.actorId}</TableCell>
              <TableCell>
                <span className={styles.mono}>
                  {row.appSlug}:{row.action}
                </span>
              </TableCell>
              <TableCell>
                {row.resourceType}
                {row.resourceId ? ` ${row.resourceId}` : ''}
              </TableCell>
              <TableCell>
                <Badge appearance="tint" color={outcomeColor[row.outcome] ?? 'subtle'}>
                  {row.outcome}
                </Badge>
                {row.reason ? <Text size={200}> {row.reason}</Text> : null}
              </TableCell>
              <TableCell>
                <span className={styles.mono} title={row.hash}>
                  {row.hash.slice(0, 12)}
                </span>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
