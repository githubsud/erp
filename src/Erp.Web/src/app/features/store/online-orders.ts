import { Component, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { concatMap, firstValueFrom, map, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { CommerceApi, OnlineOrder } from '../../core/commerce.api';
import { todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { SHARED } from '../../shared/shared';

/**
 * The queue of orders from the online store (cash on delivery). Deliver = the usual delivery note for the goods and one invoice for the
 * goods and the delivery fee; Cash received = the usual customer receipt. Each step is an ordinary Sales document, so stock, cost and
 * the books follow the existing rules.
 */
@Component({
  selector: 'app-online-orders',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'store.orders' | t }}</h1>
      <span class="spacer"></span>
      <mat-chip-listbox [value]="stage()" (change)="setStage($event.value)" [attr.aria-label]="'store.orders' | t">
        <mat-chip-option value="open">{{ 'store.stage.open' | t }}</mat-chip-option>
        <mat-chip-option value="done">{{ 'store.stage.done' | t }}</mat-chip-option>
        <mat-chip-option value="all">{{ 'store.stage.all' | t }}</mat-chip-option>
      </mat-chip-listbox>
    </div>
    @if (canCollect && cashAccounts().length > 1) {
      <mat-form-field subscriptSizing="dynamic" class="cash">
        <mat-label>{{ 'store.cashTo' | t }}</mat-label>
        <mat-select [formControl]="cashAccount">
          @for (a of cashAccounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (!loading() && !orders().length) { <p class="secondary">{{ 'store.noOrders' | t }}</p> }
    <div class="cards">
      @for (o of orders(); track o.id) {
        <mat-card appearance="outlined" class="order" [attr.data-order]="o.orderNo">
          <div class="head">
            <b class="ltr">{{ o.orderNo }}</b>
            <span class="chip" [class]="o.stage">{{ 'store.stageOf.' + o.stage | t }}</span>
            <span class="spacer"></span>
            <span class="secondary ltr">{{ placed(o) }}</span>
          </div>
          <div class="who">
            <mat-icon>person</mat-icon> {{ o.customerName }}
            <a class="ltr" [href]="'tel:' + o.phone">{{ o.phone }}</a>
          </div>
          <div class="who">
            <mat-icon>location_on</mat-icon>
            <span>{{ o.address }}</span>
            @if (o.latitude !== null && o.longitude !== null) {
              <a [href]="'https://maps.google.com/?q=' + o.latitude + ',' + o.longitude" target="_blank" rel="noopener">{{ 'store.map' | t }}</a>
            }
          </div>
          @if (o.addressNotes) { <p class="secondary">{{ o.addressNotes }}</p> }
          <table class="lines">
            @for (l of o.lines; track $index) {
              <tr><td>{{ i18n.isArabic() ? l.titleAr : l.titleEn }}</td><td class="ltr">{{ l.quantity }} {{ l.uomCode }}</td><td class="num">{{ l.amount | money }}</td></tr>
            }
            @if (o.deliveryFee) { <tr><td>{{ 'store.deliveryFee' | t }}</td><td></td><td class="num">{{ o.deliveryFee | money }}</td></tr> }
            <tr class="total"><td>{{ 'app.total' | t }}</td><td></td><td class="num">{{ o.total | money }}</td></tr>
          </table>
          <div class="actions">
            <span class="secondary">{{ 'store.cod' | t }} · <span class="ltr">{{ o.soNo }}</span></span>
            <span class="spacer"></span>
            @if (o.stage === 'New' && canDeliver) {
              <button mat-flat-button [disabled]="busy() === o.id" (click)="deliver(o)"><mat-icon>local_shipping</mat-icon> {{ 'store.deliver' | t }}</button>
            }
            @if (o.stage === 'ToCollect' && canCollect) {
              <button mat-flat-button [disabled]="busy() === o.id || !cashAccount.value" (click)="collect(o)">
                <mat-icon>payments</mat-icon> {{ 'store.collect' | t: { amount: (o.outstanding | money) } }}
              </button>
            }
          </div>
        </mat-card>
      }
    </div>
  `,
  styles: `
    .spacer { flex: 1; }
    .page-header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .cash { margin-block-end: 12px; min-width: 260px; }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 12px; }
    .order { padding: 12px 16px; display: flex; flex-direction: column; gap: 6px; }
    .head, .who, .actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .who mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--mat-sys-on-surface-variant); }
    .lines { width: 100%; border-collapse: collapse; margin-block: 4px; }
    .lines td { padding: 4px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .lines .num { text-align: end; white-space: nowrap; }
    .lines .total td { font-weight: 600; border-bottom: 0; }
    .chip { padding: 2px 10px; border-radius: 12px; font-size: 12px; background: var(--mat-sys-surface-container-high); }
    .chip.New { background: #fde7c3; color: #6b4300; }
    .chip.ToCollect { background: #dbe8ff; color: #0b3f8a; }
    .chip.Done { background: #d6f0dd; color: #1b6e3a; }
  `
})
export class OnlineOrdersPage implements OnInit {
  private readonly commerce = inject(CommerceApi);
  private readonly sales = inject(SalesApi);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18nService);
  protected readonly canDeliver = this.auth.can('sal.deliveries:post') && this.auth.can('sal.invoices:post');
  protected readonly canCollect = this.auth.can('sal.receipts:post');
  protected readonly stage = signal<'open' | 'done' | 'all'>('open');
  protected readonly orders = signal<OnlineOrder[]>([]);
  protected readonly loading = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly cashAccounts = signal<Account[]>([]);
  protected readonly cashAccount = new FormControl('', { nonNullable: true });

  private static readonly time = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Qatar', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false
  });

  /** When the order was placed, in Qatar time (dd/MM/yyyy HH:mm). */
  protected placed(o: OnlineOrder): string {
    return OnlineOrdersPage.time.format(new Date(o.placedAt)).replace(',', '');
  }

  ngOnInit(): void {
    this.load();
    if (this.canCollect)
      this.api
        .accounts()
        .pipe(map((list) => list.filter((a) => a.isPostable && a.isActive && a.subtype === 'cash')))
        .subscribe((list) => {
          this.cashAccounts.set(list);
          this.cashAccount.setValue(list[0]?.id ?? '');
        });
  }

  protected setStage(stage: 'open' | 'done' | 'all' | undefined): void {
    this.stage.set(stage ?? 'open');
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.commerce.orders(this.stage()).subscribe({
      next: (list) => {
        this.orders.set(list);
        this.loading.set(false);
      },
      error: (e) => {
        this.loading.set(false);
        this.notify.error(e);
      }
    });
  }

  /** The goods still to deliver leave stock on one delivery note; one invoice covers them and the delivery fee. */
  protected async deliver(o: OnlineOrder): Promise<void> {
    this.busy.set(o.id);
    try {
      const order = await firstValueFrom(this.sales.order(o.salesOrderId));
      const toDeliver = order.lines.filter((l) => l.itemType === 'Stock' && l.quantity > l.quantityDelivered);
      const note = toDeliver.length
        ? await firstValueFrom(this.sales.createDelivery({
            salesOrderId: order.id, deliveryDate: todayIso(), deliveryAddress: o.address, notes: o.addressNotes,
            lines: toDeliver.map((l) => ({ salesOrderLineId: l.id, quantity: l.quantity - l.quantityDelivered }))
          }))
        : null;
      const services = order.lines.filter((l) => l.itemType === 'Service' && l.quantity > l.quantityInvoiced);
      const lines = [
        ...(note?.lines ?? []).map((l) => ({ deliveryNoteLineId: l.id, quantity: l.quantity })),
        ...services.map((l) => ({ salesOrderLineId: l.id, quantity: l.quantity - l.quantityInvoiced }))
      ];
      if (lines.length)
        await firstValueFrom(this.sales.createInvoice({ customerId: order.customerId, invoiceDate: todayIso(), customerRef: o.orderNo, lines })
          .pipe(concatMap((invoice) => this.sales.postInvoice(invoice.id))));
      this.notify.info(this.i18n.t('store.delivered', { no: o.orderNo }));
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(null);
      this.load();
    }
  }

  /** Cash handed over by the driver: a receipt allocated to the order's open invoices. */
  protected async collect(o: OnlineOrder): Promise<void> {
    this.busy.set(o.id);
    try {
      const invoices = (await firstValueFrom(this.sales.invoices({ customerId: o.customerId, openOnly: true })))
        .filter((i) => i.salesOrderId === o.salesOrderId)
        .map((i) => ({ salesInvoiceId: i.id, amount: Math.round((i.total - i.paidAmount - i.creditedAmount) * 10000) / 10000 }))
        .filter((a) => a.amount > 0);
      await firstValueFrom(invoices.length
        ? this.sales.createReceipt({
            customerId: o.customerId, date: todayIso(), method: 'Cash', depositAccountId: this.cashAccount.value, reference: o.orderNo, allocations: invoices
          })
        : of(null));
      this.notify.info(this.i18n.t('store.collected', { no: o.orderNo }));
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(null);
      this.load();
    }
  }
}
