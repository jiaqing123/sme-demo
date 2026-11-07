import { Link } from "react-router-dom";
import { useSession } from "../session/SessionProvider";

export function NotFoundPage() {
  const { session } = useSession();

  return (
    <div className="mx-auto w-full max-w-md px-4 py-12 text-center">
      <p className="text-sm font-semibold uppercase tracking-widest text-slate-500">404</p>
      <h1 className="mt-2 text-2xl font-bold tracking-tight text-slate-900">
        Page not found
      </h1>
      <p className="mt-2 text-sm text-slate-600">
        The page you asked for does not exist.
      </p>
      <div className="mt-6 flex flex-wrap justify-center gap-3">
        <Link
          to={session ? "/wallet" : "/login"}
          className="inline-flex min-h-[44px] items-center rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800"
        >
          {session ? "Go to wallet" : "Go to sign in"}
        </Link>
        <Link
          to="/wallet"
          className="inline-flex min-h-[44px] items-center rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-800 hover:bg-slate-50"
        >
          Wallet
        </Link>
      </div>
    </div>
  );
}
