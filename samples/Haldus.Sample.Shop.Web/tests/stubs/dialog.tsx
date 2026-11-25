// `@/components/ui/dialog` yerine yalnızca onu isteyen testlerde yüklenir (support/modules.ts). Gerçeği içeriğini
// bir portal içinde çizer; portal sunucuda çizilemediği için açık pencere boş çıkar. Bu taklit içeriği yerinde çizer:
// sınanan şey pencerenin içindeki formdur, Radix'in davranışı (odak tuzağı, Esc, arka plan) değildir.
import type { ComponentProps, ReactNode } from "react"

export function Dialog({
  open,
  children,
}: {
  open?: boolean
  onOpenChange?: (open: boolean) => void
  children?: ReactNode
}) {
  return open ? <>{children}</> : null
}

export function DialogContent({ children }: { children?: ReactNode }) {
  return (
    <div role="dialog" aria-modal="true" aria-labelledby="dialog-title">
      {children}
    </div>
  )
}

export function DialogHeader(props: ComponentProps<"div">) {
  return <div {...props} />
}

export function DialogFooter(props: ComponentProps<"div">) {
  return <div {...props} />
}

export function DialogTitle(props: ComponentProps<"h2">) {
  return <h2 id="dialog-title" {...props} />
}

export function DialogDescription(props: ComponentProps<"p">) {
  return <p {...props} />
}
