import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Supplier } from '../../core/trade.models';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

interface SubcontractRow {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  supplierNameEn: string;
  supplierNameAr: string;
  contractValue: number;
  retentionPercent: number;
  taxRetentionPercent: number;
  taxClearanceReceivedOn: string | null;
  status: string;
}

interface ScItem {
  id: string;
  lineNo: number;
  section: string | null;
  descriptionEn: string;
  descriptionAr: string;
  unit: string;
  quantity: number;
  rate: number;
  amount: number;
  certifiedQuantity: number;
  certifiedAmount: number;
  percentComplete: number;
}

interface Subcontract {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  projectId: string | null;
  contractValue: number;
  retentionPercent: number;
  taxRetentionPercent: number;
  taxClearanceReceivedOn: string | null;
  status: string;
  notes: string | null;
  items: ScItem[];
  certifiedToDate: number;
  percentComplete: number;
  retentionHeld: number;
  retentionReleased: number;
  taxRetentionHeld: number;
  taxRetentionReleased: number;
}

interface ScCertificate {
  id: string;
  certificateNo: string;
  periodEnd: string;
  status: 'Draft' | 'Invoiced' | 'Cancelled';
  grossToDate: number;
  gross: number;
  retention: number;
  taxRetention: number;
  net: number;
  invoiceNo: string | null;
  supplierInvoiceId: string | null;
}

interface ProjectOption {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
}

@Component({
  selector: 'app-subcontracts',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'sc.title' | t }}</h1>
      @if (auth.can('pur.subcontracts:manage')) { <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'sc.new' | t }}</button> }
    </div>
    <table class="line-editor">
      <thead><tr><th>{{ 'fa.code' | t }}</th><th>{{ 'sc.name' | t }}</th><th>{{ 'po.supplier' | t }}</th><th class="num">{{ 'prj.contract' | t }}</th>
        <th class="num">{{ 'prj.retention' | t }}</th><th class="num">{{ 'sc.taxRetention' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
      <tbody>
        @for (p of rows(); track p.id) {
          <tr class="clickable" [routerLink]="['/purchasing/subcontracts', p.id]">
            <td class="ltr">{{ p.code }}</td><td>{{ p | bi }}</td><td>{{ p | bi:'supplierName' }}</td><td class="num ltr">{{ p.contractValue | money }}</td>
            <td class="num ltr">{{ p.retentionPercent }}%</td>
            <td class="num ltr">@if (p.taxClearanceReceivedOn) { <span class="chip ok">{{ 'sc.cleared' | t }}</span> } @else { {{ p.taxRetentionPercent }}% }</td>
            <td><span class="chip" [class.ok]="p.status === 'Active'">{{ 'prj.status.' + p.status | t }}</span></td>
          </tr>
        }
      </tbody>
    </table>
    @if (!rows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
  `,
  styles: `.clickable { cursor: pointer; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class SubcontractsPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly rows = signal<SubcontractRow[]>([]);

  ngOnInit(): void {
    this.api.get<SubcontractRow[]>('purchasing/subcontracts').subscribe((r) => this.rows.set(r));
  }

  protected create(): void {
    this.dialog.open(SubcontractDialog, { data: null, width: '900px' }).afterClosed().pipe(filter(Boolean))
      .subscribe((p: Subcontract) => void this.router.navigate(['/purchasing/subcontracts', p.id]));
  }
}

@Component({
  selector: 'app-subcontract-detail',
  imports: [...SHARED],
  template: `
    @if (contract(); as p) {
      <div class="page-header">
        <a mat-icon-button routerLink="/purchasing/subcontracts"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1><span class="ltr">{{ p.code }}</span> · {{ p | bi }}</h1>
        <span class="spacer"></span>
        @if (canManage) { <button mat-stroked-button (click)="edit(p)"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</button> }
      </div>
      <div class="stats">
        <div class="stat"><span>{{ 'po.supplier' | t }}</span><b>{{ p | bi:'supplierName' }}</b></div>
        <div class="stat"><span>{{ 'prj.contract' | t }}</span><b class="ltr">{{ p.contractValue | money }}</b></div>
        <div class="stat"><span>{{ 'prj.certified' | t }}</span><b class="ltr">{{ p.certifiedToDate | money }} · {{ p.percentComplete }}%</b></div>
        <div class="stat"><span>{{ 'prj.retentionHeld' | t }}</span><b class="ltr">{{ p.retentionHeld - p.retentionReleased | money }}</b></div>
        <div class="stat"><span>{{ 'sc.taxHeld' | t }}</span>
          <b class="ltr">{{ p.taxRetentionHeld - p.taxRetentionReleased | money }}</b>
          @if (p.taxClearanceReceivedOn) { <small>{{ 'sc.clearedOn' | t }} <span class="ltr">{{ p.taxClearanceReceivedOn | d }}</span></small> }
        </div>
      </div>
      <mat-tab-group mat-stretch-tabs="false">
        <mat-tab [label]="'prj.boq' | t">
          <table class="line-editor tab-body">
            <thead><tr><th>#</th><th>{{ 'prj.description' | t }}</th><th>{{ 'prj.unit' | t }}</th><th class="num">{{ 'po.qty' | t }}</th><th class="num">{{ 'prj.rate' | t }}</th>
              <th class="num">{{ 'app.amount' | t }}</th><th class="num">{{ 'prj.done' | t }}</th><th class="num">{{ 'prj.certifiedAmount' | t }}</th></tr></thead>
            <tbody>
              @for (i of p.items; track i.id) {
                @if (i.section && (i.lineNo === 1 || p.items[i.lineNo - 2]?.section !== i.section)) { <tr class="section"><td colspan="8">{{ i.section }}</td></tr> }
                <tr><td class="ltr">{{ i.lineNo }}</td><td>{{ i | bi:'description' }}</td><td>{{ i.unit }}</td><td class="num ltr">{{ i.quantity }}</td>
                  <td class="num ltr">{{ i.rate | money }}</td><td class="num ltr">{{ i.amount | money }}</td>
                  <td class="num ltr">{{ i.certifiedQuantity }} <span class="secondary">({{ i.percentComplete }}%)</span></td><td class="num ltr">{{ i.certifiedAmount | money }}</td></tr>
              }
            </tbody>
          </table>
        </mat-tab>
        <mat-tab [label]="'prj.certificates' | t">
          <div class="tab-body">
            <div class="actions">
              @if (canManage && p.status === 'Active') { <button mat-flat-button (click)="newCertificate(p)"><mat-icon>add</mat-icon> {{ 'prj.newCertificate' | t }}</button> }
              @if (canInvoice && p.retentionHeld > p.retentionReleased) { <button mat-stroked-button (click)="release(p)">{{ 'prj.releaseRetention' | t }}</button> }
              @if (canInvoice && !p.taxClearanceReceivedOn) { <button mat-stroked-button (click)="taxClearance()">{{ 'sc.recordClearance' | t }}</button> }
            </div>
            <table class="line-editor">
              <thead><tr><th>{{ 'prj.certificate' | t }}</th><th>{{ 'prj.periodEnd' | t }}</th><th class="num">{{ 'prj.gross' | t }}</th><th class="num">{{ 'prj.retention' | t }}</th>
                <th class="num">{{ 'sc.taxRetention' | t }}</th><th class="num">{{ 'sc.net' | t }}</th><th class="num">{{ 'prj.toDate' | t }}</th><th>{{ 'app.status' | t }}</th><th></th></tr></thead>
              <tbody>
                @for (c of certificates(); track c.id) {
                  <tr>
                    <td class="ltr">{{ c.certificateNo }}</td><td class="ltr">{{ c.periodEnd | d }}</td><td class="num ltr">{{ c.gross | money }}</td>
                    <td class="num ltr">{{ c.retention | money }}</td><td class="num ltr">{{ c.taxRetention | money }}</td><td class="num ltr"><b>{{ c.net | money }}</b></td>
                    <td class="num ltr">{{ c.grossToDate | money }}</td>
                    <td><span class="chip" [class.ok]="c.status === 'Invoiced'" [class.warn]="c.status === 'Draft'" [class.bad]="c.status === 'Cancelled'">{{ 'prj.certStatus.' + c.status | t }}</span>
                      @if (c.supplierInvoiceId) { <a class="ltr" [routerLink]="['/purchasing/invoices', c.supplierInvoiceId]">{{ c.invoiceNo }}</a> }</td>
                    <td>@if (c.status === 'Draft' && canInvoice) { <button mat-stroked-button (click)="invoice(c)">{{ 'sc.book' | t }}</button> }</td>
                  </tr>
                }
              </tbody>
            </table>
            <p class="secondary">{{ 'sc.hint' | t }}</p>
          </div>
        </mat-tab>
      </mat-tab-group>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .stats { display: flex; gap: 12px; flex-wrap: wrap; margin: 12px 0; }
    .stat { display: flex; flex-direction: column; padding: 10px 16px; border-radius: 12px; background: var(--mat-sys-surface-container); min-width: 170px; }
    .stat b { color: var(--mat-sys-primary); }
    tr.section td { font-weight: 700; background: var(--mat-sys-surface-container-low); }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; }
  `
})
export class SubcontractDetailPage implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly auth = inject(AuthService);
  protected readonly canManage = this.auth.can('pur.subcontracts:manage');
  protected readonly canInvoice = this.auth.can('pur.invoices:post');
  protected readonly contract = signal<Subcontract | null>(null);
  protected readonly certificates = signal<ScCertificate[]>([]);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.api.get<Subcontract>(`purchasing/subcontracts/${this.id()}`).subscribe({ next: (p) => this.contract.set(p), error: (e) => this.notify.error(e) });
    this.api.get<ScCertificate[]>(`purchasing/subcontracts/${this.id()}/certificates`).subscribe((c) => this.certificates.set(c));
  }

  protected edit(p: Subcontract): void {
    this.dialog.open(SubcontractDialog, { data: p, width: '900px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected newCertificate(p: Subcontract): void {
    this.dialog.open(ScCertificateDialog, { data: p, width: '820px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  private done = {
    next: () => {
      this.notify.success();
      this.load();
    },
    error: (e: unknown) => this.notify.error(e)
  };

  protected invoice(c: ScCertificate): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'sc.book', message: 'sc.bookConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post(`purchasing/subcontracts/${this.id()}/certificates/${c.id}/invoice`, { date: todayIso() })))
      .subscribe(this.done);
  }

  protected release(p: Subcontract): void {
    const held = p.retentionHeld - p.retentionReleased;
    const answer = prompt('Retention to release / المحتجزات المراد الإفراج عنها', String(held));
    if (!answer) return;
    this.api.post(`purchasing/subcontracts/${this.id()}/release-retention`, { date: todayIso(), amount: Number(answer) }).subscribe(this.done);
  }

  protected taxClearance(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'sc.recordClearance', message: 'sc.clearanceConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post(`purchasing/subcontracts/${this.id()}/tax-clearance`, { receivedOn: todayIso(), releaseDate: todayIso() })))
      .subscribe(this.done);
  }
}

type ItemGroup = FormGroup<{ section: FormControl<string>; descriptionEn: FormControl<string>; descriptionAr: FormControl<string>; unit: FormControl<string>;
  quantity: FormControl<number>; rate: FormControl<number> }>;

@Component({
  selector: 'app-subcontract-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data ? 'app.edit' : 'sc.new') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'fa.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" [readonly]="!!data" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'po.supplier' | t }}</mat-label>
          <mat-select formControlName="supplierId">@for (s of suppliers(); track s.id) { <mat-option [value]="s.id">{{ s | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sc.project' | t }}</mat-label>
          <mat-select formControlName="projectId"><mat-option [value]="''">—</mat-option>
            @for (p of projects(); track p.id) { <mat-option [value]="p.id"><span class="ltr">{{ p.code }}</span> {{ p | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'prj.retention' | t }} %</mat-label><input matInput type="number" min="0" max="50" formControlName="retentionPercent" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sc.taxRetention' | t }} %</mat-label><input matInput type="number" min="0" max="100" formControlName="taxRetentionPercent" dir="ltr" />
          <mat-hint>{{ 'sc.taxRetentionHint' | t }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
          <mat-select formControlName="status">@for (s of ['Active', 'Completed', 'Closed']; track s) { <mat-option [value]="s">{{ 'prj.status.' + s | t }}</mat-option> }</mat-select></mat-form-field>
      </form>
      <h3>{{ 'prj.boq' | t }}</h3>
      @if (boqLocked) { <p class="secondary">{{ 'prj.boqLocked' | t }}</p> }
      <table class="line-editor">
        <thead><tr><th>{{ 'prj.section' | t }}</th><th>{{ 'prj.descriptionEn' | t }}</th><th>{{ 'prj.descriptionAr' | t }}</th><th>{{ 'prj.unit' | t }}</th>
          <th class="num">{{ 'po.qty' | t }}</th><th class="num">{{ 'prj.rate' | t }}</th><th></th></tr></thead>
        <tbody>
          @for (i of items.controls; track i; let n = $index) {
            <tr [formGroup]="i">
              <td><mat-form-field><input matInput formControlName="section" /></mat-form-field></td>
              <td><mat-form-field><input matInput formControlName="descriptionEn" dir="ltr" /></mat-form-field></td>
              <td><mat-form-field><input matInput formControlName="descriptionAr" dir="rtl" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput formControlName="unit" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" formControlName="rate" dir="ltr" /></mat-form-field></td>
              <td>@if (!boqLocked) { <button mat-icon-button (click)="items.removeAt(n)"><mat-icon>delete</mat-icon></button> }</td>
            </tr>
          }
        </tbody>
      </table>
      <div class="foot">
        <button mat-button (click)="addItem()"><mat-icon>add</mat-icon> {{ (boqLocked ? 'prj.addVariation' : 'doc.addLine') | t }}</button>
        <span class="spacer"></span>
        {{ 'prj.contract' | t }} <b class="ltr">{{ total() | money }}</b>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || items.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.foot { display: flex; align-items: center; gap: 8px; } .spacer { flex: 1; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class SubcontractDialog implements OnInit {
  protected readonly data = inject<Subcontract | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<SubcontractDialog, Subcontract>);
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly projects = signal<ProjectOption[]>([]);
  protected readonly boqLocked = !!this.data && this.data.certifiedToDate > 0;
  private readonly existing = this.data?.items.length ?? 0;
  protected readonly form = this.fb.group({
    code: [this.data?.code ?? '', Validators.required], supplierId: [this.data?.supplierId ?? '', Validators.required], nameEn: [this.data?.nameEn ?? ''],
    nameAr: [this.data?.nameAr ?? ''], projectId: [this.data?.projectId ?? ''], retentionPercent: [this.data?.retentionPercent ?? 10],
    taxRetentionPercent: [this.data?.taxRetentionPercent ?? (null as unknown as number)], status: [this.data?.status ?? 'Active']
  });
  protected readonly items = new FormArray<ItemGroup>([]);
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    return this.items.controls.reduce((s, i) => s + (Number(i.value.quantity) || 0) * (Number(i.value.rate) || 0), 0);
  });

  ngOnInit(): void {
    this.items.valueChanges.subscribe(() => this.version.update((v) => v + 1));
    this.trade.suppliers().subscribe((s) => this.suppliers.set(s.filter((x) => x.isActive && x.currency === 'QAR' && (x.isResident || x.hasPermanentEstablishment))));
    // Projects live in Sales; the list is optional (a purchaser without sales access just leaves it blank).
    this.api.get<ProjectOption[]>('sales/projects').subscribe({ next: (p) => this.projects.set(p), error: () => this.projects.set([]) });
    for (const i of this.data?.items ?? []) this.addItem(i.section ?? '', i.descriptionEn, i.descriptionAr, i.unit, i.quantity, i.rate, this.boqLocked);
    if (!this.data) this.addItem();
  }

  protected addItem(section = '', en = '', ar = '', unit = 'm2', quantity = 1, rate = 0, locked = false): void {
    const g = this.fb.group({
      section: [section], descriptionEn: [en, Validators.required], descriptionAr: [ar, Validators.required], unit: [unit, Validators.required],
      quantity: [quantity, [Validators.required, Validators.min(0.000001)]], rate: [rate, [Validators.required, Validators.min(0)]]
    }) as ItemGroup;
    if (locked) g.disable();
    this.items.push(g);
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const all = this.items.getRawValue().map((i) => ({ ...i, section: i.section || null, quantity: Number(i.quantity), rate: Number(i.rate) }));
    const tax = v.taxRetentionPercent as number | null | string;
    const body = { ...v, projectId: v.projectId || null, retentionPercent: Number(v.retentionPercent),
      taxRetentionPercent: tax === null || tax === '' ? null : Number(tax), notes: null, items: this.boqLocked ? null : all };
    const save = this.data ? this.api.put<Subcontract>(`purchasing/subcontracts/${this.data.id}`, body) : this.api.post<Subcontract>('purchasing/subcontracts', body);
    save.pipe(switchMap((p) => (this.boqLocked && all.length > this.existing ? this.api.post<Subcontract>(`purchasing/subcontracts/${p.id}/items`, all.slice(this.existing)) : [p])))
      .subscribe({ next: (p) => this.ref.close(p), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-sc-certificate-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'prj.newCertificate' | t }}</h2>
    <mat-dialog-content>
      <mat-form-field><mat-label>{{ 'prj.periodEnd' | t }}</mat-label><input matInput type="date" [formControl]="periodEnd" /></mat-form-field>
      <p class="secondary">{{ 'prj.certificateHint' | t }}</p>
      <table class="line-editor">
        <thead><tr><th>{{ 'prj.description' | t }}</th><th class="num">{{ 'po.qty' | t }}</th><th class="num">{{ 'prj.previous' | t }}</th><th class="narrow">{{ 'prj.toDateQty' | t }}</th>
          <th class="num">{{ 'prj.thisPeriod' | t }}</th></tr></thead>
        <tbody>
          @for (i of data.items; track i.id) {
            <tr><td>{{ i | bi:'description' }} <span class="secondary">({{ i.unit }})</span></td><td class="num ltr">{{ i.quantity }}</td>
              <td class="num ltr">{{ i.certifiedQuantity }}</td>
              <td class="narrow"><mat-form-field><input matInput type="number" [min]="i.certifiedQuantity" [max]="i.quantity" [formControl]="qty[i.id]" dir="ltr" /></mat-form-field></td>
              <td class="num ltr">{{ thisPeriod(i) | money }}</td></tr>
          }
          <tr class="total-row"><td colspan="4">{{ 'prj.gross' | t }}</td><td class="num ltr">{{ gross() | money }}</td></tr>
        </tbody>
      </table>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="gross() <= 0" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class ScCertificateDialog {
  protected readonly data = inject<Subcontract>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ScCertificateDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly periodEnd = new FormControl(todayIso(), { nonNullable: true });
  protected readonly qty: Record<string, FormControl<number>> = Object.fromEntries(this.data.items.map((i) => [i.id, new FormControl(i.certifiedQuantity, { nonNullable: true })]));
  private readonly version = signal(0);
  protected readonly gross = computed(() => {
    this.version();
    return this.data.items.reduce((s, i) => s + this.thisPeriod(i), 0);
  });

  constructor() {
    for (const c of Object.values(this.qty)) c.valueChanges.subscribe(() => this.version.update((v) => v + 1));
  }

  protected thisPeriod(i: ScItem): number {
    return Math.round(((Number(this.qty[i.id].value) || 0) - i.certifiedQuantity) * i.rate * 100) / 100;
  }

  protected save(): void {
    const progress = this.data.items.map((i) => ({ itemId: i.id, quantityToDate: Number(this.qty[i.id].value) || 0 }))
      .filter((p) => p.quantityToDate > (this.data.items.find((i) => i.id === p.itemId)?.certifiedQuantity ?? 0));
    this.api.post(`purchasing/subcontracts/${this.data.id}/certificates`, { periodEnd: this.periodEnd.value, progress, notes: null })
      .subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}
