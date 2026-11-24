import { useTranslation } from "react-i18next"
import { TriangleAlertIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty"
import { Skeleton } from "@/components/ui/skeleton"
import { TableCell, TableRow } from "@/components/ui/table"
import { problemMessage } from "@/lib/problem"

/** Liste yüklenirken tablonun yerini tutar; satır yüksekliği gerçek satırlarla aynıdır ki sayfa zıplamasın. */
export function TableSkeleton({
  columns,
  rows = 5,
}: {
  columns: number
  rows?: number
}) {
  return (
    <>
      {Array.from({ length: rows }, (_, row) => (
        <TableRow key={row}>
          {Array.from({ length: columns }, (_, column) => (
            <TableCell key={column}>
              <Skeleton className="h-5 w-full max-w-40" />
            </TableCell>
          ))}
        </TableRow>
      ))}
    </>
  )
}

export function LoadError({
  error,
  onRetry,
}: {
  error: unknown
  onRetry: () => void
}) {
  const { t } = useTranslation()

  return (
    <Empty className="border" role="alert">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <TriangleAlertIcon aria-hidden="true" />
        </EmptyMedia>
        <EmptyTitle>{t("common.loadFailed")}</EmptyTitle>
        <EmptyDescription>
          {problemMessage(error, t("common.unexpectedError"))}
        </EmptyDescription>
      </EmptyHeader>
      <EmptyContent>
        <Button variant="outline" onClick={onRetry}>
          {t("common.retry")}
        </Button>
      </EmptyContent>
    </Empty>
  )
}

export function EmptyState({
  icon,
  title,
  description,
  children,
}: {
  icon: React.ReactNode
  title: string
  description?: string
  children?: React.ReactNode
}) {
  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">{icon}</EmptyMedia>
        <EmptyTitle>{title}</EmptyTitle>
        {description ? (
          <EmptyDescription>{description}</EmptyDescription>
        ) : null}
      </EmptyHeader>
      {children ? <EmptyContent>{children}</EmptyContent> : null}
    </Empty>
  )
}
