import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Field,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { ApiError, Page, api, useSession } from '@platform/sdk';
import { BASE, describeError, usd } from '../model';
import type { Intake } from '../model';
import { useLoad } from './useLoad';
import { ClassificationBadge, ErrorBar, Loading, SuccessBar, preStyle } from './shared';
import { RoiCard } from './RequestPage';

const APP_ROOT = '/apps/barebone-builder';
const statusColor = { submitted: 'informative', approved: 'success', rejected: 'danger' } as const;

export function QueuePage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const list = useLoad<Intake[]>(`${BASE}/intakes`);
  return (
    <Page title="Intake queue">
      <Text block style={{ marginBottom: 12 }}>
        Requesters see their own intakes; Builders see all. Approval is maker-checker: you cannot approve your own request.
      </Text>
      <Loading loading={list.loading && !list.data} error={list.error}>
        <Table aria-label="Intakes" size="small">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Title</TableHeaderCell>
              <TableHeaderCell>Slug</TableHeaderCell>
              <TableHeaderCell>Requester</TableHeaderCell>
              <TableHeaderCell>Classification</TableHeaderCell>
              <TableHeaderCell>Annual value</TableHeaderCell>
              <TableHeaderCell>Status</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {(list.data ?? []).map((i) => (
              <TableRow
                key={i.id}
                aria-selected={i.id === id}
                style={{ cursor: 'pointer', background: i.id === id ? '#ebf3fc' : undefined }}
                onClick={() => void navigate(`${APP_ROOT}/intakes/${encodeURIComponent(i.id)}`)}
              >
                <TableCell>
                  <Button appearance="transparent" size="small">
                    {i.title}
                  </Button>
                </TableCell>
                <TableCell>
                  <Text font="monospace">{i.appSlug}</Text>
                </TableCell>
                <TableCell>{i.requesterId}</TableCell>
                <TableCell>
                  <ClassificationBadge value={i.dataClassification} />
                </TableCell>
                <TableCell>{usd(i.roi.annualValueUsd)}</TableCell>
                <TableCell>
                  <Badge color={statusColor[i.status]}>{i.status}</Badge>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        {list.data?.length === 0 && <Text>No intakes visible to you.</Text>}
      </Loading>
      {id && <IntakeDetail key={id} id={id} onChanged={list.reload} />}
    </Page>
  );
}

function IntakeDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const { session } = useSession();
  const isBuilder = session?.user?.roles.includes('Builder') ?? false;
  const detail = useLoad<Intake>(`${BASE}/intakes/${encodeURIComponent(id)}`);
  const [result, setResult] = useState<Intake | null>(null);
  const [denied, setDenied] = useState<{ title: string; reason: string | null } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [rejectReason, setRejectReason] = useState('');
  const [busy, setBusy] = useState(false);
  const intake = result ?? detail.data;

  const decide = async (kind: 'approve' | 'reject') => {
    setBusy(true);
    setDenied(null);
    setError(null);
    try {
      const body = kind === 'approve' ? {} : { reason: rejectReason };
      setResult(await api.post<Intake>(`${BASE}/intakes/${encodeURIComponent(id)}/${kind}`, body));
      onChanged();
    } catch (e: unknown) {
      if (e instanceof ApiError && e.status === 403) {
        setDenied({ title: e.problem?.title ?? 'Forbidden', reason: e.problem?.reason ?? null });
      } else {
        setError(describeError(e));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card style={{ marginTop: 16 }} aria-label="Intake detail">
      <Loading loading={detail.loading && !intake} error={detail.error}>
        {intake && (
          <>
            <CardHeader
              header={<Text weight="semibold" size={500}>{intake.title}</Text>}
              description={
                <Text font="monospace">
                  {intake.id} · {intake.appSlug}
                </Text>
              }
              action={<Badge color={statusColor[intake.status]}>{intake.status}</Badge>}
            />
            <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: 16 }}>
              <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '4px 12px', margin: 0 }}>
                <dt>Problem</dt>
                <dd style={{ margin: 0 }}>{intake.problem}</dd>
                <dt>Users</dt>
                <dd style={{ margin: 0 }}>{intake.users}</dd>
                <dt>Data touched</dt>
                <dd style={{ margin: 0 }}>{intake.dataTouched}</dd>
                <dt>Classification</dt>
                <dd style={{ margin: 0 }}>
                  <ClassificationBadge value={intake.dataClassification} /> · {intake.access}
                </dd>
                <dt>Requester</dt>
                <dd style={{ margin: 0 }}>{intake.requesterId}</dd>
                <dt>Decided by</dt>
                <dd style={{ margin: 0 }}>
                  {intake.decidedBy ? `${intake.decidedBy} at ${intake.decidedAt ?? ''}` : '—'}
                </dd>
                {intake.rejectReason && (
                  <>
                    <dt>Reject reason</dt>
                    <dd style={{ margin: 0 }}>{intake.rejectReason}</dd>
                  </>
                )}
              </dl>
              <RoiCard roi={intake.roi} error={null} />
            </div>

            {denied && (
              <MessageBar intent="error" aria-label="Access denied">
                <MessageBarBody>
                  <MessageBarTitle>Access denied (403){denied.reason ? `: ${denied.reason}` : ''}</MessageBarTitle>
                  {denied.reason === 'maker-checker'
                    ? 'Maker-checker: you submitted this intake, so a different Builder must decide it. The denial was written to the audit trail.'
                    : denied.title}
                </MessageBarBody>
              </MessageBar>
            )}
            {error && <ErrorBar title="Action failed" text={error} />}

            {isBuilder && intake.status === 'submitted' && (
              <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
                <Button appearance="primary" disabled={busy} onClick={() => void decide('approve')}>
                  Approve &amp; register
                </Button>
                <Field label="Reject reason" style={{ minWidth: 280 }}>
                  <Textarea value={rejectReason} onChange={(_, d) => setRejectReason(d.value)} maxLength={1000} />
                </Field>
                <Button disabled={busy || rejectReason.trim().length < 3} onClick={() => void decide('reject')}>
                  Reject
                </Button>
              </div>
            )}

            {intake.status === 'approved' && (
              <>
                <SuccessBar
                  title="Approved and registered in the catalog (stage builder-only)"
                  text={
                    result
                      ? result.seededSyntheticData
                        ? 'Seeded synthetic data for the app.'
                        : 'No synthetic data seeded (app code not deployed yet, or already seeded).'
                      : 'Promote it from Manage app.'
                  }
                />
                {intake.generatedManifestYaml && (
                  <>
                    <Text weight="semibold">Generated platform.yaml</Text>
                    <pre style={preStyle} aria-label="Generated platform.yaml">
                      {intake.generatedManifestYaml}
                    </pre>
                  </>
                )}
              </>
            )}
          </>
        )}
      </Loading>
    </Card>
  );
}
