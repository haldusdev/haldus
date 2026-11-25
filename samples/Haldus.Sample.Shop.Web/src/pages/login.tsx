import { Navigate, useLocation } from "react-router"

import { LoginForm } from "@/components/login-form"
import { returnPath } from "@/lib/return-path"
import { useSession } from "@/lib/session"

/** Giriş ekranı (shadcn login-01). Oturum açılınca kullanıcının gitmek istediği sayfaya dönülür. */
export function LoginPage() {
  const session = useSession()
  const location = useLocation()
  const state: unknown = location.state
  const from = returnPath(
    typeof state === "object" && state !== null && "from" in state
      ? state.from
      : undefined
  )

  if (session) {
    return <Navigate to={from} replace />
  }

  return (
    <div className="flex min-h-svh w-full items-center justify-center p-6 md:p-10">
      <div className="w-full max-w-sm">
        {/* Kimlik sağlayıcıyla giriş sayfadan ayrılır; dönüş adresi onunla birlikte taşınır. */}
        <LoginForm returnTo={from} />
      </div>
    </div>
  )
}
