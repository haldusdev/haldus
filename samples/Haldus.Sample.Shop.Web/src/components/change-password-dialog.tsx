import { useState } from "react"
import { useMutation } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { toast } from "sonner"

import { changeOwnPassword } from "@/lib/auth"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { fieldErrors, problemMessage, toFieldErrors } from "@/lib/problem"

/** Parola değiştirme penceresi. Backend bu oturumu açık bırakır, hesabın öteki oturumlarını kapatır. */
export function ChangePasswordDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const { t } = useTranslation()
  const [currentPassword, setCurrentPassword] = useState("")
  const [newPassword, setNewPassword] = useState("")

  const change = useMutation({
    mutationFn: () => changeOwnPassword(currentPassword, newPassword),
    onSuccess: () => {
      toast.success(t("password.changed"))
      close(false)
    },
  })

  const errors = fieldErrors(change.error)
  const hasFieldErrors = Object.keys(errors).length > 0

  function close(next: boolean) {
    if (!next) {
      setCurrentPassword("")
      setNewPassword("")
      change.reset()
    }

    onOpenChange(next)
  }

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent>
        <form
          noValidate
          onSubmit={(event) => {
            event.preventDefault()
            change.mutate()
          }}
        >
          <DialogHeader>
            <DialogTitle>{t("password.title")}</DialogTitle>
            <DialogDescription>{t("password.description")}</DialogDescription>
          </DialogHeader>
          <FieldGroup className="py-4">
            {change.error && !hasFieldErrors ? (
              <p role="alert" className="text-sm text-destructive">
                {problemMessage(change.error, t("common.unexpectedError"))}
              </p>
            ) : null}
            <Field data-invalid={errors.currentPassword ? true : undefined}>
              <FieldLabel htmlFor="currentPassword">
                {t("password.current")}
              </FieldLabel>
              <Input
                id="currentPassword"
                type="password"
                value={currentPassword}
                onChange={(event) => setCurrentPassword(event.target.value)}
                autoComplete="current-password"
                aria-invalid={errors.currentPassword ? true : undefined}
                required
              />
              <FieldError errors={toFieldErrors(errors.currentPassword)} />
            </Field>
            <Field data-invalid={errors.newPassword ? true : undefined}>
              <FieldLabel htmlFor="changedPassword">
                {t("password.new")}
              </FieldLabel>
              <Input
                id="changedPassword"
                type="password"
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
                autoComplete="new-password"
                aria-invalid={errors.newPassword ? true : undefined}
                aria-describedby="changedPassword-help"
                required
              />
              <FieldDescription id="changedPassword-help">
                {t("login.passwordHelp")}
              </FieldDescription>
              <FieldError errors={toFieldErrors(errors.newPassword)} />
            </Field>
          </FieldGroup>
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              disabled={change.isPending}
              onClick={() => close(false)}
            >
              {t("common.cancel")}
            </Button>
            <Button type="submit" disabled={change.isPending}>
              {change.isPending ? t("common.saving") : t("common.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
