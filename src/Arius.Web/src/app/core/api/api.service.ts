import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AccountDto, CreateRepositoryRequest, JobDto, RepositoryDto, ScheduleDto, SnapshotDto, StatsDto } from './api-models';

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

  getSnapshots(id: number): Observable<SnapshotDto[]> {
    return this.http.get<SnapshotDto[]>(`/api/repos/${id}/snapshots`);
  }

  getStats(id: number, version?: string | null): Observable<StatsDto> {
    const query = version ? `?version=${encodeURIComponent(version)}` : '';
    return this.http.get<StatsDto>(`/api/repos/${id}/stats${query}`);
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
}
