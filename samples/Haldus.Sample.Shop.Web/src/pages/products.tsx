import { useEffect, useState } from "react"
import { useQueryClient } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { PackageIcon, PencilIcon, PlusIcon, Trash2Icon } from "lucide-react"
import { toast } from "sonner"

import { useListCategories } from "@/api/categories/categories"
import type { CategoryResponse, ProductResponse } from "@/api/model"
import {
  getListProductsQueryKey,
  useCreateProduct,
  useDeleteProduct,
  useListProducts,
  useUpdateProduct,
} from "@/api/products/products"
import { ConfirmDialog } from "@/components/confirm-dialog"
import { DataPagination } from "@/components/data-pagination"
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
import { Textarea } from "@/components/ui/textarea"
import { formatAmount } from "@/lib/format"
import { fieldErrors, problemMessage, toFieldErrors } from "@/lib/problem"

const PAGE_SIZE = 10
const NO_CATEGORY = "none"

const sortOptions = [
  { value: "name", label: "products.sortNameAsc" },
  { value: "-name", label: "products.sortNameDesc" },
  { value: "price", label: "products.sortPriceAsc" },
  { value: "-price", label: "products.sortPriceDesc" },
] as const

function useDebounced<T>(value: T, delay: number): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delay)
    return () => clearTimeout(timer)
  }, [value, delay])

  return debounced
}

export function ProductsPage() {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState("")
  const [sort, setSort] = useState<string>("name")
  const [editing, setEditing] = useState<ProductResponse | "new" | null>(null)
  const [deleting, setDeleting] = useState<ProductResponse | null>(null)

  const term = useDebounced(search.trim(), 300)

  const products = useListProducts({
    page,
    pageSize: PAGE_SIZE,
    sort,
    search: term.length > 0 ? term : undefined,
  })
  const categories = useListCategories({ pageSize: 100 })
  const categoryList = categories.data?.data.items ?? []
  const categoryNames = new Map(
    categoryList.map((category) => [category.id, category.name])
  )

  const queryClient = useQueryClient()
  const remove = useDeleteProduct({
    mutation: {
      onSuccess: async () => {
        toast.success(t("products.deleted"))
        setDeleting(null)
        await queryClient.invalidateQueries({
          queryKey: getListProductsQueryKey(),
        })
      },
    },
  })

  const result = products.data?.data
  const items = result?.items ?? []

  return (
    <>
      <PageHeader
        title={t("products.title")}
        description={t("products.description")}
      >
        <Button onClick={() => setEditing("new")}>
          <PlusIcon data-icon="inline-start" aria-hidden="true" />
          {t("products.new")}
        </Button>
      </PageHeader>

      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="search"
          value={search}
          onChange={(event) => {
            setSearch(event.target.value)
            setPage(1)
          }}
          placeholder={t("products.searchPlaceholder")}
          aria-label={t("common.search")}
          className="w-full sm:max-w-xs"
        />
        <Select
          value={sort}
          onValueChange={(value) => {
            setSort(value)
            setPage(1)
          }}
        >
          <SelectTrigger
            aria-label={t("products.sort")}
            className="w-full sm:w-56"
          >
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {sortOptions.map((option) => (
              <SelectItem key={option.value} value={option.value}>
                {t(option.label)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {products.isError ? (
        <LoadError
          error={products.error}
          onRetry={() => void products.refetch()}
        />
      ) : !products.isPending && items.length === 0 ? (
        <EmptyState
          icon={<PackageIcon aria-hidden="true" />}
          title={
            term.length > 0 ? t("products.emptySearch") : t("products.empty")
          }
          description={
            term.length > 0 ? undefined : t("products.emptyDescription")
          }
        >
          {term.length > 0 ? null : (
            <Button onClick={() => setEditing("new")}>
              {t("products.new")}
            </Button>
          )}
        </EmptyState>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("products.name")}</TableHead>
                  <TableHead>{t("products.category")}</TableHead>
                  <TableHead className="text-right">
                    {t("products.price")}
                  </TableHead>
                  <TableHead className="w-24 text-right">
                    {t("common.actions")}
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {products.isPending ? (
                  <TableSkeleton columns={4} />
                ) : (
                  items.map((product) => (
                    <TableRow key={product.id}>
                      <TableCell className="max-w-80">
                        <div className="truncate font-medium">
                          {product.name}
                        </div>
                        {product.description ? (
                          <div className="truncate text-xs text-muted-foreground">
                            {product.description}
                          </div>
                        ) : null}
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {product.categoryId === null
                          ? "—"
                          : (categoryNames.get(product.categoryId) ??
                            `#${product.categoryId}`)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatAmount(product.price)}
                      </TableCell>
                      <TableCell className="text-right">
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label={t("products.editAction", {
                            name: product.name,
                          })}
                          onClick={() => setEditing(product)}
                        >
                          <PencilIcon aria-hidden="true" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label={t("products.deleteAction", {
                            name: product.name,
                          })}
                          onClick={() => setDeleting(product)}
                        >
                          <Trash2Icon aria-hidden="true" />
                        </Button>
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

      {editing ? (
        <ProductDialog
          product={editing === "new" ? null : editing}
          categories={categoryList}
          onClose={() => setEditing(null)}
        />
      ) : null}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => {
          if (!open) {
            setDeleting(null)
            remove.reset()
          }
        }}
        title={t("products.deleteTitle")}
        description={t("products.deleteDescription", {
          name: deleting?.name ?? "",
        })}
        confirmLabel={t("common.delete")}
        pending={remove.isPending}
        error={
          remove.isError
            ? problemMessage(remove.error, t("common.unexpectedError"))
            : undefined
        }
        onConfirm={() => {
          if (deleting) {
            remove.mutate({ id: deleting.id })
          }
        }}
      />
    </>
  )
}

function ProductDialog({
  product,
  categories,
  onClose,
}: {
  product: ProductResponse | null
  categories: CategoryResponse[]
  onClose: () => void
}) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [name, setName] = useState(product?.name ?? "")
  const [price, setPrice] = useState(product ? String(product.price) : "")
  const [description, setDescription] = useState(product?.description ?? "")
  const [categoryId, setCategoryId] = useState(
    product?.categoryId == null ? NO_CATEGORY : String(product.categoryId)
  )

  const options = {
    mutation: {
      onSuccess: async () => {
        toast.success(product ? t("products.updated") : t("products.created"))
        onClose()
        await queryClient.invalidateQueries({
          queryKey: getListProductsQueryKey(),
        })
      },
    },
  }

  const create = useCreateProduct(options)
  const update = useUpdateProduct(options)
  const mutation = product ? update : create

  // Doğrulamayı backend yapar; alan hataları yanıttan okunur ve ilgili alanın altında gösterilir.
  const errors = fieldErrors(mutation.error)
  const general =
    mutation.isError && Object.keys(errors).length === 0
      ? problemMessage(mutation.error, t("common.unexpectedError"))
      : undefined

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const data = {
      name: name.trim(),
      price: price.trim() === "" ? 0 : Number(price),
      description: description.trim() === "" ? null : description.trim(),
      categoryId: categoryId === NO_CATEGORY ? null : Number(categoryId),
      // PUT kaydın tamamını değiştirir; var olan çanta geri gönderilmezse boşalırdı.
      metadata: product?.metadata,
    }

    if (product) {
      update.mutate({ id: product.id, data })
    } else {
      create.mutate({ data })
    }
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
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            {product ? t("products.edit") : t("products.new")}
          </DialogTitle>
        </DialogHeader>
        <form onSubmit={submit} noValidate>
          <FieldGroup>
            <Field data-invalid={errors.name ? true : undefined}>
              <FieldLabel htmlFor="product-name">
                {t("products.name")}
              </FieldLabel>
              <Input
                id="product-name"
                value={name}
                onChange={(event) => setName(event.target.value)}
                aria-invalid={errors.name ? true : undefined}
                autoFocus
                required
              />
              <FieldError errors={toFieldErrors(errors.name)} />
            </Field>
            <Field data-invalid={errors.price ? true : undefined}>
              <FieldLabel htmlFor="product-price">
                {t("products.price")}
              </FieldLabel>
              <Input
                id="product-price"
                type="number"
                inputMode="decimal"
                min={0}
                step="0.01"
                value={price}
                onChange={(event) => setPrice(event.target.value)}
                aria-invalid={errors.price ? true : undefined}
              />
              <FieldError errors={toFieldErrors(errors.price)} />
            </Field>
            <Field data-invalid={errors.categoryId ? true : undefined}>
              <FieldLabel htmlFor="product-category">
                {t("products.category")}
              </FieldLabel>
              <Select value={categoryId} onValueChange={setCategoryId}>
                <SelectTrigger id="product-category" className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NO_CATEGORY}>
                    {t("common.none")}
                  </SelectItem>
                  {categories.map((category) => (
                    <SelectItem key={category.id} value={String(category.id)}>
                      {category.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <FieldError errors={toFieldErrors(errors.categoryId)} />
            </Field>
            <Field data-invalid={errors.description ? true : undefined}>
              <FieldLabel htmlFor="product-description">
                {t("products.descriptionField")}
              </FieldLabel>
              <Textarea
                id="product-description"
                value={description}
                onChange={(event) => setDescription(event.target.value)}
                rows={3}
              />
              <FieldError errors={toFieldErrors(errors.description)} />
            </Field>
            {general ? <FieldError>{general}</FieldError> : null}
          </FieldGroup>
          <DialogFooter className="mt-6">
            <Button type="button" variant="outline" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" disabled={mutation.isPending}>
              {mutation.isPending ? t("common.saving") : t("common.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
