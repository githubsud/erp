import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from './api.service';

export interface ModuleInfo {
  code: string;
  nameEn: string;
  nameAr: string;
  enabled: boolean;
  requires: string[];
}

/** Which optional modules are switched on for the company; the menu hides the others (the API refuses them too). */
@Injectable({ providedIn: 'root' })
export class ModulesService {
  private readonly api = inject(ApiService);
  private readonly modules = signal<ModuleInfo[]>([]);
  readonly disabled = computed(() => new Set(this.modules().filter((m) => !m.enabled).map((m) => m.code)));

  load(): void {
    this.api.get<ModuleInfo[]>('core/modules').subscribe({ next: (m) => this.modules.set(m), error: () => undefined });
  }

  set(list: ModuleInfo[]): void {
    this.modules.set(list);
  }

  /** The module a screen belongs to, from its link. */
  static moduleOf(link: string): string | null {
    if (link.startsWith('/sales')) return 'sales';
    if (link.startsWith('/purchasing')) return 'purchasing';
    if (link.startsWith('/inventory') || link === '/settings/trade') return 'inventory';
    if (['/employees', '/payroll', '/payroll-inputs', '/attendance', '/leave', '/documents', '/settlements', '/gratuity', '/hr-reports', '/settings/payroll']
      .some((p) => link === p || link.startsWith(p + '/'))) return 'hr';
    return null;
  }
}
