import { Body1 } from '@fluentui/react-components';
import { AuditViewer, Page, VerifyAuditButton } from '@platform/sdk';

export default function AuditPage() {
  return (
    <Page title="Audit trail">
      <Body1>
        Append-only, hash-chained audit log of every mutation, denial and PII reveal. Verification recomputes the HMAC chain
        server-side.
      </Body1>
      <VerifyAuditButton />
      <AuditViewer />
    </Page>
  );
}
