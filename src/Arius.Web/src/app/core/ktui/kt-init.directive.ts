import { AfterViewInit, Directive, inject } from '@angular/core';
import { MetronicInitService } from '../services/metronic-init.service';

/**
 * Re-initialises Metronic's KTUI JS for a host whose `data-kt-*` markup enters the DOM dynamically
 * (dropdown menus inside `@if`/`@for`, drawers, etc.). Put `ktInit` on such a host so KTUI re-scans
 * it after Angular renders. The shell separately re-inits on every navigation.
 */
@Directive({
  selector: '[ktInit]',
  standalone: true,
})
export class KtInitDirective implements AfterViewInit {
  private readonly kt = inject(MetronicInitService);

  ngAfterViewInit(): void {
    queueMicrotask(() => this.kt.init());
  }
}
