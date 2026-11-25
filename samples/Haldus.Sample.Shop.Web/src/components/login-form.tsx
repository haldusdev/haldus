import { useState } from "react"
import { useMutation } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { cn } from "cn"

import {
  forgotPassword,
  login,
  register,
  resetPassword,
  useGetAuthConfig,
} from "@/api/auth/auth"
import type { ExternalProviderConfig } from "@/api/model"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldSeparator,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { openSession } from "@/lib/auth"
import { fieldErrors, problemMessage, toFieldErrors } from "@/lib/problem"
import { startProviderSignIn } from "@/lib/provider"
import { HOME_PATH } from "@/lib/return-path"

type Mode = "signIn" | "register" | "forgot" | "reset"

const copy = {
  signIn: {
    title: "login.title",
    description: "login.description",
    submit: "login.submit",
  },
  register: {
    title: "login.registerTitle",
    description: "login.registerDescription",
    submit: "login.registerSubmit",
  },
  forgot: {
    title: "login.forgotTitle",
    description: "login.forgotDescription",
    submit: "login.forgotSubmit",
  },
  reset: {
    title: "login.resetTitle",
    description: "login.resetDescription",
    submit: "login.resetSubmit",
  },
} as const

const notices = {
  codeSent: "login.codeSent",
  passwordReset: "login.passwordReset",
} as const

/**
 * Giriş, kayıt ve parola sıfırlama formu (shadcn login-01). Doğrulama backend'dedir: alan hataları yanıttaki
 * `HttpValidationProblemDetails.errors` içinden, öteki hatalar (yanlış parola, alınmış e-posta, çok fazla deneme)
 * `detail` alanından okunur.
 *
 * Backend bir kimlik sağlayıcı tanıtıyorsa (`GET /auth/config`) onunla giriş düğmesi de gösterilir. Yapılandırma
 * yüklenirken veya yüklenemezse form olduğu gibi çalışır; yalnızca o düğme görünmez.
 */
export function LoginForm({
  className,
  returnTo = HOME_PATH,
  ...props
}: React.ComponentProps<"div"> & {
  /** Girişten sonra açılacak sayfa; kimlik sağlayıcıya gidip dönerken onunla birlikte taşınır. */
  returnTo?: string
}) {
  const { t } = useTranslation()
  const [mode, setMode] = useState<Mode>("signIn")
  const [notice, setNotice] = useState<keyof typeof notices>()
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")
  const [code, setCode] = useState("")
  const [newPassword, setNewPassword] = useState("")

  const submission = useMutation({
    mutationFn: async (target: Mode) => {
      const address = email.trim()

      if (target === "forgot") {
        // Yanıt, e-postanın kayıtlı olup olmadığını söylemez; her durumda kod adımına geçilir.
        await forgotPassword({ email: address })
        return
      }

      if (target === "reset") {
        await resetPassword({ email: address, code: code.trim(), newPassword })
        return
      }

      // Giriş ve oturumun açılması tek adımdır: token alınır, hesabın kimliği sorulur ve oturum yazılır.
      const tokens =
        target === "register"
          ? await register({ email: address, password })
          : await login({ email: address, password })

      await openSession(tokens.data)
    },
    onSuccess: (_, target) => {
      if (target === "forgot") {
        show("reset", "codeSent")
      } else if (target === "reset") {
        setPassword("")
        show("signIn", "passwordReset")
      }
    },
  })

  const config = useGetAuthConfig({ query: { staleTime: 5 * 60_000 } })
  const provider = config.data?.data.provider ?? null

  // Yönlendirme sayfadan ayrılır; söz yalnızca sağlayıcıya ulaşılamazsa (hata) ya da kullanıcı tarayıcının geri
  // düğmesiyle bu sayfaya dönerse (başarı) sonuçlanır ve düğme yeniden kullanılabilir olur.
  const redirect = useMutation({
    mutationFn: (target: ExternalProviderConfig) =>
      startProviderSignIn(target, returnTo),
  })

  const errors = fieldErrors(submission.error)
  const hasFieldErrors = Object.keys(errors).length > 0
  const text = copy[mode]
  const withPassword = mode === "signIn" || mode === "register"
  const alert = redirect.error
    ? t("login.providerUnavailable")
    : submission.error && !hasFieldErrors
      ? problemMessage(submission.error, t("common.unexpectedError"))
      : undefined

  function show(next: Mode, message?: keyof typeof notices) {
    setMode(next)
    setNotice(message)
    setCode("")
    setNewPassword("")
    submission.reset()
    redirect.reset()
  }

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNotice(undefined)
    redirect.reset()
    submission.mutate(mode)
  }

  function signInWithProvider(target: ExternalProviderConfig) {
    setNotice(undefined)
    submission.reset()
    redirect.mutate(target)
  }

  return (
    <div className={cn("flex flex-col gap-6", className)} {...props}>
      <Card>
        <CardHeader>
          <CardTitle>{t(text.title)}</CardTitle>
          <CardDescription>{t(text.description)}</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} noValidate>
            <FieldGroup>
              {alert ? (
                <p
                  role="alert"
                  className="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
                >
                  {alert}
                </p>
              ) : null}
              {notice && !alert && !submission.error ? (
                <p
                  role="status"
                  className="rounded-md border bg-muted p-3 text-sm"
                >
                  {t(notices[notice])}
                </p>
              ) : null}
              <Field data-invalid={errors.email ? true : undefined}>
                <FieldLabel htmlFor="email">{t("login.email")}</FieldLabel>
                <Input
                  id="email"
                  type="email"
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  autoComplete="email"
                  inputMode="email"
                  spellCheck={false}
                  aria-invalid={errors.email ? true : undefined}
                  required
                />
                <FieldError errors={toFieldErrors(errors.email)} />
              </Field>
              {withPassword ? (
                <Field data-invalid={errors.password ? true : undefined}>
                  <div className="flex items-center">
                    <FieldLabel htmlFor="password">
                      {t("login.password")}
                    </FieldLabel>
                    {mode === "signIn" ? (
                      <Button
                        type="button"
                        variant="link"
                        size="sm"
                        className="ml-auto h-auto p-0"
                        onClick={() => show("forgot")}
                      >
                        {t("login.forgot")}
                      </Button>
                    ) : null}
                  </div>
                  <Input
                    id="password"
                    type="password"
                    value={password}
                    onChange={(event) => setPassword(event.target.value)}
                    autoComplete={
                      mode === "register" ? "new-password" : "current-password"
                    }
                    aria-invalid={errors.password ? true : undefined}
                    aria-describedby={
                      mode === "register" ? "password-help" : undefined
                    }
                    required
                  />
                  {mode === "register" ? (
                    <FieldDescription id="password-help">
                      {t("login.passwordHelp")}
                    </FieldDescription>
                  ) : null}
                  <FieldError errors={toFieldErrors(errors.password)} />
                </Field>
              ) : null}
              {mode === "reset" ? (
                <>
                  <Field data-invalid={errors.code ? true : undefined}>
                    <FieldLabel htmlFor="code">{t("login.code")}</FieldLabel>
                    <Input
                      id="code"
                      value={code}
                      onChange={(event) => setCode(event.target.value)}
                      autoComplete="one-time-code"
                      autoCapitalize="characters"
                      spellCheck={false}
                      maxLength={12}
                      className="font-mono tracking-widest"
                      aria-invalid={errors.code ? true : undefined}
                      required
                    />
                    <FieldError errors={toFieldErrors(errors.code)} />
                  </Field>
                  <Field data-invalid={errors.newPassword ? true : undefined}>
                    <FieldLabel htmlFor="newPassword">
                      {t("login.newPassword")}
                    </FieldLabel>
                    <Input
                      id="newPassword"
                      type="password"
                      value={newPassword}
                      onChange={(event) => setNewPassword(event.target.value)}
                      autoComplete="new-password"
                      aria-invalid={errors.newPassword ? true : undefined}
                      aria-describedby="newPassword-help"
                      required
                    />
                    <FieldDescription id="newPassword-help">
                      {t("login.passwordHelp")}
                    </FieldDescription>
                    <FieldError errors={toFieldErrors(errors.newPassword)} />
                  </Field>
                </>
              ) : null}
              <Field>
                <Button type="submit" disabled={submission.isPending}>
                  {submission.isPending
                    ? t("login.submitting")
                    : t(text.submit)}
                </Button>
                {mode === "signIn" ? (
                  <FieldDescription className="text-center">
                    {t("login.noAccount")}{" "}
                    <Button
                      type="button"
                      variant="link"
                      className="h-auto p-0"
                      onClick={() => show("register")}
                    >
                      {t("login.switchToRegister")}
                    </Button>
                  </FieldDescription>
                ) : null}
                {mode === "register" ? (
                  <FieldDescription className="text-center">
                    {t("login.haveAccount")}{" "}
                    <Button
                      type="button"
                      variant="link"
                      className="h-auto p-0"
                      onClick={() => show("signIn")}
                    >
                      {t("login.switchToSignIn")}
                    </Button>
                  </FieldDescription>
                ) : null}
                {mode === "forgot" || mode === "reset" ? (
                  <div className="flex items-center justify-between">
                    <Button
                      type="button"
                      variant="link"
                      className="h-auto p-0"
                      onClick={() => show("signIn")}
                    >
                      {t("login.backToSignIn")}
                    </Button>
                    {mode === "reset" ? (
                      <Button
                        type="button"
                        variant="link"
                        className="h-auto p-0"
                        onClick={() => show("forgot")}
                      >
                        {t("login.newCode")}
                      </Button>
                    ) : null}
                  </div>
                ) : null}
              </Field>
              {provider && withPassword ? (
                <>
                  <FieldSeparator className="*:data-[slot=field-separator-content]:bg-card">
                    {t("login.providerOr")}
                  </FieldSeparator>
                  <Field>
                    <Button
                      type="button"
                      variant="outline"
                      disabled={redirect.isPending}
                      onClick={() => signInWithProvider(provider)}
                    >
                      {redirect.isPending
                        ? t("login.providerRedirecting")
                        : t("login.providerSignIn")}
                    </Button>
                  </Field>
                </>
              ) : null}
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
