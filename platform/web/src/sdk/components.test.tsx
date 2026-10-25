import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AccessDenied, MaskedValue, StageBadge, VerifyAuditButton } from './components';

describe('sdk components', () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(['builder-only', 'demo', 'live'] as const)('StageBadge renders %s', (stage) => {
    render(<StageBadge stage={stage} />);
    expect(screen.getByText(stage)).toBeInTheDocument();
  });

  it('AccessDenied shows the reason', () => {
    render(<AccessDenied reason="stage-builder-only" />);
    expect(screen.getByText('Access denied')).toBeInTheDocument();
    expect(screen.getByText('stage-builder-only')).toBeInTheDocument();
  });

  it('MaskedValue prefers the revealed value', () => {
    const { rerender } = render(<MaskedValue masked="J*** D***" />);
    expect(screen.getByText('J*** D***')).toBeInTheDocument();
    rerender(<MaskedValue masked="J*** D***" revealed="Jane Doe" />);
    expect(screen.getByText('Jane Doe')).toBeInTheDocument();
  });

  it('VerifyAuditButton shows a broken chain', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ valid: false, checked: 7, firstBrokenId: 4, reason: 'hash mismatch' })),
    );
    render(<VerifyAuditButton />);
    await userEvent.click(screen.getByRole('button', { name: 'Verify audit chain' }));
    expect(await screen.findByText('Chain broken at row #4')).toBeInTheDocument();
  });
});
