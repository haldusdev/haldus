import assert from "node:assert/strict"
import { after, before, describe, it } from "node:test"

import type { CategoryResponse, ProductResponse } from "@/api/model"

import { calls } from "../support/fetch.ts"
import {
  accessibilityProblems,
  accessibleName,
  all,
  one,
  text,
  untranslated,
} from "../support/html.ts"
import {
  createQueryClient,
  fill,
  languages,
  locales,
  render,
  setLanguage,
} from "../support/ui.tsx"

const { getListCategoriesQueryKey } =
  await import("@/api/categories/categories")
const { getListProductsQueryKey } = await import("@/api/products/products")
const { ProductsPage } = await import("@/pages/products")

const lamp: ProductResponse = {
  id: "p-1",
  name: "Desk lamp",
  price: 1234.5,
  description: "Warm light",
  categoryId: 3,
  metadata: {},
}
const cable: ProductResponse = {
  id: "p-2",
  name: "Cable",
  price: 9,
  description: null,
  categoryId: null,
  metadata: {},
}
const orphan: ProductResponse = {
  id: "p-3",
  name: "Mystery box",
  price: 0.5,
  description: null,
  categoryId: 7,
  metadata: {},
}
const lighting: CategoryResponse = {
  id: 3,
  name: "Lighting",
  parentId: null,
  position: 0,
}

/**
 * Önbelleği, sayfanın ilk çizimde soracağı sorguların yanıtlarıyla doldurur. Anahtarlar üretilen kancaların
 * kullandığı işlevlerden gelir; sayfa başka parametrelerle sorarsa veri bulunmaz ve testler yükleniyor halini görür.
 */
function seeded(products: ProductResponse[], totalCount = products.length) {
  const client = createQueryClient()

  client.setQueryData(
    getListProductsQueryKey({ page: 1, pageSize: 10, sort: "name" }),
    {
      data: { items: products, page: 1, pageSize: 10, totalCount },
      status: 200,
      headers: new Headers(),
    }
  )
  client.setQueryData(getListCategoriesQueryKey({ pageSize: 100 }), {
    data: { items: [lighting], page: 1, pageSize: 100, totalCount: 1 },
    status: 200,
    headers: new Headers(),
  })

  return client
}

function cells(row: Parameters<typeof all>[0]): string[] {
  return all(row, "td").map(text)
}

// Arama, sıralama, sayfa değiştirme, ürün penceresi ve silme tıklamayla veya yazmayla başlar; isteğin kendisi de
// (yükleme bittiğinde tablonun dolması, hata hali) bir effect'tir. Burada yalnızca önbelleğin o anki haline göre
// yapılan ilk çizim sınanır.
describe("ProductsPage", () => {
  after(() => setLanguage("en"))

  for (const language of languages) {
    describe(language, () => {
      const copy = locales[language]
      const amount = (value: number) =>
        new Intl.NumberFormat(language, {
          minimumFractionDigits: 2,
          maximumFractionDigits: 2,
        }).format(value)

      before(() => setLanguage(language))

      it("has a heading, a new-product button and labelled list controls", () => {
        const { root } = render(<ProductsPage />)

        assert.equal(text(one(root, "h1")), copy.products.title)
        assert.ok(text(root).includes(copy.products.description))
        assert.ok(all(root, "button").map(text).includes(copy.products.new))

        const search = one(root, "input[type=search]")

        assert.equal(accessibleName(root, search), copy.common.search)
        assert.equal(
          search.attributes.placeholder,
          copy.products.searchPlaceholder
        )
        assert.equal(
          accessibleName(root, one(root, "button[role=combobox]")),
          copy.products.sort
        )
      })

      describe("while the list is loading", () => {
        it("keeps the table header and fills the body with placeholders", () => {
          const { root } = render(<ProductsPage />)

          assert.deepEqual(all(root, "th").map(text), [
            copy.products.name,
            copy.products.category,
            copy.products.price,
            copy.common.actions,
          ])

          const rows = all(one(root, "tbody"), "tr")

          assert.equal(rows.length, 5)

          for (const row of rows) {
            assert.equal(all(row, "[data-slot=skeleton]").length, 4)
            assert.equal(text(row), "")
          }
        })

        it("shows neither an empty state, an error nor pagination", () => {
          const { root } = render(<ProductsPage />)

          assert.equal(all(root, "[data-slot=empty]").length, 0)
          assert.equal(all(root, "[role=alert]").length, 0)
          assert.equal(all(root, "nav").length, 0)
        })

        it("is labelled and fully translated", () => {
          const { root } = render(<ProductsPage />)

          assert.deepEqual(accessibilityProblems(root), [])
          assert.deepEqual(untranslated(root), [])
        })
      })

      describe("with products in the cache", () => {
        const page = () =>
          render(<ProductsPage />, { client: seeded([cable, lamp, orphan]) })

        it("renders one row per product, in the order received", () => {
          const { root } = page()
          const rows = all(one(root, "tbody"), "tr")

          assert.equal(all(root, "[data-slot=skeleton]").length, 0)
          assert.deepEqual(
            rows.map((row) => cells(row).slice(0, 3)),
            [
              ["Cable", "—", amount(9)],
              ["Desk lampWarm light", "Lighting", amount(1234.5)],
              ["Mystery box", "#7", amount(0.5)],
            ]
          )
        })

        it("formats prices for the interface language", () => {
          const prices = all(one(page().root, "tbody"), "tr").map(
            (row) => cells(row)[2]
          )

          assert.equal(prices[1], language === "tr" ? "1.234,50" : "1,234.50")
        })

        it("gives each row its own named edit and delete buttons", () => {
          const { root } = page()
          const names = all(one(root, "tbody"), "button").map((button) =>
            accessibleName(root, button)
          )

          assert.deepEqual(
            names,
            [cable, lamp, orphan].flatMap((product) => [
              fill(copy.products.editAction, { name: product.name }),
              fill(copy.products.deleteAction, { name: product.name }),
            ])
          )
        })

        it("summarises the page and disables paging on a single page", () => {
          const { root } = page()
          const pagination = one(root, "nav")

          assert.equal(
            text(one(pagination, "p")),
            fill(copy.pagination.summary_other, { page: 1, pages: 1, count: 3 })
          )
          assert.deepEqual(
            all(pagination, "button").map((button) => [
              text(button),
              "disabled" in button.attributes,
            ]),
            [
              [copy.pagination.previous, true],
              [copy.pagination.next, true],
            ]
          )
        })

        it("enables the next page when there are more records", () => {
          const { root } = render(<ProductsPage />, {
            client: seeded([cable, lamp, orphan], 25),
          })
          const pagination = one(root, "nav")

          assert.equal(
            text(one(pagination, "p")),
            fill(copy.pagination.summary_other, {
              page: 1,
              pages: 3,
              count: 25,
            })
          )
          assert.deepEqual(
            all(pagination, "button").map(
              (button) => "disabled" in button.attributes
            ),
            [true, false]
          )
        })

        it("is labelled and fully translated", () => {
          const { root } = page()

          assert.deepEqual(accessibilityProblems(root), [])
          assert.deepEqual(untranslated(root), [])
        })
      })

      describe("with an empty list in the cache", () => {
        it("invites to create the first product instead of an empty table", () => {
          const { root } = render(<ProductsPage />, { client: seeded([]) })
          const empty = one(root, "[data-slot=empty]")

          assert.equal(all(root, "table").length, 0)
          assert.equal(all(root, "nav").length, 0)
          assert.ok(text(empty).includes(copy.products.empty))
          assert.ok(text(empty).includes(copy.products.emptyDescription))
          assert.equal(text(one(empty, "button")), copy.products.new)
          assert.deepEqual(accessibilityProblems(root), [])
          assert.deepEqual(untranslated(root), [])
        })
      })
    })
  }

  it("shows product text as text, never as markup", () => {
    const { root } = render(<ProductsPage />, {
      client: seeded([{ ...cable, name: "<script>alert(1)</script>" }]),
    })

    assert.equal(all(root, "script").length, 0)
    assert.ok(text(one(root, "tbody")).includes("<script>alert(1)</script>"))
  })

  it("does not call the backend while rendering", () => {
    render(<ProductsPage />)
    render(<ProductsPage />, { client: seeded([lamp]) })

    assert.equal(calls.length, 0)
  })
})
