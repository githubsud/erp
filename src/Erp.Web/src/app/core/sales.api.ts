import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';
import {
  CreditNote, Customer, CustomerAgingReport, CustomerExposure, CustomerReceipt, DeliveryNote, PdcReport, PriceList, Quotation, SalesAnalysisReport,
  SalesInvoice, SalesOrder, SalesSettings, Statement, UninvoicedRow
} from './sales.models';

/** Sales and receivables endpoints. */
@Injectable({ providedIn: 'root' })
export class SalesApi {
  private readonly api = inject(ApiService);

  settings = () => this.api.get<SalesSettings>('sales/settings');
  saveSettings = (body: SalesSettings) => this.api.put<SalesSettings>('sales/settings', body);

  customers = (search?: string) => this.api.get<Customer[]>('sales/customers', { search });
  customer = (id: string) => this.api.get<Customer>(`sales/customers/${id}`);
  saveCustomer = (id: string | null, body: unknown) =>
    id ? this.api.put<Customer>(`sales/customers/${id}`, body) : this.api.post<Customer>('sales/customers', body);
  exposure = (id: string) => this.api.get<CustomerExposure>(`sales/customers/${id}/exposure`);
  statement = (id: string, from: string, to: string) => this.api.get<Statement>(`sales/customers/${id}/statement`, { from, to });
  statementPdf = (id: string, from: string, to: string) => this.api.download(`sales/customers/${id}/statement.pdf`, { from, to }, 'statement.pdf');

  priceLists = () => this.api.get<PriceList[]>('sales/price-lists');
  savePriceList = (id: string | null, body: unknown) =>
    id ? this.api.put<PriceList>(`sales/price-lists/${id}`, body) : this.api.post<PriceList>('sales/price-lists', body);
  prices = (customerId: string | null, itemIds: string[]) =>
    this.api.get<{ itemId: string; unitPrice: number | null }[]>('sales/prices', { customerId, itemIds: itemIds.join(',') });

  quotations = (status?: string, customerId?: string) => this.api.get<Quotation[]>('sales/quotations', { status, customerId });
  quotation = (id: string) => this.api.get<Quotation>(`sales/quotations/${id}`);
  saveQuotation = (id: string | null, body: unknown) =>
    id ? this.api.put<Quotation>(`sales/quotations/${id}`, body) : this.api.post<Quotation>('sales/quotations', body);
  deleteQuotation = (id: string) => this.api.delete(`sales/quotations/${id}`);
  quotationAction = (id: string, action: 'send' | 'reject') => this.api.post<Quotation>(`sales/quotations/${id}/${action}`);
  convertQuotation = (id: string, body: unknown) => this.api.post<SalesOrder>(`sales/quotations/${id}/convert`, body);
  quotationPdf = (id: string) => this.api.download(`sales/quotations/${id}/pdf`, undefined, 'quotation.pdf');

  orders = (status?: string, customerId?: string) => this.api.get<SalesOrder[]>('sales/orders', { status, customerId });
  order = (id: string) => this.api.get<SalesOrder>(`sales/orders/${id}`);
  saveOrder = (id: string | null, body: unknown) =>
    id ? this.api.put<SalesOrder>(`sales/orders/${id}`, body) : this.api.post<SalesOrder>('sales/orders', body);
  orderAction = (id: string, action: 'confirm' | 'approve-credit' | 'reject' | 'cancel' | 'close') =>
    this.api.post<SalesOrder>(`sales/orders/${id}/${action}`);

  deliveries = (params: { salesOrderId?: string; customerId?: string } = {}) => this.api.get<DeliveryNote[]>('sales/deliveries', params);
  createDelivery = (body: unknown) => this.api.post<DeliveryNote>('sales/deliveries', body);
  reverseDelivery = (id: string, date: string, reason: string) => this.api.post<DeliveryNote>(`sales/deliveries/${id}/reverse`, { date, reason });
  invoiceDelivery = (id: string, invoiceDate: string) => this.api.post<SalesInvoice>(`sales/deliveries/${id}/invoice`, { invoiceDate });
  deliveryPdf = (id: string) => this.api.download(`sales/deliveries/${id}/pdf`, undefined, 'delivery_note.pdf');

  invoices = (params: { customerId?: string; status?: string; openOnly?: boolean } = {}) => this.api.get<SalesInvoice[]>('sales/invoices', params);
  invoice = (id: string) => this.api.get<SalesInvoice>(`sales/invoices/${id}`);
  createInvoice = (body: unknown) => this.api.post<SalesInvoice>('sales/invoices', body);
  deleteInvoice = (id: string) => this.api.delete(`sales/invoices/${id}`);
  postInvoice = (id: string) => this.api.post<SalesInvoice>(`sales/invoices/${id}/post`);
  reverseInvoice = (id: string, date: string, reason: string) => this.api.post<SalesInvoice>(`sales/invoices/${id}/reverse`, { date, reason });
  invoicePdf = (id: string) => this.api.download(`sales/invoices/${id}/pdf`, undefined, 'invoice.pdf');

  creditNotes = (params: { customerId?: string; salesInvoiceId?: string } = {}) => this.api.get<CreditNote[]>('sales/credit-notes', params);
  createCreditNote = (body: unknown) => this.api.post<CreditNote>('sales/credit-notes', body);
  reverseCreditNote = (id: string, date: string, reason: string) => this.api.post<CreditNote>(`sales/credit-notes/${id}/reverse`, { date, reason });
  creditNotePdf = (id: string) => this.api.download(`sales/credit-notes/${id}/pdf`, undefined, 'credit_note.pdf');

  receipts = (params: { customerId?: string; status?: string } = {}) => this.api.get<CustomerReceipt[]>('sales/receipts', params);
  createReceipt = (body: unknown) => this.api.post<CustomerReceipt>('sales/receipts', body);
  depositCheque = (id: string, date: string, bankAccountId: string) => this.api.post<CustomerReceipt>(`sales/receipts/${id}/deposit`, { date, bankAccountId });
  bounceCheque = (id: string, date: string, reason: string) => this.api.post<CustomerReceipt>(`sales/receipts/${id}/bounce`, { date, reason });
  reverseReceipt = (id: string, date: string, reason: string) => this.api.post<CustomerReceipt>(`sales/receipts/${id}/reverse`, { date, reason });

  aging = (asOf: string) => this.api.get<CustomerAgingReport>('sales/reports/aging', { asOf });
  pdc = (dueBy?: string) => this.api.get<PdcReport>('sales/reports/pdc', { dueBy });
  uninvoiced = () => this.api.get<UninvoicedRow[]>('sales/reports/uninvoiced');
  analysis = (from: string, to: string, groupBy: 'item' | 'customer') => this.api.get<SalesAnalysisReport>('sales/reports/analysis', { from, to, groupBy });
}
