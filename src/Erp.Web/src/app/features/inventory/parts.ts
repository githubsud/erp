import { Component, effect, inject, input, signal } from '@angular/core';
import { FormArray, FormBuilder, Validators } from '@angular/forms';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { yearsText } from '../../core/part-search';
import { TradeApi } from '../../core/trade.api';
import { ItemParts } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

/** A spare part's other numbers (OEM and replacement brands) and the cars it fits; the item search finds the part by any of them. */
@Component({
  selector: 'app-item-parts',
  imports: [...SHARED],
  template: `
    <p class="secondary">{{ 'parts.hint' | t }}</p>
    @if (canManage) {
      <div class="sub">{{ 'parts.numbers' | t }}</div>
      <table class="line-editor">
        <tbody>
          @for (l of numbers.controls; track l; let i = $index) {
            <tr [formGroup]="l">
              <td><mat-form-field><mat-label>{{ 'parts.partNo' | t }}</mat-label><input matInput formControlName="partNo" dir="ltr" autocomplete="off" /></mat-form-field></td>
              <td><mat-form-field><mat-label>{{ 'item.brand' | t }}</mat-label><input matInput formControlName="brand" dir="ltr" /></mat-form-field></td>
              <td><button mat-icon-button (click)="numbers.removeAt(i)"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
        </tbody>
      </table>
      <button mat-button (click)="addNumber()"><mat-icon>add</mat-icon> {{ 'parts.addNumber' | t }}</button>

      <div class="sub">{{ 'parts.cars' | t }}</div>
      <table class="line-editor">
        <tbody>
          @for (l of fits.controls; track l; let i = $index) {
            <tr [formGroup]="l" class="car">
              <td><mat-form-field><mat-label>{{ 'parts.make' | t }}</mat-label><input matInput formControlName="make" dir="ltr" /></mat-form-field></td>
              <td><mat-form-field><mat-label>{{ 'parts.model' | t }}</mat-label><input matInput formControlName="model" dir="ltr" /></mat-form-field></td>
              <td class="year"><mat-form-field><mat-label>{{ 'parts.yearFrom' | t }}</mat-label><input matInput type="number" formControlName="yearFrom" dir="ltr" /></mat-form-field></td>
              <td class="year"><mat-form-field><mat-label>{{ 'parts.yearTo' | t }}</mat-label><input matInput type="number" formControlName="yearTo" dir="ltr" /></mat-form-field></td>
              <td><mat-form-field><mat-label>{{ 'parts.engine' | t }}</mat-label><input matInput formControlName="engine" dir="ltr" /></mat-form-field></td>
              <td><button mat-icon-button (click)="fits.removeAt(i)"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
        </tbody>
      </table>
      <div class="row">
        <button mat-button (click)="addFit()"><mat-icon>add</mat-icon> {{ 'parts.addCar' | t }}</button>
        <button mat-stroked-button [disabled]="numbers.invalid || fits.invalid" (click)="save()">{{ 'app.save' | t }}</button>
      </div>
    } @else {
      @for (n of saved().partNumbers; track n.id) { <span class="chip ltr">{{ n.partNo }}@if (n.brand) { · {{ n.brand }} }</span> }
      <ul>
        @for (f of saved().fitments; track f.id) { <li class="ltr">{{ f.make }} {{ f.model }} {{ years(f) }} @if (f.engine) { · {{ f.engine }} }</li> }
      </ul>
    }
  `,
  styles: `
    .sub { font: var(--mat-sys-title-small); margin-top: 12px; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .year { width: 96px; }
    .chip { margin-inline-end: 6px; }
  `
})
export class ItemPartsCard {
  readonly itemId = input.required<string>();
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder);
  protected readonly canManage = inject(AuthService).can('inv.items:manage');
  protected readonly numbers = new FormArray<ReturnType<ItemPartsCard['number']>>([]);
  protected readonly fits = new FormArray<ReturnType<ItemPartsCard['fit']>>([]);
  /** As saved (a signal, so the lists redraw when the server answers). */
  protected readonly saved = signal<ItemParts>({ partNumbers: [], fitments: [] });
  protected readonly years = yearsText;

  constructor() {
    effect(() => this.trade.parts(this.itemId()).subscribe((p) => this.show(p)));
  }

  private number(partNo = '', brand: string | null = '') {
    return this.fb.nonNullable.group({ partNo: [partNo, Validators.required], brand: [brand ?? ''] });
  }

  private fit(f?: ItemParts['fitments'][number]) {
    const year = [Validators.min(1950), Validators.max(2100)];
    return this.fb.group({
      make: [f?.make ?? '', Validators.required],
      model: [f?.model ?? '', Validators.required],
      yearFrom: [f?.yearFrom ?? (null as number | null), year],
      yearTo: [f?.yearTo ?? (null as number | null), year],
      engine: [f?.engine ?? '']
    });
  }

  protected addNumber(): void {
    this.numbers.push(this.number());
  }

  /** A new car starts as a copy of the last one's make (most parts fit several models of one maker). */
  protected addFit(): void {
    const last = this.fits.at(this.fits.length - 1)?.getRawValue();
    this.fits.push(this.fit(last ? { id: '', make: last.make ?? '', model: '', yearFrom: null, yearTo: null, engine: null } : undefined));
  }

  private show(p: ItemParts): void {
    this.saved.set(p);
    this.numbers.clear();
    this.fits.clear();
    p.partNumbers.forEach((n) => this.numbers.push(this.number(n.partNo, n.brand)));
    p.fitments.forEach((f) => this.fits.push(this.fit(f)));
  }

  protected save(): void {
    const year = (x: number | null | undefined) => (x === null || x === undefined || (x as unknown) === '' ? null : Number(x));
    this.trade
      .saveParts(this.itemId(), {
        partNumbers: this.numbers.getRawValue().map((n) => ({ partNo: n.partNo.trim(), brand: n.brand.trim() || null })),
        fitments: this.fits.getRawValue().map((f) => ({
          make: (f.make ?? '').trim(), model: (f.model ?? '').trim(), yearFrom: year(f.yearFrom), yearTo: year(f.yearTo), engine: (f.engine ?? '').trim() || null
        }))
      })
      .subscribe({
        next: (p) => {
          this.show(p);
          this.notify.success();
        },
        error: (e) => this.notify.error(e)
      });
  }
}
