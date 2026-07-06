import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, OnDestroy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api/api.service';
import { RealtimeService } from '../../core/api/realtime.service';
import { JobSnapshot, CostEstimateMsg, JobDetailDto, JobOutcome, ResumeInfo, isNonTerminal } from '../../core/api/api-models';
import { LayeredBarComponent } from '../../shared/layered-bar/layered-bar.component';
import { formatBytes, formatCount, formatCurrency } from '../../shared/format';
import { formatEta, formatDuration, formatThroughput, hydratedByLabel, statusMeta, phaseSentence, archiveBarLayers, restoreBarLayers, resolveRehydrationWindowHours } from '../../shared/job-format';
import { Subscription } from 'rxjs';

/** One stage-summary row (derived from the live snapshot). */
interface Stage { label: string; sub: string; state: 'done' | 'running' | 'pending'; }

/**
 * `/jobs/:id` — the unified archive/restore job detail page (design README §Screens 2–4).
 * ONE component drives both kinds; the palette, KPI tiles, stage list and rehydration wait card
 * switch on `kind()`. On open it loads the persisted row (`getJob`) AND attaches to the live
 * SignalR stream (`attachToJob` → snapshot, then `jobProgress` deltas); reconnect re-attach is
 * automatic (RealtimeService). The layered bar reads byte fields; the cost modal renders from a
 * live `CostEstimate` push (captured per jobId — no replay on reload); warnings load lazily.
 */
@Component({
  selector: 'arius-job-detail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, LayeredBarComponent],
  template: `
    @if (detail(); as d) {
      <div data-testid="job-detail" style="max-width:1020px;background:#fff;border:1px solid #e4e4e7;border-radius:16px;box-shadow:0 1px 2px rgba(0,0,0,.04);overflow:hidden">

        <!-- Breadcrumb -->
        <div style="display:flex;align-items:center;height:56px;padding:0 24px;border-bottom:1px solid #f0f0f2;font-size:14px">
          <a routerLink="/" style="color:#a1a1aa;text-decoration:none">Arius</a>
          <span style="color:#d4d4d8;margin:0 6px">&rsaquo;</span>
          <a routerLink="/jobs" style="color:#a1a1aa;text-decoration:none">Jobs</a>
          <span style="color:#d4d4d8;margin:0 6px">&rsaquo;</span>
          <span style="color:#27272a;font-weight:600">{{ kindLabel() }} &middot; {{ d.repo }}</span>
        </div>

        <div style="padding:28px 32px 32px">

          <!-- Header -->
          <div style="display:flex;align-items:flex-start;justify-content:space-between;gap:16px">
            <div style="display:flex;align-items:center;gap:14px">
              <div style="width:44px;height:44px;border-radius:12px;display:flex;align-items:center;justify-content:center"
                   [style.background]="kind() === 'restore' ? '#f5f3ff' : '#eff6ff'"
                   [style.color]="accent()">
                <i class="ki-filled" [class.ki-cloud-download]="kind() === 'restore'" [class.ki-cloud-add]="kind() !== 'restore'" style="font-size:20px"></i>
              </div>
              <div>
                <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                  <h1 class="ar-heading" style="margin:0;font-size:20px;font-weight:700">{{ kindLabel() }} &middot; {{ d.repo }}</h1>
                  <span data-testid="job-status"
                        style="display:inline-flex;align-items:center;gap:5px;font-size:12px;font-weight:600;border-radius:999px;padding:3px 10px"
                        [style.background]="meta().bg" [style.color]="meta().color" [style.border]="meta().border">
                    @if (status() === 'running') {
                      <span style="width:7px;height:7px;border-radius:999px" [style.background]="meta().dot"
                            [style.animation]="meta().pulse ? 'ar-pulse 1.4s infinite' : 'none'"></span>
                    } @else {
                      <i class="ki-filled {{ meta().icon }}" style="font-size:12px"></i>
                    }
                    {{ meta().label }}
                  </span>
                </div>
                <div style="font-size:12.5px;color:#a1a1aa;margin-top:5px">
                  @if (d.startedAt) { Started {{ d.startedAt | date:'HH:mm' }} &middot; }
                  @if (elapsedSeconds() != null) { elapsed {{ formatDuration(elapsedSeconds()) }} &middot; }
                  <span style="text-transform:capitalize">{{ d.trigger }}</span> &middot; {{ d.repo }}
                </div>
              </div>
            </div>
            @if (isNonTerminal(status())) {
              <div style="text-align:right">
                <div style="font-size:24px;font-weight:700;letter-spacing:-.02em"
                     [style.color]="status() === 'rehydrating' ? '#b45309' : '#18181b'">{{ bigEta() }}</div>
                @if (subEta()) { <div style="font-size:12.5px;color:#71717a">{{ subEta() }}</div> }
              </div>
            }
          </div>

          <!-- Layered progress bar -->
          <div style="margin-top:26px">
            <arius-layered-bar [kind]="kind()" [scanned]="scannedPct()" [middle]="middlePct()" [top]="topPct()" />
          </div>
          <div style="display:flex;gap:20px;margin-top:10px;font-size:12.5px;color:#52525b;flex-wrap:wrap">
            @if (kind() === 'restore') {
              <span><span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#ede9fe;margin-right:6px"></span>Planned &middot; {{ formatCount(plannedChunks()) }} chunks ({{ formatBytes(snap()?.restoreTotalBytes) }})</span>
              <span><span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#c4b5fd;margin-right:6px"></span>Hydrated &amp; ready &middot; {{ formatCount(readyChunks()) }} chunks</span>
              <span><span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#7c3aed;margin-right:6px"></span>Restored to disk &middot; {{ formatCount(snap()?.filesRestored) }} files ({{ formatBytes(snap()?.bytesRestored) }})</span>
            } @else {
              <span><span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#dbeafe;margin-right:6px"></span>Scanned &middot; {{ formatBytes(snap()?.scannedBytes) }} of {{ formatBytes(snap()?.totalBytes) }}</span>
              <span><span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#93c5fd;margin-right:6px"></span>Hashed &amp; routed &middot; {{ round(middlePct()) }}%</span>
              <span><span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#2563eb;margin-right:6px"></span>Uploaded &middot; {{ formatBytes(snap()?.uploadedBytes) }} of {{ formatBytes(snap()?.totalNewBytes) }}</span>
            }
          </div>

          <!-- Rehydration wait card (restore, while rehydrating) -->
          @if (kind() === 'restore' && status() === 'rehydrating') {
            <div style="display:flex;align-items:center;gap:14px;margin-top:20px;background:#fffbeb;border:1px solid #fde68a;border-radius:12px;padding:14px 18px;flex-wrap:wrap">
              <i class="ki-filled ki-moon" style="font-size:20px;color:#b45309"></i>
              <div style="flex:1;min-width:220px">
                <div style="font-size:13.5px;font-weight:600;color:#92400e">
                  {{ formatCount(snap()?.chunksNeedingRehydration) }} chunks are being moved out of Archive tier by Azure@if (rehydrateWindowHours() != null) { (~{{ rehydrateWindowHours() }} h)}
                </div>
                <div style="font-size:12.5px;color:#a16207;margin-top:2px">
                  Status is checked periodically — often at first for High priority, backed off for Standard. You can close this page.
                  @if (hydratedBy()) { &middot; {{ hydratedBy() }} }
                </div>
              </div>
              <label style="display:flex;align-items:center;gap:9px;font-size:12.5px;font-weight:600;color:#92400e;cursor:pointer">
                <button data-testid="autoresume-toggle" type="button" (click)="toggleAutoResume()" [attr.aria-pressed]="autoResume()"
                        style="width:34px;height:20px;border-radius:999px;position:relative;flex-shrink:0;border:none;padding:0"
                        [style.background]="autoResume() ? '#b45309' : '#d4d4d8'">
                  <span style="position:absolute;top:2px;width:16px;height:16px;border-radius:999px;background:#fff;transition:left .15s,right .15s"
                        [style.left]="autoResume() ? 'auto' : '2px'" [style.right]="autoResume() ? '2px' : 'auto'"></span>
                </button>
                Automatically restore as chunks become available
              </label>
              <button data-testid="restore-now" type="button" (click)="resumeNow()"
                      style="height:32px;padding:0 13px;border-radius:8px;font-size:12.5px;font-weight:600;background:#7c3aed;color:#fff;border:none">Restore now</button>
            </div>
          }

          <!-- Review-cost prompt: awaiting-cost reload with no live push this session (no modal replay) -->
          @if (status() === 'awaiting-cost' && !cost()) {
            <div style="display:flex;align-items:center;gap:12px;margin-top:20px;background:#fffbeb;border:1px solid #fde68a;border-radius:12px;padding:14px 18px">
              <i class="ki-filled ki-dollar" style="font-size:18px;color:#b45309"></i>
              <div style="font-size:13px;color:#92400e">This restore is paused for cost approval. The estimate is delivered live — reattach from the job stream to review and approve it.</div>
            </div>
          }

          <!-- KPI tiles -->
          <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin-top:22px">
            @if (kind() === 'restore') {
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Restored</div>
                <div style="font-size:19px;font-weight:700;color:#18181b;margin-top:3px">{{ formatCount(snap()?.filesRestored) }} / {{ formatCount(snap()?.restoreTotalFiles) }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">{{ formatBytes(snap()?.bytesRestored) }} on disk</div>
              </div>
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Ready to download</div>
                <div style="font-size:19px;font-weight:700;color:#18181b;margin-top:3px">{{ formatCount(readyChunks()) }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">chunks hydrated</div>
              </div>
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Rehydrating</div>
                <div style="font-size:19px;font-weight:700;color:#b45309;margin-top:3px">{{ formatCount(snap()?.chunksNeedingRehydration) }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">{{ formatCount(snap()?.chunksPending) }} pending</div>
              </div>
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Priority</div>
                <div style="font-size:19px;font-weight:700;color:#6d28d9;margin-top:3px;text-transform:capitalize">{{ priority() }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">{{ priority() === 'high' ? 'faster hydration' : 'lower cost' }}</div>
              </div>
            } @else {
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Uploaded</div>
                <div style="font-size:19px;font-weight:700;color:#18181b;margin-top:3px">{{ formatBytes(snap()?.uploadedBytes) }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">of {{ formatBytes(snap()?.totalNewBytes) }} new data</div>
              </div>
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Deduplicated</div>
                <div style="font-size:19px;font-weight:700;color:#6d28d9;margin-top:3px">{{ formatCount(snap()?.dedupedFiles) }} files</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">{{ formatBytes(snap()?.dedupedBytes) }} not re-uploaded</div>
              </div>
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Throughput</div>
                <div style="font-size:19px;font-weight:700;color:#18181b;margin-top:3px">{{ formatThroughput(snap()?.throughputBytesPerSec) }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">sustained</div>
              </div>
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">{{ status() === 'completed' ? 'Duration' : 'Est. finish' }}</div>
                <div style="font-size:19px;font-weight:700;color:#18181b;margin-top:3px">{{ status() === 'completed' ? formatDuration(outcome()?.durationSeconds) : finishTime() }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">{{ status() === 'completed' ? 'total run' : 'upload + snapshot' }}</div>
              </div>
            }
          </div>

          <!-- Stage summary -->
          <div style="margin-top:20px;border:1px solid #ececef;border-radius:12px;padding:4px 16px 6px">
            @for (s of stages(); track s.label; let first = $first) {
              <div style="display:flex;align-items:center;gap:12px;padding:10px 0" [style.borderTop]="first ? 'none' : '1px solid #f6f6f7'">
                <span style="width:9px;height:9px;border-radius:999px;flex-shrink:0"
                      [style.background]="s.state === 'done' ? '#22c55e' : s.state === 'running' ? accent() : '#e4e4e7'"
                      [style.animation]="s.state === 'running' ? 'ar-pulse 1.4s infinite' : 'none'"></span>
                <span style="font-size:13px;font-weight:600;width:130px" [style.color]="s.state === 'pending' ? '#a1a1aa' : '#27272a'">{{ s.label }}</span>
                <span style="font-size:12.5px;color:#71717a;flex:1">{{ s.sub }}</span>
                <span style="font-size:12px;color:#a1a1aa">{{ s.state === 'pending' ? '' : s.state }}</span>
              </div>
            }
          </div>

          <!-- Footer -->
          <div style="display:flex;align-items:center;gap:16px;margin-top:22px">
            @if (isNonTerminal(status())) {
              <button data-testid="job-cancel" type="button" (click)="cancel()"
                      style="font-size:13px;font-weight:600;color:#dc2626;background:none;border:none">Cancel job</button>
            }
            @if (warningCount() > 0) {
              <button data-testid="warnings-toggle" type="button" (click)="toggleWarnings()"
                      style="display:inline-flex;align-items:center;gap:7px;font-size:13px;font-weight:600;color:#b45309;background:none;border:none">
                <i class="ki-filled ki-information-2" style="font-size:15px"></i>{{ warningsLabel() }}
              </button>
            }
          </div>

          <!-- Warnings panel — verbatim [WRN] log lines -->
          @if (warningsOpen()) {
            <div data-testid="warnings-panel" style="margin-top:12px;border:1px solid #fde68a;border-radius:12px;overflow:hidden">
              <div style="display:flex;align-items:center;gap:8px;background:#fffbeb;padding:9px 14px;border-bottom:1px solid #fde68a">
                <span style="font-size:12px;font-weight:600;color:#92400e">Warnings — raw log messages</span>
                <button type="button" (click)="copyWarnings()" style="margin-left:auto;font-size:11.5px;font-weight:600;color:#b45309;background:none;border:none">Copy</button>
              </div>
              <div style="background:#fffdf5;padding:10px 14px;font-family:var(--ar-font-mono);font-size:11.5px;line-height:1.7;color:#78350f;overflow-x:auto">
                @for (line of warnings(); track $index) {
                  <div style="white-space:pre">{{ line }}</div>
                } @empty {
                  <div style="color:#a16207">No warnings.</div>
                }
              </div>
            </div>
          }
        </div>
      </div>

      <!-- Cost confirmation modal (renders only while a live CostEstimate is captured) -->
      @if (cost(); as c) {
        <div style="position:fixed;inset:0;background:rgba(9,9,11,.4);display:flex;align-items:center;justify-content:center;z-index:1000;padding:20px">
          <div data-testid="cost-modal" style="width:480px;max-width:100%;background:#fff;border:1px solid #e4e4e7;border-radius:16px;box-shadow:0 20px 50px rgba(9,9,11,.22);overflow:hidden">
            <div style="padding:22px 24px">
              <div style="display:flex;align-items:center;gap:11px">
                <div style="width:36px;height:36px;border-radius:10px;background:#fffbeb;color:#b45309;display:flex;align-items:center;justify-content:center"><i class="ki-filled ki-dollar" style="font-size:17px"></i></div>
                <div style="font-size:15.5px;font-weight:700;color:#18181b">Confirm restore cost</div>
              </div>
              <div style="font-size:13px;color:#52525b;line-height:1.6;margin-top:12px">
                {{ formatCount(c.chunksNeedingRehydration) }} of {{ formatCount(c.chunksAvailable + c.chunksNeedingRehydration) }} chunks ({{ formatBytes(c.bytesNeedingRehydration) }}) are in Archive tier and need rehydration before download.
              </div>
              <div style="margin-top:14px;border:1px solid #f0f0f2;border-radius:11px;overflow:hidden">
                <div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px">
                  <span style="font-size:12.5px;color:#52525b">Download size</span>
                  <span style="font-size:12.5px;color:#27272a">{{ formatBytes(c.downloadBytes) }}</span>
                </div>
                <div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px;border-top:1px solid #f0f0f2">
                  <span style="font-size:12.5px;color:#52525b">In Archive tier</span>
                  <span style="font-size:12.5px;color:#27272a">{{ formatBytes(c.bytesNeedingRehydration) }}</span>
                </div>
                <div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px;border-top:1px solid #f0f0f2;background:#fafafb">
                  <span style="font-size:12.5px;color:#18181b;font-weight:700">Total excl. rehydration</span>
                  <span style="font-size:12.5px;color:#18181b;font-weight:700">{{ formatCurrency(priority() === 'high' ? c.totalHigh : c.totalStandard) }}</span>
                </div>
              </div>
              <div style="display:flex;gap:10px;margin-top:14px">
                <button data-testid="prio-standard" type="button" (click)="priority.set('standard')"
                        [attr.aria-pressed]="priority() === 'standard'"
                        style="flex:1;border-radius:10px;padding:11px;text-align:left;background:#fff;cursor:pointer"
                        [style.border]="priority() === 'standard' ? '2px solid #7c3aed' : '1px solid #e4e4e7'"
                        [style.background]="priority() === 'standard' ? '#f5f3ff' : '#fff'">
                  <div style="font-size:12.5px;font-weight:700" [style.color]="priority() === 'standard' ? '#6d28d9' : '#27272a'">Standard &middot; {{ formatCurrency(c.totalStandard) }}</div>
                  <div style="font-size:11.5px;margin-top:2px" [style.color]="priority() === 'standard' ? '#7c3aed' : '#a1a1aa'">up to {{ c.standardWaitHours }} h wait</div>
                </button>
                <button data-testid="prio-high" type="button" (click)="priority.set('high')"
                        [attr.aria-pressed]="priority() === 'high'"
                        style="flex:1;border-radius:10px;padding:11px;text-align:left;cursor:pointer"
                        [style.border]="priority() === 'high' ? '2px solid #7c3aed' : '1px solid #e4e4e7'"
                        [style.background]="priority() === 'high' ? '#f5f3ff' : '#fff'">
                  <div style="font-size:12.5px;font-weight:700" [style.color]="priority() === 'high' ? '#6d28d9' : '#27272a'">High priority &middot; {{ formatCurrency(c.totalHigh) }}</div>
                  <div style="font-size:11.5px;margin-top:2px" [style.color]="priority() === 'high' ? '#7c3aed' : '#a1a1aa'">up to {{ c.highWaitHours }} h</div>
                </button>
              </div>
            </div>
            <div style="display:flex;align-items:center;justify-content:flex-end;gap:10px;padding:14px 24px;border-top:1px solid #f0f0f2;background:#fafafb">
              <button data-testid="cost-decline" type="button" (click)="decline()" style="height:38px;padding:0 14px;border-radius:9px;font-size:13px;font-weight:600;color:#71717a;background:none;border:none;cursor:pointer">Cancel restore</button>
              <button data-testid="cost-approve" type="button" (click)="approve()" style="height:38px;padding:0 16px;border-radius:9px;font-size:13px;font-weight:600;background:#7c3aed;color:#fff;border:none;cursor:pointer">Rehydrate &amp; restore</button>
            </div>
          </div>
        </div>
      }
    } @else {
      <div style="padding:40px;text-align:center;color:#a1a1aa;font-size:13px">Loading job…</div>
    }
  `,
  styles: [`
    :host { display:block; }
    @keyframes ar-pulse { 50% { opacity:.45 } }
  `],
})
export class JobDetailComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  readonly id = input.required<string>();   // route param (withComponentInputBinding — verified enabled)

  protected readonly detail = signal<JobDetailDto | null>(null);
  protected readonly snap = signal<JobSnapshot | null>(null);
  protected readonly status = signal<string>('running');
  protected readonly cost = signal<CostEstimateMsg | null>(null);
  protected readonly warningsOpen = signal(false);
  protected readonly warnings = signal<string[]>([]);
  protected readonly priority = signal<'standard' | 'high'>('standard');
  protected readonly autoResume = signal(true);
  protected readonly resume = signal<ResumeInfo | null>(null);
  private subs: Subscription[] = [];
  private currentId = '';

  protected readonly kind = computed(() => this.detail()?.kind === 'restore' ? 'restore' : 'archive');
  protected readonly kindLabel = computed(() => this.kind() === 'restore' ? 'Restore' : 'Archive');
  protected readonly accent = computed(() => this.kind() === 'restore' ? '#6d28d9' : '#3b82f6');
  protected readonly meta = computed(() => statusMeta(this.status()));
  protected readonly outcome = computed<JobOutcome | null>(() => {
    const o = this.detail()?.outcome;
    if (!o) return null;
    try { return JSON.parse(o) as JobOutcome; } catch { return null; }
  });

  // layered-bar percentages
  private layers = computed(() => { const s = this.snap(); if (!s) return { scanned: this.kind() === 'restore' ? 100 : 0, middle: 0, top: 0 };
    return this.kind() === 'restore' ? restoreBarLayers(s) : archiveBarLayers(s); });
  protected readonly scannedPct = computed(() => this.layers().scanned);
  protected readonly middlePct  = computed(() => this.layers().middle);
  protected readonly topPct     = computed(() => this.layers().top);

  // legend / tile derivations — planned uses the authoritative chunk total (includes needs-rehydration)
  protected readonly plannedChunks = computed(() => this.snap()?.chunksTotal ?? 0);
  protected readonly readyChunks   = computed(() => { const s = this.snap(); return s ? s.chunksAvailable + s.chunksRehydrated : 0; });

  // header ETA / meta
  protected readonly elapsedSeconds = computed<number | null>(() => {
    const d = this.detail(); if (!d?.startedAt) return null;
    this.snap();   // recompute on each progress push for a live-ish elapsed
    const end = d.finishedAt ? new Date(d.finishedAt) : new Date();
    return Math.max(0, (end.getTime() - new Date(d.startedAt).getTime()) / 1000);
  });
  protected readonly finishTime = computed(() => {
    const eta = this.snap()?.etaSeconds;
    if (eta == null) return 'estimating…';
    return new Date(Date.now() + eta * 1000).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  });
  protected readonly rehydrateWindowHours = computed<number | null>(() =>
    resolveRehydrationWindowHours(this.cost(), this.resume(), this.priority()));
  protected readonly hydratedBy = computed(() => {
    const w = this.rehydrateWindowHours();
    return w != null ? hydratedByLabel(this.detail()?.startedAt ?? null, w) : '';
  });
  protected readonly bigEta = computed(() => {
    if (this.status() === 'rehydrating') return this.hydratedBy() || 'Waiting on Azure';
    return formatEta(this.snap()?.etaSeconds);
  });
  protected readonly subEta = computed(() => {
    if (this.status() === 'rehydrating') return 'Status checked periodically';
    const tp = this.snap()?.throughputBytesPerSec ?? 0;
    return tp > 0 ? formatThroughput(tp) + ' sustained' : '';
  });

  // footer warnings
  protected readonly warningCount = computed(() => this.snap()?.warningCount ?? this.detail()?.warningCount ?? 0);
  protected readonly warningsLabel = computed(() => { const n = this.warningCount(); return n === 1 ? 'There is 1 warning' : `There are ${n} warnings`; });

  // stage summary rows (derived from the live snapshot + status)
  protected readonly stages = computed<Stage[]>(() => this.kind() === 'restore' ? this.restoreStages() : this.archiveStages());

  private archiveStages(): Stage[] {
    const s = this.snap();
    const done = this.status() === 'completed';
    if (!s) return [
      { label: 'Scan', sub: 'walking the source tree', state: 'running' },
      { label: 'Hash & route', sub: 'deduplicating against the repository', state: 'pending' },
      { label: 'Upload', sub: 'new chunks to Azure', state: 'pending' },
      { label: 'Snapshot', sub: 'chunk index + filetree + snapshot', state: 'pending' },
    ];
    const scanDone = s.totalBytes > 0 && s.scannedBytes >= s.totalBytes;
    const hashDone = s.totalBytes > 0 && s.hashedBytes >= s.totalBytes;
    const uploadDone = s.totalNewBytes > 0 && s.uploadedBytes >= s.totalNewBytes;
    const pick = (d: boolean, r: boolean): Stage['state'] => done || d ? 'done' : r ? 'running' : 'pending';
    return [
      { label: 'Scan', sub: `${formatBytes(s.scannedBytes)} scanned`, state: pick(scanDone, true) },
      { label: 'Hash & route', sub: `${formatBytes(s.hashedBytes)} hashed · ${formatCount(s.dedupedFiles)} deduped`, state: pick(hashDone, scanDone || s.hashedBytes > 0) },
      { label: 'Upload', sub: `${formatBytes(s.uploadedBytes)} of ${formatBytes(s.totalNewBytes)}`, state: pick(uploadDone, hashDone || s.uploadedBytes > 0) },
      { label: 'Snapshot', sub: 'chunk index + filetree + snapshot', state: done ? 'done' : uploadDone ? 'running' : 'pending' },
    ];
  }

  private restoreStages(): Stage[] {
    const s = this.snap();
    const st = this.status();
    const done = st === 'completed';
    if (!s) return [
      { label: 'Plan', sub: 'resolving the snapshot to chunks', state: 'running' },
      { label: 'Confirm cost', sub: 'rehydration estimate', state: 'pending' },
      { label: 'Rehydrate', sub: 'moving chunks out of Archive tier', state: 'pending' },
      { label: 'Download', sub: 'chunks to disk', state: 'pending' },
      { label: 'Verify', sub: 'hash check + pointer cleanup', state: 'pending' },
    ];
    const total = this.plannedChunks();
    const planDone = s.restoreTotalBytes > 0 || total > 0;
    const costDone = st === 'rehydrating' || st === 'running' || done;
    const rehydrateDone = total > 0 && s.chunksPending === 0;
    const downloadDone = s.restoreTotalBytes > 0 && s.bytesRestored >= s.restoreTotalBytes;
    return [
      { label: 'Plan', sub: `${formatCount(total)} chunks · ${formatBytes(s.restoreTotalBytes)}`, state: done || planDone ? 'done' : 'running' },
      { label: 'Confirm cost', sub: st === 'awaiting-cost' ? 'waiting for approval' : `${formatCount(s.chunksNeedingRehydration)} chunks need rehydration`, state: st === 'awaiting-cost' ? 'running' : costDone ? 'done' : 'pending' },
      { label: 'Rehydrate', sub: `${formatCount(this.readyChunks())} ready · ${formatCount(s.chunksPending)} pending`, state: done || rehydrateDone ? 'done' : (st === 'rehydrating' || s.chunksPending > 0 ? 'running' : 'pending') },
      { label: 'Download', sub: `${formatBytes(s.bytesRestored)} of ${formatBytes(s.restoreTotalBytes)}`, state: done || downloadDone ? 'done' : (s.bytesRestored > 0 ? 'running' : 'pending') },
      { label: 'Verify', sub: 'hash check + pointer cleanup', state: done ? 'done' : 'pending' },
    ];
  }

  constructor() {
    // React to the route id: load the persisted row + attach for live state.
    effect(() => this.attach(this.id()));
  }

  attach(id: string): void {
    if (id === this.currentId) return;
    this.teardown(); this.currentId = id;
    this.api.getJob(id).subscribe(d => {
      if (this.currentId !== id) return;   // a newer attach won the race
      this.detail.set(d); this.status.set(d.status); if (d.snapshot) this.snap.set(d.snapshot);
      if (d.cost) this.cost.set(d.cost);
      if (d.resume) { this.resume.set(d.resume); this.autoResume.set(d.resume.autoResume); }
    });
    void this.realtime.attachToJob(id).then(st => { if (st && this.currentId === id) {
      this.snap.set(st.snapshot); this.status.set(st.status);
      if (st.cost) this.cost.set(st.cost);
      if (st.resume) { this.resume.set(st.resume); this.autoResume.set(st.resume.autoResume); }
    } });
    this.subs.push(this.realtime.jobProgress(id).subscribe(s => this.snap.set(s)));
    this.subs.push(this.realtime.jobCost(id).subscribe(c => this.cost.set(c)));
    this.subs.push(this.realtime.jobDone(id).subscribe(d => { this.status.set(d.status); this.cost.set(null); this.api.getJob(id).subscribe(x => this.detail.set(x)); }));
  }

  protected phaseSentence = phaseSentence;
  protected formatBytes = formatBytes; protected formatCount = formatCount; protected formatCurrency = formatCurrency;
  protected formatEta = formatEta; protected formatDuration = formatDuration;
  protected formatThroughput = formatThroughput; protected hydratedByLabel = hydratedByLabel; protected isNonTerminal = isNonTerminal;
  protected round = Math.round;

  protected toggleWarnings(): void {
    const open = !this.warningsOpen(); this.warningsOpen.set(open);
    if (open) this.api.getJobWarnings(this.currentId).subscribe(w => this.warnings.set(w.lines));
  }
  protected copyWarnings(): void { void navigator.clipboard?.writeText(this.warnings().join('\n')); }
  protected cancel(): void {
    if (this.status() === 'rehydrating' && !confirm('Rehydration is already paid — cancelling does not refund it. Cancel anyway?')) return;
    void this.realtime.cancelJob(this.currentId);
  }
  protected approve(): void { void this.realtime.approveRestore(this.currentId, this.priority()); this.cost.set(null); }
  protected decline(): void { void this.realtime.declineRestore(this.currentId); this.cost.set(null); }
  protected toggleAutoResume(): void { const on = !this.autoResume(); this.autoResume.set(on); void this.realtime.setAutoResume(this.currentId, on); }
  protected resumeNow(): void { void this.realtime.resumeRestore(this.currentId); }

  ngOnDestroy(): void { if (this.currentId) void this.realtime.detachFromJob(this.currentId); this.teardown(); }
  private teardown(): void { this.subs.forEach(s => s.unsubscribe()); this.subs = []; }
}
