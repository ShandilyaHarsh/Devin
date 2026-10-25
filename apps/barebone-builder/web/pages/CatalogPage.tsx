import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Badge,
  Card,
  CardHeader,
  Text,
} from '@fluentui/react-components';
import { Page, StageBadge } from '@platform/sdk';
import type { CatalogEntry } from '../model';
import { useLoad } from './useLoad';
import { ClassificationBadge, Loading, gridStyle, preStyle } from './shared';

export function CatalogPage() {
  const { data, error, loading } = useLoad<CatalogEntry[]>('/api/platform/catalog');
  return (
    <Page title="Catalog & manifests">
      <Text block style={{ marginBottom: 12 }}>
        Every app registered on the platform. Access is enforced from this catalog (stage + access list), not from the
        files in Git.
      </Text>
      <Loading loading={loading} error={error}>
        <div style={gridStyle}>
          {(data ?? []).map((e) => (
            <CatalogCard key={e.slug} entry={e} />
          ))}
        </div>
      </Loading>
    </Page>
  );
}

function CatalogCard({ entry }: { entry: CatalogEntry }) {
  const { data: detail } = useLoad<CatalogEntry>(
    entry.manifestYaml ? null : `/api/platform/catalog/${encodeURIComponent(entry.slug)}`,
  );
  const yaml = entry.manifestYaml || detail?.manifestYaml || '';
  return (
    <Card aria-label={`Catalog entry ${entry.displayName}`}>
      <CardHeader
        header={<Text weight="semibold" size={400}>{entry.displayName}</Text>}
        description={<Text font="monospace">{entry.slug}</Text>}
        action={<StageBadge stage={entry.stage} />}
      />
      <Text>{entry.description}</Text>
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
        <ClassificationBadge value={entry.dataClassification} />
        <Badge appearance="outline">{entry.access}</Badge>
        <Badge appearance="outline" color={entry.hasCode ? 'success' : 'subtle'}>
          {entry.hasCode ? 'code deployed' : 'no code yet'}
        </Badge>
      </div>
      <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '2px 12px', margin: 0 }}>
        <dt>Owner</dt>
        <dd style={{ margin: 0 }}>
          {entry.owner.team} ({entry.owner.email})
        </dd>
        <dt>Business owner</dt>
        <dd style={{ margin: 0 }}>
          {entry.businessOwner.name} ({entry.businessOwner.email})
        </dd>
        <dt>Access list</dt>
        <dd style={{ margin: 0 }}>{entry.accessList.length ? entry.accessList.join(', ') : '(empty)'}</dd>
        <dt>Requested by</dt>
        <dd style={{ margin: 0 }}>{entry.requesterId ?? '—'}</dd>
        <dt>Approved by</dt>
        <dd style={{ margin: 0 }}>{entry.approvedBy ?? '—'}</dd>
      </dl>
      <Accordion collapsible>
        <AccordionItem value="yaml">
          <AccordionHeader>platform.yaml</AccordionHeader>
          <AccordionPanel>
            <pre style={preStyle}>{yaml || '(not available)'}</pre>
          </AccordionPanel>
        </AccordionItem>
      </Accordion>
    </Card>
  );
}
