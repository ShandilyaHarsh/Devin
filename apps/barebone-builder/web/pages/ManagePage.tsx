import { useState } from 'react';
import {
  Button,
  Card,
  CardHeader,
  Checkbox,
  Select,
  Text,
} from '@fluentui/react-components';
import { Page, StageBadge, api } from '@platform/sdk';
import type { Stage } from '@platform/sdk';
import { BASE, STAGES, describeError } from '../model';
import type { CatalogEntry, DirectoryUser } from '../model';
import { useLoad } from './useLoad';
import { ClassificationBadge, ErrorBar, Loading, SuccessBar } from './shared';

const stageHelp: Record<Stage, string> = {
  'builder-only': 'Only Builders can open the app.',
  demo: 'Builders and the original requester can open it.',
  live: 'Only users on the access list can open it.',
};

export function ManagePage() {
  const catalog = useLoad<CatalogEntry[]>('/api/platform/catalog');
  const [selected, setSelected] = useState<string>('');
  const manageable = (catalog.data ?? []).filter((e) => e.slug !== 'barebone-builder');
  const current = manageable.find((e) => e.slug === selected) ?? manageable[0];

  return (
    <Page title="Manage app">
      <Loading loading={catalog.loading && !catalog.data} error={catalog.error}>
        {manageable.length === 0 ? (
          <Text>No registered apps yet. Approve an intake first.</Text>
        ) : (
          <>
            <Select
              aria-label="App"
              value={current?.slug}
              onChange={(_, d) => setSelected(d.value)}
              style={{ maxWidth: 360, marginBottom: 16 }}
            >
              {manageable.map((e) => (
                <option key={e.slug} value={e.slug}>
                  {e.displayName} ({e.slug})
                </option>
              ))}
            </Select>
            {current && <ManageApp key={current.slug} entry={current} onChanged={catalog.reload} />}
          </>
        )}
      </Loading>
    </Page>
  );
}

function ManageApp({ entry: initial, onChanged }: { entry: CatalogEntry; onChanged: () => void }) {
  const [entry, setEntry] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [picked, setPicked] = useState<Set<string>>(new Set(initial.accessList));
  const directory = useLoad<DirectoryUser[]>(`${BASE}/directory`);
  const everyoneAllowed = entry.dataClassification === 'public' || entry.dataClassification === 'internal';
  const slug = encodeURIComponent(entry.slug);

  const run = async (label: string, call: () => Promise<CatalogEntry>) => {
    setBusy(true);
    setError(null);
    setSaved(null);
    try {
      const updated = await call();
      setEntry(updated);
      setPicked(new Set(updated.accessList));
      setSaved(label);
      onChanged();
    } catch (e: unknown) {
      setError(describeError(e));
    } finally {
      setBusy(false);
    }
  };

  const toggle = (id: string, on: boolean) =>
    setPicked((p) => {
      const next = new Set(p);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });

  const stageIndex = STAGES.indexOf(entry.stage);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {error && <ErrorBar title="Change refused" text={error} />}
      {saved && <SuccessBar title={saved} text="Saved in the catalog and written to the audit trail." />}
      <Card aria-label="Stage">
        <CardHeader
          header={<Text weight="semibold">Stage</Text>}
          description={
            <span>
              {entry.displayName} · <ClassificationBadge value={entry.dataClassification} /> · current{' '}
              <StageBadge stage={entry.stage} />
            </span>
          }
        />
        <ol style={{ display: 'flex', gap: 8, listStyle: 'none', padding: 0, margin: 0, alignItems: 'stretch' }}>
          {STAGES.map((s, i) => (
            <li key={s} style={{ flex: 1 }}>
              <Button
                style={{ width: '100%', height: '100%', flexDirection: 'column' }}
                appearance={i === stageIndex ? 'primary' : i < stageIndex ? 'secondary' : 'outline'}
                disabled={busy || i === stageIndex}
                aria-current={i === stageIndex ? 'step' : undefined}
                onClick={() =>
                  void run(`Stage set to ${s}`, () => api.post<CatalogEntry>(`${BASE}/apps/${slug}/stage`, { stage: s }))
                }
              >
                <Text weight="semibold">
                  {i + 1}. {s}
                </Text>
                <Text size={200}>{stageHelp[s]}</Text>
              </Button>
            </li>
          ))}
        </ol>
      </Card>
      <Card aria-label="Access list">
        <CardHeader
          header={<Text weight="semibold">Access list (applies in live)</Text>}
          description={`Current: ${entry.accessList.length ? entry.accessList.join(', ') : '(empty — nobody in live)'}`}
        />
        <Loading loading={directory.loading} error={directory.error}>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))' }}>
            <Checkbox
              label={everyoneAllowed ? 'Everyone (*)' : 'Everyone (*) — not allowed for this classification'}
              disabled={!everyoneAllowed}
              checked={picked.has('*')}
              onChange={(_, d) => toggle('*', d.checked === true)}
            />
            {(directory.data ?? []).map((u) => (
              <Checkbox
                key={u.id}
                label={`${u.displayName} (${u.roles.join(', ') || 'no roles'})`}
                checked={picked.has(u.id)}
                onChange={(_, d) => toggle(u.id, d.checked === true)}
              />
            ))}
          </div>
        </Loading>
        <div>
          <Button
            appearance="primary"
            disabled={busy}
            onClick={() =>
              void run('Access list saved', () =>
                api.put<CatalogEntry>(`${BASE}/apps/${slug}/access-list`, { userIds: [...picked].sort() }),
              )
            }
          >
            Save access list
          </Button>
        </div>
      </Card>
    </div>
  );
}
