/** Wire types for the auth and resource APIs. JSON is snake_case throughout. */

export interface ChallengeResponse {
  challenge_id: string;
  expires_in: number;
  resend_in: number;
}

export interface TokenResponse {
  access_token: string;
  token_type: string;
  expires_in: number;
}

export interface UserInfo {
  id: string;
  name: string;
  phone: string;
  org: string;
}

export interface MeResponse {
  id: string;
  org: string;
  name: string;
}

export interface Card {
  id: string;
  brand: string;
  last4: string;
  expiry_month: number;
  expiry_year: number;
  status: string;
  currency: string;
  display_order: number;
  art_url: string;
}

export interface CardListResponse {
  items: Card[];
}

export interface Transaction {
  id: string;
  card_id: string;
  amount_minor: number;
  currency: string;
  direction: "debit" | "credit";
  merchant: string;
  status: "posted" | "pending";
  occurred_at: string;
}

export interface TransactionListResponse {
  items: Transaction[];
  next_cursor: string | null;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  error?: {
    code?: string;
    message?: string;
    errors?: Record<string, string[]>;
  };
}

export type FieldErrors = Record<string, string[]>;
