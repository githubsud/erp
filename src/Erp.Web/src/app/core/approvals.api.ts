import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';

export interface ApprovalStepRule {
  minAmount: number;
  roleCode: string;
}

export interface ApprovalDocumentType {
  docType: string;
  nameEn: string;
  nameAr: string;
  steps: ApprovalStepRule[];
}

export interface ApprovalStep {
  stepNo: number;
  roleCode: string;
  decision: 'Approved' | 'Rejected' | null;
  decidedBy: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  comment: string | null;
}

export interface ApprovalRequest {
  id: string;
  docType: string;
  docTypeEn: string;
  docTypeAr: string;
  docId: string;
  docNo: string;
  amount: number;
  summaryEn: string;
  summaryAr: string;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn';
  currentStep: number;
  requestedBy: string;
  requestedByName: string | null;
  requestedAt: string;
  decidedAt: string | null;
  steps: ApprovalStep[];
  canDecide: boolean;
}

/** Where each document type opens in the app. */
export const APPROVAL_LINKS: Record<string, (id: string) => string> = {
  purchase_order: (id) => `/purchasing/orders/${id}`,
  purchase_request: () => '/purchasing/requests'
};

@Injectable({ providedIn: 'root' })
export class ApprovalsApi {
  private readonly api = inject(ApiService);

  rules = () => this.api.get<ApprovalDocumentType[]>('approvals/rules');
  saveRules = (docType: string, steps: ApprovalStepRule[]) => this.api.put<ApprovalDocumentType>(`approvals/rules/${docType}`, steps);
  settings = () => this.api.get<{ allowSelfApproval: boolean }>('approvals/settings');
  saveSettings = (allowSelfApproval: boolean) => this.api.put<{ allowSelfApproval: boolean }>('approvals/settings', { allowSelfApproval });
  inbox = () => this.api.get<ApprovalRequest[]>('approvals/inbox');
  requests = (params: { docType?: string; docId?: string; mine?: boolean }) => this.api.get<ApprovalRequest[]>('approvals/requests', params);
  approve = (id: string, comment: string | null) => this.api.post<void>(`approvals/${id}/approve`, { comment });
  reject = (id: string, comment: string) => this.api.post<void>(`approvals/${id}/reject`, { comment });
}
