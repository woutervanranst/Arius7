import { Injectable } from '@angular/core';

type ToastVariant = 'error' | 'success' | 'info' | 'warning';

/** Minimal shape of the Metronic KTToast global (loaded via ktui.min.js, initialised by MetronicInitService). */
interface KTToastApi {
  show(options: { message: string; variant?: string }): void;
}

/**
 * Thin wrapper over Metronic's KTToast for user-facing feedback (e.g. a realtime action that never reached
 * the server). Resolves the global lazily so it works regardless of init order, and no-ops if the vendor
 * bundle is unavailable (tests / SSR) rather than throwing.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private get toast(): KTToastApi | undefined {
    return (globalThis as Record<string, unknown>)['KTToast'] as KTToastApi | undefined;
  }

  error(message: string): void { this.show(message, 'error'); }
  success(message: string): void { this.show(message, 'success'); }
  info(message: string): void { this.show(message, 'info'); }

  private show(message: string, variant: ToastVariant): void {
    this.toast?.show({ message, variant });
  }
}
