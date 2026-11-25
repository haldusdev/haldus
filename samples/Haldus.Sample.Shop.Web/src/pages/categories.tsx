import { useState } from "react"
import { useQueryClient } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { FolderTreeIcon, PlusIcon, Trash2Icon } from "lucide-react"
import { toast } from "sonner"

import {
  getListCategoriesQueryKey,
  useCreateCategory,
  useDeleteCategory,
  useListCategories,
} from "@/api/categories/categories"
import type { CategoryResponse } from "@/api/model"
import { ConfirmDialog } from "@/components/confirm-dialog"
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
import { fieldErrors, problemMessage, toFieldErrors } from "@/lib/problem"

const NO_PARENT = "none"

type TreeRow = { category: CategoryResponse; depth: number }

/** Düz listeyi ağaç sırasına dizer: her düğüm üst düğümünün altında, kardeşler kendi sıralarında. */
function toTree(categories: CategoryResponse[]): TreeRow[] {
  const children = new Map<number | null, CategoryResponse[]>()

  for (const category of categories) {
    const siblings = children.get(category.parentId) ?? []
    siblings.push(category)
    children.set(category.parentId, siblings)
  }

  const rows: TreeRow[] = []

  function walk(parentId: number | null, depth: number) {
    const siblings = [...(children.get(parentId) ?? [])].sort(
      (left, right) => left.position - right.position
    )

    for (const category of siblings) {
      rows.push({ category, depth })
      walk(category.id, depth + 1)
    }
  }

  walk(null, 0)
  return rows
}

export function CategoriesPage() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [creating, setCreating] = useState(false)
  const [deleting, setDeleting] = useState<CategoryResponse | null>(null)

  const categories = useListCategories({ pageSize: 100 })
  const items = categories.data?.data.items ?? []
  const rows = toTree(items)

  const remove = useDeleteCategory({
    mutation: {
      onSuccess: async () => {
        toast.success(t("categories.deleted"))
        setDeleting(null)
        await queryClient.invalidateQueries({
          queryKey: getListCategoriesQueryKey(),
        })
      },
    },
  })

  return (
    <>
      <PageHeader
        title={t("categories.title")}
        description={t("categories.description")}
      >
        <Button onClick={() => setCreating(true)}>
          <PlusIcon data-icon="inline-start" aria-hidden="true" />
          {t("categories.new")}
        </Button>
      </PageHeader>

      {categories.isError ? (
        <LoadError
          error={categories.error}
          onRetry={() => void categories.refetch()}
        />
      ) : !categories.isPending && rows.length === 0 ? (
        <EmptyState
          icon={<FolderTreeIcon aria-hidden="true" />}
          title={t("categories.empty")}
          description={t("categories.emptyDescription")}
        >
          <Button onClick={() => setCreating(true)}>
            {t("categories.new")}
          </Button>
        </EmptyState>
      ) : (
        <div className="overflow-hidden rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("categories.name")}</TableHead>
                <TableHead className="w-24 text-right">
                  {t("categories.position")}
                </TableHead>
                <TableHead className="w-24 text-right">
                  {t("common.actions")}
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {categories.isPending ? (
                <TableSkeleton columns={3} />
              ) : (
                rows.map(({ category, depth }) => (
                  <TableRow key={category.id}>
                    <TableCell>
                      <span
                        className="font-medium"
                        style={{ paddingInlineStart: `${depth * 1.25}rem` }}
                      >
                        {category.name}
                      </span>
                    </TableCell>
                    <TableCell className="text-right text-muted-foreground tabular-nums">
                      {category.position + 1}
                    </TableCell>
                    <TableCell className="text-right">
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={t("categories.deleteAction", {
                          name: category.name,
                        })}
                        onClick={() => setDeleting(category)}
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
      )}

      {creating ? (
        <CategoryDialog categories={items} onClose={() => setCreating(false)} />
      ) : null}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => {
          if (!open) {
            setDeleting(null)
            remove.reset()
          }
        }}
        title={t("categories.deleteTitle")}
        description={t("categories.deleteDescription", {
          name: deleting?.name ?? "",
        })}
        confirmLabel={t("common.delete")}
        pending={remove.isPending}
        // Ürünü veya alt kategorisi olan kategori silinemez (409); nedeni backend açıklar.
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

function CategoryDialog({
  categories,
  onClose,
}: {
  categories: CategoryResponse[]
  onClose: () => void
}) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [name, setName] = useState("")
  const [parentId, setParentId] = useState(NO_PARENT)

  const create = useCreateCategory({
    mutation: {
      onSuccess: async () => {
        toast.success(t("categories.created"))
        onClose()
        await queryClient.invalidateQueries({
          queryKey: getListCategoriesQueryKey(),
        })
      },
    },
  })

  const errors = fieldErrors(create.error)
  const general =
    create.isError && Object.keys(errors).length === 0
      ? problemMessage(create.error, t("common.unexpectedError"))
      : undefined

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()

    create.mutate({
      data: {
        name: name.trim(),
        parentId: parentId === NO_PARENT ? null : Number(parentId),
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
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("categories.new")}</DialogTitle>
        </DialogHeader>
        <form onSubmit={submit} noValidate>
          <FieldGroup>
            <Field data-invalid={errors.name ? true : undefined}>
              <FieldLabel htmlFor="category-name">
                {t("categories.name")}
              </FieldLabel>
              <Input
                id="category-name"
                value={name}
                onChange={(event) => setName(event.target.value)}
                aria-invalid={errors.name ? true : undefined}
                autoFocus
                required
              />
              <FieldError errors={toFieldErrors(errors.name)} />
            </Field>
            <Field data-invalid={errors.parentId ? true : undefined}>
              <FieldLabel htmlFor="category-parent">
                {t("categories.parent")}
              </FieldLabel>
              <Select value={parentId} onValueChange={setParentId}>
                <SelectTrigger id="category-parent" className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NO_PARENT}>
                    {t("categories.noParent")}
                  </SelectItem>
                  {toTree(categories).map(({ category, depth }) => (
                    <SelectItem key={category.id} value={String(category.id)}>
                      {"— ".repeat(depth)}
                      {category.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <FieldError errors={toFieldErrors(errors.parentId)} />
            </Field>
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
