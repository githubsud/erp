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
  onBehalfOfName?: string | null;
}

export interface Delegation {
  id: string;
  delegatorUserId: string;
  delegatorName: string;
  delegateUserId: string;
  delegateName: string;
  fromDate: string;
  toDate: string;
  docType: string | null;
  reason: string | null;
  active: boolean;
  revokedAt: string | null;
}

export interface Colleague {
  id: string;
  displayNameEn: string;
  displayNameAr: string;
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
  delegations = () => this.api.get<Delegation[]>('approvals/delegations');
  colleagues = () => this.api.get<Colleague[]>('approvals/delegates');
  delegate = (body: { delegateUserId: string; fromDate: string; toDate: string; docType: string | null; reason: string | null }) =>
    this.api.post<string>('approvals/delegations', body);
  revoke = (id: string) => this.api.post<void>(`approvals/delegations/${id}/revoke`, {});
}
