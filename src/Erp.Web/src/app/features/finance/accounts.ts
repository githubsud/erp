import { Component, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface AccountRow extends Account {
  level: number;
}

@Component({
  selector: 'app-accounts',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'nav.accounts' | t }}</h1></div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="code">
          <th mat-header-cell *matHeaderCellDef>{{ 'finance.code' | t }}</th>
          <td mat-cell *matCellDef="let a" class="ltr">{{ a.code }}</td>
        </ng-container>
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'finance.account' | t }}</th>
          <td mat-cell *matCellDef="let a" [style.padding-inline-start.px]="16 + a.level * 20" [class.group]="!a.isPostable">
            {{ a | bi }}
            @if (a.isControl) { <span class="chip info">{{ a.controlSubledger }}</span> }
            @if (a.subtype) { <span class="chip">{{ a.subtype }}</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="type">
          <th mat-header-cell *matHeaderCellDef>{{ 'finance.type' | t }}</th>
          <td mat-cell *matCellDef="let a">{{ a.accountType }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
    </div>
  `,
  styles: `.group { font-weight: 700; }`
})
export class AccountsPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['code', 'name', 'type'];
  private readonly accounts = signal<Account[]>([]);

  /** Depth comes from the parent chain so the chart reads as a tree. */
  protected readonly rows = computed<AccountRow[]>(() => {
    const byId = new Map(this.accounts().map((a) => [a.id, a]));
    const depth = (a: Account): number => (a.parentId && byId.has(a.parentId) ? 1 + depth(byId.get(a.parentId)!) : 0);
    return this.accounts().map((a) => ({ ...a, level: depth(a) }));
  });

  constructor() {
    this.api.accounts().subscribe({ next: (a) => this.accounts.set(a), error: (e) => this.notify.error(e) });
  }
}
