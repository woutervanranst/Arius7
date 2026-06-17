import { Injectable, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { RealtimeService } from '../api/realtime.service';
import { SearchHitDto } from '../api/api-models';

/** Drives the global cross-repository file-search overlay (⌘K / top-bar search). */
@Injectable({ providedIn: 'root' })
export class SearchStore {
  private readonly realtime = inject(RealtimeService);

  readonly open = signal(false);
  readonly query = signal('');
  readonly results = signal<SearchHitDto[]>([]);
  readonly loading = signal(false);

  private subscription?: Subscription;
  private debounce?: ReturnType<typeof setTimeout>;

  openSearch(): void { this.open.set(true); }

  close(): void {
    this.open.set(false);
    clearTimeout(this.debounce);
    this.debounce = undefined;
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.loading.set(false);
  }

  setQuery(value: string): void {
    this.query.set(value);
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => this.run(value), 250);
  }

  private run(value: string): void {
    this.subscription?.unsubscribe();
    this.results.set([]);
    if (!value.trim()) { this.loading.set(false); return; }
    this.loading.set(true);
    this.subscription = this.realtime.searchAll(value).subscribe({
      next: hit => this.results.update(a => [...a, hit]),
      error: () => this.loading.set(false),
      complete: () => this.loading.set(false),
    });
  }
}
