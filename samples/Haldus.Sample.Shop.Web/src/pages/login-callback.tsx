import { useEffect, useState } from "react"
import { useTranslation } from "react-i18next"
import { Link, Navigate } from "react-router"

import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { finishProviderSignIn } from "@/lib/auth"
import { problemStatus } from "@/lib/problem"
import { isProviderError } from "@/lib/provider"

type Outcome =
  | { status: "pending" }
  | { status: "signedIn"; returnTo: string }
  | { status: "failed"; error: unknown }

/**
 * Kimlik sağlayıcının kullanıcıyı geri gönderdiği sayfa. Adresteki kodu oturuma çevirir ve kullanıcının girişten önce
 * istediği sayfaya gider; olmazsa nedenini söyler ve giriş ekranına dönüş yolu verir.
 */
export function LoginCallbackPage() {
  const { t } = useTranslation()
  const [outcome, setOutcome] = useState<Outcome>({ status: "pending" })

  useEffect(() => {
    let mounted = true

    finishProviderSignIn(window.location.href).then(
      (returnTo) => {
        if (mounted) {
          setOutcome({ status: "signedIn", returnTo })
        }
      },
      (error: unknown) => {
        if (mounted) {
          setOutcome({ status: "failed", error })
        }
      }
    )

    return () => {
      mounted = false
    }
  }, [])

  if (outcome.status === "signedIn") {
    return <Navigate to={outcome.returnTo} replace />
  }

  const failed = outcome.status === "failed"

  return (
    <div className="flex min-h-svh w-full items-center justify-center p-6 md:p-10">
      <div className="w-full max-w-sm">
        <Card>
          <CardHeader>
            <CardTitle>
              {failed
                ? t("login.callbackFailedTitle")
                : t("login.callbackTitle")}
            </CardTitle>
            {failed ? null : (
              <CardDescription role="status">
                {t("login.callbackPending")}
              </CardDescription>
            )}
          </CardHeader>
          {failed ? (
            <CardContent className="flex flex-col gap-6">
              <p
                role="alert"
                className="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
              >
                {t(reason(outcome.error))}
              </p>
              <Button asChild>
                <Link to="/login" replace>
                  {t("login.backToSignIn")}
                </Link>
              </Button>
            </CardContent>
          ) : null}
        </Card>
      </div>
    </div>
  )
}

/**
 * Hatanın kullanıcıya söylenecek nedeni. Sağlayıcının ve backend'in kendi metinleri gösterilmez: sağlayıcınınki
 * arayüzün dilinde değildir, backend'in 401 yanıtı ise çoğunlukla gövdesizdir.
 */
function reason(error: unknown) {
  if (isProviderError(error)) {
    return "login.callbackDenied"
  }

  const status = problemStatus(error)

  // Sağlayıcı girişi tamamladı ama backend token'ı ya da hesabı kabul etmedi.
  if (status === 401 || status === 403) {
    return "login.callbackNotAccepted"
  }

  // Geri kalanı: adresteki `state` bu sekmede başlatılmış bir girişe ait değil, ağ veya sunucu hatası, backend'in
  // artık bir sağlayıcı tanıtmaması.
  return "login.callbackFailed"
}
