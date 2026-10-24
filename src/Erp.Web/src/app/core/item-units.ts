import { Injectable, inject, signal } from '@angular/core';
import { TradeApi } from './trade.api';
import { ItemUnit } from './trade.models';

/** The units of the items on a document (piece, box of 12…), loaded once per item. Provide it in the page's component. */
@Injectable()
export class ItemUnitsCache {
  private readonly trade = inject(TradeApi);
  private readonly map = signal(new Map<string, ItemUnit[]>());

  of(itemId: string | null | undefined): ItemUnit[] {
    return itemId ? (this.map().get(itemId) ?? []) : [];
  }

  load(itemId: string | null | undefined): void {
    if (!itemId || this.map().has(itemId)) return;
    this.map.update((m) => new Map(m).set(itemId, []));
    this.trade.units(itemId).subscribe((u) => this.map.update((m) => new Map(m).set(itemId, u)));
  }

  /** Base units in one of `uomId` (1 for the base unit). */
  factor(itemId: string | null | undefined, uomId: string | null | undefined): number {
    return (uomId && this.of(itemId).find((u) => !u.isBase && u.uomId === uomId)?.factor) || 1;
  }
}

/** Stock in boxes and pieces: 40 pieces with a box of 12 is "3 BOX12 + 4 PCS" (the biggest unit that fits, then loose pieces). */
export function inBoxes(quantity: number, baseCode: string, units: { uomCode: string; factor: number; isBase?: boolean }[]): string {
  const box = units.filter((u) => !u.isBase && u.factor > 1 && quantity >= u.factor).sort((a, b) => b.factor - a.factor)[0];
  if (!box) return `${quantity} ${baseCode}`;
  const boxes = Math.floor(quantity / box.factor);
  const rest = Math.round((quantity - boxes * box.factor) * 1000000) / 1000000;
  return rest ? `${boxes} ${box.uomCode} + ${rest} ${baseCode}` : `${boxes} ${box.uomCode}`;
}

/** Pieces first, then the boxes they make: "30 PCS (3 BOX10)", "37 PCS (3 BOX10 + 7 PCS)"; just "7 PCS" when less than a box. */
export function withBoxes(quantity: number, baseCode: string, units: { uomCode: string; factor: number; isBase?: boolean }[]): string {
  const boxes = inBoxes(quantity, baseCode, units);
  const pieces = `${quantity} ${baseCode}`;
  return boxes === pieces ? pieces : `${pieces} (${boxes})`;
}
