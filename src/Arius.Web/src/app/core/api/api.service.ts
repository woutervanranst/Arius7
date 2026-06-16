import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AccountDto, RepositoryDto, SnapshotDto, StatsDto } from './api-models';

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
}
