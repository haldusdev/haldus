import { useState } from "react"
import { useQueryClient } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { Link, useNavigate } from "react-router"
import { PlusIcon, ReceiptTextIcon, XIcon } from "lucide-react"
import { toast } from "sonner"

import { OrderStatus } from "@/api/model"
import {
  getListOrdersQueryKey,
  useCreateOrder,
  useListOrders,
} from "@/api/orders/orders"
import { DataPagination } from "@/components/data-pagination"
import { OrderStatusBadge } from "@/components/order-status-badge"
import { PageHeader } from "@/components/page-header"
import { EmptyState, LoadError, TableSkeleton } from "@/components/query-state"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Field,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { formatAmount, formatDateTime } from "@/lib/format"
import { fieldErrors, problemMessage, toFieldErrors } from "@/lib/problem"

const PAGE_SIZE = 10
const ALL = "all"
const statuses = Object.values(OrderStatus)

function isStatus(value: string): value is OrderStatus {
  return statuses.some((status) => status === value)
}

export function OrdersPage() {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState<OrderStatus | typeof ALL>(ALL)
  const [creating, setCreating] = useState(false)

  const orders = useListOrders({
    page,
    pageSize: PAGE_SIZE,
    status: status === ALL ? undefined : status,
  })
  const result = orders.data?.data
  const items = result?.items ?? []

  return (
    <>
      <PageHeader
        title={t("orders.title")}
        description={t("orders.description")}
      >
        <Button onClick={() => setCreating(true)}>
          <PlusIcon data-icon="inline-start" aria-hidden="true" />
          {t("orders.new")}
        </Button>
      </PageHeader>

      <Select
        value={status}
        onValueChange={(value) => {
          setStatus(isStatus(value) ? value : ALL)
          setPage(1)
        }}
      >
        <SelectTrigger
          aria-label={t("orders.filterByStatus")}
          className="w-full sm:w-56"
        >
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={ALL}>{t("orders.allStatuses")}</SelectItem>
          {statuses.map((option) => (
            <SelectItem key={option} value={option}>
              {t(`orders.status.${option}`)}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>

      {orders.isError ? (
        <LoadError error={orders.error} onRetry={() => void orders.refetch()} />
      ) : !orders.isPending && items.length === 0 ? (
        <EmptyState
          icon={<ReceiptTextIcon aria-hidden="true" />}
          title={status === ALL ? t("orders.empty") : t("orders.emptyFiltered")}
          description={
            status === ALL ? t("orders.emptyDescription") : undefined
          }
        >
          {status === ALL ? (
            <Button onClick={() => setCreating(true)}>{t("orders.new")}</Button>
          ) : null}
        </EmptyState>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("orders.customer")}</TableHead>
                  <TableHead>{t("orders.number")}</TableHead>
                  <TableHead>{t("orders.statusLabel")}</TableHead>
                  <TableHead className="text-right">
                    {t("orders.items")}
                  </TableHead>
                  <TableHead className="text-right">
                    {t("orders.total")}
                  </TableHead>
                  <TableHead>{t("orders.created")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {orders.isPending ? (
                  <TableSkeleton columns={6} />
                ) : (
                  items.map((order) => (
                    <TableRow key={order.id}>
                      <TableCell className="max-w-64 truncate font-medium">
                        <Link
                          to={`/orders/${order.id}`}
                          className="underline-offset-4 hover:underline focus-visible:underline"
                          aria-label={t("orders.open", {
                            customer: order.customerName,
                          })}
                        >
                          {order.customerName}
                        </Link>
                      </TableCell>
                      <TableCell className="font-mono text-xs text-muted-foreground">
                        {order.number ?? "—"}
                      </TableCell>
                      <TableCell>
                        <OrderStatusBadge status={order.status} />
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {order.itemCount}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatAmount(order.total)}
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {formatDateTime(order.createdAt)}
                      </TableCell>
                    </TableRow>
                  ))
                )}
              </TableBody>
            </Table>
          </div>
          {result ? (
            <DataPagination
              page={result.page}
              pageSize={result.pageSize}
              totalCount={result.totalCount}
              onPageChange={setPage}
            />
          ) : null}
        </>
      )}

      {creating ? <OrderDialog onClose={() => setCreating(false)} /> : null}
    </>
  )
}

type Line = {
  key: string
  productName: string
  quantity: string
  unitPrice: string
}

function newLine(): Line {
  return {
    key: crypto.randomUUID(),
    productName: "",
    quantity: "1",
    unitPrice: "",
  }
}

function OrderDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [customerName, setCustomerName] = useState("")
  const [lines, setLines] = useState<Line[]>(() => [newLine()])

  // Aynı pencereden yinelenen gönderim ikinci bir sipariş oluşturmasın: backend aynı anahtarı bir kez işler.
  const [idempotencyKey] = useState(() => crypto.randomUUID())

  const create = useCreateOrder({
    request: { headers: { "Idempotency-Key": idempotencyKey } },
    mutation: {
      onSuccess: async (response) => {
        toast.success(t("orders.form.createdToast"))
        await queryClient.invalidateQueries({
          queryKey: getListOrdersQueryKey(),
        })
        await navigate(`/orders/${response.data.id}`)
      },
    },
  })

  const errors = fieldErrors(create.error)
  const general =
    create.isError && Object.keys(errors).length === 0
      ? problemMessage(create.error, t("common.unexpectedError"))
      : undefined

  function change(key: string, patch: Partial<Line>) {
    setLines((current) =>
      current.map((line) => (line.key === key ? { ...line, ...patch } : line))
    )
  }

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()

    create.mutate({
      data: {
        customerName: customerName.trim(),
        orderItems: lines
          .filter((line) => line.productName.trim().length > 0)
          .map((line) => ({
            productName: line.productName.trim(),
            quantity: Number(line.quantity),
            unitPrice:
              line.unitPrice.trim() === "" ? 0 : Number(line.unitPrice),
          })),
      },
    })
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) {
          onClose()
        }
      }}
    >
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{t("orders.new")}</DialogTitle>
        </DialogHeader>
        <form onSubmit={submit} noValidate>
          <FieldGroup>
            <Field data-invalid={errors.customerName ? true : undefined}>
              <FieldLabel htmlFor="order-customer">
                {t("orders.form.customerName")}
              </FieldLabel>
              <Input
                id="order-customer"
                value={customerName}
                onChange={(event) => setCustomerName(event.target.value)}
                aria-invalid={errors.customerName ? true : undefined}
                autoFocus
                required
              />
              <FieldError errors={toFieldErrors(errors.customerName)} />
            </Field>

            <FieldSet>
              <FieldLegend variant="label">
                {t("orders.form.lines")}
              </FieldLegend>
              <div className="flex flex-col gap-3">
                {lines.map((line, index) => {
                  const prefix = `orderItems[${index}]`
                  const lineErrors = [
                    ...(errors[`${prefix}.productName`] ?? []),
                    ...(errors[`${prefix}.quantity`] ?? []),
                    ...(errors[`${prefix}.unitPrice`] ?? []),
                  ]

                  return (
                    <div key={line.key} className="flex flex-col gap-1">
                      <div className="grid grid-cols-[1fr_5rem_7rem_auto] items-end gap-2">
                        <Field>
                          <FieldLabel
                            htmlFor={`line-product-${line.key}`}
                            className={index === 0 ? undefined : "sr-only"}
                          >
                            {t("orders.form.product")}
                          </FieldLabel>
                          <Input
                            id={`line-product-${line.key}`}
                            value={line.productName}
                            onChange={(event) =>
                              change(line.key, {
                                productName: event.target.value,
                              })
                            }
                          />
                        </Field>
                        <Field>
                          <FieldLabel
                            htmlFor={`line-quantity-${line.key}`}
                            className={index === 0 ? undefined : "sr-only"}
                          >
                            {t("orders.form.quantity")}
                          </FieldLabel>
                          <Input
                            id={`line-quantity-${line.key}`}
                            type="number"
                            inputMode="numeric"
                            min={1}
                            value={line.quantity}
                            onChange={(event) =>
                              change(line.key, { quantity: event.target.value })
                            }
                          />
                        </Field>
                        <Field>
                          <FieldLabel
                            htmlFor={`line-price-${line.key}`}
                            className={index === 0 ? undefined : "sr-only"}
                          >
                            {t("orders.form.unitPrice")}
                          </FieldLabel>
                          <Input
                            id={`line-price-${line.key}`}
                            type="number"
                            inputMode="decimal"
                            min={0}
                            step="0.01"
                            value={line.unitPrice}
                            onChange={(event) =>
                              change(line.key, {
                                unitPrice: event.target.value,
                              })
                            }
                          />
                        </Field>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          disabled={lines.length === 1}
                          aria-label={t("orders.form.removeLine", {
                            index: index + 1,
                          })}
                          onClick={() =>
                            setLines((current) =>
                              current.filter(
                                (candidate) => candidate.key !== line.key
                              )
                            )
                          }
                        >
                          <XIcon aria-hidden="true" />
                        </Button>
                      </div>
                      {lineErrors.length > 0 ? (
                        <FieldError errors={toFieldErrors(lineErrors)} />
                      ) : null}
                    </div>
                  )
                })}
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="self-start"
                onClick={() => setLines((current) => [...current, newLine()])}
              >
                <PlusIcon data-icon="inline-start" aria-hidden="true" />
                {t("orders.form.addLine")}
              </Button>
            </FieldSet>
            {general ? <FieldError>{general}</FieldError> : null}
          </FieldGroup>
          <DialogFooter className="mt-6">
            <Button type="button" variant="outline" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" disabled={create.isPending}>
              {create.isPending ? t("common.saving") : t("common.create")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
