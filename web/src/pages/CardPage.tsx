import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ApiError, apiFetch } from "../api/client";
import type { Card, CardListResponse } from "../api/types";
import { CardFace } from "../components/CardFace";
import { ErrorState } from "../components/ErrorState";
import { CardSummarySkeleton } from "../components/Skeleton";
import { formatExpiry, formatMaskedNumber, formatStatus } from "../lib/format";

type CardState =
  | { status: "loading" }
  | { status: "ready"; card: Card }
  | { status: "missing" }
  | { status: "error"; message: string };

export function CardPage() {
  const { cardId } = useParams<{ cardId: string }>();
  const [state, setState] = useState<CardState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let active = true;
    setState({ status: "loading" });
    apiFetch<CardListResponse>("/api/v1/me/cards")
      .then((response) => {
        if (!active) return;
        const card = (response.items ?? []).find((item) => item.id === cardId);
        setState(card ? { status: "ready", card } : { status: "missing" });
      })
      .catch((error: unknown) => {
        if (!active) return;
        if (error instanceof ApiError && error.status === 401) return;
        setState({
          status: "error",
          message:
            error instanceof ApiError
              ? error.message
              : "This card could not be loaded. Try again.",
        });
      });
    return () => {
      active = false;
    };
  }, [attempt, cardId]);

  const retry = useCallback(() => setAttempt((value) => value + 1), []);

  return (
    <div className="space-y-5">
      <Link
        to="/wallet"
        className="inline-flex min-h-[44px] items-center rounded-lg px-1 text-sm font-semibold text-slate-700 underline-offset-2 hover:underline"
      >
        ← Back to wallet
      </Link>

      {state.status === "loading" ? <CardSummarySkeleton /> : null}

      {state.status === "error" ? <ErrorState message={state.message} onRetry={retry} /> : null}

      {state.status === "missing" ? (
        <div className="rounded-xl border border-slate-200 bg-white p-6">
          <h1 className="text-xl font-bold text-slate-900">Card not found</h1>
          <p className="mt-2 text-sm text-slate-600">
            This card does not belong to your account, or it no longer exists.
          </p>
          <Link
            to="/wallet"
            className="mt-4 inline-flex min-h-[44px] items-center rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800"
          >
            Return to wallet
          </Link>
        </div>
      ) : null}

      {state.status === "ready" ? (
        <div className="space-y-6">
          <div className="max-w-md">
            <CardFace card={state.card} />
          </div>
          <div>
            <h1 className="text-2xl font-bold tracking-tight text-slate-900">
              {formatStatus(state.card.brand)} {formatMaskedNumber(state.card.last4)}
            </h1>
            <dl className="mt-4 divide-y divide-slate-200 rounded-xl border border-slate-200 bg-white">
              <div className="flex justify-between gap-4 p-4">
                <dt className="text-sm text-slate-600">Expiry</dt>
                <dd className="text-sm font-medium text-slate-900">
                  {formatExpiry(state.card.expiry_month, state.card.expiry_year)}
                </dd>
              </div>
              <div className="flex justify-between gap-4 p-4">
                <dt className="text-sm text-slate-600">Status</dt>
                <dd className="text-sm font-medium text-slate-900">
                  {formatStatus(state.card.status)}
                </dd>
              </div>
              <div className="flex justify-between gap-4 p-4">
                <dt className="text-sm text-slate-600">Currency</dt>
                <dd className="text-sm font-medium text-slate-900">{state.card.currency}</dd>
              </div>
            </dl>
          </div>

          <div className="rounded-xl border border-dashed border-slate-300 bg-white p-6">
            <h2 className="font-semibold text-slate-900">Transactions</h2>
            <p className="mt-1 text-sm text-slate-600">
              Transactions will be added in the next step.
            </p>
          </div>
        </div>
      ) : null}
    </div>
  );
}
