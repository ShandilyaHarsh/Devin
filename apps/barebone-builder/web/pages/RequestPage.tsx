import { useEffect, useState } from 'react';
import type { ChangeEvent, FormEvent } from 'react';
import { Link } from 'react-router';
import { Button, Card, CardHeader, Field, Input, Select, Text, Textarea } from '@fluentui/react-components';
import { Page, api } from '@platform/sdk';
import { ACCESS_MODES, BASE, CLASSIFICATIONS, describeError, usd, validationErrors } from '../model';
import type { Intake, IntakeCreate, Roi } from '../model';
import { ErrorBar, SuccessBar } from './shared';

const initial: IntakeCreate = {
  title: '',
  appSlug: '',
  problem: '',
  users: '',
  dataTouched: '',
  dataClassification: 'internal',
  access: 'read-only',
  hoursSavedPerUserPerWeek: 2,
  numberOfUsers: 10,
};

export function RequestPage() {
  const [form, setForm] = useState<IntakeCreate>(initial);
  const [roi, setRoi] = useState<Roi | null>(null);
  const [roiError, setRoiError] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [created, setCreated] = useState<Intake | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const qs = new URLSearchParams({
      hoursSavedPerUserPerWeek: String(form.hoursSavedPerUserPerWeek),
      numberOfUsers: String(form.numberOfUsers),
      access: form.access,
      dataClassification: form.dataClassification,
    });
    const handle = setTimeout(() => {
      api
        .get<Roi>(`${BASE}/roi?${qs.toString()}`)
        .then((r) => {
          setRoi(r);
          setRoiError(null);
        })
        .catch((e: unknown) => {
          setRoi(null);
          setRoiError(describeError(e));
        });
    }, 250);
    return () => clearTimeout(handle);
  }, [form.hoursSavedPerUserPerWeek, form.numberOfUsers, form.access, form.dataClassification]);

  const text = (key: keyof IntakeCreate) => (e: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
    setForm((f) => ({ ...f, [key]: e.target.value }));
  const num = (key: 'hoursSavedPerUserPerWeek' | 'numberOfUsers') => (e: ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [key]: Number(e.target.value) }));
  const err = (key: string) => errors[key]?.join(' ');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setSubmitError(null);
    setErrors({});
    try {
      setCreated(await api.post<Intake>(`${BASE}/intakes`, form));
      setForm(initial);
    } catch (ex: unknown) {
      setErrors(validationErrors(ex));
      setSubmitError(describeError(ex));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Page title="Request a tool">
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(320px, 2fr) minmax(280px, 1fr)', gap: 24 }}>
        <form onSubmit={(e) => void submit(e)} style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {created && (
            <SuccessBar
              title="Intake submitted"
              text={`“${created.title}” (${created.id}) is waiting for a Builder. A different Builder must approve it (maker-checker).`}
            />
          )}
          {submitError && <ErrorBar title="Not submitted" text={submitError} />}
          <Field label="Title" required validationMessage={err('title')}>
            <Input value={form.title} onChange={text('title')} maxLength={120} />
          </Field>
          <Field label="App slug" required hint="lowercase, e.g. vendor-spend-dashboard" validationMessage={err('appSlug')}>
            <Input value={form.appSlug} onChange={text('appSlug')} maxLength={41} />
          </Field>
          <Field label="Problem" required validationMessage={err('problem')}>
            <Textarea value={form.problem} onChange={text('problem')} maxLength={2000} />
          </Field>
          <Field label="Who will use it" required validationMessage={err('users')}>
            <Input value={form.users} onChange={text('users')} maxLength={500} />
          </Field>
          <Field label="Data touched" required hint="Never card numbers." validationMessage={err('dataTouched')}>
            <Input value={form.dataTouched} onChange={text('dataTouched')} maxLength={500} />
          </Field>
          <div style={{ display: 'flex', gap: 12 }}>
            <Field label="Data classification" validationMessage={err('dataClassification')}>
              <Select
                value={form.dataClassification}
                onChange={(_, d) => setForm((f) => ({ ...f, dataClassification: d.value as IntakeCreate['dataClassification'] }))}
              >
                {CLASSIFICATIONS.map((c) => (
                  <option key={c}>{c}</option>
                ))}
              </Select>
            </Field>
            <Field label="Access" validationMessage={err('access')}>
              <Select
                value={form.access}
                onChange={(_, d) => setForm((f) => ({ ...f, access: d.value as IntakeCreate['access'] }))}
              >
                {ACCESS_MODES.map((a) => (
                  <option key={a}>{a}</option>
                ))}
              </Select>
            </Field>
          </div>
          <div style={{ display: 'flex', gap: 12 }}>
            <Field label="Hours saved per user per week" validationMessage={err('hoursSavedPerUserPerWeek')}>
              <Input type="number" min={0} max={40} step={0.5} value={String(form.hoursSavedPerUserPerWeek)} onChange={num('hoursSavedPerUserPerWeek')} />
            </Field>
            <Field label="Number of users" validationMessage={err('numberOfUsers')}>
              <Input type="number" min={1} max={10000} value={String(form.numberOfUsers)} onChange={num('numberOfUsers')} />
            </Field>
          </div>
          <div>
            <Button appearance="primary" type="submit" disabled={busy}>
              Submit intake
            </Button>{' '}
            <Link to="../intakes">Go to intake queue</Link>
          </div>
        </form>
        <RoiCard roi={roi} error={roiError} />
      </div>
    </Page>
  );
}

export function RoiCard({ roi, error }: { roi: Roi | null; error: string | null }) {
  return (
    <Card aria-label="ROI preview">
      <CardHeader header={<Text weight="semibold">ROI preview (live)</Text>} />
      {error && <ErrorBar title="ROI unavailable" text={error} />}
      {roi && (
        <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '4px 12px', margin: 0 }}>
          <dt>Annual hours saved</dt>
          <dd style={{ margin: 0 }}>{roi.annualHoursSaved.toLocaleString('en-US')}</dd>
          <dt>Annual value</dt>
          <dd style={{ margin: 0 }}>{usd(roi.annualValueUsd)}</dd>
          <dt>Build cost</dt>
          <dd style={{ margin: 0 }}>
            {usd(roi.buildCostUsd)} ({roi.buildHours} h)
          </dd>
          <dt>Annual run cost</dt>
          <dd style={{ margin: 0 }}>{usd(roi.annualRunCostUsd)}</dd>
          <dt>Payback</dt>
          <dd style={{ margin: 0 }}>{roi.paybackMonths === null ? 'never (run cost ≥ value)' : `${roi.paybackMonths} months`}</dd>
          <dt>Recommendation</dt>
          <dd style={{ margin: 0 }}>
            <Text weight="bold" style={{ color: roi.recommendation === 'build' ? '#107c10' : '#bc4b09' }}>
              {roi.recommendation === 'build' ? 'Build' : 'Review'}
            </Text>
          </dd>
        </dl>
      )}
    </Card>
  );
}
