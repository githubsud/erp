import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from './api.service';

export interface ModuleInfo {
  code: string;
  nameEn: string;
  nameAr: string;
  enabled: boolean;
  requires: string[];
  licensed?: boolean;
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
    if (starts('/ai') || link === '/settings/ai') return 'ai';
    if (link.startsWith('/sales')) return 'sales';
    if (link.startsWith('/purchasing')) return 'purchasing';
    if (link.startsWith('/inventory') || link === '/settings/trade') return 'inventory';
    if (['/employees', '/payroll', '/payroll-inputs', '/attendance', '/leave', '/documents', '/settlements', '/gratuity', '/hr-reports', '/settings/payroll',
      '/rosters', '/field-staff'].some(starts)) return 'hr';
    return null;
  }
}
