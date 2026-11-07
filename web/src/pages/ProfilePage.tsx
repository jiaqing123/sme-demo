import { useCallback, useEffect, useState } from "react";
import { ApiError, apiFetch } from "../api/client";
import type { MeResponse, UserInfo } from "../api/types";
import { ErrorState } from "../components/ErrorState";
import { ProfileSkeleton } from "../components/Skeleton";
import { useSession } from "../session/SessionProvider";

type ProfileState =
  | { status: "loading" }
  | { status: "ready"; me: MeResponse; userInfo: UserInfo }
  | { status: "error"; message: string };

export function ProfilePage() {
  const { signOut } = useSession();
  const [state, setState] = useState<ProfileState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let active = true;
    setState({ status: "loading" });
    // Name and organisation come from the API; phone comes from auth. Neither
    // is read from the JWT.
    Promise.all([
      apiFetch<MeResponse>("/api/v1/me"),
      apiFetch<UserInfo>("/auth/userinfo"),
    ])
      .then(([me, userInfo]) => {
        if (active) setState({ status: "ready", me, userInfo });
      })
      .catch((error: unknown) => {
        if (!active) return;
        if (error instanceof ApiError && error.status === 401) return;
        setState({
          status: "error",
          message:
            error instanceof ApiError
              ? error.message
              : "Your profile could not be loaded. Try again.",
        });
      });
    return () => {
      active = false;
    };
  }, [attempt]);

  const retry = useCallback(() => setAttempt((value) => value + 1), []);

  const hasProfile =
    state.status === "ready" &&
    Boolean(state.me.name || state.me.org || state.userInfo.phone);

  return (
    <div className="mx-auto w-full max-w-xl space-y-5">
      <h1 className="text-2xl font-bold tracking-tight text-slate-900">Profile</h1>

      {state.status === "loading" ? <ProfileSkeleton /> : null}

      {state.status === "error" ? <ErrorState message={state.message} onRetry={retry} /> : null}

      {state.status === "ready" && !hasProfile ? (
        <div className="rounded-xl border border-dashed border-slate-300 bg-white p-6">
          <p className="font-medium text-slate-800">No profile data</p>
          <p className="mt-1 text-sm text-slate-600">
            Your account details are not available yet.
          </p>
        </div>
      ) : null}

      {state.status === "ready" && hasProfile ? (
        <dl className="divide-y divide-slate-200 rounded-xl border border-slate-200 bg-white">
          <div className="flex justify-between gap-4 p-4">
            <dt className="text-sm text-slate-600">Name</dt>
            <dd className="text-sm font-medium text-slate-900">{state.me.name || "—"}</dd>
          </div>
          <div className="flex justify-between gap-4 p-4">
            <dt className="text-sm text-slate-600">Organisation</dt>
            <dd className="text-sm font-medium text-slate-900">{state.me.org || "—"}</dd>
          </div>
          <div className="flex justify-between gap-4 p-4">
            <dt className="text-sm text-slate-600">Phone</dt>
            <dd className="text-sm font-medium text-slate-900">
              {state.userInfo.phone || "—"}
            </dd>
          </div>
          <div className="flex justify-between gap-4 p-4">
            <dt className="text-sm text-slate-600">User ID</dt>
            <dd className="break-all text-right text-sm font-medium text-slate-900">
              {state.userInfo.id || state.me.id || "—"}
            </dd>
          </div>
        </dl>
      ) : null}

      <button
        type="button"
        onClick={signOut}
        className="inline-flex min-h-[44px] items-center rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-800 hover:bg-slate-50"
      >
        Sign out
      </button>
    </div>
  );
}
