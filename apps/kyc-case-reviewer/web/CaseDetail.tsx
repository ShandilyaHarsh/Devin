import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Field,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  Textarea,
  Title3,
} from '@fluentui/react-components';
import { AccessDenied, ApiError, MaskedValue, Page, api, newIdempotencyKey, useSession } from '@platform/sdk';
import {
  API,
  APP_ROOT,
  describeError,
  explainReason,
  statusColor,
  type CaseDetail,
  type RevealedPii,
} from './model';

const MAX_TEXT = 2000;

type Feedback = { intent: 'success' | 'error' | 'info'; title: string; text: string } | null;

interface SentDecision {
  key: string;
  body: { decision: string; rationale: string };
  decisionsBefore: number;
}

function FeedbackBar({ feedback }: { feedback: Feedback }) {
  if (!feedback) return null;
  return (
    <MessageBar intent={feedback.intent}>
      <MessageBarBody>
        <MessageBarTitle>{feedback.title}</MessageBarTitle>
        {feedback.text}
      </MessageBarBody>
    </MessageBar>
  );
}

export function CaseDetailPage() {
  const { id = '' } = useParams();
  const { session } = useSession();
  const roles = session?.user?.roles ?? [];
  const isAnalyst = roles.includes('KYC Analyst');
  const isSenior = roles.includes('KYC Senior Reviewer');
  const casePath = `${API}/cases/${encodeURIComponent(id)}`;

  const [detail, setDetail] = useState<CaseDetail | null>(null);
  const [denied, setDenied] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [revealed, setRevealed] = useState<RevealedPii | null>(null);
  const [feedback, setFeedback] = useState<Feedback>(null);
  const [busy, setBusy] = useState(false);

  const [noteText, setNoteText] = useState('');
  const [recommendation, setRecommendation] = useState('approve');
  const [recRationale, setRecRationale] = useState('');
  const [decision, setDecision] = useState('approve');
  const [decRationale, setDecRationale] = useState('');
  const [sentDecision, setSentDecision] = useState<SentDecision | null>(null);
  const [replayResult, setReplayResult] = useState<Feedback>(null);

  const load = useCallback(async () => {
    try {
      setDetail(await api.get<CaseDetail>(casePath));
      setLoadError(null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setDenied(explainReason(err.problem?.reason) ?? err.problem?.title ?? 'forbidden');
      } else {
        setLoadError(describeError(err));
      }
    }
  }, [casePath]);

  useEffect(() => {
    void load();
  }, [load]);

  const run = async (title: string, action: () => Promise<void>) => {
    setBusy(true);
    setFeedback(null);
    try {
      await action();
      setFeedback({ intent: 'success', title, text: 'Saved atomically with its audit record.' });
    } catch (err) {
      setFeedback({ intent: 'error', title: `${title} failed`, text: describeError(err) });
    } finally {
      setBusy(false);
    }
  };

  const reveal = () =>
    run('PII revealed (audited)', async () => {
      setRevealed(await api.post<RevealedPii>(`${casePath}/reveal`, {}));
    });

  const assign = () =>
    run('Assigned to you', async () => {
      setDetail(await api.post<CaseDetail>(`${casePath}/assign`, {}));
      await load();
    });

  const addNote = () =>
    run('Note added', async () => {
      await api.post<CaseDetail>(`${casePath}/notes`, { text: noteText });
      setNoteText('');
      await load();
    });

  const recommend = () =>
    run('Recommendation submitted for senior review', async () => {
      await api.post<CaseDetail>(`${casePath}/recommendation`, { recommendation, rationale: recRationale });
      setRecRationale('');
      await load();
    });

  const decide = () =>
    run('Decision recorded', async () => {
      const sent: SentDecision = {
        key: newIdempotencyKey(),
        body: { decision, rationale: decRationale },
        decisionsBefore: detail?.decisions.length ?? 0,
      };
      await api.postWithMeta<CaseDetail>(`${casePath}/decision`, sent.body, { idempotencyKey: sent.key });
      setSentDecision(sent);
      setReplayResult(null);
      setDecRationale('');
      await load();
    });

  const resend = async () => {
    if (!sentDecision) return;
    setBusy(true);
    try {
      const { data, replayed } = await api.postWithMeta<CaseDetail>(`${casePath}/decision`, sentDecision.body, {
        idempotencyKey: sentDecision.key,
      });
      const fresh = await api.get<CaseDetail>(casePath);
      setDetail(fresh);
      setReplayResult(
        replayed
          ? {
              intent: 'success',
              title: 'Replayed: no second change',
              text: `Server returned the stored response (Idempotency-Replayed: true). Decisions on case: ${fresh.decisions.length}; status still ${data.status}.`,
            }
          : { intent: 'error', title: 'Not replayed', text: 'The server processed the request again.' },
      );
    } catch (err) {
      setReplayResult({
        intent: 'info',
        title: 'Second request refused: no second change',
        text: describeError(err),
      });
    } finally {
      setBusy(false);
    }
  };

  if (denied) return <AccessDenied reason={denied} />;
  if (loadError) {
    return (
      <Page title="Case">
        <MessageBar intent="error">
          <MessageBarBody>{loadError}</MessageBarBody>
        </MessageBar>
      </Page>
    );
  }
  if (!detail) return <Spinner label="Loading case" />;

  const p = detail.permissions;
  const decideHint = p?.decide;
  const open = detail.status === 'new' || detail.status === 'in-review';

  return (
    <Page title={`Case ${detail.caseRef}`}>
      <Link to={APP_ROOT}>← Back to case queue</Link>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <Badge appearance="filled" color={statusColor(detail.status)}>
          {detail.status}
        </Badge>
        <Text>Risk score: {detail.riskScore}</Text>
        <Text>Assigned: {detail.assignedAnalystId ?? 'unassigned'}</Text>
        {detail.preparedBy && <Text>Prepared by: {detail.preparedBy}</Text>}
      </div>

      <FeedbackBar feedback={feedback} />

      <Card>
        <CardHeader header={<Title3>Customer (PII)</Title3>} />
        <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', columnGap: 16, margin: 0 }}>
          <dt>Name</dt>
          <dd>
            <MaskedValue masked={detail.customerName} revealed={revealed?.customerName} />
          </dd>
          <dt>Date of birth</dt>
          <dd>
            <MaskedValue masked={detail.dateOfBirth} revealed={revealed?.dateOfBirth ?? undefined} />
          </dd>
          <dt>SSN (last 4 only)</dt>
          <dd>
            <MaskedValue masked={detail.ssnLast4} revealed={revealed ? `***-**-${revealed.ssnLast4}` : undefined} />
          </dd>
        </dl>
        <div>
          <Button appearance="primary" disabled={busy || !!revealed} onClick={() => void reveal()}>
            Reveal PII (audited)
          </Button>
          {p && !p.reveal.allowed && <Text> Not permitted: {explainReason(p.reveal.reason)}</Text>}
        </div>
      </Card>

      {p?.assign.allowed && (
        <Button disabled={busy} onClick={() => void assign()}>
          Assign to me
        </Button>
      )}

      <Card>
        <CardHeader header={<Title3>Documents (metadata only)</Title3>} />
        <ul>
          {detail.documents.map((d) => (
            <li key={d.name}>
              {d.name} — {d.type} — uploaded {new Date(d.uploadedAt).toLocaleDateString()}
            </li>
          ))}
        </ul>
      </Card>

      {detail.recommendation && (
        <Card>
          <CardHeader header={<Title3>Analyst recommendation</Title3>} />
          <Text>
            <b>{detail.recommendation}</b> by {detail.preparedBy}: {detail.recommendationRationale}
          </Text>
        </Card>
      )}

      {detail.decisions.length > 0 && (
        <Card>
          <CardHeader header={<Title3>Final decision</Title3>} />
          {detail.decisions.map((d) => (
            <Text key={d.id} block>
              <b>{d.decision}</b> by {d.deciderId} ({new Date(d.createdAt).toLocaleString()}): {d.rationale}
            </Text>
          ))}
        </Card>
      )}

      <Card>
        <CardHeader header={<Title3>Notes ({detail.notes.length})</Title3>} />
        <ul>
          {detail.notes.map((n) => (
            <li key={n.id}>
              <b>{n.authorId}</b> ({new Date(n.createdAt).toLocaleString()}): {n.text}
            </li>
          ))}
        </ul>
        <Field label="Add note" hint={`${noteText.length}/${MAX_TEXT}`}>
          <Textarea value={noteText} maxLength={MAX_TEXT} onChange={(_, v) => setNoteText(v.value)} />
        </Field>
        <div>
          <Button disabled={busy || noteText.trim().length === 0} onClick={() => void addNote()}>
            Add note
          </Button>
          {p && !p.addNote.allowed && <Text> Not permitted: {explainReason(p.addNote.reason)}</Text>}
        </div>
      </Card>

      {isAnalyst && open && (
        <Card>
          <CardHeader header={<Title3>Recommendation (maker)</Title3>} />
          <Field label="Recommendation">
            <Select value={recommendation} onChange={(_, v) => setRecommendation(v.value)}>
              <option value="approve">approve</option>
              <option value="reject">reject</option>
              <option value="escalate">escalate</option>
            </Select>
          </Field>
          <Field label="Rationale" required>
            <Textarea value={recRationale} maxLength={MAX_TEXT} onChange={(_, v) => setRecRationale(v.value)} />
          </Field>
          <div>
            <Button appearance="primary" disabled={busy} onClick={() => void recommend()}>
              Submit for senior review
            </Button>
            {p && !p.recommend.allowed && <Text> Not permitted: {explainReason(p.recommend.reason)}</Text>}
          </div>
        </Card>
      )}

      {isSenior && detail.status === 'pending-senior-review' && (
        <Card>
          <CardHeader header={<Title3>Final decision (checker)</Title3>} />
          {decideHint && !decideHint.allowed && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>Policy will deny this</MessageBarTitle>
                {explainReason(decideHint.reason)}
              </MessageBarBody>
            </MessageBar>
          )}
          <Field label="Decision">
            <Select value={decision} onChange={(_, v) => setDecision(v.value)}>
              <option value="approve">approve</option>
              <option value="reject">reject</option>
            </Select>
          </Field>
          <Field label="Rationale" required>
            <Textarea value={decRationale} maxLength={MAX_TEXT} onChange={(_, v) => setDecRationale(v.value)} />
          </Field>
          <div>
            <Button appearance="primary" disabled={busy} onClick={() => void decide()}>
              Record decision
            </Button>
          </div>
        </Card>
      )}

      {sentDecision && (
        <Card>
          <CardHeader header={<Title3>Idempotency check</Title3>} />
          <Text block>
            Idempotency-Key: <code>{sentDecision.key}</code>
          </Text>
          <div>
            <Button disabled={busy} onClick={() => void resend()}>
              Resend same request (same Idempotency-Key)
            </Button>
          </div>
          <FeedbackBar feedback={replayResult} />
        </Card>
      )}
    </Page>
  );
}
