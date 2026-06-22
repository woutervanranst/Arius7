import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { StatisticsTabComponent } from './statistics-tab.component';
import { ApiService } from '../../../core/api/api.service';
import { StatisticsDto } from '../../../core/api/api-models';

/**
 * Deterministic coverage of the Statistics tab's per-tier breakdown — the live Playwright suite
 * cannot exercise this (its seed archives to a single tier), so multi-tier rendering and the
 * empty-state gate are verified here against a mocked StatisticsDto.
 */
describe('StatisticsTabComponent', () => {
  function render(stats: StatisticsDto) {
    TestBed.configureTestingModule({
      imports: [StatisticsTabComponent],
      providers: [{ provide: ApiService, useValue: { getStatistics: () => of(stats) } }],
    });
    const fixture = TestBed.createComponent(StatisticsTabComponent);
    fixture.componentRef.setInput('repoId', '1');
    fixture.detectChanges();   // runs the load effect; of() emits synchronously
    return fixture.nativeElement as HTMLElement;
  }

  const multiTier: StatisticsDto = {
    files: 10,
    originalSize: 1000,
    deduplicatedSize: 200,
    storedSize: 100,
    uniqueChunks: 5,
    storedByTier: [
      { tier: 'Cool', uniqueChunks: 3, storedSize: 40 },
      { tier: 'Archive', uniqueChunks: 2, storedSize: 60 },
    ],
  };

  afterEach(() => TestBed.resetTestingModule());

  it('renders the five KPI cards with real figures', () => {
    const el = render(multiTier);
    const cards = el.querySelectorAll('[data-testid="kpi-card"]');
    expect(cards.length).toBe(5);
    expect(el.textContent).not.toContain('—');
    expect(el.textContent).toContain('Original size');
    expect(el.textContent).toContain('Deduplicated size');
    expect(el.textContent).toContain('Unique chunks');
  });

  it('shows the savings note (original → stored reduction)', () => {
    const el = render(multiTier);
    const savings = el.querySelector('[data-testid="savings"]');
    expect(savings).not.toBeNull();
    expect(savings!.textContent).toContain('90%'); // 1 - 100/1000
  });

  it('renders one tier row per StatisticsDto.storedByTier entry, in order', () => {
    const el = render(multiTier);

    const panel = el.querySelector('[data-testid="tier-breakdown"]');
    expect(panel).not.toBeNull();
    expect(panel!.textContent).toContain('Stored size by tier');

    const rows = el.querySelectorAll('[data-testid="tier-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Cool');
    expect(rows[0].textContent).toContain('3 chunks');
    expect(rows[1].textContent).toContain('Archive');
    expect(rows[1].textContent).toContain('2 chunks');
  });

  it('hides the tier-breakdown panel when storedByTier is empty', () => {
    const el = render({ ...multiTier, storedByTier: [] });
    expect(el.querySelector('[data-testid="tier-breakdown"]')).toBeNull();
    // KPI cards still render (a repo with no cached chunk-index coverage shows zeroed figures).
    expect(el.querySelectorAll('[data-testid="kpi-card"]').length).toBe(5);
  });
});
