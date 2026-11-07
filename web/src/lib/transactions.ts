import type { Transaction } from "../api/types";
import { formatDate, localDateKey } from "./format";

export interface TransactionDateGroup {
  /** Local calendar day, `YYYY-MM-DD`. */
  key: string;
  /** `31 Oct 2026`, in the browser's local time zone. */
  label: string;
  transactions: Transaction[];
}

/**
 * Groups transactions by local calendar day while preserving the API's
 * newest-first order. Groups appear in first-seen order and rows keep their
 * order inside a group; nothing is sorted here.
 */
export function groupTransactionsByDate(items: Transaction[]): TransactionDateGroup[] {
  const groups: TransactionDateGroup[] = [];
  const byKey = new Map<string, TransactionDateGroup>();

  for (const transaction of items) {
    const key = localDateKey(transaction.occurred_at);
    let group = byKey.get(key);
    if (!group) {
      group = {
        key,
        label: formatDate(transaction.occurred_at),
        transactions: [],
      };
      byKey.set(key, group);
      groups.push(group);
    }
    group.transactions.push(transaction);
  }

  return groups;
}

/**
 * Appends a cursor page, dropping any transaction whose id is already present.
 * A cursor page can overlap the previous one if rows arrive while paging, so
 * the client never trusts the server to be disjoint.
 */
export function appendUniqueTransactions(
  existing: Transaction[],
  incoming: Transaction[],
): Transaction[] {
  const seen = new Set(existing.map((transaction) => transaction.id));
  const appended: Transaction[] = [];
  for (const transaction of incoming) {
    if (seen.has(transaction.id)) continue;
    seen.add(transaction.id);
    appended.push(transaction);
  }
  return appended.length === 0 ? existing : [...existing, ...appended];
}
