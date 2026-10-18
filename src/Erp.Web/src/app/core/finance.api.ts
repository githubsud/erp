import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';
import { ConfigService } from './config.service';

export interface StatementAccount {
  accountId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  amount: number;
  compare: number | null;
}

export interface StatementSection {
  code: string;
  nameEn: string;
  nameAr: string;
  kind: 'income' | 'cost_of_sales' | 'expense' | 'asset' | 'liability' | 'equity';
  accounts: StatementAccount[];
  total: number;
  compareTotal: number | null;
}

export interface ProfitAndLoss {
  from: string;
  to: string;
  compareFrom: string | null;
  compareTo: string | null;
  sections: StatementSection[];
  revenue: number;
  compareRevenue: number | null;
  grossProfit: number;
  compareGrossProfit: number | null;
  netProfit: number;
  compareNetProfit: number | null;
}

export interface BalanceSheet {
  asOf: string;
  compareAsOf: string | null;
  sections: StatementSection[];
  totalAssets: number;
  compareTotalAssets: number | null;
  totalLiabilities: number;
  compareTotalLiabilities: number | null;
  totalEquity: number;
  compareTotalEquity: number | null;
  profitForYear: number;
  compareProfitForYear: number | null;
  isBalanced: boolean;
}

export interface JournalVoucher {
  id: string;
  voucherNo: string;
  voucherDate: string;
  memoEn: string | null;
  memoAr: string | null;
  status: 'Posted' | 'Reversed';
  journalEntryId: string;
  reversalEntryId: string | null;
}

export interface CostCenter {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
}

export interface OpeningInvoiceResult {
  id: string;
  invoiceNo: string;
  partyId: string;
  reference: string;
  invoiceDate: string;
  dueDate: string;
  amount: number;
}

export interface BankMatch {
  journalLineId: string;
  journalEntryId: string;
  entryNo: string;
  postingDate: string;
  sourceType: string;
  sourceNo: string;
  memo: string | null;
  amount: number;
}

export interface BankLine {
  id: string;
  lineNo: number;
  date: string;
  description: string | null;
  reference: string | null;
  amount: number;
  status: 'Unmatched' | 'Matched' | 'Ignored';
  journalEntryId: string | null;
  matches: BankMatch[];
}

export interface BankStatement {
  id: string;
  statementNo: string;
  accountId: string;
  accountCode: string;
  accountNameEn: string;
  accountNameAr: string;
  dateFrom: string;
  dateTo: string;
  openingBalance: number | null;
  closingBalance: number | null;
  fileName: string | null;
  status: 'Open' | 'Reconciled';
  lineCount: number;
  unmatchedCount: number;
  inflows: number;
  outflows: number;
  lines: BankLine[] | null;
}

export interface LedgerItem {
  journalLineId: string;
  journalEntryId: string;
  entryNo: string;
  postingDate: string;
  sourceType: string;
  sourceNo: string;
  memo: string | null;
  amount: number;
}

export interface BankReconciliation {
  accountId: string;
  asOf: string;
  reconcileFrom: string | null;
  ledgerBalance: number;
  statementBalance: number | null;
  statementDate: string | null;
  unclearedLedgerItems: LedgerItem[];
  unrecordedBankItems: BankLine[];
  adjustedLedgerBalance: number;
  adjustedStatementBalance: number | null;
  difference: number | null;
}

export interface ImportResult {
  statementId: string;
  lines: number;
  autoMatched: number;
  errors: string[];
}

/** Finance endpoints added for statements, vouchers, opening balances and bank reconciliation. */
export interface CashFlowLine {
  code: string;
  nameEn: string;
  nameAr: string;
  amount: number;
}

export interface CashFlow {
  from: string;
  to: string;
  netProfit: number;
  sections: { kind: 'operating' | 'investing' | 'financing'; lines: CashFlowLine[]; total: number }[];
  netChange: number;
  openingCash: number;
  closingCash: number;
  reconciles: boolean;
}

@Injectable({ providedIn: 'root' })
export class FinanceApi {
  private readonly api = inject(ApiService);
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  cashFlow = (from: string, to: string) => this.api.get<CashFlow>('finance/reports/cash-flow', { from, to });
  profitAndLoss = (from: string, to: string, compareFrom?: string | null, compareTo?: string | null, costCenterId?: string | null) =>
    this.api.get<ProfitAndLoss>('finance/reports/profit-and-loss', { from, to, compareFrom, compareTo, costCenterId });
  profitAndLossPdf = (from: string, to: string, compareFrom?: string | null, compareTo?: string | null) =>
    this.api.download('finance/reports/profit-and-loss.pdf', { from, to, compareFrom, compareTo }, 'income_statement.pdf');
  balanceSheet = (asOf: string, compareAsOf?: string | null) => this.api.get<BalanceSheet>('finance/reports/balance-sheet', { asOf, compareAsOf });
  balanceSheetPdf = (asOf: string, compareAsOf?: string | null) =>
    this.api.download('finance/reports/balance-sheet.pdf', { asOf, compareAsOf }, 'balance_sheet.pdf');

  vouchers = () => this.api.get<JournalVoucher[]>('finance/journal-vouchers');
  createVoucher = (body: unknown) => this.api.post<JournalVoucher>('finance/journal-vouchers', body);
  reverseVoucher = (id: string, date: string, reason: string) => this.api.post<JournalVoucher>(`finance/journal-vouchers/${id}/reverse`, { date, reason });
  costCenters = () => this.api.get<CostCenter[]>('finance/cost-centers');

  customerOpening = (body: unknown) => this.api.post<OpeningInvoiceResult[]>('sales/opening-balances', body);
  supplierOpening = (body: unknown) => this.api.post<OpeningInvoiceResult[]>('purchasing/opening-balances', body);

  statements = (accountId?: string) => this.api.get<BankStatement[]>('finance/bank-statements', { accountId });
  statement = (id: string) => this.api.get<BankStatement>(`finance/bank-statements/${id}`);
  deleteStatement = (id: string) => this.api.delete(`finance/bank-statements/${id}`);
  autoMatch = (id: string) => this.api.post<{ matched: number }>(`finance/bank-statements/${id}/auto-match`);
  statementTemplate = () => this.api.download('finance/bank-statements/template.csv', undefined, 'bank_statement_template.csv');
  importStatement = (accountId: string, file: File, closingBalance?: number | null) => {
    const form = new FormData();
    form.append('file', file);
    const params: Record<string, string> = { accountId };
    if (closingBalance !== null && closingBalance !== undefined) params['closingBalance'] = String(closingBalance);
    return this.http.post<ImportResult>(`${this.config.apiBaseUrl}/api/finance/bank-statements/import`, form, { params });
  };
  candidates = (lineId: string) => this.api.get<LedgerItem[]>(`finance/bank-statements/lines/${lineId}/candidates`);
  match = (lineId: string, journalLineIds: string[]) => this.api.post<void>(`finance/bank-statements/lines/${lineId}/match`, { journalLineIds });
  unmatch = (lineId: string) => this.api.post<void>(`finance/bank-statements/lines/${lineId}/unmatch`);
  ignore = (lineId: string, ignored: boolean) => this.api.post<void>(`finance/bank-statements/lines/${lineId}/ignore?ignored=${ignored}`);
  postLine = (lineId: string, body: unknown) => this.api.post<void>(`finance/bank-statements/lines/${lineId}/post`, body);
  reconciliation = (accountId: string, asOf: string) => this.api.get<BankReconciliation>('finance/reports/bank-reconciliation', { accountId, asOf });
}
