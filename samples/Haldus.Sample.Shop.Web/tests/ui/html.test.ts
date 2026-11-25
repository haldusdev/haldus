import assert from "node:assert/strict"
import { describe, it } from "node:test"

import {
  accessibilityProblems,
  accessibleName,
  all,
  keyPaths,
  one,
  parse,
  text,
  untranslated,
} from "../support/html.ts"

// Bileşen testleri bu okuyucuya dayanır; yanlış okursa onların söylediği de yanlış olur.
describe("html scanner", () => {
  describe("parse", () => {
    it("builds a tree from nested and void elements", () => {
      const root = parse(
        '<form><label for="a">Name</label><input id="a" type="text"/><br><p>Hi <b>there</b></p></form>'
      )
      const form = one(root, "form")

      assert.deepEqual(
        form.children.map((child) =>
          typeof child === "string" ? child : child.tag
        ),
        ["label", "input", "br", "p"]
      )
      assert.equal(text(one(root, "p")), "Hi there")
      assert.equal(one(root, "b").parent?.tag, "p")
    })

    it("lowercases attribute names and decodes entities", () => {
      const root = parse(
        '<input autoComplete="email" aria-label="Tom &amp; Jerry&#x27;s" required=""/><p>1 &lt; 2 &gt; 0 &quot;ok&quot;</p>'
      )
      const input = one(root, "input")

      assert.equal(input.attributes.autocomplete, "email")
      assert.equal(input.attributes["aria-label"], "Tom & Jerry's")
      assert.equal(input.attributes.required, "")
      assert.equal(text(one(root, "p")), '1 < 2 > 0 "ok"')
    })

    it("skips comments and keeps script content out of the text", () => {
      const root = parse(
        "<div>a<!-- note -->b<script>if (a < b) {}</script></div>"
      )

      assert.equal(text(root), "ab")
    })

    it("rejects markup that does not nest", () => {
      assert.throws(() => parse("<div><p></div>"), /Unexpected <\/div>/)
      assert.throws(() => parse("<div>"), /never closed/)
    })
  })

  describe("selectors", () => {
    const root = parse(
      '<div><input id="email" type="email"/><input type="password"/><button type="submit" disabled="">Go</button></div>'
    )

    it("matches by tag, id and attribute", () => {
      assert.equal(all(root, "input").length, 2)
      assert.equal(one(root, "#email").attributes.type, "email")
      assert.equal(one(root, "input[type=password]").attributes.id, undefined)
      assert.equal(one(root, 'button[type="submit"][disabled]').tag, "button")
      assert.equal(all(root, "[disabled]").length, 1)
    })

    it("one() insists on a single match", () => {
      assert.throws(() => one(root, "input"), /found 2/)
      assert.throws(() => one(root, "select"), /found 0/)
    })
  })

  describe("accessible name", () => {
    it("reads label[for], a wrapping label, aria-label and aria-labelledby", () => {
      const root = parse(
        '<label for="a">First</label><input id="a"/>' +
          "<label>Second <input/></label>" +
          '<input aria-label="Third"/>' +
          '<h2 id="t">Fourth</h2><input aria-labelledby="t"/>'
      )
      const names = all(root, "input").map((input) =>
        accessibleName(root, input)
      )

      assert.deepEqual(names, ["First", "Second", "Third", "Fourth"])
    })

    it("does not count a placeholder or hidden text as a name", () => {
      const root = parse(
        '<input placeholder="Search"/><button><svg aria-hidden="true"><title>x</title></svg></button>'
      )

      assert.equal(accessibleName(root, one(root, "input")), "")
      assert.equal(accessibleName(root, one(root, "button")), "")
    })
  })

  describe("accessibilityProblems", () => {
    it("accepts labelled fields and named buttons", () => {
      const root = parse(
        '<label for="q">Search</label><input id="q" placeholder="Name"/>' +
          '<button aria-label="Delete"><svg aria-hidden="true"></svg></button>' +
          '<button>Save</button><a href="/x">Open</a>' +
          '<select aria-hidden="true"></select><input type="hidden"/>'
      )

      assert.deepEqual(accessibilityProblems(root), [])
    })

    it("reports a field that only has a placeholder", () => {
      assert.deepEqual(
        accessibilityProblems(
          parse('<input type="search" placeholder="Search"/>')
        ),
        ['<input type="search" placeholder="Search"> has no label']
      )
    })

    it("reports icon-only buttons and empty links", () => {
      assert.deepEqual(
        accessibilityProblems(
          parse(
            '<button type="button"><svg aria-hidden="true"></svg></button><a href="/x"></a>'
          )
        ),
        [
          '<button type="button"> has no accessible name',
          '<a href="/x"> has no accessible name',
        ]
      )
    })

    it("reports more than one main landmark", () => {
      assert.deepEqual(
        accessibilityProblems(parse("<main><main>x</main></main>")),
        ["2 <main> landmarks; a page has one"]
      )
    })

    it("reports duplicate ids and references to missing ids", () => {
      assert.deepEqual(
        accessibilityProblems(
          parse(
            '<p id="a">x</p><p id="a">y</p><label for="gone">L</label>' +
              '<input aria-label="N" aria-describedby="help"/>'
          )
        ),
        [
          'id "a" is used more than once',
          '<label> points to missing id "gone"',
          '<input> points to missing id "help"',
        ]
      )
    })
  })

  describe("untranslated", () => {
    it("finds raw keys and unfilled placeholders in text and labels", () => {
      const root = parse(
        '<h1>login.title</h1><p>Sent to {{email}}.</p><input aria-label="common.search" placeholder="orders.form.product"/>'
      )

      assert.deepEqual(untranslated(root).sort(), [
        "common.search",
        "login.title",
        "orders.form.product",
        "{{email}}",
      ])
    })

    it("ignores e-mail addresses, numbers and ordinary sentences", () => {
      const root = parse(
        '<p>We sent a code to ada@example.com. Total: 1,234.50 · 1.234,50</p><a href="/orders/a.b">Open</a><p class="a.b">End.</p>'
      )

      assert.deepEqual(untranslated(root), [])
    })
  })

  it("keyPaths lists every leaf of a resource tree", () => {
    assert.deepEqual(keyPaths({ a: { b: "x", c: { d: "y" } }, e: "z" }), [
      "a.b",
      "a.c.d",
      "e",
    ])
  })
})
