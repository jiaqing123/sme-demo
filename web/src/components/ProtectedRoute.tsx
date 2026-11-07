import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useSession } from "../session/SessionProvider";

export function ProtectedRoute() {
  const { session, sessionExpired } = useSession();
  const location = useLocation();

  if (!session) {
    const reason = sessionExpired ? "expired" : "required";
    return (
      <Navigate
        to={`/login?reason=${reason}`}
        replace
        state={{ from: `${location.pathname}${location.search}` }}
      />
    );
  }

  return <Outlet />;
}
