import assert from "node:assert/strict"
import { describe, it } from "node:test"

import en from "../../src/i18n/locales/en.json"
import tr from "../../src/i18n/locales/tr.json"
import { keyPaths } from "../support/html.ts"

function lookup(resources: unknown, key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (value, part) =>
        typeof value === "object" && value !== null
          ? (value as Record<string, unknown>)[part]
          : undefined,
      resources
    )
}

function placeholders(value: unknown): string[] {
  return typeof value === "string"
    ? [...value.matchAll(/\{\{\s*(\w+)\s*\}\}/g)]
        .map((match) => match[1])
        .sort()
    : []
}

// Eksik bir anahtar derlemeyi bozmaz: i18next İngilizceye düşer ve Türkçe arayüzde İngilizce metin görünür.
describe("translation resources", () => {
  const keys = keyPaths(en).sort()

  it("en and tr have exactly the same keys", () => {
    assert.deepEqual(keyPaths(tr).sort(), keys)
  })

  it("no text is empty", () => {
    for (const [language, resources] of Object.entries({ en, tr })) {
      const empty = keys.filter((key) => {
        const value = lookup(resources, key)
        return typeof value !== "string" || value.trim() === ""
      })

      assert.deepEqual(empty, [], `empty texts in ${language}`)
    }
  })

  it("a text uses the same placeholders in both languages", () => {
    const different = keys.filter(
      (key) =>
        placeholders(lookup(en, key)).join() !==
        placeholders(lookup(tr, key)).join()
    )

    assert.deepEqual(different, [])
  })

  it("every language has a name in every language", () => {
    assert.deepEqual(Object.keys(en.languages).sort(), ["en", "tr"])
    assert.deepEqual(Object.keys(tr.languages).sort(), ["en", "tr"])
  })
})
