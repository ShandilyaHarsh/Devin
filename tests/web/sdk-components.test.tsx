import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AccessDenied, MaskedValue, StageBadge, VerifyAuditButton } from '@platform/sdk';
import { json, pathOf, stubFetch } from './helpers';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('StageBadge', () => {
  it.each(['builder-only', 'demo', 'live'] as const)('renders %s', (stage) => {
    render(<StageBadge stage={stage} />);
    expect(screen.getByText(stage)).toBeInTheDocument();
  });
});

describe('AccessDenied', () => {
  it('shows the text and the reason', () => {
    render(<AccessDenied reason="app-not-registered" />);
    expect(screen.getByText(/Access denied/)).toBeInTheDocument();
    expect(screen.getByText(/app-not-registered/)).toBeInTheDocument();
  });

  it('works without a reason', () => {
    render(<AccessDenied />);
    expect(screen.getByText(/Access denied/)).toBeInTheDocument();
  });
});

describe('MaskedValue', () => {
  it('renders the masked value by default and never the revealed one', () => {
    const { container } = render(<MaskedValue masked="J*** D***" />);
    expect(screen.getByText('J*** D***')).toBeInTheDocument();
    expect(container.textContent).not.toContain('Jane');
  });

  it('renders the revealed value when given', () => {
    render(<MaskedValue masked="J*** D***" revealed="Jane Doe" />);
    expect(screen.getByText('Jane Doe')).toBeInTheDocument();
    expect(screen.queryByText('J*** D***')).not.toBeInTheDocument();
  });
});

describe('VerifyAuditButton', () => {
  it('shows a valid chain with the row count after clicking', async () => {
    const calls = stubFetch((c) =>
      pathOf(c.url) === '/api/platform/audit/verify' ? json({ valid: true, checked: 42, firstBrokenId: null, reason: null }) : undefined,
    );
    render(<VerifyAuditButton />);
    expect(calls.filter((c) => pathOf(c.url) === '/api/platform/audit/verify')).toHaveLength(0);

    await userEvent.click(screen.getByRole('button', { name: /Verify audit chain/ }));

    expect(await screen.findByText(/Chain valid \(42 rows\)/)).toBeInTheDocument();
    const verifyCalls = calls.filter((c) => pathOf(c.url) === '/api/platform/audit/verify');
    expect(verifyCalls).toHaveLength(1);
    expect(verifyCalls[0]!.method).toBe('GET');
    expect(screen.queryByText(/Chain broken/)).not.toBeInTheDocument();
  });

  it('shows the first broken row when the chain is broken', async () => {
    stubFetch((c) =>
      pathOf(c.url) === '/api/platform/audit/verify' ? json({ valid: false, checked: 7, firstBrokenId: 5, reason: 'hash mismatch' }) : undefined,
    );
    render(<VerifyAuditButton />);
    await userEvent.click(screen.getByRole('button', { name: /Verify audit chain/ }));
    expect(await screen.findByText(/Chain broken at row #5/)).toBeInTheDocument();
    expect(screen.queryByText(/Chain valid/)).not.toBeInTheDocument();
  });
});
