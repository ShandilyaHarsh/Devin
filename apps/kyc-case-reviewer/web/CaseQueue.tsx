import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
} from '@fluentui/react-components';
import { AccessDenied, ApiError, MaskedValue, Page, api } from '@platform/sdk';
import { API, APP_ROOT, describeError, statusColor, type CaseSummary } from './model';

export function CaseQueue() {
  const [cases, setCases] = useState<CaseSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [denied, setDenied] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const [reloads, setReloads] = useState(0);

  useEffect(() => {
    let cancelled = false;
    api
      .get<CaseSummary[]>(`${API}/cases`)
      .then((rows) => {
        if (cancelled) return;
        setCases(rows);
        setError(null);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 403) {
          setDenied(err.problem.reason ?? err.problem.title ?? 'forbidden');
        } else {
          setError(describeError(err));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [reloads]);

  const assign = async (id: string) => {
    setBusy(id);
    try {
      await api.post(`${API}/cases/${encodeURIComponent(id)}/assign`, {});
      setReloads((n) => n + 1);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setBusy(null);
    }
  };

  if (denied) return <AccessDenied reason={denied} />;

  return (
    <Page title="Case queue">
      <Text block>Customer PII is masked. Open a case to reveal it (every reveal is audited).</Text>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {!cases ? (
        <Spinner label="Loading cases" />
      ) : (
        <Table aria-label="KYC cases">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Case</TableHeaderCell>
              <TableHeaderCell>Customer</TableHeaderCell>
              <TableHeaderCell>Risk score</TableHeaderCell>
              <TableHeaderCell>Status</TableHeaderCell>
              <TableHeaderCell>Assigned analyst</TableHeaderCell>
              <TableHeaderCell>Actions</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {cases.map((c) => (
              <TableRow key={c.id}>
                <TableCell>
                  <Link to={`${APP_ROOT}/cases/${encodeURIComponent(c.id)}`}>{c.caseRef}</Link>
                </TableCell>
                <TableCell>
                  <MaskedValue masked={c.customerName} />
                </TableCell>
                <TableCell>
                  <Badge appearance="tint" color={c.riskScore >= 70 ? 'danger' : c.riskScore >= 40 ? 'warning' : 'success'}>
                    {c.riskScore}
                  </Badge>
                </TableCell>
                <TableCell>
                  <Badge appearance="filled" color={statusColor(c.status)}>
                    {c.status}
                  </Badge>
                </TableCell>
                <TableCell>{c.assignedAnalystId ?? <Text italic>unassigned</Text>}</TableCell>
                <TableCell>
                  {c.canAssign && (
                    <Button size="small" appearance="primary" disabled={busy === c.id} onClick={() => void assign(c.id)}>
                      Assign to me
                    </Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {cases && cases.length === 0 && <Text>No cases visible to you.</Text>}
    </Page>
  );
}
