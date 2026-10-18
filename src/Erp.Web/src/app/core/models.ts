/** Shapes returned by the ERP API (dates are ISO strings: yyyy-MM-dd; enums are their C# names). */

export interface UserInfo {
  id: string;
  tenantId: string;
  companyId: string | null;
  email: string;
  displayNameEn: string;
  displayNameAr: string;
  preferredLanguage: 'ar' | 'en';
}

export interface LoginResponse {
  accessToken: string | null;
  expiresAt: string | null;
  user: UserInfo | null;
  permissions: string[] | null;
  refreshToken: string | null;
  mfaRequired: boolean;
  mfaToken: string | null;
  mfaEnabled: boolean;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Problem {
  title?: string;
  status?: number;
  code?: string;
  message_ar?: string;
  details?: { errors?: (string | { record?: number; field: string; message: string })[] } & Record<string, unknown>;
}

export type Gender = 'Male' | 'Female';
export type SocialInsuranceScheme = 'None' | 'Grsia' | 'GccHome';

export interface Employee {
  id: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  nationality: string;
  gender: Gender;
  dateOfBirth: string | null;
  joiningDate: string;
  status: 'Active' | 'Terminated';
  terminationDate: string | null;
  departmentId: string | null;
  branchId: string | null;
  costCenterId: string | null;
  jobTitleEn: string | null;
  jobTitleAr: string | null;
  mobile: string | null;
  email: string | null;
  qid: string | null;
  visaId: string | null;
  passportNo: string | null;
  bankShortName: string | null;
  iban: string | null;
  inWps: boolean;
  socialInsuranceScheme: SocialInsuranceScheme;
  openingGratuityLiability: number | null;
  sensitiveMasked: boolean;
}

export interface EmployeeRequest {
  empNo: string;
  nameEn: string | null;
  nameAr: string | null;
  nationality: string;
  gender: Gender;
  dateOfBirth: string | null;
  joiningDate: string;
  departmentId: string | null;
  branchId: string | null;
  costCenterId: string | null;
  jobTitleEn: string | null;
  jobTitleAr: string | null;
  mobile: string | null;
  email: string | null;
  qid: string | null;
  visaId: string | null;
  passportNo: string | null;
  bankShortName: string | null;
  iban: string | null;
  inWps: boolean;
  socialInsuranceScheme: SocialInsuranceScheme;
  openingGratuityLiability: number;
}

export interface ContractComponent {
  code: string;
  nameEn: string;
  nameAr: string;
  amount: number;
}

export interface Contract {
  id: string;
  startDate: string;
  endDate: string | null;
  contractType: 'Limited' | 'Unlimited';
  probationMonths: number;
  accommodationProvided: boolean;
  foodProvided: boolean;
  gratuityDaysPerYear: number | null;
  gratuityBasis: 'Basic' | 'BasicPlusFixedAllowances';
  annualLeaveDays: number | null;
  status: 'Active' | 'Ended';
  components: ContractComponent[];
  monthlyGross: number;
  annualAirTicketAmount: number | null;
}

export interface EmployeeDocument {
  id: string;
  docType: string;
  docNo: string | null;
  issueDate: string | null;
  expiryDate: string | null;
  notes: string | null;
}

export interface CompanyDocument extends EmployeeDocument {
  nameEn: string;
  nameAr: string;
  responsibleUserId: string | null;
}

export interface ExpiringDocument {
  scope: 'employee' | 'company';
  documentId: string;
  docType: string;
  docNo: string | null;
  ownerNameEn: string;
  ownerNameAr: string;
  employeeId: string | null;
  expiryDate: string;
  daysLeft: number;
  status: 'Valid' | 'Expiring' | 'Expired';
}

export interface LeaveType {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isPaid: boolean;
  affectsService: boolean;
  deductsAnnualBalance: boolean;
}

export interface LeaveRequest {
  id: string;
  employeeId: string;
  leaveTypeCode: string;
  fromDate: string;
  toDate: string;
  days: number;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Cancelled';
  reason: string | null;
}

export interface LeaveBalance {
  employeeId: string;
  asOf: string;
  entitlementDaysPerYear: number;
  accrued: number;
  taken: number;
  balance: number;
}

export interface PayComponent {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: 'Earning' | 'Deduction';
  wpsField: string;
  isFixed: boolean;
  prorateByDays: boolean;
  inGratuityBasis: boolean;
  isSystem: boolean;
  isActive: boolean;
}

export type OvertimeType = 'Normal' | 'Night' | 'RestDay' | 'Holiday';

export interface PayrollInput {
  id: string;
  employeeId: string;
  year: number;
  month: number;
  componentCode: string;
  overtimeType: OvertimeType | null;
  hours: number | null;
  amount: number | null;
  notes: string | null;
}

export type PayrollStatus = 'Draft' | 'Calculated' | 'Approved' | 'Posted' | 'Paid' | 'Reversed';

export interface PayrollRun {
  id: string;
  runNo: string;
  year: number;
  month: number;
  periodStart: string;
  periodEnd: string;
  dueDate: string;
  wpsDeadline: string;
  status: PayrollStatus;
  employeeCount: number;
  totalGross: number;
  totalDeductions: number;
  totalNet: number;
  totalGratuityAccrual: number;
  totalLeaveAccrual: number;
  totalTicketAccrual: number;
  calculatedAt: string | null;
  approvedAt: string | null;
  postedAt: string | null;
  paymentDate: string | null;
  journalEntryId: string | null;
  gratuityJournalEntryId: string | null;
  paymentJournalEntryId: string | null;
}

export interface PayslipSummary {
  id: string;
  employeeId: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  paidDays: number;
  unpaidDays: number;
  gross: number;
  deductions: number;
  net: number;
  inWps: boolean;
  warnings: string | null;
  halfPayDays: number;
}

export interface PayslipLine {
  lineNo: number;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: 'Earning' | 'Deduction';
  quantity: number | null;
  rate: number | null;
  amount: number;
}

export interface Payslip {
  summary: PayslipSummary;
  basicSalary: number;
  overtimeHours: number;
  lines: PayslipLine[];
}

export interface PayrollRunDetail {
  run: PayrollRun;
  payslips: PayslipSummary[];
}

export interface WpsFile {
  id: string;
  fileName: string;
  formatVersion: string;
  payerBankShortName: string;
  totalSalaries: number;
  recordCount: number;
  sha256: string;
  status: string;
  createdAt: string;
}

export type SettlementReason = 'Resignation' | 'Termination' | 'EndOfContract' | 'Retirement' | 'Death';

export interface SettlementLine {
  lineNo: number;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: 'Earning' | 'Deduction';
  quantity: number | null;
  rate: number | null;
  amount: number;
}

export interface Settlement {
  id: string;
  settlementNo: string;
  employeeId: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  lastWorkingDay: string;
  reason: SettlementReason;
  status: 'Draft' | 'Posted' | 'Paid' | 'Reversed';
  joiningDate: string;
  serviceDays: number;
  excludedDays: number;
  basisWage: number;
  eligibleForGratuity: boolean;
  gratuityAmount: number;
  gratuityProvision: number;
  leaveBalanceDays: number;
  dailyLeaveRate: number;
  unpaidSalaryFrom: string | null;
  unpaidSalaryDays: number;
  totalEarnings: number;
  totalDeductions: number;
  netPayable: number;
  notes: string | null;
  journalEntryId: string | null;
  paymentDate: string | null;
  lines: SettlementLine[];
}

export interface Advance {
  id: string;
  advanceNo: string;
  employeeId: string;
  advanceDate: string;
  amount: number;
  monthlyRecovery: number | null;
  status: string;
  notes: string | null;
  journalEntryId: string;
}

export interface GratuityRow {
  employeeId: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  joiningDate: string;
  basisWage: number;
  serviceDays: number;
  liability: number;
  provision: number;
  eligibleForPayout: boolean;
  leaveProvision: number;
  ticketProvision: number;
}

export interface HrSettings {
  employerEid: string | null;
  payerEid: string | null;
  payerQid: string | null;
  payerBankShortName: string | null;
  payerIban: string | null;
  sifFormatVersion: string;
  gratuityDaysPerYear: number;
  gratuityDivisor: number;
  gratuityMinServiceMonths: number;
  dailyRateDivisor: number;
  hoursPerDay: number;
  overtimeNormalRate: number;
  overtimeNightRate: number;
  overtimeRestDayRate: number;
  overtimeHolidayRate: number;
  deductionCapPercent: number;
  minimumBasicWage: number;
  minimumHousingAllowance: number;
  minimumFoodAllowance: number;
  wpsDepositDays: number;
  leaveSalaryBasis: 'Basic' | 'BasicPlusFixedAllowances';
  sickFullPayDays: number;
  sickHalfPayDays: number;
  sickMinServiceMonths: number;
  siEmployeeRatePercent: number;
  siEmployerRatePercent: number;
  siMaxContributionSalary: number;
  accrueLeaveSalary: boolean;
}

export interface Department {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
}

export interface Account {
  id: string;
  parentId: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  accountType: string;
  normalBalance: string;
  isControl: boolean;
  controlSubledger: string | null;
  subtype: string | null;
  isPostable: boolean;
  isActive: boolean;
}

export interface TrialBalanceRow {
  accountId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  accountType: string;
  opening: number;
  debit: number;
  credit: number;
  closing: number;
}

export interface TrialBalance {
  from: string;
  to: string;
  rows: TrialBalanceRow[];
  totalOpening: number;
  totalDebit: number;
  totalCredit: number;
  totalClosing: number;
  isBalanced: boolean;
}

export interface JournalEntrySummary {
  id: string;
  entryNo: string;
  postingDate: string;
  sourceType: string;
  sourceId: string;
  sourceNo: string;
  reversalOfId: string | null;
  memoEn: string | null;
  memoAr: string | null;
  totalDebit: number;
}

export interface JournalLine {
  lineNo: number;
  accountId: string;
  accountCode: string;
  accountNameEn: string;
  accountNameAr: string;
  partyType: string | null;
  partyId: string | null;
  costCenterId: string | null;
  currency: string;
  fxRate: number;
  debitTxn: number;
  creditTxn: number;
  debitBase: number;
  creditBase: number;
  description: string | null;
}

export interface JournalEntryDetail {
  entry: JournalEntrySummary;
  lines: JournalLine[];
}

export interface Company {
  id: string;
  nameEn: string;
  nameAr: string;
  crNo: string | null;
  establishmentId: string | null;
  tin: string | null;
  foreignOwnershipPct: number;
  addressEn: string | null;
  addressAr: string | null;
  phone: string | null;
  email: string | null;
  baseCurrency: string;
}

export interface AppUser {
  id: string;
  email: string;
  displayNameEn: string;
  displayNameAr: string;
  preferredLanguage: string;
  isActive: boolean;
  lastLoginAt: string | null;
  roles: string[];
}

export interface Role {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isSystem: boolean;
  permissions: string[];
}
