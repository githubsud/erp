import { HttpBackend, HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

interface RuntimeConfig {
  /** Base URL of the ERP API. Empty = same origin (dev proxy or reverse proxy). */
  apiBaseUrl: string;
}

/**
 * Loads /config.json at start-up so one static build can be deployed to any environment
 * (e.g. Cloudflare Pages) and pointed at its API without rebuilding.
 */
@Injectable({ providedIn: 'root' })
export class ConfigService {
  // HttpBackend bypasses interceptors (no token exists yet).
  private readonly http = new HttpClient(inject(HttpBackend));
  private config: RuntimeConfig = { apiBaseUrl: '' };

  get apiBaseUrl(): string {
    return this.config.apiBaseUrl.replace(/\/$/, '');
  }

  async load(): Promise<void> {
    try {
      this.config = { ...this.config, ...(await firstValueFrom(this.http.get<RuntimeConfig>('config.json'))) };
    } catch {
      /* no config.json: same-origin API */
    }
  }
}
