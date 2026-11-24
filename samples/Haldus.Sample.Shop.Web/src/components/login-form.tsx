import { useState } from "react"
import { useMutation } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { cn } from "cn"

import { login, register } from "@/api/auth/auth"
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
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { openSession } from "@/lib/auth"
import { fieldErrors, problemMessage, toFieldErrors } from "@/lib/problem"

type Mode = "signIn" | "register"

const copy = {
  signIn: {
    title: "login.title",
    description: "login.description",
    submit: "login.submit",
    prompt: "login.noAccount",
    switch: "login.switchToRegister",
  },
  register: {
    title: "login.registerTitle",
    description: "login.registerDescription",
    submit: "login.registerSubmit",
    prompt: "login.haveAccount",
    switch: "login.switchToSignIn",
  },
} as const

/**
 * Giriş ve kayıt formu (shadcn login-01). Doğrulama backend'dedir: alan hataları yanıttaki
 * `HttpValidationProblemDetails.errors` içinden, öteki hatalar (yanlış parola, alınmış e-posta) `detail` alanından okunur.
 */
export function LoginForm({
  className,
  ...props
}: React.ComponentProps<"div">) {
  const { t } = useTranslation()
  const [mode, setMode] = useState<Mode>("signIn")
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")

  // Giriş ve oturumun açılması tek adımdır: token alınır, hesabın kimliği sorulur ve oturum yazılır.
  const submission = useMutation({
    mutationFn: async (credentials: { email: string; password: string }) => {
      const tokens =
        mode === "register"
          ? await register(credentials)
          : await login(credentials)

      await openSession(tokens.data)
    },
  })

  const errors = fieldErrors(submission.error)
  const hasFieldErrors = Object.keys(errors).length > 0
  const text = copy[mode]

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    submission.mutate({ email: email.trim(), password })
  }

  function switchMode() {
    setMode(mode === "signIn" ? "register" : "signIn")
    submission.reset()
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
              {submission.error && !hasFieldErrors ? (
                <p
                  role="alert"
                  className="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
                >
                  {problemMessage(
                    submission.error,
                    t("common.unexpectedError")
                  )}
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
              <Field data-invalid={errors.password ? true : undefined}>
                <FieldLabel htmlFor="password">
                  {t("login.password")}
                </FieldLabel>
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
              <Field>
                <Button type="submit" disabled={submission.isPending}>
                  {submission.isPending
                    ? t("login.submitting")
                    : t(text.submit)}
                </Button>
                <FieldDescription className="text-center">
                  {t(text.prompt)}{" "}
                  <Button
                    type="button"
                    variant="link"
                    className="h-auto p-0"
                    onClick={switchMode}
                  >
                    {t(text.switch)}
                  </Button>
                </FieldDescription>
              </Field>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}