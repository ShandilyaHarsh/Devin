import type { ReactNode } from 'react';
import { Badge, MessageBar, MessageBarBody, MessageBarTitle, Spinner } from '@fluentui/react-components';
import type { DataClassification } from '@platform/sdk';

const classificationColor: Record<DataClassification, 'success' | 'informative' | 'warning' | 'danger'> = {
  public: 'success',
  internal: 'informative',
  confidential: 'warning',
  restricted: 'danger',
};

export function ClassificationBadge({ value }: { value: DataClassification }) {
  return (
    <Badge appearance="tint" color={classificationColor[value]}>
      {value}
    </Badge>
  );
}

export function Loading({ loading, error, children }: { loading: boolean; error: string | null; children: ReactNode }) {
  if (error) return <ErrorBar title="Could not load" text={error} />;
  if (loading) return <Spinner label="Loading…" />;
  return <>{children}</>;
}

export function ErrorBar({ title, text }: { title: string; text: string }) {
  return (
    <MessageBar intent="error">
      <MessageBarBody>
        <MessageBarTitle>{title}</MessageBarTitle>
        {text}
      </MessageBarBody>
    </MessageBar>
  );
}

export function SuccessBar({ title, text }: { title: string; text: string }) {
  return (
    <MessageBar intent="success">
      <MessageBarBody>
        <MessageBarTitle>{title}</MessageBarTitle>
        {text}
      </MessageBarBody>
    </MessageBar>
  );
}

export const preStyle = {
  background: '#f5f5f5',
  border: '1px solid #e0e0e0',
  borderRadius: 4,
  padding: 12,
  fontSize: 12,
  overflowX: 'auto',
  margin: 0,
} as const;

export const gridStyle = {
  display: 'grid',
  gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))',
  gap: 16,
} as const;
