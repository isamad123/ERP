import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ScrollRegion } from './ScrollRegion';

describe('ScrollRegion', () => {
  it('is a named region that takes the keyboard focus, and keeps the screen class', () => {
    render(<ScrollRegion label="Leave ledger" className="erp-table-scroll"><table><tbody><tr><td>x</td></tr></tbody></table></ScrollRegion>);
    const region = screen.getByRole('region', { name: 'Leave ledger' });
    expect(region.tabIndex).toBe(0);
    expect(region.className).toContain('erp-scroll-region');
    expect(region.className).toContain('erp-table-scroll');
    region.focus();
    expect(document.activeElement).toBe(region);
  });
});
