import { Component, HostListener, inject, signal, ViewEncapsulation } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs/operators';
import { MetronicInitService } from './core/services/metronic-init.service';
import { ArchiveRestoreDrawerComponent } from './features/drawer/archive-restore-drawer.component';
import { PropertiesDrawerComponent } from './features/drawer/properties-drawer.component';
import { GlobalSearchOverlayComponent } from './features/search/global-search-overlay.component';
import { SearchStore } from './core/state/search.store';

interface RailItem { label: string; icon: string; link: string; }

/**
 * The Arius application shell (Metronic demo8): an icon rail over a muted page, a floating white
 * content card with a top bar (breadcrumb + global search) and a scrollable main region.
 */
@Component({
  selector: 'body[app-root]',
  standalone: true,
  encapsulation: ViewEncapsulation.None,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ArchiveRestoreDrawerComponent, PropertiesDrawerComponent, GlobalSearchOverlayComponent],
  template: `
    <!-- Icon rail -->
    <aside class="fixed top-0 bottom-0 start-0 z-20 flex flex-col items-center bg-muted py-4"
           style="width:86px">
      <a routerLink="/overview" class="block" title="Arius">
        <img src="assets/media/arius-iceberg.svg" alt="Arius" style="width:38px;height:38px;border-radius:10px" />
      </a>

      <nav class="flex flex-col items-center gap-2 mt-5">
        @for (item of nav; track item.link) {
          <a [routerLink]="item.link"
             [attr.data-testid]="'rail-' + item.label.toLowerCase()"
             routerLinkActive="!text-[#3b82f6] !bg-white !border-[#ececef] shadow-sm"
             class="flex flex-col items-center justify-center gap-1 text-[#71717a] border border-transparent transition-colors"
             style="width:64px;height:62px;border-radius:13px">
            <i class="ki-filled {{ item.icon }}" style="font-size:21px"></i>
            <span style="font-size:10.5px;font-weight:600;line-height:1">{{ item.label }}</span>
          </a>
        }
      </nav>

      <div class="mt-auto flex flex-col items-center gap-4">
        <button type="button" class="text-[#a1a1aa] hover:text-[#71717a]" title="Notifications">
          <i class="ki-filled ki-notification-status" style="font-size:20px"></i>
        </button>
        <div class="relative">
          <div style="width:40px;height:40px;border-radius:9999px;background:linear-gradient(135deg,#0091e1,#5bd6fd)"></div>
          <span class="absolute" style="right:0;bottom:0;width:10px;height:10px;border-radius:9999px;background:#22c55e;border:2px solid #f4f4f5"></span>
        </div>
      </div>
    </aside>

    <!-- Floating content card -->
    <div class="flex flex-col grow overflow-hidden bg-white"
         style="margin:14px 14px 14px 100px; border:1px solid #e4e4e7; border-radius:16px; box-shadow:0 1px 2px rgba(0,0,0,.04)">
      <!-- Top bar -->
      <header class="flex items-center justify-between shrink-0 px-6"
              style="height:64px; border-bottom:1px solid #f0f0f2">
        <div class="text-[14px]">
          <span style="color:#a1a1aa">Arius</span>
          <span style="color:#d4d4d8" class="mx-1.5">›</span>
          <span style="color:#27272a;font-weight:600" data-testid="breadcrumb-current">{{ crumb() }}</span>
        </div>
        @if (searchVisible()) {
          <button type="button" data-testid="topbar-search" (click)="search.openSearch()" class="flex items-center gap-2 px-3"
                  style="width:300px;height:38px;background:#f4f4f5;border-radius:9px;color:#71717a;cursor:text">
            <i class="ki-filled ki-magnifier" style="font-size:16px"></i>
            <span class="grow text-left text-[13.5px]">Search files across repositories…</span>
            <kbd class="text-[11px] px-1.5 py-0.5 rounded" style="background:#fff;border:1px solid #e4e4e7;color:#a1a1aa">⌘K</kbd>
          </button>
        }
      </header>

      <!-- Main scroll region -->
      <main class="grow overflow-y-auto" style="padding:24px 26px 36px">
        <router-outlet></router-outlet>
      </main>
    </div>

    <!-- Global slide-overs (archive / restore / properties) + cross-repo search overlay -->
    <arius-drawer></arius-drawer>
    <arius-properties-drawer></arius-properties-drawer>
    <arius-global-search></arius-global-search>
  `,
})
export class AppComponent {
  private readonly router = inject(Router);
  private readonly kt = inject(MetronicInitService);
  protected readonly search = inject(SearchStore);

  protected readonly currentSegment = signal('overview');
  protected readonly searchVisible = () => this.currentSegment() !== 'overview'; // hidden on Overview (per spec)

  protected readonly nav: RailItem[] = [
    { label: 'Overview', icon: 'ki-element-11', link: '/overview' },
    { label: 'Repos', icon: 'ki-folder', link: '/repos' },
    { label: 'Jobs', icon: 'ki-technology-2', link: '/jobs' },
    { label: 'Settings', icon: 'ki-setting-2', link: '/settings' },
  ];

  constructor() {
    // demo8 layout variables + muted page background on the host <body>.
    document.body.classList.add('flex', 'h-full', 'overflow-hidden', 'bg-muted');
    document.body.style.setProperty('--header-height', '64px');
    document.body.style.setProperty('--sidebar-width', '86px');

    this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe(() => {
      this.currentSegment.set(this.router.url.split('/').filter(Boolean)[0] ?? 'overview');
      queueMicrotask(() => this.kt.init());
    });
  }

  @HostListener('document:keydown', ['$event'])
  protected onKeydown(event: KeyboardEvent): void {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.search.openSearch();
    } else if (event.key === 'Escape' && this.search.open()) {
      this.search.close();
    }
  }

  protected crumb(): string {
    const segment = this.currentSegment();
    return segment.charAt(0).toUpperCase() + segment.slice(1);
  }
}
