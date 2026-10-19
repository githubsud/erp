// Inventory and purchasing (the Trade bundle). Mirrors the API DTOs.

export interface Master {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
  branchId?: string | null;
}

export type ItemType = 'Stock' | 'Service';

export interface Item {
  id: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  itemType: ItemType;
  itemGroupId: string;
  uomId: string;
  uomCode: string;
  barcode: string | null;
  reorderLevel: number | null;
  purchasePrice: number | null;
  salesPrice: number | null;
  hsCode: string | null;
  isActive: boolean;
  quantityOnHand: number;
  averageCost: number;
  stockValue: number;
  trackBatches: boolean;
  trackSerials?: boolean;
  reorderQuantity?: number | null;
}

/** A unit an item is bought or sold in; one holds `factor` base units (the base unit has factor 1). */
export interface ItemUnit {
  itemId: string;
  uomId: string;
  uomCode: string;
  factor: number;
  isBase: boolean;
}

export interface StockRow {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  uomCode: string;
  itemGroupId: string;
  quantity: number;
  averageCost: number;
  value: number;
  reorderLevel: number | null;
  belowReorder: boolean;
}

export interface ValuationReport {
  asOf: string;
  rows: { itemId: string; sku: string; nameEn: string; nameAr: string; quantity: number; value: number }[];
  totalValue: number;
  ledgerBalance: number;
  difference: number;
}

export interface LedgerRow {
  id: string;
  date: string;
  sourceType: string;
  sourceNo: string;
  sourceId: string;
  warehouseId: string | null;
  quantity: number;
  unitCost: number;
  value: number;
  quantityAfter: number;
  valueAfter: number;
  isReversal: boolean;
}

export interface ItemLedger {
  itemId: string;
  from: string;
  to: string;
  openingQuantity: number;
  openingValue: number;
  rows: LedgerRow[];
}

export type AdjustmentReason = 'Opening' | 'Damage' | 'Expiry' | 'Count' | 'Other';

export interface DocLine {
  lineNo: number;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  quantity: number;
  unitCost?: number | null;
  value: number;
}

export interface Adjustment {
  id: string;
  adjustmentNo: string;
  warehouseId: string;
  adjustmentDate: string;
  reason: AdjustmentReason;
  status: 'Posted' | 'Reversed';
  notes: string | null;
  totalValue: number;
  journalEntryId: string | null;
  stockCountId: string | null;
  lines: DocLine[];
}

export interface Transfer {
  id: string;
  transferNo: string;
  fromWarehouseId: string;
  toWarehouseId: string;
  transferDate: string;
  status: 'Posted' | 'Reversed';
  notes: string | null;
  lines: DocLine[];
}

export interface CountLine {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  uomCode: string;
  systemQuantity: number;
  countedQuantity: number | null;
  variance: number | null;
}

export interface StockCount {
  id: string;
  countNo: string;
  warehouseId: string;
  countDate: string;
  status: 'Open' | 'Posted' | 'Cancelled';
  notes: string | null;
  stockAdjustmentId: string | null;
  lineCount: number;
  countedLines: number;
  lines: CountLine[];
}

export type WhtCategory = 'None' | 'Royalties' | 'TechnicalFees' | 'Interest' | 'Commissions' | 'Brokerage' | 'DirectorFees' | 'AttendanceFees' | 'OtherServices';

export const WHT_CATEGORIES: WhtCategory[] = [
  'None', 'Royalties', 'TechnicalFees', 'Interest', 'Commissions', 'Brokerage', 'DirectorFees', 'AttendanceFees', 'OtherServices'
];

export interface Supplier {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  country: string;
  isResident: boolean;
  hasPermanentEstablishment: boolean;
  subjectToWht: boolean;
  taxCardNo: string | null;
  crNo: string | null;
  email: string | null;
  phone: string | null;
  paymentTermsDays: number;
  defaultWhtCategory: WhtCategory;
  isActive: boolean;
  currency: string;
}

/** Currencies Qatari importers deal in most; any ISO code is accepted by the server. */
export const CURRENCIES = ['QAR', 'USD', 'EUR', 'GBP', 'AED', 'SAR', 'KWD', 'OMR', 'BHD', 'INR', 'CNY', 'JPY', 'TRY', 'EGP'];

export type OrderStatus = 'Draft' | 'PendingApproval' | 'Approved' | 'PartiallyReceived' | 'Received' | 'Closed' | 'Cancelled';

export interface OrderLine {
  id: string;
  lineNo: number;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  itemType: ItemType;
  uomCode: string;
  description: string | null;
  quantity: number;
  unitPrice: number;
  amount: number;
  quantityReceived: number;
  quantityInvoiced: number;
  whtCategory: WhtCategory;
  uomId: string | null;
  orderUomCode: string | null;
  uomFactor: number;
  uomQuantity: number;
  uomPrice: number;
}

export interface PurchaseOrder {
  id: string;
  poNo: string;
  supplierId: string;
  supplierCode: string;
  supplierNameEn: string;
  supplierNameAr: string;
  orderDate: string;
  expectedDate: string | null;
  warehouseId: string;
  status: OrderStatus;
  total: number;
  notes: string | null;
  requiresApproval: boolean;
  approvedAt: string | null;
  gtaNotificationDue: string | null;
  gtaNotificationRef: string | null;
  lines: OrderLine[];
  currency: string;
  exchangeRate: number;
}

export interface ReceiptLine {
  id: string;
  lineNo: number;
  purchaseOrderLineId: string;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  quantity: number;
  unitCost: number;
  value: number;
  quantityInvoiced: number;
}

export interface GoodsReceipt {
  id: string;
  grnNo: string;
  purchaseOrderId: string;
  poNo: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  warehouseId: string;
  receiptDate: string;
  supplierDeliveryNote: string | null;
  status: 'Posted' | 'Reversed';
  totalValue: number;
  journalEntryId: string | null;
  lines: ReceiptLine[];
}

export type InvoiceLineType = 'Goods' | 'Service' | 'LandedCost';
export type InvoiceStatus = 'Draft' | 'Posted' | 'PartiallyPaid' | 'Paid' | 'Reversed';
export type MatchStatus = 'NotApplicable' | 'Matched' | 'Variance' | 'Approved';

export interface InvoiceLine {
  id: string;
  lineNo: number;
  lineType: InvoiceLineType;
  goodsReceiptLineId: string | null;
  purchaseOrderLineId: string | null;
  itemId: string | null;
  sku: string | null;
  description: string | null;
  quantity: number;
  unitPrice: number;
  amount: number;
  receiptUnitCost: number | null;
  variance: number;
  whtCategory: WhtCategory;
  whtRate: number;
  whtAmount: number;
  quantityReturned: number;
}

export interface SupplierInvoice {
  id: string;
  invoiceNo: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  supplierRef: string;
  invoiceDate: string;
  dueDate: string;
  purchaseOrderId: string | null;
  total: number;
  whtAmount: number;
  paidAmount: number;
  openAmount: number;
  status: InvoiceStatus;
  matchStatus: MatchStatus;
  notes: string | null;
  journalEntryId: string | null;
  lines: InvoiceLine[];
  isOpening: boolean;
  debitedAmount: number;
  currency: string;
  exchangeRate: number;
  totalBase: number;
  openBase: number;
}

export interface DebitNote {
  id: string;
  debitNoteNo: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  supplierInvoiceId: string;
  invoiceNo: string;
  supplierRef: string;
  noteDate: string;
  warehouseId: string | null;
  reason: string;
  total: number;
  status: 'Posted' | 'Reversed';
  journalEntryId: string | null;
  lines: { lineNo: number; lineType: 'Return' | 'Allowance'; supplierInvoiceLineId: string | null; itemId: string | null; sku: string | null;
    description: string | null; quantity: number; unitPrice: number; amount: number; stockValue: number }[];
}

export type ChargeType = 'Freight' | 'Clearing' | 'CustomsDuty' | 'Insurance' | 'Other';

export interface LandedCost {
  id: string;
  voucherNo: string;
  voucherDate: string;
  method: 'Value' | 'Quantity';
  status: 'Posted' | 'Reversed';
  totalAmount: number;
  notes: string | null;
  journalEntryId: string | null;
  charges: { lineNo: number; chargeType: ChargeType; description: string | null; amount: number; supplierInvoiceId: string | null }[];
  allocations: { goodsReceiptLineId: string; itemId: string; sku: string; basis: number; amount: number; toInventory: number; toCostOfSales: number }[];
}

export interface SupplierPayment {
  id: string;
  paymentNo: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  paymentDate: string;
  paymentAccountId: string;
  reference: string | null;
  grossAmount: number;
  whtAmount: number;
  netAmount: number;
  status: 'Posted' | 'Reversed';
  journalEntryId: string | null;
  allocations: { supplierInvoiceId: string; invoiceNo: string; supplierRef: string; amount: number; whtAmount: number }[];
  currency: string;
  exchangeRate: number;
  fxDifference: number;
  chequeNo: string | null;
  chequeDate: string | null;
  pdcStatus: 'Issued' | 'Cleared' | null;
  pdcClearedDate: string | null;
}

export interface PurchaseSettings {
  priceTolerancePercent: number;
  taxClearanceRetentionPercent?: number;
}

export interface GrniReport {
  rows: { goodsReceiptId: string; grnNo: string; receiptDate: string; supplierNameEn: string; supplierNameAr: string; sku: string;
    quantityNotInvoiced: number; unitCost: number; value: number }[];
  total: number;
  ledgerBalance: number;
  difference: number;
}

export interface AgingReport {
  asOf: string;
  rows: { supplierId: string; code: string; nameEn: string; nameAr: string; current: number; days1To30: number; days31To60: number;
    days61To90: number; over90: number; total: number }[];
  total: number;
  ledgerBalance: number;
}

export interface WhtReport {
  year: number;
  month: number;
  filingDeadline: string;
  rows: { paymentDate: string; paymentNo: string; supplierNameEn: string; supplierNameAr: string; country: string; taxCardNo: string | null;
    invoiceNo: string; categories: string; gross: number; rate: number; tax: number }[];
  totalGross: number;
  totalTax: number;
}
