import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, apiFetch } from "../api/client";
import type { Transaction, TransactionListResponse } from "../api/types";
import { formatMoney, formatTime } from "../lib/format";
import { appendUniqueTransactions, groupTransactionsByDate } from "../lib/transactions";
import { ErrorState } from "./ErrorState";
import { TransactionListSkeleton } from "./Skeleton";

export const TRANSACTIONS_PAGE_SIZE = 10;

/** One page of transactions plus a cursor when another page exists. */
export function transactionsPath(cardId: string, cursor?: string | null): string {
  const base = `/api/v1/me/cards/${encodeURIComponent(cardId)}/transactions?limit=${TRANSACTIONS_PAGE_SIZE}`;
  return cursor ? `${base}&cursor=${encodeURIComponent(cursor)}` : base;
}

function messageOf(error: unknown): string {
  return error instanceof ApiError
    ? error.message
    : "Your transactions could not be loaded. Try again.";
}

export function isUnauthorized(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401;
}

type TransactionsState =
  | { status: "loading" }
  | {
      status: "ready";
      items: Transaction[];
      nextCursor: string | null;
      loadingMore: boolean;
      loadMoreError: string | null;
    }
  | { status: "error"; message: string };

function TransactionRow({ transaction }: { transaction: Transaction }) {
  const pending = transaction.status === "pending";
  return (
    <li className="flex items-start justify-between gap-3 px-4 py-3">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="break-words font-medium text-slate-900">
            {transaction.merchant}
          </span>
          {pending ? (
            <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-semibold text-amber-900">
              Pending
            </span>
          ) : null}
        </div>
        <time
          dateTime={transaction.occurred_at}
          className="mt-0.5 block text-xs text-slate-500"
        >
          {formatTime(transaction.occurred_at)}
        </time>
      </div>
      <span
        className={`shrink-0 pl-2 text-right font-semibold tabular-nums ${
          transaction.direction === "credit" ? "text-emerald-700" : "text-slate-900"
        }`}
      >
        {formatMoney(transaction.amount_minor, transaction.currency, transaction.direction)}
      </span>
    </li>
  );
}

export function TransactionList({ cardId }: { cardId: string }) {
  const [state, setState] = useState<TransactionsState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const loadingMoreRef = useRef(false);

  useEffect(() => {
    let active = true;
    loadingMoreRef.current = false;
    setState({ status: "loading" });
    apiFetch<TransactionListResponse>(transactionsPath(cardId))
      .then((response) => {
        if (!active) return;
        setState({
          status: "ready",
          items: response.items ?? [],
          nextCursor: response.next_cursor ?? null,
          loadingMore: false,
          loadMoreError: null,
        });
      })
      .catch((error: unknown) => {
        if (!active) return;
        // The single client cleared the session and redirected; the page must
        // not flash an error block while the router leaves it.
        if (isUnauthorized(error)) return;
        setState({ status: "error", message: messageOf(error) });
      });
    return () => {
      active = false;
    };
  }, [attempt, cardId]);

  const retry = useCallback(() => setAttempt((value) => value + 1), []);

  const loadMore = useCallback(async () => {
    if (loadingMoreRef.current) return;
    const cursor = state.status === "ready" ? state.nextCursor : null;
    if (!cursor) return;

    loadingMoreRef.current = true;
    setState((current) =>
      current.status === "ready"
        ? { ...current, loadingMore: true, loadMoreError: null }
        : current,
    );

    try {
      const response = await apiFetch<TransactionListResponse>(transactionsPath(cardId, cursor));
      setState((current) =>
        current.status === "ready"
          ? {
              ...current,
              items: appendUniqueTransactions(current.items, response.items ?? []),
              nextCursor: response.next_cursor ?? null,
              loadingMore: false,
              loadMoreError: null,
            }
          : current,
      );
    } catch (error: unknown) {
      if (isUnauthorized(error)) {
        setState((current) =>
          current.status === "ready" ? { ...current, loadingMore: false } : current,
        );
        return;
      }
      // Keep the rows already on screen and retry the same cursor.
      setState((current) =>
        current.status === "ready"
          ? { ...current, loadingMore: false, loadMoreError: messageOf(error) }
          : current,
      );
    } finally {
      loadingMoreRef.current = false;
    }
  }, [cardId, state]);

  const groups =
    state.status === "ready" ? groupTransactionsByDate(state.items) : [];

  return (
    <section aria-labelledby="transactions-heading" className="space-y-4">
      <h2 id="transactions-heading" className="text-lg font-semibold text-slate-900">
        Transactions
      </h2>

      {state.status === "loading" ? <TransactionListSkeleton /> : null}

      {state.status === "error" ? <ErrorState message={state.message} onRetry={retry} /> : null}

      {state.status === "ready" && state.items.length === 0 ? (
        <p className="rounded-xl border border-dashed border-slate-300 bg-white p-6 text-sm text-slate-600">
          No transactions yet.
        </p>
      ) : null}

      {state.status === "ready" && state.items.length > 0 ? (
        <div className="space-y-4">
          {groups.map((group) => (
            <section key={group.key} aria-labelledby={`transactions-${group.key}`}>
              <h3
                id={`transactions-${group.key}`}
                className="mb-2 text-sm font-semibold text-slate-600"
              >
                {group.label}
              </h3>
              <ul
                aria-labelledby={`transactions-${group.key}`}
                className="divide-y divide-slate-200 overflow-hidden rounded-xl border border-slate-200 bg-white"
              >
                {group.transactions.map((transaction) => (
                  <TransactionRow key={transaction.id} transaction={transaction} />
                ))}
              </ul>
            </section>
          ))}
        </div>
      ) : null}

      {state.status === "ready" && state.nextCursor ? (
        <div className="space-y-2">
          <button
            type="button"
            onClick={loadMore}
            disabled={state.loadingMore}
            aria-busy={state.loadingMore}
            className="inline-flex min-h-[44px] w-full items-center justify-center rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-900 hover:bg-slate-100 disabled:cursor-wait disabled:text-slate-500 sm:w-auto"
          >
            {state.loadingMore ? "Loading…" : "Load more"}
          </button>
          {state.loadMoreError ? (
            <ErrorState
              message={`Could not load more transactions. ${state.loadMoreError}`}
              onRetry={loadMore}
              retryLabel="Retry loading more"
            />
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
