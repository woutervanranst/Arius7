import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, Observable } from 'rxjs';
import { AccountDto, CostSettingsDto, CreateRepositoryRequest, DownloadSettingsDto, DownloadStateDto, DownloadUsageDto, FsListDto, GlobalDefaultsDto, JobDetailDto, JobDto, JobWarningsDto, PendingDownloadDto, RepositoryDefaultsDto, RepositoryDto, ScheduleDto, SnapshotDto, StatisticsDto } from './api-models';
import { SetValues } from '../defaults/defaults';

/** Link target for fetching a download job's staged files; used as a plain link, never fetched by JS. */
export function downloadContentUrl(jobId: string, set: 'all' | 'remaining'): string {
  return `/api/jobs/${jobId}/download/content?set=${set}`;
}

/** Typed REST client for Arius.Api. Entry streaming lives in RealtimeService (SignalR). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  listAccounts(): Observable<AccountDto[]> {
    return this.http.get<AccountDto[]>('/api/accounts');
  }

  getAccount(id: number): Observable<AccountDto> {
    return this.http.get<AccountDto>(`/api/accounts/${id}`);
  }

  createAccount(name: string, accountKey: string | null): Observable<AccountDto> {
    return this.http.post<AccountDto>('/api/accounts', { name, accountKey });
  }

  /** Edit-account flyout: rotate the key (omit/null to keep the stored one). */
  updateAccount(id: number, body: { accountKey?: string | null }): Observable<AccountDto> {
    return this.http.patch<AccountDto>(`/api/accounts/${id}`, body);
  }

  deleteAccount(id: number): Observable<void> {
    return this.http.delete<void>(`/api/accounts/${id}`);
  }

  /** Lists directories as the Arius.Api host/container sees them (server-side local-path picker). */
  listDirectories(path?: string | null): Observable<FsListDto> {
    const query = path ? `?path=${encodeURIComponent(path)}` : '';
    return this.http.get<FsListDto>(`/api/fs/list${query}`);
  }

  listRepositories(): Observable<RepositoryDto[]> {
    return this.http.get<RepositoryDto[]>('/api/repos');
  }

  getRepository(id: number): Observable<RepositoryDto> {
    return this.http.get<RepositoryDto>(`/api/repos/${id}`);
  }

  patchRepository(id: number, body: Partial<{ alias: string; localPath: string; passphrase: string }>): Observable<RepositoryDto> {
    return this.http.patch<RepositoryDto>(`/api/repos/${id}`, body);
  }

  /** Removes the repository from Arius's registry. The Azure container and its blobs are left intact. */
  deleteRepository(id: number): Observable<void> {
    return this.http.delete<void>(`/api/repos/${id}`);
  }

  getSnapshots(id: number): Observable<SnapshotDto[]> {
    return this.http.get<SnapshotDto[]>(`/api/repos/${id}/snapshots`);
  }

  // `full` loads the whole chunk index server-side so the repository-wide storage figures are complete
  // (slower) rather than reflecting only browsed coverage; the Statistics screen lazy-loads with full=true.
  getStatistics(id: number, version?: string | null, full = false): Observable<StatisticsDto> {
    const params = new URLSearchParams();
    if (version) params.set('version', version);
    if (full) params.set('full', 'true');
    const query = params.toString();
    return this.http.get<StatisticsDto>(`/api/repos/${id}/stats${query ? `?${query}` : ''}`);
  }

  createRepository(req: CreateRepositoryRequest): Observable<RepositoryDto> {
    return this.http.post<RepositoryDto>('/api/repos', req);
  }

  /** Jobs list, optionally filtered by repository and/or status ('active' = the non-terminal set). */
  getJobs(opts?: { repositoryId?: number; status?: 'active' | 'terminal' | string }): Observable<JobDto[]> {
    const params = new URLSearchParams();
    if (opts?.repositoryId != null) params.set('repositoryId', String(opts.repositoryId));
    if (opts?.status) params.set('status', opts.status);
    const query = params.toString();
    return this.http.get<JobDto[]>(`/api/jobs${query ? `?${query}` : ''}`);
  }

  getJob(id: string): Observable<JobDetailDto> {
    return this.http.get<JobDetailDto>(`/api/jobs/${id}`);
  }

  getJobWarnings(id: string): Observable<JobWarningsDto> {
    return this.http.get<JobWarningsDto>(`/api/jobs/${id}/warnings`);
  }

  getDownload(jobId: string): Observable<DownloadStateDto> {
    return this.http.get<DownloadStateDto>(`/api/jobs/${jobId}/download`);
  }

  cleanUpDownload(jobId: string): Observable<void> {
    return this.http.post<void>(`/api/jobs/${jobId}/download/cleanup`, null);
  }

  /** The repository's download job with files still to fetch; the server answers 204 (no body) when there is none. */
  getPendingDownload(repoId: number): Observable<PendingDownloadDto | null> {
    return this.http.get<PendingDownloadDto | null>(`/api/repos/${repoId}/downloads/pending`);
  }

  getDownloadSettings(): Observable<DownloadSettingsDto> {
    return this.http.get<DownloadSettingsDto>('/api/settings/downloads');
  }

  putDownloadSettings(settings: DownloadSettingsDto): Observable<DownloadSettingsDto> {
    return this.http.put<DownloadSettingsDto>('/api/settings/downloads', settings);
  }

  getCostSettings(): Observable<CostSettingsDto> {
    return this.http.get<CostSettingsDto>('/api/settings/cost');
  }

  putCostSettings(settings: CostSettingsDto): Observable<CostSettingsDto> {
    return this.http.put<CostSettingsDto>('/api/settings/cost', settings);
  }

  getDownloadUsage(): Observable<DownloadUsageDto> {
    return this.http.get<DownloadUsageDto>('/api/downloads/usage');
  }

  cleanUpAllDownloads(): Observable<DownloadUsageDto> {
    return this.http.post<DownloadUsageDto>('/api/downloads/cleanup', null);
  }

  getSchedules(repoId: number): Observable<ScheduleDto[]> {
    return this.http.get<ScheduleDto[]>(`/api/repos/${repoId}/schedules`);
  }

  createSchedule(repoId: number, cron: string, kind = 'archive'): Observable<ScheduleDto> {
    return this.http.post<ScheduleDto>(`/api/repos/${repoId}/schedules`, { cron, kind });
  }

  deleteSchedule(repoId: number, scheduleId: number): Observable<void> {
    return this.http.delete<void>(`/api/repos/${repoId}/schedules/${scheduleId}`);
  }

  getGlobalDefaults(): Observable<GlobalDefaultsDto> {
    return this.http.get<GlobalDefaultsDto>('/api/settings/defaults');
  }

  /** Body is the set-values object: absent/null inherits. The server rejects unknown keys, so send only option keys. */
  putGlobalDefaults(set: SetValues): Observable<GlobalDefaultsDto> {
    return this.http.put<GlobalDefaultsDto>('/api/settings/defaults', set);
  }

  getRepositoryDefaults(repoId: number): Observable<RepositoryDefaultsDto> {
    return this.http.get<RepositoryDefaultsDto>(`/api/repos/${repoId}/defaults`);
  }

  putRepositoryDefaults(repoId: number, set: SetValues): Observable<RepositoryDefaultsDto> {
    return this.http.put<RepositoryDefaultsDto>(`/api/repos/${repoId}/defaults`, set);
  }

  /** The running backend's build version, read from the X-Arius-Version response header. */
  getAppVersion(): Observable<string | null> {
    return this.http.get('/api/health', { observe: 'response' }).pipe(
      map(res => res.headers.get('X-Arius-Version')),
    );
  }
}
