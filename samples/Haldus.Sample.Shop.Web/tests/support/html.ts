// Sunucuda çizilen HTML'i okumak için elle yazılmış küçük bir tarayıcı. Bir DOM kütüphanesi yoktur; React'in ürettiği
// (iyi biçimli) çıktıyı ağaca çevirir ve testlerin sorduğu şeyleri yanıtlar: öğe bulma, metin, erişilebilir ad.

export type HtmlElement = {
  tag: string
  attributes: Record<string, string>
  children: HtmlNode[]
  parent: HtmlElement | null
}

export type HtmlNode = HtmlElement | string

const VOID = new Set([
  "area",
  "base",
  "br",
  "col",
  "embed",
  "hr",
  "img",
  "input",
  "link",
  "meta",
  "source",
  "track",
  "wbr",
])

const ENTITIES: Record<string, string> = {
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
  nbsp: " ",
}

const TAG = /<(\/?)([a-zA-Z][^\s/>]*)/y
const TAG_END = /\s*(\/?)>/y
const ATTRIBUTE = /\s*([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/y

function decode(text: string): string {
  return text.replace(
    /&(#x[0-9a-f]+|#\d+|[a-z]+);/gi,
    (whole, code: string) => {
      if (/^#x/i.test(code)) {
        return String.fromCodePoint(parseInt(code.slice(2), 16))
      }

      if (code.startsWith("#")) {
        return String.fromCodePoint(Number(code.slice(1)))
      }

      return ENTITIES[code.toLowerCase()] ?? whole
    }
  )
}

/** HTML'i ağaca çevirir. Kök, çıktının en üstündeki öğeleri tutan adsız bir kaptır. */
export function parse(html: string): HtmlElement {
  const root: HtmlElement = {
    tag: "#root",
    attributes: {},
    children: [],
    parent: null,
  }
  let current = root
  let index = 0

  while (index < html.length) {
    const open = html.indexOf("<", index)

    if (open === -1) {
      current.children.push(decode(html.slice(index)))
      break
    }

    if (open > index) {
      current.children.push(decode(html.slice(index, open)))
    }

    if (html.startsWith("<!--", open)) {
      const end = html.indexOf("-->", open)
      index = end === -1 ? html.length : end + 3
      continue
    }

    TAG.lastIndex = open
    const tag = TAG.exec(html)

    if (!tag) {
      throw new Error(
        `Unexpected "<" at ${open}: ${html.slice(open, open + 40)}`
      )
    }

    const name = tag[2].toLowerCase()
    index = TAG.lastIndex

    if (tag[1] === "/") {
      if (current.tag !== name || !current.parent) {
        throw new Error(`Unexpected </${name}> inside <${current.tag}>`)
      }

      current = current.parent
      index = html.indexOf(">", index) + 1
      continue
    }

    const element: HtmlElement = {
      tag: name,
      attributes: {},
      children: [],
      parent: current,
    }
    let selfClosing: boolean

    for (;;) {
      TAG_END.lastIndex = index
      const end = TAG_END.exec(html)

      if (end) {
        selfClosing = end[1] === "/"
        index = TAG_END.lastIndex
        break
      }

      ATTRIBUTE.lastIndex = index
      const attribute = ATTRIBUTE.exec(html)

      if (!attribute) {
        throw new Error(`Unterminated <${name}> at ${open}`)
      }

      // HTML'de nitelik adları büyük-küçük harf ayırmaz; React bazılarını `autoComplete` gibi yazar.
      element.attributes[attribute[1].toLowerCase()] = decode(
        attribute[2] ?? attribute[3] ?? attribute[4] ?? ""
      )
      index = ATTRIBUTE.lastIndex
    }

    current.children.push(element)

    if (name === "script" || name === "style") {
      // İçeriği HTML değildir; kapanışına kadar olduğu gibi alınır.
      const close = html.indexOf(`</${name}`, index)
      const end = close === -1 ? html.length : close

      element.children.push(html.slice(index, end))
      index = close === -1 ? end : html.indexOf(">", close) + 1
    } else if (!selfClosing && !VOID.has(name)) {
      current = element
    }
  }

  if (current !== root) {
    throw new Error(`<${current.tag}> is never closed`)
  }

  return root
}

function isElement(node: HtmlNode): node is HtmlElement {
  return typeof node !== "string"
}

function descendants(root: HtmlElement): HtmlElement[] {
  return root.children
    .filter(isElement)
    .flatMap((child) => [child, ...descendants(child)])
}

/** `input[type=email]`, `#email`, `[role=alert]` gibi tek parçalı seçicileri anlar; birleştirici (boşluk, `>`) yoktur. */
function matcher(selector: string): (element: HtmlElement) => boolean {
  const shape =
    /^([a-z][a-z0-9-]*)?((?:#[\w-]+|\[[\w-]+(?:=[^\]]*)?\])*)$/i.exec(selector)

  if (!shape) {
    throw new Error(`Unsupported selector: ${selector}`)
  }

  const tag = shape[1]?.toLowerCase()
  const conditions = [
    ...shape[2].matchAll(/#([\w-]+)|\[([\w-]+)(?:=([^\]]*))?\]/g),
  ].map((part) =>
    part[1] === undefined
      ? {
          name: part[2].toLowerCase(),
          value: part[3]?.replace(/^(["'])(.*)\1$/, "$2"),
        }
      : { name: "id", value: part[1] }
  )

  return (element) =>
    (tag === undefined || element.tag === tag) &&
    conditions.every(({ name, value }) =>
      value === undefined
        ? name in element.attributes
        : element.attributes[name] === value
    )
}

export function all(root: HtmlElement, selector: string): HtmlElement[] {
  return descendants(root).filter(matcher(selector))
}

/** Seçiciye uyan tek öğeyi verir; hiç yoksa veya birden çoksa hata fırlatır. */
export function one(root: HtmlElement, selector: string): HtmlElement {
  const found = all(root, selector)

  if (found.length !== 1) {
    throw new Error(
      `Expected exactly one "${selector}" but found ${found.length}.`
    )
  }

  return found[0]
}

function collapse(text: string): string {
  return text.replace(/\s+/g, " ").trim()
}

function content(node: HtmlNode, visibleOnly: boolean): string {
  if (!isElement(node)) {
    return node
  }

  if (node.tag === "script" || node.tag === "style") {
    return ""
  }

  if (visibleOnly && isHidden(node, false)) {
    return ""
  }

  if (node.tag === "img") {
    return node.attributes.alt ?? ""
  }

  return node.children.map((child) => content(child, visibleOnly)).join("")
}

/** Öğenin altındaki bütün metin, boşlukları sadeleştirilmiş olarak. */
export function text(node: HtmlNode): string {
  return collapse(content(node, false))
}

function isHidden(element: HtmlElement, withAncestors = true): boolean {
  if (
    element.attributes["aria-hidden"] === "true" ||
    "hidden" in element.attributes ||
    (element.tag === "input" && element.attributes.type === "hidden")
  ) {
    return true
  }

  return withAncestors && element.parent !== null && isHidden(element.parent)
}

function closest(element: HtmlElement, tag: string): HtmlElement | null {
  for (let node = element.parent; node; node = node.parent) {
    if (node.tag === tag) {
      return node
    }
  }

  return null
}

function byId(root: HtmlElement, id: string): HtmlElement | undefined {
  return descendants(root).find((element) => element.attributes.id === id)
}

function ids(value: string | undefined): string[] {
  return value?.split(/\s+/).filter((id) => id.length > 0) ?? []
}

const LABELABLE = new Set(["input", "select", "textarea", "button"])
const FIELDS = new Set(["input", "select", "textarea"])

/**
 * Yardımcı teknolojinin öğe için okuyacağı ad; yalnızca testlerin ihtiyaç duyduğu kadarıyla: `aria-labelledby`,
 * `aria-label`, `<label>` ve (alan olmayan öğelerde) görünen metin. `placeholder` ad sayılmaz.
 */
export function accessibleName(
  root: HtmlElement,
  element: HtmlElement
): string {
  const labelledBy = ids(element.attributes["aria-labelledby"])

  if (labelledBy.length > 0) {
    return collapse(
      labelledBy
        .map((id) => byId(root, id))
        .map((target) => (target ? content(target, false) : ""))
        .join(" ")
    )
  }

  const label = collapse(element.attributes["aria-label"] ?? "")

  if (label) {
    return label
  }

  if (LABELABLE.has(element.tag)) {
    const id = element.attributes.id
    const wrapping = closest(element, "label")
    const labels = [
      ...(id
        ? all(root, "label").filter((item) => item.attributes.for === id)
        : []),
      ...(wrapping ? [wrapping] : []),
    ]
    const labelled = collapse(
      labels.map((item) => content(item, true)).join(" ")
    )

    if (labelled) {
      return labelled
    }
  }

  return FIELDS.has(element.tag) ? "" : collapse(content(element, true))
}

function describe(element: HtmlElement): string {
  const shown = ["id", "type", "name", "role", "href", "placeholder"]
    .filter((name) => name in element.attributes)
    .map((name) => ` ${name}="${element.attributes[name]}"`)

  return `<${element.tag}${shown.join("")}>`
}

/**
 * Çizilen HTML'deki temel erişilebilirlik sorunlarını sayar; boş liste sorun bulunmadığını söyler:
 * - her alanın (`input`, `select`, `textarea`) bir etiketi vardır; yalnızca `placeholder` yetmez,
 * - her düğmenin ve bağlantının bir adı vardır (metin veya `aria-label`),
 * - `id` değerleri benzersizdir ve `for`, `aria-labelledby`, `aria-describedby` var olan bir öğeyi gösterir,
 * - en çok bir `<main>` vardır.
 *
 * Görsel şeyleri (karşıtlık, odak halkası, dokunma alanı) ve davranışı (klavye, odak yönetimi) ölçemez.
 */
export function accessibilityProblems(root: HtmlElement): string[] {
  const problems: string[] = []
  const elements = descendants(root)
  const seen = new Set<string>()

  for (const element of elements) {
    const id = element.attributes.id

    if (id !== undefined) {
      if (seen.has(id)) {
        problems.push(`id "${id}" is used more than once`)
      }

      seen.add(id)
    }
  }

  const mains = elements.filter(
    (element) => element.tag === "main" && !isHidden(element)
  )

  if (mains.length > 1) {
    problems.push(`${mains.length} <main> landmarks; a page has one`)
  }

  for (const element of elements) {
    for (const name of ["for", "aria-labelledby", "aria-describedby"]) {
      for (const id of ids(element.attributes[name])) {
        if (!seen.has(id)) {
          problems.push(`${describe(element)} points to missing id "${id}"`)
        }
      }
    }

    if (isHidden(element)) {
      continue
    }

    const named =
      FIELDS.has(element.tag) ||
      element.tag === "button" ||
      element.attributes.role === "button" ||
      (element.tag === "a" && "href" in element.attributes)

    if (named && accessibleName(root, element) === "") {
      problems.push(
        FIELDS.has(element.tag)
          ? `${describe(element)} has no label`
          : `${describe(element)} has no accessible name`
      )
    }
  }

  return problems
}

const SPOKEN_ATTRIBUTES = ["aria-label", "placeholder", "title", "alt"]

/** Kullanıcının okuduğu veya ekran okuyucunun söylediği her metin: metin düğümleri ve adlandıran nitelikler. */
export function spoken(node: HtmlNode): string[] {
  if (!isElement(node)) {
    return [node]
  }

  if (node.tag === "script" || node.tag === "style") {
    return []
  }

  return [
    ...SPOKEN_ATTRIBUTES.flatMap((name) =>
      name in node.attributes ? [node.attributes[name]] : []
    ),
    ...node.children.flatMap(spoken),
  ]
}

/**
 * Kullanıcıya gösterilen metinlerde çevrilmeden kalmış gibi görünen parçaları bulur: i18next bulamadığı anahtarı
 * olduğu gibi yazar (`login.title`), doldurulmayan yer tutucu da öyle kalır (`{{email}}`). E-posta adresleri
 * anahtara benzediği için atlanır.
 */
export function untranslated(root: HtmlElement): string[] {
  return spoken(root).flatMap((value) => {
    const said = value.replace(/[^\s@]+@[^\s@]+/g, " ")

    return [
      ...(said.match(/[A-Za-z_][\w-]*(?:\.[A-Za-z_][\w-]*)+/g) ?? []),
      ...(said.match(/\{\{[^}]*\}\}/g) ?? []),
    ]
  })
}

/** Çeviri dosyasındaki bütün metinlerin anahtar yolları (`login.title`). */
export function keyPaths(resources: unknown, prefix = ""): string[] {
  if (typeof resources !== "object" || resources === null) {
    return [prefix]
  }

  return Object.entries(resources).flatMap(([key, value]) =>
    keyPaths(value, prefix ? `${prefix}.${key}` : key)
  )
}
