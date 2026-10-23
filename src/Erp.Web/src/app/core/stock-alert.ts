import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { I18nService } from './i18n/i18n.service';
import { StockRow } from './trade.models';

/** 'out': not enough for this sale (or none left); 'low': what is left is at or below the reorder level; 'ok' otherwise. */
export type StockLevel = 'out' | 'low' | 'ok';

export function stockLevel(onHand: number, reorderLevel: number | null | undefined, take = 0): StockLevel {
  const left = onHand - take;
  if (left < 0 || onHand <= 0) return 'out';
  if (reorderLevel != null && left <= reorderLevel) return 'low';
  return 'ok';
}

export interface LowItem {
  nameEn: string;
  nameAr: string;
  left: number;
}

/** After a sale: names the sold items that are now at or below their reorder level, with a way to the reorder suggestions. */
@Injectable({ providedIn: 'root' })
export class LowStockNotice {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);

  /** Asks the server (company-wide stock) which of these items are now low. Quietly does nothing without the permission. */
  afterSale(itemIds: (string | null | undefined)[]): void {
    const ids = new Set(itemIds.filter((id): id is string => !!id));
    if (ids.size === 0 || !this.auth.can('inv.items:read')) return;
    this.api.get<StockRow[]>('inventory/stock', { belowReorder: true }).subscribe({
      next: (rows) => this.show(rows.filter((r) => ids.has(r.itemId)).map((r) => ({ nameEn: r.nameEn, nameAr: r.nameAr, left: r.quantity }))),
      error: () => undefined
    });
  }

  show(items: LowItem[]): void {
    if (items.length === 0) return;
    const canReorder = this.canReorder();
    const ref = this.snack.open(this.text(items), this.i18n.t(canReorder ? 'stock.reorderNow' : 'app.close'), { duration: 10000, panelClass: 'snack-warn' });
    if (canReorder) ref.onAction().subscribe(() => this.openReorder());
  }

  /** "Running low: Brake pads (2), Belt (1). Reorder now?" — or "Tell the manager." for someone who cannot reorder. */
  text(items: LowItem[]): string {
    const names = items.map((i) => `${this.i18n.isArabic() ? i.nameAr : i.nameEn} (${i.left})`).join(this.i18n.isArabic() ? '، ' : ', ');
    return this.i18n.t(this.canReorder() ? 'stock.lowAfterSale' : 'stock.lowAfterSaleTell', { items: names });
  }

  canReorder(): boolean {
    return this.auth.can('pur.suppliers:read');
  }

  openReorder(): void {
    void this.router.navigate(['/purchasing/reorder']);
  }
}
