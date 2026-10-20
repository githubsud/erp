import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { ModulesService } from './modules.service';

export interface AiStatus {
  provider: string;
  name: string;
  isPrivate: boolean;
  model: string;
  canReadImages: boolean;
  usedToday: number;
  dailyLimit: number | null;
}

export interface AskAnswer {
  answer: string;
  toolsUsed: string[];
  provider: string;
  private: boolean;
}

export interface AiSummary {
  date: string;
  language: string;
  text: string;
  facts: string;
  provider: string;
  createdAt: string;
  emailedAt: string | null;
  whatsAppUrl: string;
}

export interface AiDraft {
  text: string;
  whatsAppUrl: string | null;
  provider: string;
}

export interface DocumentFields {
  nameEn: string | null;
  nameAr: string | null;
  idNumber: string | null;
  passportNumber: string | null;
  nationality: string | null;
  dateOfBirth: string | null;
  expiryDate: string | null;
  gender: 'M' | 'F' | null;
  occupation: string | null;
}

export type DraftKind = 'payment_reminder' | 'order_ready' | 'appointment_reminder' | 'document_renewal' | 'thank_you' | 'custom';

/** Models often leave a blank line after every bullet; on a phone that doubles the card's height. */
const tidy = (s: AiSummary): AiSummary => ({ ...s, text: s.text.replace(/\r/g, '').replace(/\n\s*\n+/g, '\n').trim() });

/** The AI assistant: questions, daily summary, message drafts and ID photo reading. All of it is optional (module "ai"). */
@Injectable({ providedIn: 'root' })
export class AiService {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModulesService);

  /** The assistant is on for the company and the user may use it. */
  enabled(permission = 'ai.assistant:use'): boolean {
    return this.auth.can(permission) && !this.modules.disabled().has('ai');
  }

  status = (): Observable<AiStatus | null> => this.api.get<AiStatus | null>('ai/status');
  ask = (question: string, history: { role: string; text: string }[]) => this.api.post<AskAnswer>('ai/ask', { question, history });
  latestSummary = (language: string) => this.api.get<AiSummary | null>('ai/summary/latest', { language }).pipe(map((s) => s && tidy(s)));
  writeSummary = (language: string) => this.api.post<AiSummary>('ai/summary', { language }).pipe(map(tidy));
  draft = (kind: DraftKind, language: string, facts: Record<string, string>, phone?: string | null, instructions?: string | null) =>
    this.api.post<AiDraft>('ai/draft', { kind, language, facts, phone, instructions });

  readDocument(kind: 'qid' | 'passport', file: File): Observable<DocumentFields> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.post<DocumentFields>(`ai/read-document?kind=${kind}`, form);
  }

  /** wa.me link with the given text (keeps the phone of a link the server built). */
  static whatsApp(url: string | null, text: string): string {
    return `${(url ?? 'https://wa.me/').split('?')[0]}?text=${encodeURIComponent(text)}`;
  }
}
