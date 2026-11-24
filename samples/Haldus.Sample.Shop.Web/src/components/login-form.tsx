import { useState } from "react"
import { useTranslation } from "react-i18next"
import { cn } from "cn"

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
import { isGuid, signIn } from "@/lib/session"

// Her kurulumda aynı örnek kiracı önerilir; böylece yeniden girişte önceki siparişler görünür.
const DEMO_TENANT = "11111111-1111-4111-8111-111111111111"

export function LoginForm({
  className,
  ...props
}: React.ComponentProps<"div">) {
  const { t } = useTranslation()
  const [userId, setUserId] = useState("demo")
  const [tenantId, setTenantId] = useState(DEMO_TENANT)
  const [submitted, setSubmitted] = useState(false)

  const userIdError =
    userId.trim().length === 0 ? t("login.userIdRequired") : undefined
  const tenantIdError = isGuid(tenantId.trim())
    ? undefined
    : t("login.tenantIdInvalid")

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitted(true)

    if (!userIdError && !tenantIdError) {
      signIn({ userId: userId.trim(), tenantId: tenantId.trim() })
    }
  }

  return (
    <div className={cn("flex flex-col gap-6", className)} {...props}>
      <Card>
        <CardHeader>
          <CardTitle>{t("login.title")}</CardTitle>
          <CardDescription>{t("login.description")}</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} noValidate>
            <FieldGroup>
              <Field data-invalid={submitted && userIdError ? true : undefined}>
                <FieldLabel htmlFor="userId">{t("login.userId")}</FieldLabel>
                <Input
                  id="userId"
                  value={userId}
                  onChange={(event) => setUserId(event.target.value)}
                  autoComplete="username"
                  aria-invalid={submitted && userIdError ? true : undefined}
                  aria-describedby="userId-help"
                  required
                />
                <FieldDescription id="userId-help">
                  {t("login.userIdHelp")}
                </FieldDescription>
                {submitted && userIdError ? (
                  <FieldError>{userIdError}</FieldError>
                ) : null}
              </Field>
              <Field
                data-invalid={submitted && tenantIdError ? true : undefined}
              >
                <div className="flex items-center">
                  <FieldLabel htmlFor="tenantId">
                    {t("login.tenantId")}
                  </FieldLabel>
                  <Button
                    type="button"
                    variant="link"
                    size="sm"
                    className="ml-auto h-auto p-0"
                    onClick={() => setTenantId(crypto.randomUUID())}
                  >
                    {t("login.newTenant")}
                  </Button>
                </div>
                <Input
                  id="tenantId"
                  value={tenantId}
                  onChange={(event) => setTenantId(event.target.value)}
                  autoComplete="off"
                  spellCheck={false}
                  className="font-mono"
                  aria-invalid={submitted && tenantIdError ? true : undefined}
                  aria-describedby="tenantId-help"
                  required
                />
                <FieldDescription id="tenantId-help">
                  {t("login.tenantIdHelp")}
                </FieldDescription>
                {submitted && tenantIdError ? (
                  <FieldError>{tenantIdError}</FieldError>
                ) : null}
              </Field>
              <Field>
                <Button type="submit">{t("login.submit")}</Button>
              </Field>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
