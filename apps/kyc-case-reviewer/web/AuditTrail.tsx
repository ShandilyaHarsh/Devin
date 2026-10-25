import { Text } from '@fluentui/react-components';
import { AuditViewer, Page, VerifyAuditButton } from '@platform/sdk';
import { SLUG } from './model';

export function AuditTrail() {
  return (
    <Page title="Audit trail">
      <Text block>
        Every change, denial and PII reveal in this app is written to a hash-chained audit log in the same transaction.
      </Text>
      <VerifyAuditButton />
      <AuditViewer app={SLUG} />
    </Page>
  );
}
