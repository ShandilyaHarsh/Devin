import { Link as RouterLink } from 'react-router';
import {
  Badge,
  Body1,
  Button,
  Caption1,
  Card,
  CardFooter,
  CardHeader,
  Spinner,
  Text,
  makeStyles,
  tokens,
  type BadgeProps,
} from '@fluentui/react-components';
import { Page, StageBadge, type DataClassification } from '@platform/sdk';
import { ApiErrorView } from './ApiErrorView';
import { useCatalog } from './useCatalog';

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))', gap: tokens.spacingHorizontalL },
  meta: { display: 'grid', gridTemplateColumns: 'max-content 1fr', columnGap: tokens.spacingHorizontalM, rowGap: tokens.spacingVerticalXS },
  badges: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap', alignItems: 'center' },
});

const classificationColor: Record<DataClassification, BadgeProps['color']> = {
  public: 'success',
  internal: 'informative',
  confidential: 'warning',
  restricted: 'danger',
};

function describeAccessList(list: string[]): string {
  if (list.includes('*')) return 'All authenticated users';
  return list.length ? list.join(', ') : 'None (stage rules apply)';
}

export default function CatalogPage() {
  const styles = useStyles();
  const { entries, error } = useCatalog();

  return (
    <Page title="App catalog">
      <Body1>Every internal app registered on the platform, with its owner, data classification and rollout stage.</Body1>
      {error ? <ApiErrorView error={error} /> : null}
      {!entries && !error ? <Spinner label="Loading catalog" /> : null}
      {entries && entries.length === 0 ? <Text>No apps are registered yet.</Text> : null}
      <div className={styles.grid}>
        {entries?.map((entry) => (
          <Card key={entry.slug} aria-label={entry.displayName}>
            <CardHeader
              header={<Text weight="semibold" size={500}>{entry.displayName}</Text>}
              description={<Caption1>{entry.slug}</Caption1>}
              action={<StageBadge stage={entry.stage} />}
            />
            <Body1>{entry.description}</Body1>
            <div className={styles.badges}>
              <Badge appearance="tint" color={classificationColor[entry.dataClassification] ?? 'subtle'}>
                {entry.dataClassification}
              </Badge>
              <Badge appearance="outline">{entry.access}</Badge>
              {!entry.hasCode ? <Badge appearance="outline" color="warning">no code yet</Badge> : null}
            </div>
            <div className={styles.meta}>
              <Caption1>Owner</Caption1>
              <Text size={200}>
                {entry.owner.team} ({entry.owner.email})
              </Text>
              <Caption1>Business owner</Caption1>
              <Text size={200}>{entry.businessOwner.name}</Text>
              <Caption1>Access list</Caption1>
              <Text size={200}>{describeAccessList(entry.accessList)}</Text>
            </div>
            <CardFooter>
              <RouterLink to={`/apps/${encodeURIComponent(entry.slug)}`}>
                <Button appearance="primary" tabIndex={-1}>
                  Open {entry.displayName}
                </Button>
              </RouterLink>
            </CardFooter>
          </Card>
        ))}
      </div>
    </Page>
  );
}
