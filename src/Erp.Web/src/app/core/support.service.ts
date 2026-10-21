import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { ApiService } from './api.service';
import { ConfigService } from './config.service';

export type TicketType = 'Problem' | 'Question' | 'Suggestion' | 'Requirement';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export type TicketStatus = 'New' | 'InProgress' | 'WaitingCustomer' | 'Solved' | 'Closed';

export const TICKET_TYPES: TicketType[] = ['Problem', 'Question', 'Suggestion', 'Requirement'];
export const TICKET_PRIORITIES: TicketPriority[] = ['Low', 'Normal', 'High', 'Urgent'];
export const TICKET_STATUSES: TicketStatus[] = ['New', 'InProgress', 'WaitingCustomer', 'Solved', 'Closed'];
export const TYPE_ICON: Record<TicketType, string> = { Problem: 'bug_report', Question: 'help', Suggestion: 'lightbulb', Requirement: 'playlist_add' };

/** Pictures (phone screenshots), PDF, Excel and text, as the server accepts. */
export const SUPPORT_FILE_ACCEPT = '.png,.jpg,.jpeg,.webp,.gif,.pdf,.xlsx,.csv,.txt';
export const SUPPORT_MAX_FILES = 5;
export const SUPPORT_MAX_BYTES = 5 * 1024 * 1024;

export interface TicketRow {
  id: string;
  ticketNo: string;
  tenantId: string;
  customerCode: string | null;
  customerName: string | null;
  type: TicketType;
  priority: TicketPriority;
  status: TicketStatus;
  subject: string;
  requesterName: string;
  createdAt: string;
  lastActivityAt: string;
  responseDueAt: string;
  firstResponseAt: string | null;
  overdue: boolean;
  unread: boolean;
  duplicateOfNo: string | null;
}

export interface TicketFile {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
}

export interface TicketMessage {
  id: string;
  authorKind: 'Customer' | 'Admin';
  authorName: string;
  body: string;
  isInternal: boolean;
  createdAt: string;
  attachments: TicketFile[];
}

export interface TicketDetail {
  ticket: TicketRow;
  language: 'ar' | 'en';
  page: string | null;
  device: string | null;
  requesterEmail: string | null;
  messages: TicketMessage[];
  duplicates: TicketRow[];
}

export interface NewTicket {
  type: TicketType;
  priority: TicketPriority;
  subject: string;
  body: string;
  language: string;
  page: string | null;
  files: File[];
}

/** Files over the limit or of another type, as a translation key with the file name; null when all are fine. */
export function checkSupportFiles(files: File[]): { key: string; name?: string } | null {
  if (files.length > SUPPORT_MAX_FILES) return { key: 'help.tooManyFiles' };
  const allowed = SUPPORT_FILE_ACCEPT.split(',');
  for (const f of files) {
    if (!allowed.some((ext) => f.name.toLowerCase().endsWith(ext))) return { key: 'help.fileType', name: f.name };
    if (f.size > SUPPORT_MAX_BYTES) return { key: 'help.fileSize', name: f.name };
  }
  return null;
}

/** Opens a downloaded file in a new tab (pictures, PDF) or saves it (anything else). */
export function openBlob(blob: Blob, file: TicketFile): void {
  const href = URL.createObjectURL(blob);
  if (file.contentType.startsWith('image/') || file.contentType === 'application/pdf') window.open(href, '_blank', 'noopener');
  else {
    const a = document.createElement('a');
    a.href = href;
    a.download = file.fileName;
    a.click();
  }
  setTimeout(() => URL.revokeObjectURL(href), 60_000);
}

/** The company's requests to the vendor ("Help & suggestions"), and the count of answers not yet read for the toolbar badge. */
@Injectable({ providedIn: 'root' })
export class SupportService {
  private readonly api = inject(ApiService);
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  readonly unread = signal(0);
  private timer: ReturnType<typeof setInterval> | null = null;

  /** Checks for new answers now and every five minutes while the shell is open. */
  watch(destroy: DestroyRef): void {
    this.refreshUnread();
    if (this.timer) return;
    this.timer = setInterval(() => this.refreshUnread(), 5 * 60_000);
    destroy.onDestroy(() => {
      if (this.timer) clearInterval(this.timer);
      this.timer = null;
    });
  }

  refreshUnread(): void {
    if (!navigator.onLine) return;
    this.api.get<{ count: number }>('support/unread').subscribe({ next: (r) => this.unread.set(r.count), error: () => undefined });
  }

  tickets = () => this.api.get<TicketRow[]>('support/tickets');

  ticket = (id: string) => this.api.get<TicketDetail>(`support/tickets/${id}`).pipe(tap(() => this.refreshUnread()));

  create(t: NewTicket): Observable<TicketDetail> {
    const form = new FormData();
    form.append('type', t.type);
    form.append('priority', t.priority);
    form.append('subject', t.subject);
    form.append('body', t.body);
    form.append('language', t.language);
    if (t.page) form.append('page', t.page);
    for (const f of t.files) form.append('files', f, f.name);
    return this.api.post<TicketDetail>('support/tickets', form);
  }

  reply(id: string, body: string, files: File[]): Observable<TicketDetail> {
    const form = new FormData();
    form.append('body', body);
    for (const f of files) form.append('files', f, f.name);
    return this.api.post<TicketDetail>(`support/tickets/${id}/messages`, form);
  }

  solve = (id: string) => this.api.post<TicketDetail>(`support/tickets/${id}/solve`);

  open(file: TicketFile): void {
    this.http.get(`${this.config.apiBaseUrl}/api/support/attachments/${file.id}`, { responseType: 'blob' })
      .subscribe((b) => openBlob(b, file));
  }
}
