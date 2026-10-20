import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateChildFn, Router } from '@angular/router';
import { Observable, catchError, map, of, shareReplay, tap } from 'rxjs';
import { ApiService } from './api.service';

export interface ModuleInfo {
  code: string;
  nameEn: string;
  nameAr: string;
  enabled: boolean;
  requires: string[];
  licensed?: boolean;
}

const CACHE_KEY = 'erp.modules';

/** Which optional modules are switched on for the company; the menu hides the others (the API refuses them too). */
@Injectable({ providedIn: 'root' })
export class ModulesService {
  private readonly api = inject(ApiService);
  private readonly modules = signal<ModuleInfo[]>([]);
  readonly disabled = computed(() => new Set(this.modules().filter((m) => !m.enabled).map((m) => m.code)));

  private loading: Observable<void> | null = null;

  load(): void {
    this.loading = null;
    this.ensureLoaded().subscribe();
  }

  /** Loads the switches once (shared by the menu and the route guard). */
  ensureLoaded(): Observable<void> {
    this.loading ??= this.api.get<ModuleInfo[]>('core/modules').pipe(
      tap((m) => {
        this.modules.set(m);
        try {
          localStorage.setItem(CACHE_KEY, JSON.stringify(m));
        } catch {
          /* storage blocked */
        }
      }),
      map(() => undefined),
      // Offline: the switches kept from last time, so the menu stays the same.
      catchError(() => {
        try {
          this.modules.set(JSON.parse(localStorage.getItem(CACHE_KEY) ?? '[]') as ModuleInfo[]);
        } catch {
          /* nothing kept */
        }
        return of(undefined);
      }),
      shareReplay(1)
    );
    return this.loading;
  }

  set(list: ModuleInfo[]): void {
    this.modules.set(list);
  }

  /** The module a screen belongs to, from its link (most specific first). */
  static moduleOf(link: string): string | null {
    const starts = (p: string) => link === p || link.startsWith(p + '/');
    if (starts('/sales/pos')) return 'pos';
    if (starts('/sales/crm')) return 'crm';
    if (starts('/sales/projects')) return 'projects';
    if (starts('/purchasing/subcontracts')) return 'subcontracts';
    if (starts('/finance/assets')) return 'assets';
    if (starts('/finance/budgets')) return 'budgets';
    if (starts('/tailoring')) return 'tailoring';
    if (starts('/salon')) return 'salon';
    if (starts('/workshop')) return 'workshop';
    if (starts('/ai') || link === '/settings/ai') return 'ai';
    if (link.startsWith('/sales')) return 'sales';
    if (link.startsWith('/purchasing')) return 'purchasing';
    if (link.startsWith('/inventory') || link === '/settings/trade') return 'inventory';
    if (['/employees', '/payroll', '/payroll-inputs', '/attendance', '/leave', '/documents', '/settlements', '/gratuity', '/hr-reports', '/settings/payroll',
      '/rosters', '/field-staff'].some(starts)) return 'hr';
    return null;
  }
}

/** Screens of a module that is off (not in the plan, or switched off) are not opened from a bookmark or typed address: back to the dashboard. */
export const moduleGuard: CanActivateChildFn = (_route, state) => {
  const modules = inject(ModulesService);
  const router = inject(Router);
  return modules.ensureLoaded().pipe(
    map(() => {
      const module = ModulesService.moduleOf(state.url.split('?')[0]);
      return module && modules.disabled().has(module) ? router.createUrlTree(['/dashboard'], { queryParams: { off: module } }) : true;
    })
  );
};
