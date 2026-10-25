import { Card, CardHeader, Text } from '@fluentui/react-components';
import { Page } from '@platform/sdk';
import type { Capability } from '../model';
import { useLoad } from './useLoad';
import { Loading, gridStyle } from './shared';

export function CapabilitiesPage() {
  const { data, error, loading } = useLoad<Capability[]>('/api/platform/capabilities');
  return (
    <Page title="Platform capabilities">
      <Text block style={{ marginBottom: 12 }}>
        What every app gets for free from the platform, and where to see it in this demo.
      </Text>
      <Loading loading={loading} error={error}>
        <div style={gridStyle}>
          {(data ?? []).map((c) => (
            <Card key={c.id} aria-label={c.name}>
              <CardHeader header={<Text weight="semibold">{c.name}</Text>} />
              <Text>{c.description}</Text>
              <Text size={200} style={{ color: '#555' }}>
                <strong>How to see it:</strong> {c.howToSee}
              </Text>
            </Card>
          ))}
        </div>
      </Loading>
    </Page>
  );
}
