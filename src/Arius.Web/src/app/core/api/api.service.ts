import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AccountDto, AppInfoDto, CreateRepositoryRequest, JobDto, RepositoryDto, ScheduleDto, SnapshotDto, StatisticsDto } from './api-models';

/** Typed REST client for Arius.Api. Entry streaming lives in RealtimeService (SignalR). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  listAccounts(): Observable<AccountDto[]> {
    return this.http.get<AccountDto[]>('/api/accounts');
  }

  createAccount(name: string, accountKey: string | null): Observable<AccountDto> {
    return this.http.post<AccountDto>('/api/accounts', { name, accountKey });
  }

  listRepositories(): Observable<RepositoryDto[]> {
    return this.http.get<RepositoryDto[]>('/api/repos');
  }

  getRepository(id: number): Observable<RepositoryDto> {
    return this.http.get<RepositoryDto>(`/api/repos/${id}`);
  }

  patchRepository(id: number, body: Partial<{ alias: string; localPath: string; defaultTier: string; passphrase: string }>): Observable<RepositoryDto> {
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

  getJobs(): Observable<JobDto[]> {
    return this.http.get<JobDto[]>('/api/jobs');
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

  /** The running backend's build version (git tag of the deployed image). */
  getAppInfo(): Observable<AppInfoDto> {
    return this.http.get<AppInfoDto>('/api/info');
  }
}
