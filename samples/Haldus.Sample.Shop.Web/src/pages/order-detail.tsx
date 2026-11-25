import { useState } from "react"
import { useQueryClient } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { Link, useNavigate, useParams } from "react-router"
import { ArrowLeftIcon, ReceiptTextIcon } from "lucide-react"
import { toast } from "sonner"

import { OrderStatus, type OrderResponse } from "@/api/model"
import {
  getGetOrderQueryKey,
  getListOrdersQueryKey,
  useCancelOrder,
  useDeleteOrder,
  useGetOrder,
  usePlaceOrder,
  useReopenOrder,
  useShipOrder,
} from "@/api/orders/orders"
import { ConfirmDialog } from "@/components/confirm-dialog"
import { OrderStatusBadge } from "@/components/order-status-badge"
import { PageHeader } from "@/components/page-header"
import { EmptyState, LoadError } from "@/components/query-state"
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
  FieldError,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field"
import { Skeleton } from "@/components/ui/skeleton"
import {
  Table,
  TableBody,
  TableCell,
  TableFooter,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Textarea } from "@/components/ui/textarea"
import { formatAmount, formatDateTime } from "@/lib/format"
import {
  fieldErrors,
  problemMessage,
  problemStatus,
  toFieldErrors,
} from "@/lib/problem"

export function OrderDetailPage() {
  const { t } = useTranslation()
  const { id = "" } = useParams()
  const order = useGetOrder(id)

  if (order.isPending) {
    return (
      <div className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-48 w-full" />
      </div>
    )
  }

  if (order.isError) {
    return problemStatus(order.error) === 404 ? (
      <EmptyState
        icon={<ReceiptTextIcon aria-hidden="true" />}
        title={t("orders.detail.notFound")}
      >
        <Button asChild variant="outline">
          <Link to="/orders">{t("orders.detail.back")}</Link>
        </Button>
      </EmptyState>
    ) : (
      <LoadError error={order.error} onRetry={() => void order.refetch()} />
    )
  }

  // Sipariş sürüm kontrollüdür: sürümü yanıtın ETag başlığında gelir ve her yazmada If-Match ile geri gönderilir.
  return (
    <OrderView
      order={order.data.data}
      etag={order.data.headers.get("ETag") ?? ""}
    />
  )
}

type Pending = "place" | "cancel" | "delete" | null

function OrderView({ order, etag }: { order: OrderResponse; etag: string }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [dialog, setDialog] = useState<Pending>(null)
  const [note, setNote] = useState("")
  const [reason, setReason] = useState("")

  const request = { headers: { "If-Match": etag } }

  async function refresh() {
    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: getGetOrderQueryKey(order.id),
      }),
      queryClient.invalidateQueries({ queryKey: getListOrdersQueryKey() }),
    ])
  }

  function transition(message: string) {
    return {
      request,
      mutation: {
        onSuccess: async () => {
          toast.success(message)
          setDialog(null)
          await refresh()
        },
        onError: async (error: unknown) => {
          // 412: kayıt bu sırada değişti. Yeniden okunur; kullanıcı güncel halle yeniden dener.
          if (problemStatus(error) === 412) {
            toast.error(t("common.changedElsewhere"))
            await refresh()
          } else if (Object.keys(fieldErrors(error)).length === 0) {
            // 409 (durum uymuyor) ve 422 (iş kuralı reddetti) açıklamasını backend, isteğin dilinde yazar.
            toast.error(problemMessage(error, t("common.unexpectedError")))
          }
        },
      },
    }
  }

  const place = usePlaceOrder(transition(t("orders.detail.placed")))
  const ship = useShipOrder(transition(t("orders.detail.shipped")))
  const cancel = useCancelOrder(transition(t("orders.detail.cancelled")))
  const reopen = useReopenOrder(transition(t("orders.detail.reopened")))
  const remove = useDeleteOrder({
    request,
    mutation: {
      onSuccess: async () => {
        toast.success(t("orders.detail.deleted"))
        await queryClient.invalidateQueries({
          queryKey: getListOrdersQueryKey(),
        })
        await navigate("/orders")
      },
    },
  })

  const busy =
    place.isPending ||
    ship.isPending ||
    cancel.isPending ||
    reopen.isPending ||
    remove.isPending
  const cancelErrors = fieldErrors(cancel.error)

  // Hangi geçişin hangi durumda yapılabildiği backend'deki durum makinesini izler (SalesEndpoints.cs).
  const canPlace = order.status === OrderStatus.Draft
  const canShip = order.status === OrderStatus.Placed
  const canCancel =
    order.status === OrderStatus.Draft || order.status === OrderStatus.Placed
  const canReopen = order.status === OrderStatus.Cancelled
  const canDelete =
    order.status === OrderStatus.Draft || order.status === OrderStatus.Cancelled

  return (
    <>
      <Button asChild variant="ghost" size="sm" className="self-start">
        <Link to="/orders">
          <ArrowLeftIcon data-icon="inline-start" aria-hidden="true" />
          {t("orders.detail.back")}
        </Link>
      </Button>

      <PageHeader
        title={order.customerName}
        description={`${order.number ?? t("orders.noNumber")} · ${formatDateTime(order.createdAt)}`}
      >
        <OrderStatusBadge status={order.status} />
      </PageHeader>

      <div className="flex flex-wrap gap-2">
        {canPlace ? (
          <Button disabled={busy} onClick={() => setDialog("place")}>
            {t("orders.detail.place")}
          </Button>
        ) : null}
        {canShip ? (
          <Button disabled={busy} onClick={() => ship.mutate({ id: order.id })}>
            {t("orders.detail.ship")}
          </Button>
        ) : null}
        {canReopen ? (
          <Button
            disabled={busy}
            onClick={() => reopen.mutate({ id: order.id, data: {} })}
          >
            {t("orders.detail.reopen")}
          </Button>
        ) : null}
        {canCancel ? (
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => setDialog("cancel")}
          >
            {t("orders.detail.cancel")}
          </Button>
        ) : null}
        {canDelete ? (
          <Button
            variant="destructive"
            disabled={busy}
            onClick={() => setDialog("delete")}
          >
            {t("orders.detail.delete")}
          </Button>
        ) : null}
      </div>

      {order.placementNote || order.cancellationReason ? (
        <dl className="grid gap-3 text-sm sm:grid-cols-2">
          {order.placementNote ? (
            <div>
              <dt className="text-muted-foreground">
                {t("orders.detail.placementNote")}
              </dt>
              <dd>{order.placementNote}</dd>
            </div>
          ) : null}
          {order.cancellationReason ? (
            <div>
              <dt className="text-muted-foreground">
                {t("orders.detail.cancellationReason")}
              </dt>
              <dd>{order.cancellationReason}</dd>
            </div>
          ) : null}
        </dl>
      ) : null}

      <section aria-labelledby="order-lines" className="flex flex-col gap-2">
        <h2 id="order-lines" className="text-sm font-medium">
          {t("orders.detail.lines")}
        </h2>
        {order.orderItems.length === 0 ? (
          <p className="rounded-lg border p-4 text-sm text-muted-foreground">
            {t("orders.detail.noLines")}
          </p>
        ) : (
          <div className="overflow-hidden rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("orders.form.product")}</TableHead>
                  <TableHead className="text-right">
                    {t("orders.form.quantity")}
                  </TableHead>
                  <TableHead className="text-right">
                    {t("orders.form.unitPrice")}
                  </TableHead>
                  <TableHead className="text-right">
                    {t("orders.detail.lineTotal")}
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {order.orderItems.map((item) => (
                  <TableRow key={item.id}>
                    <TableCell className="font-medium">
                      {item.productName}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {item.quantity}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatAmount(item.unitPrice)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatAmount(item.quantity * item.unitPrice)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
              <TableFooter>
                <TableRow>
                  <TableCell colSpan={3}>{t("orders.total")}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatAmount(order.total)}
                  </TableCell>
                </TableRow>
              </TableFooter>
            </Table>
          </div>
        )}
      </section>

      <Dialog
        open={dialog === "place"}
        onOpenChange={(open) => setDialog(open ? "place" : null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("orders.detail.place")}</DialogTitle>
            <DialogDescription>
              {t("orders.detail.placeDescription")}
            </DialogDescription>
          </DialogHeader>
          <form
            noValidate
            onSubmit={(event) => {
              event.preventDefault()
              place.mutate({
                id: order.id,
                data: { note: note.trim() === "" ? null : note.trim() },
              })
            }}
          >
            <FieldGroup>
              <Field>
                <FieldLabel htmlFor="place-note">
                  {t("orders.detail.note")}
                </FieldLabel>
                <Textarea
                  id="place-note"
                  value={note}
                  onChange={(event) => setNote(event.target.value)}
                  rows={3}
                />
              </Field>
            </FieldGroup>
            <DialogFooter className="mt-6">
              <Button
                type="button"
                variant="outline"
                onClick={() => setDialog(null)}
              >
                {t("common.close")}
              </Button>
              <Button type="submit" disabled={place.isPending}>
                {place.isPending
                  ? t("common.saving")
                  : t("orders.detail.place")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog
        open={dialog === "cancel"}
        onOpenChange={(open) => setDialog(open ? "cancel" : null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("orders.detail.cancel")}</DialogTitle>
            <DialogDescription>
              {t("orders.detail.cancelDescription")}
            </DialogDescription>
          </DialogHeader>
          <form
            noValidate
            onSubmit={(event) => {
              event.preventDefault()
              cancel.mutate({ id: order.id, data: { reason: reason.trim() } })
            }}
          >
            <FieldGroup>
              <Field data-invalid={cancelErrors.reason ? true : undefined}>
                <FieldLabel htmlFor="cancel-reason">
                  {t("orders.detail.reason")}
                </FieldLabel>
                <Textarea
                  id="cancel-reason"
                  value={reason}
                  onChange={(event) => setReason(event.target.value)}
                  aria-invalid={cancelErrors.reason ? true : undefined}
                  rows={3}
                  required
                />
                <FieldError errors={toFieldErrors(cancelErrors.reason)} />
              </Field>
            </FieldGroup>
            <DialogFooter className="mt-6">
              <Button
                type="button"
                variant="outline"
                onClick={() => setDialog(null)}
              >
                {t("common.close")}
              </Button>
              <Button
                type="submit"
                variant="destructive"
                disabled={cancel.isPending}
              >
                {cancel.isPending
                  ? t("common.saving")
                  : t("orders.detail.cancel")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={dialog === "delete"}
        onOpenChange={(open) => {
          setDialog(open ? "delete" : null)

          if (!open) {
            remove.reset()
          }
        }}
        title={t("orders.detail.deleteTitle")}
        description={t("orders.detail.deleteDescription")}
        confirmLabel={t("common.delete")}
        pending={remove.isPending}
        error={
          remove.isError
            ? problemMessage(remove.error, t("common.unexpectedError"))
            : undefined
        }
        onConfirm={() => remove.mutate({ id: order.id })}
      />
    </>
  )
}
