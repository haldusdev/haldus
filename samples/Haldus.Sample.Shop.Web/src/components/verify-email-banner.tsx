import { useState } from "react"
import { useMutation } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { MailCheckIcon } from "lucide-react"
import { toast } from "sonner"

import { resendEmailVerification } from "@/api/auth/auth"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { confirmEmail } from "@/lib/auth"
import { fieldErrors, problemMessage } from "@/lib/problem"
import { useSession } from "@/lib/session"

/**
 * E-postası doğrulanmamış hesaba gösterilen şerit: kayıt sırasında gönderilen kod burada girilir. Doğrulama yeni
 * token'lar döndürür (token'daki "doğrulandı" bilgisi değişir); oturum onlarla güncellenir.
 *
 * Kimlik sağlayıcıyla açılan oturumda gösterilmez: kod ve doğrulama uçları parola hesapları içindir. O hesabın
 * adresinin doğrulanmış sayılıp sayılmayacağına backend, sağlayıcıya ne kadar güvendiğine göre karar verir.
 */
export function VerifyEmailBanner() {
  const { t } = useTranslation()
  const session = useSession()
  const [code, setCode] = useState("")

  const verification = useMutation({
    mutationFn: () => confirmEmail(code.trim()),
    onSuccess: () => toast.success(t("verify.done")),
  })

  const resend = useMutation({
    mutationFn: () => resendEmailVerification(),
    onSuccess: () => toast.success(t("verify.resent")),
    onError: (error) =>
      toast.error(problemMessage(error, t("common.unexpectedError"))),
  })

  if (!session || session.kind !== "password" || session.emailVerified) {
    return null
  }

  const error = verification.error
    ? (fieldErrors(verification.error).code?.[0] ??
      problemMessage(verification.error, t("common.unexpectedError")))
    : undefined

  return (
    <section
      aria-labelledby="verify-email-title"
      className="flex flex-col gap-3 rounded-lg border bg-muted/50 p-4 md:flex-row md:items-center"
    >
      <MailCheckIcon
        className="hidden size-5 shrink-0 text-muted-foreground md:block"
        aria-hidden="true"
      />
      <div className="flex-1">
        <h2 id="verify-email-title" className="text-sm font-medium">
          {t("verify.title")}
        </h2>
        <p className="text-sm text-muted-foreground">
          {t("verify.description", { email: session.email })}
        </p>
        {error ? (
          <p
            id="verify-email-error"
            role="alert"
            className="mt-1 text-sm text-destructive"
          >
            {error}
          </p>
        ) : null}
      </div>
      <form
        className="flex flex-wrap items-center gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          verification.mutate()
        }}
      >
        <label htmlFor="verify-email-code" className="sr-only">
          {t("verify.code")}
        </label>
        <Input
          id="verify-email-code"
          value={code}
          onChange={(event) => setCode(event.target.value)}
          placeholder={t("verify.code")}
          autoComplete="one-time-code"
          autoCapitalize="characters"
          spellCheck={false}
          maxLength={12}
          className="w-32 font-mono tracking-widest"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? "verify-email-error" : undefined}
          required
        />
        <Button type="submit" disabled={verification.isPending}>
          {t("verify.submit")}
        </Button>
        <Button
          type="button"
          variant="ghost"
          disabled={resend.isPending}
          onClick={() => resend.mutate()}
        >
          {t("verify.resend")}
        </Button>
      </form>
    </section>
  )
}
