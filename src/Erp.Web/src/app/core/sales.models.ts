export type CreditControl = 'None' | 'Warn' | 'Block';
export type QuotationStatus = 'Draft' | 'Sent' | 'Accepted' | 'Rejected';
export type SalesOrderStatus = 'Draft' | 'CreditHold' | 'Confirmed' | 'PartiallyDelivered' | 'Delivered' | 'Closed' | 'Cancelled';
export type SalesInvoiceStatus = 'Draft' | 'Posted' | 'PartiallyPaid' | 'Paid' | 'Reversed';
export type SalesInvoiceLineType = 'Delivered' | 'Direct' | 'Service';
export type CreditNoteLineType = 'Return' | 'Allowance';
export type ReceiptMethod = 'Cash' | 'BankTransfer' | 'Cheque' | 'Card' | 'PostDatedCheque';
export type ReceiptStatus = 'Posted' | 'PdcHeld' | 'PdcCleared' | 'Bounced' | 'Reversed';

export const RECEIPT_METHODS: ReceiptMethod[] = ['BankTransfer', 'Cash', 'Cheque', 'Card', 'PostDatedCheque'];

export interface Customer {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  country: string;
  taxCardNo: string | null;
  crNo: string | null;
  email: string | null;
  phone: string | null;
  addressEn: string | null;
  addressAr: string | null;
  paymentTermsDays: number;
  creditLimit: number | null;
  priceListId: string | null;
  salespersonUserId: string | null;
  isActive: boolean;
  currency: string;
}

export interface CustomerExposure {
  customerId: string;
  receivable: number;
  chequesInHand: number;
  openOrders: number;
  exposure: number;
  creditLimit: number | null;
  available: number | null;
}

export interface SalesSettings {
  creditControl: CreditControl;
  quotationValidityDays: number;
}

export interface PriceListItem {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  unitPrice: number;
}

export interface PriceList {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
  items: PriceListItem[];
}

export interface SalesLine {
  id: string;
  lineNo: number;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  itemType: 'Stock' | 'Service';
  uomCode: string;
  description: string | null;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  amount: number;
  quantityDelivered: number;
  quantityInvoiced: number;
  uomId: string | null;
  orderUomCode: string | null;
  uomFactor: number;
  uomQuantity: number;
  uomPrice: number;
}

export interface Quotation {
  id: string;
  quoteNo: string;
  customerId: string;
  customerCode: string;
  customerNameEn: string;
  customerNameAr: string;
  quoteDate: string;
  validUntil: string;
  isExpired: boolean;
  status: QuotationStatus;
  total: number;
  notes: string | null;
  salesOrderId: string | null;
  lines: SalesLine[];
  currency: string;
  exchangeRate: number;
}

export interface SalesOrder {
  id: string;
  soNo: string;
  customerId: string;
  customerCode: string;
  customerNameEn: string;
  customerNameAr: string;
  orderDate: string;
  expectedDate: string | null;
  warehouseId: string;
  customerRef: string | null;
  quotationId: string | null;
  status: SalesOrderStatus;
  total: number;
  notes: string | null;
  creditWarning: string | null;
  creditApprovedAt: string | null;
  lines: SalesLine[];
  currency: string;
  exchangeRate: number;
}

export interface DeliveryLine {
  id: string;
  lineNo: number;
  salesOrderLineId: string;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  uomCode: string;
  quantity: number;
  unitCost: number;
  cost: number;
  quantityInvoiced: number;
}

export interface DeliveryNote {
  id: string;
  dnNo: string;
  salesOrderId: string;
  soNo: string;
  customerId: string;
  customerNameEn: string;
  customerNameAr: string;
  warehouseId: string;
  deliveryDate: string;
  deliveryAddress: string | null;
  vehicleNo: string | null;
  notes: string | null;
  status: 'Posted' | 'Reversed';
  totalCost: number;
  journalEntryId: string | null;
  lines: DeliveryLine[];
}

export interface SalesInvoiceLine {
  id: string;
  lineNo: number;
  lineType: SalesInvoiceLineType;
  deliveryNoteLineId: string | null;
  salesOrderLineId: string | null;
  itemId: string | null;
  sku: string | null;
  nameEn: string | null;
  nameAr: string | null;
  description: string | null;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  amount: number;
  unitCost: number | null;
  cost: number;
  quantityReturned: number;
}

export interface SalesInvoice {
  id: string;
  invoiceNo: string;
  customerId: string;
  customerCode: string;
  customerNameEn: string;
  customerNameAr: string;
  invoiceDate: string;
  dueDate: string;
  salesOrderId: string | null;
  warehouseId: string | null;
  customerRef: string | null;
  total: number;
  discountTotal: number;
  paidAmount: number;
  creditedAmount: number;
  openAmount: number;
  status: SalesInvoiceStatus;
  notes: string | null;
  eInvoiceUuid: string | null;
  issuedAt: string | null;
  journalEntryId: string | null;
  lines: SalesInvoiceLine[];
  currency: string;
  exchangeRate: number;
  totalBase: number;
  openBase: number;
}

export interface CreditNoteLine {
  lineNo: number;
  lineType: CreditNoteLineType;
  salesInvoiceLineId: string | null;
  itemId: string | null;
  sku: string | null;
  description: string | null;
  quantity: number;
  unitPrice: number;
  amount: number;
  cost: number;
}

export interface CreditNote {
  id: string;
  creditNoteNo: string;
  customerId: string;
  customerNameEn: string;
  customerNameAr: string;
  salesInvoiceId: string;
  invoiceNo: string;
  noteDate: string;
  warehouseId: string | null;
  reason: string;
  total: number;
  costReturned: number;
  status: 'Posted' | 'Reversed';
  journalEntryId: string | null;
  lines: CreditNoteLine[];
  unappliedAmount: number;
}

export interface CustomerReceipt {
  id: string;
  receiptNo: string;
  customerId: string;
  customerNameEn: string;
  customerNameAr: string;
  receiptDate: string;
  method: ReceiptMethod;
  depositAccountId: string | null;
  reference: string | null;
  chequeNo: string | null;
  chequeDate: string | null;
  chequeBank: string | null;
  amount: number;
  status: ReceiptStatus;
  clearedDate: string | null;
  statusReason: string | null;
  journalEntryId: string | null;
  allocations: { salesInvoiceId: string; invoiceNo: string; amount: number }[];
  unappliedAmount: number;
  currency: string;
  exchangeRate: number;
  amountBase: number;
  fxDifference: number;
}

export interface CustomerAgingRow {
  customerId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  current: number;
  days1To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  total: number;
  creditLimit: number | null;
}

export interface CustomerAgingReport {
  asOf: string;
  rows: CustomerAgingRow[];
  total: number;
  ledgerBalance: number;
}

export interface StatementLine {
  date: string;
  sourceType: string;
  sourceId: string;
  sourceNo: string;
  memoEn: string | null;
  memoAr: string | null;
  debit: number;
  credit: number;
  balance: number;
}

export interface Statement {
  customerId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  from: string;
  to: string;
  openingBalance: number;
  lines: StatementLine[];
  closingBalance: number;
}

export interface PdcRow {
  receiptId: string;
  receiptNo: string;
  customerId: string;
  customerNameEn: string;
  customerNameAr: string;
  chequeNo: string | null;
  chequeBank: string | null;
  chequeDate: string;
  amount: number;
  daysToDue: number;
}

export interface PdcReport {
  rows: PdcRow[];
  total: number;
  ledgerBalance: number;
}

export interface UninvoicedRow {
  deliveryNoteId: string;
  dnNo: string;
  deliveryDate: string;
  customerId: string;
  customerNameEn: string;
  customerNameAr: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  quantity: number;
  value: number;
}

export interface SalesAnalysisRow {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  quantity: number;
  revenue: number;
  returns: number;
  netRevenue: number;
  cost: number;
  margin: number;
  marginPercent: number | null;
}

export interface SalesAnalysisReport {
  from: string;
  to: string;
  groupBy: 'item' | 'customer';
  rows: SalesAnalysisRow[];
  netRevenue: number;
  cost: number;
  margin: number;
}
