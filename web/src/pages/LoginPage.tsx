import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { ApiError, apiFetch } from "../api/client";
import type {
  ChallengeResponse,
  FieldErrors,
  TokenResponse,
} from "../api/types";
import { Field } from "../components/Field";
import { getConfig } from "../config";
import { useSession } from "../session/SessionProvider";

type Step = "credentials" | "otp";

function messageOf(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  return "Something went wrong. Try again.";
}

function fieldErrorsOf(error: unknown): FieldErrors {
  return error instanceof ApiError ? (error.errors ?? {}) : {};
}

function firstError(errors: FieldErrors, field: string): string | undefined {
  return errors[field]?.[0];
}

/** Reads the cooldown out of "Wait 42 seconds before resending." */
function retrySeconds(message: string): number {
  const match = /(\d+)\s+seconds?/i.exec(message);
  const seconds = match ? Number(match[1]) : 60;
  return Number.isFinite(seconds) && seconds > 0 ? seconds : 60;
}

export function LoginPage() {
  const config = useMemo(() => getConfig(), []);
  const { session, signIn } = useSession();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const reason = searchParams.get("reason");

  const [step, setStep] = useState<Step>("credentials");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [challenge, setChallenge] = useState<ChallengeResponse | null>(null);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [resendIn, setResendIn] = useState(0);
  const [startOver, setStartOver] = useState(false);

  useEffect(() => {
    if (resendIn <= 0) return undefined;
    const timer = window.setTimeout(() => setResendIn((value) => Math.max(0, value - 1)), 1000);
    return () => window.clearTimeout(timer);
  }, [resendIn]);

  if (session) {
    return <Navigate to="/wallet" replace />;
  }

  const resetMessages = () => {
    setError(null);
    setFieldErrors({});
    setStartOver(false);
  };

  const beginAgain = () => {
    setStep("credentials");
    setChallenge(null);
    setCode("");
    setResendIn(0);
    resetMessages();
  };

  const applyError = (caught: unknown) => {
    setError(messageOf(caught));
    setFieldErrors(fieldErrorsOf(caught));
    // Only a 429 destroys the challenge (three wrong codes); a single wrong code
    // is an ordinary form error and the challenge stays usable.
    setStartOver(caught instanceof ApiError && caught.status === 429);
  };

  const submitCredentials = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    resetMessages();
    try {
      const response = await apiFetch<ChallengeResponse>(
        "/auth/login",
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ username, password }),
        },
        { auth: false },
      );
      setChallenge(response);
      setResendIn(response.resend_in ?? 0);
      setCode("");
      setStep("otp");
    } catch (caught) {
      applyError(caught);
    } finally {
      setBusy(false);
    }
  };

  const submitCode = async (event: FormEvent) => {
    event.preventDefault();
    if (!challenge) return;
    setBusy(true);
    resetMessages();
    try {
      const response = await apiFetch<TokenResponse>(
        `/auth/challenges/${encodeURIComponent(challenge.challenge_id)}/verify`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ code }),
        },
        { auth: false },
      );
      signIn(response.access_token, response.expires_in);
      navigate("/wallet", { replace: true });
    } catch (caught) {
      applyError(caught);
    } finally {
      setBusy(false);
    }
  };

  const resend = async () => {
    if (!challenge) return;
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      const response = await apiFetch<ChallengeResponse>(
        `/auth/challenges/${encodeURIComponent(challenge.challenge_id)}/resend`,
        { method: "POST" },
        { auth: false },
      );
      setChallenge(response);
      setResendIn(response.resend_in ?? 0);
      setCode("");
      setStartOver(false);
    } catch (caught) {
      setError(messageOf(caught));
      if (caught instanceof ApiError && caught.status === 429) {
        setResendIn(retrySeconds(caught.message));
      }
      if (caught instanceof ApiError && caught.status === 401) {
        setStartOver(true);
      }
    } finally {
      setBusy(false);
    }
  };

  const fillDemoCredentials = () => {
    if (config.demoUsername) setUsername(config.demoUsername);
    if (config.demoPassword) setPassword(config.demoPassword);
  };

  return (
    <div className="mx-auto w-full max-w-md px-4 py-10">
      <h1 className="text-2xl font-bold tracking-tight text-slate-900">SME Demo</h1>
      <p className="mt-1 text-sm text-slate-600">Sign in to view your cards and profile.</p>

      {reason === "expired" ? (
        <p role="status" className="mt-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900">
          Your session expired. Sign in again.
        </p>
      ) : null}
      {reason === "required" ? (
        <p role="status" className="mt-4 rounded-lg bg-sky-50 px-3 py-2 text-sm text-sky-900">
          Sign in to continue.
        </p>
      ) : null}

      {config.demoMode ? (
        <section
          aria-label="Demo credentials"
          className="mt-4 rounded-xl border border-sky-200 bg-sky-50 p-4 text-sm text-sky-950"
        >
          <h2 className="font-semibold">Demo credentials</h2>
          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
            <dt className="font-medium">Username</dt>
            <dd>{config.demoUsername ?? "not configured"}</dd>
            <dt className="font-medium">Password</dt>
            <dd>{config.demoPassword ?? "not configured"}</dd>
            <dt className="font-medium">OTP</dt>
            <dd>{config.demoOtp ?? "not configured"}</dd>
          </dl>
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={fillDemoCredentials}
              className="min-h-[44px] rounded-lg bg-sky-700 px-3 py-2 font-semibold text-white hover:bg-sky-800"
            >
              Use demo credentials
            </button>
            {config.demoOtp ? (
              <button
                type="button"
                onClick={() => setCode(config.demoOtp ?? "")}
                className="min-h-[44px] rounded-lg border border-sky-700 px-3 py-2 font-semibold text-sky-800 hover:bg-sky-100"
              >
                Use demo OTP
              </button>
            ) : null}
          </div>
        </section>
      ) : null}

      {step === "credentials" ? (
        <form onSubmit={submitCredentials} className="mt-6 space-y-4" noValidate>
          <Field
            id="username"
            label="Username"
            name="username"
            autoComplete="username"
            value={username}
            onChange={(event) => setUsername(event.target.value)}
            error={firstError(fieldErrors, "username")}
            required
          />
          <Field
            id="password"
            label="Password"
            name="password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            error={firstError(fieldErrors, "password")}
            required
          />
          <button
            type="submit"
            disabled={busy}
            className="inline-flex min-h-[44px] w-full items-center justify-center rounded-lg bg-slate-900 px-4 py-2 font-semibold text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {busy ? "Signing in…" : "Continue"}
          </button>
        </form>
      ) : (
        <form onSubmit={submitCode} className="mt-6 space-y-4" noValidate>
          <p className="text-sm text-slate-700">
            We sent a 6-digit code to your phone. It expires in 5 minutes.
          </p>
          <Field
            id="otp"
            label="Verification code"
            name="code"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            pattern="[0-9]{6}"
            value={code}
            onChange={(event) => setCode(event.target.value.replace(/\D/g, "").slice(0, 6))}
            hint="6 digits"
            error={firstError(fieldErrors, "code")}
            required
          />
          <button
            type="submit"
            disabled={busy}
            className="inline-flex min-h-[44px] w-full items-center justify-center rounded-lg bg-slate-900 px-4 py-2 font-semibold text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {busy ? "Verifying…" : "Verify and sign in"}
          </button>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <button
              type="button"
              onClick={resend}
              disabled={busy || resendIn > 0}
              className="inline-flex min-h-[44px] items-center rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold text-slate-800 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {resendIn > 0 ? `Resend in ${resendIn}s` : "Resend code"}
            </button>
            <button
              type="button"
              onClick={beginAgain}
              className="inline-flex min-h-[44px] items-center rounded-lg px-3 py-2 text-sm font-semibold text-slate-700 underline-offset-2 hover:underline"
            >
              Back
            </button>
          </div>
        </form>
      )}

      {error ? (
        <div
          role="alert"
          aria-live="assertive"
          className="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800"
        >
          <p>{error}</p>
          {startOver ? (
            <button
              type="button"
              onClick={beginAgain}
              className="mt-2 inline-flex min-h-[44px] items-center rounded-lg bg-red-700 px-3 py-2 font-semibold text-white hover:bg-red-800"
            >
              Start over
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
