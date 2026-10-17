import { HttpClient, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, tap, throwError, catchError } from 'rxjs';
import { ConfigService } from './config.service';
import { I18nService } from './i18n/i18n.service';
import { LoginResponse } from './models';

const STORAGE_KEY = 'erp.session';

/**
 * Session held in sessionStorage (cleared when the browser tab closes). Tokens are short-lived (60 min);
 * refresh tokens and MFA are on the roadmap.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);

  readonly session = signal<LoginResponse | null>(this.restore());
  readonly user = computed(() => this.session()?.user ?? null);
  readonly isAuthenticated = computed(() => {
    const s = this.session();
    return !!s && new Date(s.expiresAt).getTime() > Date.now();
  });
  private readonly permissions = computed(() => new Set(this.session()?.permissions ?? []));

  get token(): string | null {
    return this.session()?.accessToken ?? null;
  }

  login(tenantCode: string, email: string, password: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${this.config.apiBaseUrl}/api/auth/login`, { tenantCode, email, password }).pipe(
      tap((response) => {
        this.session.set(response);
        try {
          sessionStorage.setItem(STORAGE_KEY, JSON.stringify(response));
        } catch {
          /* ignore */
        }
        this.i18n.set(response.user.preferredLanguage === 'en' ? 'en' : 'ar');
      })
    );
  }

  logout(): void {
    this.session.set(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      /* ignore */
    }
    void this.router.navigate(['/login']);
  }

  /** Owner role holds the "*" wildcard. */
  can(permission: string): boolean {
    const set = this.permissions();
    return set.has('*') || set.has(permission);
  }

  private restore(): LoginResponse | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const session = JSON.parse(raw) as LoginResponse;
      return new Date(session.expiresAt).getTime() > Date.now() ? session : null;
    } catch {
      return null;
    }
  }
}

/** Adds the bearer token and preferred language; an expired session sends the user back to sign in. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const i18n = inject(I18nService);
  const headers: Record<string, string> = { 'Accept-Language': i18n.lang() };
  const token = auth.token;
  if (token) headers['Authorization'] = `Bearer ${token}`;
  return next(req.clone({ setHeaders: headers })).pipe(
    catchError((error) => {
      if (error?.status === 401 && token) auth.logout();
      return throwError(() => error);
    })
  );
};

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? true : inject(Router).createUrlTree(['/login']);
};

export function permissionGuard(permission: string): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    return auth.can(permission) ? true : inject(Router).createUrlTree(['/']);
  };
}
