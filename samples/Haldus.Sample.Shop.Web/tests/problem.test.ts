import assert from "node:assert/strict"
import { describe, it } from "node:test"

import {
  fieldErrors,
  problemMessage,
  problemStatus,
  toFieldErrors,
} from "@/lib/problem"

describe("problemStatus", () => {
  it("reads the status of a problem", () => {
    assert.equal(problemStatus({ status: 409, title: "Conflict" }), 409)
  })

  it("is undefined for anything that carries no numeric status", () => {
    assert.equal(problemStatus({ status: "409" }), undefined)
    assert.equal(problemStatus({ title: "Conflict" }), undefined)
    assert.equal(problemStatus(new TypeError("fetch failed")), undefined)
    assert.equal(problemStatus("409"), undefined)
    assert.equal(problemStatus(null), undefined)
    assert.equal(problemStatus(undefined), undefined)
  })
})

describe("problemMessage", () => {
  it("prefers the detail", () => {
    assert.equal(
      problemMessage({ title: "Conflict", detail: "Already shipped." }, "Oops"),
      "Already shipped."
    )
  })

  it("falls back to the title when the detail is missing or empty", () => {
    assert.equal(problemMessage({ title: "Conflict" }, "Oops"), "Conflict")
    assert.equal(
      problemMessage({ title: "Conflict", detail: "" }, "Oops"),
      "Conflict"
    )
    assert.equal(
      problemMessage({ title: "Conflict", detail: 7 }, "Oops"),
      "Conflict"
    )
  })

  it("falls back to the given text when there is neither", () => {
    assert.equal(problemMessage({ status: 500 }, "Oops"), "Oops")
    assert.equal(problemMessage({ title: "", detail: "" }, "Oops"), "Oops")
    assert.equal(problemMessage(null, "Oops"), "Oops")
    assert.equal(problemMessage("Conflict", "Oops"), "Oops")
  })

  it("does not show the message of a thrown Error", () => {
    assert.equal(problemMessage(new TypeError("fetch failed"), "Oops"), "Oops")
  })
})

describe("fieldErrors", () => {
  it("returns the messages by field", () => {
    assert.deepEqual(
      fieldErrors({ errors: { name: ["Required."], price: ["Too low."] } }),
      { name: ["Required."], price: ["Too low."] }
    )
  })

  it("lowers the first letter of every segment of a field name", () => {
    assert.deepEqual(
      fieldErrors({
        errors: {
          Name: ["a"],
          "OrderItems[0].Quantity": ["b"],
          "ShippingAddress.PostalCode": ["c"],
          sku: ["d"],
        },
      }),
      {
        name: ["a"],
        "orderItems[0].quantity": ["b"],
        "shippingAddress.postalCode": ["c"],
        sku: ["d"],
      }
    )
  })

  it("keeps the rest of the name as it is", () => {
    assert.deepEqual(fieldErrors({ errors: { SKUCode: ["a"] } }), {
      sKUCode: ["a"],
    })
  })

  it("drops messages that are not text and fields that are not lists", () => {
    assert.deepEqual(
      fieldErrors({
        errors: { name: ["Required.", 5, null], price: "Too low." },
      }),
      { name: ["Required."] }
    )
  })

  it("is empty when the error carries no field errors", () => {
    assert.deepEqual(fieldErrors({ title: "Conflict", status: 409 }), {})
    assert.deepEqual(fieldErrors({ errors: "nope" }), {})
    assert.deepEqual(fieldErrors({ errors: null }), {})
    assert.deepEqual(fieldErrors(new Error("boom")), {})
    assert.deepEqual(fieldErrors(undefined), {})
  })
})

describe("toFieldErrors", () => {
  it("wraps every message", () => {
    assert.deepEqual(toFieldErrors(["a", "b"]), [
      { message: "a" },
      { message: "b" },
    ])
  })

  it("passes a missing list through", () => {
    assert.equal(toFieldErrors(undefined), undefined)
  })
})
