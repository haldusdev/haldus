import assert from "node:assert/strict"
import { after, before, describe, it } from "node:test"

import type { OrderResponse } from "@/api/model"

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

const { getListOrdersQueryKey } = await import("@/api/orders/orders")
const { OrdersPage } = await import("@/pages/orders")

function order(overrides: Partial<OrderResponse>): OrderResponse {
  return {
    id: "o-1",
    customerName: "Ada Lovelace",
    total: 1234.5,
    itemCount: 2,
    placementNote: null,
    cancellationReason: null,
    cartId: null,
    createdAt: "2026-03-04T15:06:00Z",
    updatedAt: null,
    status: "Draft",
    number: null,
    orderItems: [],
    ...overrides,
  }
}

const draft = order({})
const placed = order({
  id: "o-2",
  customerName: "Grace Hopper",
  status: "Placed",
  number: "ORD-2026-0007",
  total: 80,
  itemCount: 1,
})

function seeded(orders: OrderResponse[]) {
  const client = createQueryClient()

  client.setQueryData(getListOrdersQueryKey({ page: 1, pageSize: 10 }), {
    data: { items: orders, page: 1, pageSize: 10, totalCount: orders.length },
    status: 200,
    headers: new Headers(),
  })

  return client
}

// Duruma göre süzme, sayfa değiştirme ve yeni sipariş penceresi tıklamayla başlar; burada sınanmaz.
describe("OrdersPage", () => {
  after(() => setLanguage("en"))

  for (const language of languages) {
    describe(language, () => {
      const copy = locales[language]

      before(() => setLanguage(language))

      it("shows placeholders while the list is loading", () => {
        const { root } = render(<OrdersPage />)

        assert.equal(text(one(root, "h1")), copy.orders.title)
        assert.deepEqual(all(root, "th").map(text), [
          copy.orders.customer,
          copy.orders.number,
          copy.orders.statusLabel,
          copy.orders.items,
          copy.orders.total,
          copy.orders.created,
        ])
        assert.equal(all(root, "[data-slot=skeleton]").length, 30)
        assert.equal(all(root, "nav").length, 0)
        assert.equal(
          accessibleName(root, one(root, "button[role=combobox]")),
          copy.orders.filterByStatus
        )
      })

      it("renders the cached orders with status, total and date", () => {
        const { root } = render(<OrdersPage />, {
          client: seeded([draft, placed]),
        })
        const rows = all(one(root, "tbody"), "tr").map((row) =>
          all(row, "td").map(text)
        )
        const amount = new Intl.NumberFormat(language, {
          minimumFractionDigits: 2,
          maximumFractionDigits: 2,
        })
        const created = text(
          new Intl.DateTimeFormat(language, {
            dateStyle: "medium",
            timeStyle: "short",
          }).format(new Date(draft.createdAt))
        )

        assert.deepEqual(rows, [
          [
            "Ada Lovelace",
            "—",
            copy.orders.status.Draft,
            "2",
            amount.format(1234.5),
            created,
          ],
          [
            "Grace Hopper",
            "ORD-2026-0007",
            copy.orders.status.Placed,
            "1",
            amount.format(80),
            created,
          ],
        ])
      })

      it("links each order to its page with a descriptive name", () => {
        const { root } = render(<OrdersPage />, {
          client: seeded([draft, placed]),
        })
        const links = all(one(root, "tbody"), "a")

        assert.deepEqual(
          links.map((link) => [
            link.attributes.href,
            accessibleName(root, link),
          ]),
          [
            [
              "/orders/o-1",
              fill(copy.orders.open, { customer: "Ada Lovelace" }),
            ],
            [
              "/orders/o-2",
              fill(copy.orders.open, { customer: "Grace Hopper" }),
            ],
          ]
        )
      })

      it("shows an empty state for a tenant without orders", () => {
        const { root } = render(<OrdersPage />, { client: seeded([]) })
        const empty = one(root, "[data-slot=empty]")

        assert.equal(all(root, "table").length, 0)
        assert.ok(text(empty).includes(copy.orders.empty))
        assert.equal(text(one(empty, "button")), copy.orders.new)
      })

      it("is labelled and fully translated in every state", () => {
        for (const client of [undefined, seeded([draft, placed]), seeded([])]) {
          const { root } = render(<OrdersPage />, { client })

          assert.deepEqual(accessibilityProblems(root), [])
          assert.deepEqual(untranslated(root), [])
        }
      })
    })
  }
})
