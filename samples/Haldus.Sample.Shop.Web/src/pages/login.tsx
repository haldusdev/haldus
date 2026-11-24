import { Navigate, useLocation } from "react-router"

import { LoginForm } from "@/components/login-form"
import { useSession } from "@/lib/session"

/** Giriş ekranı (shadcn login-01). Oturum açılınca kullanıcının gitmek istediği sayfaya dönülür. */
export function LoginPage() {
  const session = useSession()
  const location = useLocation()

  if (session) {
    const state: unknown = location.state
    const from =
      typeof state === "object" &&
      state !== null &&
      "from" in state &&
      typeof state.from === "string" &&
      state.from.startsWith("/") &&
      !state.from.startsWith("//")
        ? state.from
        : "/products"

    return <Navigate to={from} replace />
  }

  return (
    <div className="flex min-h-svh w-full items-center justify-center p-6 md:p-10">
      <div className="w-full max-w-sm">
        <LoginForm />
      </div>
    </div>
  )
}
