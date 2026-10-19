import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { Customer } from '../../core/sales.models';
import { NotifyService } from '../../core/notify.service';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

interface ProjectRow {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  customerNameEn: string;
  customerNameAr: string;
  contractValue: number;
  retentionPercent: number;
  status: string;
}

interface BoqItem {
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

interface Project {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  customerId: string;
  customerNameEn: string;
  customerNameAr: string;
  contractValue: number;
  retentionPercent: number;
  budgetCost: number;
  startDate: string | null;
  endDate: string | null;
  status: string;
  notes: string | null;
  items: BoqItem[];
  certifiedToDate: number;
  percentComplete: number;
  retentionHeld: number;
  retentionReleased: number;
}

interface Certificate {
  id: string;
  certificateNo: string;
  periodEnd: string;
  status: 'Draft' | 'Invoiced' | 'Cancelled';
  grossToDate: number;
  gross: number;
  retention: number;
  net: number;
  invoiceNo: string | null;
  salesInvoiceId: string | null;
  lines: { boqItemId: string; descriptionEn: string; descriptionAr: string; unit: string; previousQuantity: number; quantityToDate: number; amount: number }[];
}

interface Profit {
  revenue: number;
  cost: number;
  profit: number;
  marginPercent: number | null;
  budgetCost: number;
  costToBudgetPercent: number;
  income: { code: string; nameEn: string; nameAr: string; amount: number }[];
  costs: { code: string; nameEn: string; nameAr: string; amount: number }[];
}

@Component({
  selector: 'app-projects',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'prj.title' | t }}</h1>
      @if (auth.can('sal.projects:manage')) { <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'prj.new' | t }}</button> }
    </div>
    <table class="line-editor">
      <thead><tr><th>{{ 'fa.code' | t }}</th><th>{{ 'prj.name' | t }}</th><th>{{ 'cust.customer' | t }}</th><th class="num">{{ 'prj.contract' | t }}</th>
        <th class="num">{{ 'prj.retention' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
      <tbody>
        @for (p of rows(); track p.id) {
          <tr class="clickable" [routerLink]="['/sales/projects', p.id]">
            <td class="ltr">{{ p.code }}</td><td>{{ p | bi }}</td><td>{{ p | bi:'customerName' }}</td><td class="num ltr">{{ p.contractValue | money }}</td>
            <td class="num ltr">{{ p.retentionPercent }}%</td><td><span class="chip" [class.ok]="p.status === 'Active'">{{ 'prj.status.' + p.status | t }}</span></td>
          </tr>
        }
      </tbody>
    </table>
    @if (!rows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
  `,
  styles: `.clickable { cursor: pointer; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class ProjectsPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly rows = signal<ProjectRow[]>([]);

  ngOnInit(): void {
    this.api.get<ProjectRow[]>('sales/projects').subscribe((r) => this.rows.set(r));
  }

  protected create(): void {
    this.dialog.open(ProjectDialog, { data: null, width: '900px' }).afterClosed().pipe(filter(Boolean))
      .subscribe((p: Project) => void this.router.navigate(['/sales/projects', p.id]));
  }
}

@Component({
  selector: 'app-project-detail',
  imports: [...SHARED],
  template: `
    @if (project(); as p) {
      <div class="page-header">
        <a mat-icon-button routerLink="/sales/projects"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1><span class="ltr">{{ p.code }}</span> · {{ p | bi }}</h1>
        <span class="spacer"></span>
        @if (canManage) { <button mat-stroked-button (click)="edit(p)"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</button> }
      </div>
      <div class="stats">
        <div class="stat"><span>{{ 'cust.customer' | t }}</span><b>{{ p | bi:'customerName' }}</b></div>
        <div class="stat"><span>{{ 'prj.contract' | t }}</span><b class="ltr">{{ p.contractValue | money }}</b></div>
        <div class="stat"><span>{{ 'prj.certified' | t }}</span><b class="ltr">{{ p.certifiedToDate | money }} · {{ p.percentComplete }}%</b></div>
        <div class="stat"><span>{{ 'prj.retentionHeld' | t }}</span><b class="ltr">{{ p.retentionHeld - p.retentionReleased | money }}</b></div>
      </div>
      <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="$event === 2 && loadProfit()">
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
            </div>
            <table class="line-editor">
              <thead><tr><th>{{ 'prj.certificate' | t }}</th><th>{{ 'prj.periodEnd' | t }}</th><th class="num">{{ 'prj.gross' | t }}</th><th class="num">{{ 'prj.retention' | t }}</th>
                <th class="num">{{ 'prj.net' | t }}</th><th class="num">{{ 'prj.toDate' | t }}</th><th>{{ 'app.status' | t }}</th><th></th></tr></thead>
              <tbody>
                @for (c of certificates(); track c.id) {
                  <tr>
                    <td class="ltr">{{ c.certificateNo }}</td><td class="ltr">{{ c.periodEnd | d }}</td><td class="num ltr">{{ c.gross | money }}</td>
                    <td class="num ltr">{{ c.retention | money }}</td><td class="num ltr"><b>{{ c.net | money }}</b></td><td class="num ltr">{{ c.grossToDate | money }}</td>
                    <td><span class="chip" [class.ok]="c.status === 'Invoiced'" [class.warn]="c.status === 'Draft'" [class.bad]="c.status === 'Cancelled'">{{ 'prj.certStatus.' + c.status | t }}</span>
                      @if (c.salesInvoiceId) { <a class="ltr" [routerLink]="['/sales/invoices', c.salesInvoiceId]">{{ c.invoiceNo }}</a> }</td>
                    <td>@if (c.status === 'Draft' && canInvoice) { <button mat-stroked-button (click)="invoice(c)">{{ 'prj.invoice' | t }}</button> }</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </mat-tab>
        <mat-tab [label]="'prj.profit' | t">
          @if (profit(); as f) {
            <div class="stats tab-body">
              <div class="stat"><span>{{ 'prj.revenue' | t }}</span><b class="ltr">{{ f.revenue | money }}</b></div>
              <div class="stat"><span>{{ 'prj.cost' | t }}</span><b class="ltr">{{ f.cost | money }}</b></div>
              <div class="stat"><span>{{ 'fs.netProfit' | t }}</span><b class="ltr">{{ f.profit | money }} @if (f.marginPercent !== null) { · {{ f.marginPercent }}% }</b></div>
              <div class="stat"><span>{{ 'prj.budgetUsed' | t }}</span><b class="ltr">{{ f.costToBudgetPercent }}% {{ 'prj.of' | t }} {{ f.budgetCost | money }}</b></div>
            </div>
            <table class="line-editor">
              <tbody>
                @for (l of f.income; track l.code) { <tr><td><span class="ltr">{{ l.code }}</span> · {{ l | bi }}</td><td class="num ltr">{{ l.amount | money }}</td></tr> }
                @for (l of f.costs; track l.code) { <tr><td><span class="ltr">{{ l.code }}</span> · {{ l | bi }}</td><td class="num ltr">({{ l.amount | money }})</td></tr> }
              </tbody>
            </table>
            <p class="secondary">{{ 'prj.profitHint' | t }}</p>
          }
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
    .actions { display: flex; gap: 8px; margin-bottom: 12px; }
  `
})
export class ProjectDetailPage implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly auth = inject(AuthService);
  protected readonly canManage = this.auth.can('sal.projects:manage');
  protected readonly canInvoice = this.auth.can('sal.invoices:post');
  protected readonly project = signal<Project | null>(null);
  protected readonly certificates = signal<Certificate[]>([]);
  protected readonly profit = signal<Profit | null>(null);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.api.get<Project>(`sales/projects/${this.id()}`).subscribe({ next: (p) => this.project.set(p), error: (e) => this.notify.error(e) });
    this.api.get<Certificate[]>(`sales/projects/${this.id()}/certificates`).subscribe((c) => this.certificates.set(c));
  }

  protected loadProfit(): void {
    this.api.get<Profit>(`sales/projects/${this.id()}/profit`).subscribe({ next: (p) => this.profit.set(p), error: (e) => this.notify.error(e) });
  }

  protected edit(p: Project): void {
    this.dialog.open(ProjectDialog, { data: p, width: '900px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected newCertificate(p: Project): void {
    this.dialog.open(CertificateDialog, { data: p, width: '820px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected invoice(c: Certificate): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'prj.invoice', message: 'prj.invoiceConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post(`sales/projects/${this.id()}/certificates/${c.id}/invoice`, { date: todayIso() })))
      .subscribe({
        next: () => {
          this.notify.success();
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected release(p: Project): void {
    const held = p.retentionHeld - p.retentionReleased;
    const answer = prompt('Retention to release / المحتجزات المراد الإفراج عنها', String(held));
    if (!answer) return;
    this.api.post(`sales/projects/${this.id()}/release-retention`, { date: todayIso(), amount: Number(answer) }).subscribe({
      next: () => {
        this.notify.success();
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }
}

type ItemGroup = FormGroup<{ section: FormControl<string>; descriptionEn: FormControl<string>; descriptionAr: FormControl<string>; unit: FormControl<string>;
  quantity: FormControl<number>; rate: FormControl<number> }>;

@Component({
  selector: 'app-project-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data ? 'app.edit' : 'prj.new') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'fa.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" [readonly]="!!data" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'cust.customer' | t }}</mat-label>
          <mat-select formControlName="customerId">@for (c of customers(); track c.id) { <mat-option [value]="c.id">{{ c | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'prj.retention' | t }} %</mat-label><input matInput type="number" min="0" max="50" formControlName="retentionPercent" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'prj.budgetCost' | t }}</mat-label><input matInput type="number" min="0" formControlName="budgetCost" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" formControlName="startDate" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" formControlName="endDate" /></mat-form-field>
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
export class ProjectDialog implements OnInit {
  protected readonly data = inject<Project | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ProjectDialog, Project>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly customers = signal<Customer[]>([]);
  protected readonly boqLocked = !!this.data && this.data.certifiedToDate > 0;
  private readonly existing = this.data?.items.length ?? 0;
  protected readonly form = this.fb.group({
    code: [this.data?.code ?? '', Validators.required], customerId: [this.data?.customerId ?? '', Validators.required], nameEn: [this.data?.nameEn ?? ''],
    nameAr: [this.data?.nameAr ?? ''], retentionPercent: [this.data?.retentionPercent ?? 10], budgetCost: [this.data?.budgetCost ?? 0],
    startDate: [this.data?.startDate ?? ''], endDate: [this.data?.endDate ?? ''], status: [this.data?.status ?? 'Active']
  });
  protected readonly items = new FormArray<ItemGroup>([]);
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    return this.items.controls.reduce((s, i) => s + (Number(i.value.quantity) || 0) * (Number(i.value.rate) || 0), 0);
  });

  ngOnInit(): void {
    this.items.valueChanges.subscribe(() => this.version.update((v) => v + 1));
    this.api.get<Customer[]>('sales/customers').subscribe((c) => this.customers.set(c.filter((x) => x.isActive && x.currency === 'QAR')));
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
    const body = { ...v, retentionPercent: Number(v.retentionPercent), budgetCost: Number(v.budgetCost) || 0, startDate: v.startDate || null, endDate: v.endDate || null,
      notes: null, items: this.boqLocked ? null : all };
    const save = this.data ? this.api.put<Project>(`sales/projects/${this.data.id}`, body) : this.api.post<Project>('sales/projects', body);
    save.pipe(switchMap((p) => (this.boqLocked && all.length > this.existing ? this.api.post<Project>(`sales/projects/${p.id}/items`, all.slice(this.existing)) : [p])))
      .subscribe({ next: (p) => this.ref.close(p), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-certificate-dialog',
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
export class CertificateDialog {
  protected readonly data = inject<Project>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CertificateDialog, boolean>);
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

  protected thisPeriod(i: BoqItem): number {
    return Math.round(((Number(this.qty[i.id].value) || 0) - i.certifiedQuantity) * i.rate * 100) / 100;
  }

  protected save(): void {
    const progress = this.data.items.map((i) => ({ boqItemId: i.id, quantityToDate: Number(this.qty[i.id].value) || 0 }))
      .filter((p) => p.quantityToDate > (this.data.items.find((i) => i.id === p.boqItemId)?.certifiedQuantity ?? 0));
    this.api.post(`sales/projects/${this.data.id}/certificates`, { periodEnd: this.periodEnd.value, progress, notes: null })
      .subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}
