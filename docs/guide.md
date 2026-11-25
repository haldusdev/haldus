# Haldus guide

Haldus is a .NET 10 framework that generates repetitive backend code at compile time. One `Map*` call plus a
`partial class` produces the entity, the request/response DTOs, the Minimal API endpoints and the EF Core
configuration. It runs on ASP.NET Core, EF Core and PostgreSQL.

This guide is the dense reference: every `Map*` method, every chain method, the contracts for business logic and the
rules the generator enforces. It applies to Haldus 0.1 (preview; the API may change). It is written for people and for
coding agents; if you are an agent, read [Rules](#rules) and [Common mistakes](#common-mistakes) first.

## Setup

```xml
<PackageReference Include="Haldus.AspNetCore" Version="0.1.0-preview.1" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.4" />
```

The source generator ships inside `Haldus.AspNetCore`; there is no separate generator package. A class library that
contains `Map*` calls (a module) references the same package.

<!-- example: setup -->
```csharp
using System.ComponentModel.DataAnnotations;
using Haldus;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();
app.UseHaldus();
app.MapCrud<Product>();
app.Run();

// The context must be partial: Haldus adds a DbSet for every mapped entity to its other half.
public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

// You write your own fields; Haldus generates the other half (Id and everything the chain adds).
public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

This generates `ProductResponse`, `CreateProductRequest`, `UpdateProductRequest`, `PatchProductRequest` and
`GET /products`, `GET /products/{id}`, `POST /products`, `PUT /products/{id}`, `PATCH /products/{id}`,
`DELETE /products/{id}`.

Everything is in the `Haldus` namespace. To read the generated code, add
`<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` to the project and look under `obj/Generated`.

## Rules

1. **Every entity is a top-level `partial class`.** Never write `Id`, `Status`, `TenantId`, `CreatedAt` or any other
   field a declaration generates; write only your own fields. If you write no class at all, Haldus generates it in the
   namespace of the `Map*` call.
2. **One `Map*` call per entity.** An entity is declared exactly once (`HALDUS002`).
3. **The chain is read at compile time.** Traits and relationships must be chained directly onto the `Map*` call. A
   trait called later on a variable, inside an `if`, or in a loop is not seen (`HALDUS004`). Only `MapDetail`,
   `MapTransition` and `MapAction` are called on the stored builder.
4. **Arguments of declarations must be compile-time constants**: enum members, string literals,
   `TimeSpan.FromMinutes(15)` with constant arguments, lambdas of the form `x => x.Property` (`HALDUS010`).
5. **A declaration that contradicts the archetype or another declaration is a compile error** (`HALDUS007`), never
   silently ignored. Read the message: it names the two declarations and the reason.
6. **Do not hand-write what Haldus generates** (DTOs, endpoints, EF configuration for mapped entities). Change the
   chain instead and rebuild.
7. **Business logic goes into the contracts in [Logic](#logic)**, not into edited generated code.

## Conventions

| Topic | Convention |
|---|---|
| Routes | Kebab-case plural of the entity name: `StockMovement` → `/stock-movements`. |
| Key | `Id` for `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapJob` (server-generated; `Guid` v7 unless you declare `Id` as `Guid`, `int` or `long`). `Key` (string, client-supplied) for `MapStore` and `MapBalance`. `Code` (client-supplied) for `MapLookup`. |
| Request fields | Scalar properties with a public setter. DataAnnotations on the entity are copied to the request DTOs. |
| Response-only fields | Properties without a public setter (`{ get; private set; }`) and every generated field. |
| List | `GET /products?page=1&pageSize=20` (max page size 100) returns `{ "items": [], "page": 1, "pageSize": 20, "totalCount": 0 }`. |
| `PUT` vs `PATCH` | `PUT` replaces every writable field; `PATCH` changes only the fields present in the JSON (`null` clears). |
| Errors | Problem details (`application/problem+json`). Validation: 400 with `errors` keyed by camelCase field name. Not found or not visible: 404. Conflict (wrong state, duplicate, in use): 409. Rejected by business logic: 422. |
| Messages | Localized (`en` default, `tr`) by `Accept-Language`. Replace `IHaldusMessages` to override texts. |
| Tenant and user | Read from claims: `HaldusOptions.TenantClaimType` (`tenant_id`) and `UserClaimType` (`NameIdentifier`). Replace `ITenantProvider`, `ICurrentUser`, `ICurrentUserRoles` to resolve them differently. |
| Authorization | The builder returned by `Map*` is an `IEndpointConventionBuilder`: `.RequireAuthorization()`, `.AllowAnonymous()`, `.RequireRateLimiting(...)` apply to all endpoints of the resource. |
| Schema | Haldus adds its entities and its own tables (`Haldus*`) to the EF Core model of your context. |

## Archetypes

Each entity has exactly one archetype. It decides which operations exist.

| Call | Endpoints |
|---|---|
| `app.MapCrud<T>()` | list, get, create, `PUT`, `PATCH`, delete |
| `app.MapReadOnly<T>()` | list, get. Records are written by your own code, a derivation or a job. |
| `app.MapAppendOnly<T>()` | list, get, create. No update, no delete. |
| `app.MapStore<T>()` | list, get, `PUT /{key}` (create or replace), delete |
| `app.MapLookup<T>()` | unpaged list, get, `PUT /{code}`, delete. Generates `Code` and `Name`. |
| `app.MapSingleton<T>()` | one record: `GET`, `PUT`, `PATCH`. Never written yet → `GET` returns the defaults. With `.MultiTenant()` one record per tenant. |
| `owner.MapDetail<T>()` | rows owned by another record; see [Details](#details). |
| `app.MapJob<T>()` | long-running work: `POST` returns 202, `GET` shows status and progress, `POST /{id}/cancel`. See [Jobs](#jobs). |
| `app.MapFile<T>(maxBytes, contentTypes...)` | a `MapCrud` resource whose content is uploaded separately: `PUT /{id}/content`, `GET /{id}/content`. |
| `app.MapBalance<T>()` | a quantity changed only by commands; see [Balances](#balances). |
| `app.MapAssociation<TLeft, TRight>()` | many-to-many link; see [Relationships](#relationships). |
| `app.MapAction<TRequest, TResponse>()` / `app.MapAction<TRequest>()` | `POST`: a command not bound to an entity. |
| `app.MapQuery<TRequest, TResponse>()` | `GET`: a computation not bound to an entity; request properties bind from the query string. |

`.Without(CrudOperation.Delete)` and `.Only(CrudOperation.List, CrudOperation.Get)` remove standard operations
(`List`, `Get`, `Create`, `Update`, `Patch`, `Delete`).

## Relationships

<!-- fragment -->
```csharp
app.MapCrud<Category>().Hierarchical();
app.MapCrud<Product>().References<Category>(required: false);
app.MapCrud<Address>().Under<Customer>();                 // /customers/{customerId}/addresses
app.MapCrud<Plan>().Embeds<Money>();
app.MapAssociation<Product, Tag>();
```

| Declaration | Effect |
|---|---|
| `.References<T>(onDelete, required)` | Generates the foreign key (`CategoryId`). Every write verifies the target exists and is visible (field-level 400). Default `OnDelete.Restrict`: deleting a referenced record returns 409. |
| `.Under<T>()` | The resource lives under its parent's route; the parent comes from the route, not the body. Default `OnDelete.Cascade`. |
| `.Under<T>(shallow: true)` | Belongs to the parent but keeps its own route (`/addresses/{id}`); the parent is given on create, never changes, and the list filters with `?customerId=`. |
| `.Hierarchical()` | Optional `ParentId`. A node cannot be moved under itself; a node with children cannot be deleted. |
| `.Embeds<T>()` | Properties of that type are value objects: nested in the DTOs, stored in the owner's columns. Not nullable. |
| `app.MapSingleton<T>().Under<TParent>()` | One record per parent: `/customers/{customerId}/preferences`. |
| `app.MapAssociation<TLeft, TRight>()` | Generates the link entity (`ProductTag`). `PUT` / `DELETE /products/{id}/tags/{tagId}`, lists from both sides, `PUT /products/{id}/tags` with `{ "ids": [] }` replaces the set. Both ends must be mapped. |
| Link with payload | Write the link class yourself (`{Left}{Right}`, in the left end's namespace); its scalar properties become the payload sent in the `PUT` body. Links are listed under `…/links`. |
| Link with state | `app.MapAssociation<Organization, User>().Workflow<MembershipStatus>()` and bodiless `MapTransition("activate", from, to)` on its builder. |
| `app.MapAssociation<TLink, T>().ForCurrentUser()` | Links the current user to a record (favorite, star). The first type argument is the name of the link entity to generate. `PUT` / `DELETE` / `GET /me/favorites/{productId}`, `GET /me/favorites`. |
| `.TypedBy<TType>()` | Required, create-only reference to a type record. Empty nullable fields that exist on the type with the same name and type are filled from it on every write. |
| `.RoleOf<TPlayer>()` | Required, immutable reference; at most one role record per player (409). Also read by the player's id: `GET /staff-members/by-person-id/{personId}`. |
| `.SubtypeOf<TBase>()` | Table-per-hierarchy, one level. The class really derives from the base (`public partial class Car : Vehicle`). The base lists all kinds with a `kind` field (`?kind=car`). |
| `.AttachedTo<T>()` (repeatable) | The resource can hang on several targets; it is served under each (`/orders/{orderId}/comments`) and exactly one target is set per record. |
| `.Attachable<TFile>()`, `.Commentable<TComment>()` | The same declaration written on the target side. |
| `.Taggable<TTag>()` | Same as `MapAssociation<TEntity, TTag>()`. |

Visibility follows relationships automatically: a resource under a tenant-scoped parent is tenant-scoped in every
query that touches it.

## State

<!-- fragment -->
```csharp
var orders = app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)                                  // PUT / PATCH only in these states
    .DeletableIn(OrderStatus.Draft, OrderStatus.Cancelled)
    .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed);

orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped);            // POST /orders/{id}/ship
orders.MapTransition<PlaceOrder>(from: OrderStatus.Draft, to: OrderStatus.Placed);          // route from the type name
orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
```

- `Status` is generated, stored and returned by name, cannot be written from a request body and changes only through
  transitions. The list filters with `?status=`.
- A transition in the wrong state returns 409. The route of `MapTransition<TRequest>` is the request type name without
  the entity name and a `Request` / `Command` suffix (`CancelOrder` → `cancel`); pass `name:` to override.
- An entity has at most one `Workflow`. Further independent states are axes: `.Axis<PaymentState>()` generates a
  `PaymentState` field; a transition belongs to the axis whose enum type its states use. `EditableIn`, `DeletableIn`
  and `Numbered(assignOn:)` look only at the main state.
- `.Document()` is a fixed state machine (`DocumentStatus`: `Draft`, `Submitted`, `Cancelled`): only a draft is edited
  and deleted, `POST /{id}/submit` and `/cancel` exist automatically, `POST /{id}/amend` creates a new draft from a
  cancelled document. It replaces `Workflow`, `EditableIn` and `DeletableIn`.
- A state derived from other records is not an axis: use a rollup field and `Compute()` (see [Rollups](#rollups)).

## Logic

Haldus generates loading, state checks, validation and saving. You supply the logic through one of these contracts.
Handler classes are discovered and registered in DI automatically; a missing handler fails at startup.

| Where | Contract |
|---|---|
| Computed fields | Entity implements `IComputed { void Compute(); }`. Called before every save, after the request is applied and embedded rows are loaded. |
| Transition | Request implements `ITransition<TEntity> { void Apply(TEntity entity); }`, **or** a class implements `ITransitionHandler<TEntity, TRequest> { Task HandleAsync(TEntity entity, TRequest request, CancellationToken ct); }`, **or** a lambda is passed to `MapTransition`. |
| Entity action | `owner.MapAction("redeem", coupon => coupon.Redeem())` (`POST /coupons/{id}/redeem`), `owner.MapAction<TRequest>()` with `IEntityAction<TEntity>` or `IEntityActionHandler<TEntity, TRequest>`, `owner.MapAction<TRequest, TResponse>()` with `IEntityActionHandler<TEntity, TRequest, TResponse>`. An action changes the record without changing its state. |
| Free action | `app.MapAction<TRequest, TResponse>()` with `IActionHandler<TRequest, TResponse>` (or `IActionHandler<TRequest>`), or a lambda. |
| Query | `app.MapQuery<TRequest, TResponse>()` with `IQueryHandler<TRequest, TResponse>`, or a lambda. The request needs a public parameterless constructor and settable properties (`HALDUS008`). |
| Job | `IJobHandler<TJob> { Task HandleAsync(TJob job, JobContext context, CancellationToken ct); }`, or a lambda passed to `MapJob`. |
| List filter | `IFilter<TEntity> { IQueryable<TEntity> Apply(IQueryable<TEntity> query); }`; see [Reading](#reading). |

To reject a transition, an entity action, a free action or a query, throw `TransitionRejectedException(message)`: the
response is 422 with the message as `detail`, and for transitions and entity actions nothing is saved. To fail a job with a message for the client, throw `JobFailedException(message)`. Lambdas receive no injected
services; use the overload that takes `IServiceProvider`. Logic may add or remove embedded rows; Haldus assigns their
ids and order.

## Details

<!-- fragment -->
```csharp
var orders = app.MapCrud<Order>();

orders.MapDetail<OrderItem>();                  // embedded in the order's requests and responses
orders.MapDetail<OrderNote>().AsSubresource();  // own endpoints: /orders/{orderId}/order-notes
orders.MapDetail<OrderLine>().AsCommands();     // embedded in responses, written one by one
```

- Embedded rows are written with their owner. `PUT` syncs them: a row with an id is updated, without an id added,
  absent rows deleted. `PATCH` syncs only if the collection is sent. Rows keep the order they were sent in.
- The owner gets a collection property named after the plural of the detail (`OrderItems`); use it in `Compute()`.
- `.AsCommands()`: `POST /orders/{id}/order-lines`, `PUT` / `DELETE /orders/{id}/order-lines/{rowId}`; each is a write
  of the owner.
- `AsSubresource` and `AsCommands` must be chained directly onto the `MapDetail` call.
- An embedded detail's `Id` is a `Guid`; a detail cannot have its own detail.

## Reading

| Declaration | Effect |
|---|---|
| `.Filterable<TFilter>()` | Your `IFilter<T>` class is bound to the list endpoint; its scalar properties become optional camelCase query parameters. `query.WhereIf(condition, predicate)` helps. |
| `.Sortable(x => x.Name, x => x.CreatedAt)` | `?sort=-createdAt,name` (minus is descending). Only listed fields; anything else is 400. |
| `.Searchable(x => x.Name, x => x.Price)` | `POST /products/search` with a typed body: `{ "where": { "name": { "startsWith": "a" }, "price": { "gte": 50, "lt": 900 }, "or": [ … ], "not": { … } }, "sort": [ { "field": "price", "descending": true } ], "page": 1, "pageSize": 20 }`. String operators: `eq`, `ne`, `contains`, `startsWith`, `endsWith`, `in`, `isNull`, `ignoreCase`; ordered types add `gt`, `gte`, `lt`, `lte`. Only listed fields; nothing is searchable by default (`HALDUS006`). |
| `.NaturalKey(x => x.Code)` | The field is unique in the record's scope (409) and adds `GET /coupons/by-code/{code}`. Other endpoints keep using `Id`. |

There are no automatic per-field query parameters and no filter expression language; filtering is `Filterable` and
`Searchable` only.

## Traits

### Scope and ownership

| Declaration | Effect |
|---|---|
| `.MultiTenant()` | `TenantId`; every query is filtered by the tenant from the token. |
| `.OwnedByCurrentUser(underMe: true)` | `OwnerId`; only the creator sees the record (others get 404, no user gets 401). Served under `/me` (`/me/carts`); `underMe: false` keeps the plain route. Combines with `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapJob`. |

### Fields and keys

| Declaration | Effect |
|---|---|
| `.Auditable(trackUser: false)` | `CreatedAt`, `UpdatedAt`; with `trackUser` also the creating and updating user. |
| `.CreateOnly(x => x.Code)` | Listed fields are in the `POST` request only. |
| `.KeyStrategy(KeyStrategy.Prefixed, "ord")` | Key format: `Sequential` (default, Guid v7), `Random` (Guid v4), `Prefixed` (`ord_…`), `Ulid`, `Client` (`POST /enrollments?id={guid}`; a second create with the same id is 409). |
| `.Numbered("INV-{yyyy}-{seq:6}", assignOn, gapless: true)` | Unique `Number` field. Gapless by default (a counter row locked in the same transaction, restarting per tenant and per date part in the pattern); `gapless: false` uses a database sequence. |
| `.Metadata()` | String key-value bag (max 50 keys). `PATCH` merges (`null` removes a key), `PUT` replaces. |
| `.CustomFields()` | A `customFields` bag whose fields the tenant defines: `GET /custom-fields`, `PUT` / `DELETE /custom-fields/{key}`. Values are validated against the definitions on every write. |
| `.Translatable(x => x.Title, x => x.Body)` | `PUT` / `DELETE /{id}/translations/{locale}`; reads translate only with `?locale=tr`. The response always carries all translations under `translations`. |
| `.Sensitive(x => x.NationalId)` | Write-only: in requests, never in any response, export, event or stored revision. |
| `.Encrypted(x => x.Iban)` | String fields stored encrypted (ASP.NET Data Protection) and write-only like `Sensitive`. |
| `.Counts<TChild>(into:)`, `.Sums<TChild>(c => c.Amount, into:)` | See [Rollups](#rollups). |

### Lifecycle

| Declaration | Effect |
|---|---|
| `.SoftDeletable()` | Delete is reversible; `restore` and `purge` endpoints. |
| `.Archivable()` | `POST /{id}/archive`, `/unarchive`; archived records are hidden from the list. |
| `.ConcurrencyChecked()` | The version travels in the `ETag` header. Every changing request needs `If-Match` (missing: 428, mismatch: 412). |
| `.Expiring()` / `.Expiring(TimeSpan lifetime, sliding: false)` | `ExpiresAt`: set by the client, or computed on create, or restarted on every write (`sliding`). An expired record is hidden and cannot be written; `POST /{id}/expire` ends it now. |
| `.Retention(TimeSpan period)` | Records older than the period are physically deleted by maintenance. |
| `.Idempotent(required: false)` | Create and transitions honour the `Idempotency-Key` header. |
| `.EffectiveDated(x => x.Currency)` | `EffectiveFrom` (required) / `EffectiveTo`; periods cannot overlap within the scope (409); `?asOf=2026-06-01` returns what is valid that day. |
| `.Recurring()` | Recurrence rule fields (`startsAt` (required), `recurrenceFrequency`, `recurrenceInterval`, `recurrenceUntil`, `recurrenceCount`, `recurrenceByDay`, `recurrenceTimeZone`); `GET /{id}/occurrences?from=&to=`, `PUT` / `DELETE /{id}/occurrences/{start}` for single-occurrence exceptions. Not full iCalendar `RRULE`. |

### Operations on a record

| Declaration | Effect |
|---|---|
| `.Batch(atomic: true, maxItems)` | `POST /batch-get`, `/batch-create`, `/batch-update`, `/batch-delete`. Atomic by default; `atomic: false` returns 207 with a result per item. |
| `.Cloneable()` | `POST /{id}/clone`; an optional patch-shaped body overrides fields of the copy. |
| `.Orderable()` | Integer `Position` (gapless, from 0). `POST /{id}/move` with `{ "position": 2 }` renumbers the siblings. |
| `.Previewable()` | `POST /preview` and `POST /{id}/preview`: validates and computes, saves nothing. |
| `.Purgeable()` | `POST /purge?…` with the list filter as criterion: counts by default, deletes with `force=true` (needs `Filterable` and `Batch`). |
| `.ChangeSets(states...)` | An editing session over a record that is not directly editable: `POST /{id}/edits`, `PATCH /{id}/edits/{editId}`, `GET …/preview`, `POST …/commit`, `DELETE`. |

### Records created from records

| Declaration | Effect |
|---|---|
| `.Reversible(states...)` | `POST /{id}/reverse` creates the neutralizing counter-record (`ReversesId`, `ReversedById`). Entity may implement `IReversal<T> { void Reverse(T original); }`. |
| `.Supersedes(states...)` | `POST /{id}/supersede` creates the replacement; the old record stays, read-only. `ISupersession<T> { void Supersede(T previous); }`. |
| `.CreatedFrom<TSource>(on: null)` | `POST /orders/from-cart/{cartId}`; same-named fields are copied, `ICreatedFrom<TSource> { void CreateFrom(TSource source); }` fills the rest. With `on: "capture"` the record is born by that transition of the source instead. |
| `.DerivedFrom<TSource>()` | A `MapReadOnly` projection of a `MapAppendOnly` source: your `IDerivation<TSource, TTarget>` class runs in the same transaction as every source record. |

If the resource has a state machine, `Reversible`, `Supersedes` and `ChangeSets` must list the states in which they
are allowed.

### Approval

| Declaration | Effect |
|---|---|
| `.Approvable(levels: 1, requiredFor...)` | `POST /{id}/approve`, `/reject` (optional `{ "note" }`), `GET /{id}/approvals`. Nobody who wrote the content may decide (403); the same person cannot approve two levels; any content change drops collected approvals; an approved record cannot be updated. Listed transitions and actions are blocked (409) until approved. |
| `.ApprovalRoles("manager", "finance")` | Level n may be decided only by users in role n (asked through `ICurrentUserRoles`). Entity may implement `IApprovalLevels { int RequiredApprovalLevels { get; } }`. |

### Versions and publishing

| Declaration | Effect |
|---|---|
| `.Revisioned()` | A snapshot after every write: `GET /{id}/revisions`, `GET /{id}/revisions/{revisionId}`, `POST …/rollback`. |
| `.Publishable()` | Ordinary endpoints write the draft; `POST /{id}/publish`, `/unpublish`; the published copy is read at `GET /published` and `/published/{id}`. |
| `.ScheduledPublishing()` | With `Publishable`: `POST /{id}/schedule` with `{ "publishAt" }`, `DELETE /{id}/schedule`. |
| `.ChangeFeed()` | `GET /changes?since={cursor}` returns records changed since the cursor and the ids of deleted ones. |
| `.ReadTracking()` | For user-owned records: `ReadAt`, `POST /{id}/read`, `/unread`, `POST /read-all`, `GET /unread-count`, `?unread=true`. |
| `.ReadReceipts()` | Per-user read state of shared records (stored apart from the record); same routes plus `GET /{id}/read-state`. |

### Secrets and tokens

| Declaration | Effect |
|---|---|
| `.SecretOnce(prefix: "sk", recoverable: false)` | A 256-bit secret generated on the server, shown only in the create response; stored as a SHA-256 hash, last four characters in `SecretHint`. `recoverable: true` also stores it encrypted. Look one up with `FindBySecretAsync`. |
| `.Rotatable(overlap: null)` | `POST /{id}/rotate`; the old secret dies immediately or after the overlap. |
| `.TokenAccess(prefix: "inv", "accept")` | Anonymous access by a token in the `X-Access-Token` header: `GET /by-token`, `POST /by-token/accept` for the listed transitions or actions. |
| `.TokenOnly()` | With `TokenAccess`: no id-based endpoints at all, only create and `/by-token`. |

### Files

| Declaration | Effect |
|---|---|
| `app.MapFile<T>(maxBytes, "image/*", "application/pdf")` | `POST` creates the record, `PUT /{id}/content` uploads (raw body or one multipart file part; 413, 415), `GET /{id}/content` downloads as attachment. |
| `.SignedLinks()` | `POST /{id}/content/link?expiresInSeconds=300` returns a temporary anonymous download URL. |

Content is stored in the database by default; `AddHaldusFileSystemStorage(path)` or `AddHaldusS3Storage(...)`
(`Haldus.Storage.S3`) move it elsewhere; implement `IHaldusFileStorage` for another store. Deleting a file record removes
its content however the record is deleted, including a database cascade from the record it is attached to or lives
under; outside the database the content is removed once the transaction commits. `ExecuteDelete` bypasses this.

### Events

`.PublishesEvents()` is a transactional outbox: `{resource}.created`, `.updated`, `.deleted` and
`{resource}.{transition}` are written in the same save as the record and delivered afterwards, at least once and in
order, to every registered `IHaldusEventPublisher`. The payload is the record in its response shape. Ready publishers:
`AddHaldusWebhookPublisher` (in the core), `AddHaldusSqsPublisher`, `AddHaldusRabbitMqPublisher`,
`AddHaldusKafkaPublisher` (separate packages).

## Rollups

<!-- fragment -->
```csharp
app.MapCrud<Bill>().Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount");
app.MapAppendOnly<BillPayment>().References<Bill>();

public partial class Bill : IComputed
{
    public decimal Amount { get; set; }
    public BillState State { get; private set; }
    public void Compute() => State = PaidAmount >= Amount ? BillState.Paid : BillState.Unpaid;   // PaidAmount is generated
}
```

`Counts<TChild>()` generates `{Child}Count`, `Sums<TChild>(field)` generates `{Child}{Field}Sum` unless `into:` names
the field. The field is recounted from scratch in the child's transaction whenever a child is saved, then the
parent's `Compute()` runs. Tenant-scoped children cannot be rolled up into a global record; user-owned children need
`acrossOwners: true`.

## Balances

<!-- example: balance -->
```csharp
app.MapBalance<Inventory>().MultiTenant().Buckets("available", "reserved").Holds(from: "available", to: "reserved").Idempotent();
public partial class Inventory;   // Key and one decimal field per bucket are generated
```

`POST /inventories/{key}/adjust` (`{ "delta", "bucket", "reason" }`), `/set` (`{ "quantity", "expected" }`), `/move`
(`{ "from", "to", "quantity" }`), `GET /{key}/entries` (the ledger), and with `Holds`: `POST /{key}/holds`,
`…/holds/{holdId}/capture`, `…/release`. A balance never goes below zero (409) unless `.AllowNegative()`. Never write
a balance with `PUT` or from your own code: the ledger would miss it.

## Jobs

<!-- example: jobs -->
```csharp
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();   // or AddHaldusInMemoryJobs(), AddHaldusHangfireJobs()
app.MapJob<ReportExport>().MultiTenant();

public partial class ReportExport
{
    public int Year { get; set; }                       // input: from the request
    public string? DownloadUrl { get; private set; }    // output: response only
}
```

Haldus adds `Status` (`Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled`), `Progress` and `Error`. The job runs
with the identity and language of the request that created it. The handler must tolerate being run again.
`.Rerunnable()` adds `POST /{id}/rerun`. `.Exports<TResource>()` and `.Imports<TResource>()` generate the handler:
export writes CSV or JSON downloadable from `GET /{id}/content`; import creates records from the request's `content`
atomically and needs `.Batch()` on the target.

## Modules

Features can live in separate class libraries, each with its own `Map*` calls and entities. Modules referenced by the
host are found automatically.

<!-- fragment -->
```csharp
builder.Services.AddHaldus<AppDbContext>();            // shared context for modules without their own
builder.Services.AddHaldusModule<CatalogDbContext>();  // a module with its own context and schema
```

`References<T>()` may point into another module (a real foreign key in the same context, id-only across contexts).
`Under<T>()` across modules needs the same context. Both ends of `MapAssociation` must be in the same module.

## Frontend

Haldus generates no frontend code. The contract is the OpenAPI document of the application
(`Microsoft.AspNetCore.OpenApi`); generate typed clients from it (the samples use orval with TanStack Query).

## Diagnostics

All diagnostics are errors.

### HALDUS001

The entity is not a top-level `partial class`. Make it `public partial class X` outside any other type.

### HALDUS002

The entity is mapped by more than one `Map*` call. Keep one.

### HALDUS003

Unsupported key type for the archetype. `Id` (`MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapFile`) may be `Guid`, `int`
or `long`; a job's `Id` must be `Guid`; `Key` of `MapStore` may also be `string`; `Code` of `MapLookup` and `Key` of
`MapBalance` must be `string`. Or remove the property and let Haldus generate it.

### HALDUS004

A declaration was called on a variable instead of being chained onto the `Map*` (or `MapDetail`) call. Move it into
the chain.

### HALDUS005

A `Searchable` field is not of the form `item => item.Property` or is not a scalar property of the entity.

### HALDUS006

`Searchable()` was called without fields. List the searchable fields.

### HALDUS007

Two declarations cannot be combined, or a declaration contradicts the archetype. The message names both and the
reason; remove one or use the alternative it suggests.

### HALDUS008

A `MapQuery` request type needs a public parameterless constructor and settable properties.

### HALDUS009

A declaration points to a type that is not mapped: a parent (`Under`), a base type (`SubtypeOf`), a derivation or rollup
source, or a detail owner. Add a `Map*` call for the target in the same project. `References<T>()` may point to a
compiled entity of another module and reports this only when the type is unknown altogether.

### HALDUS010

A declaration argument cannot be read at compile time or is invalid (a non-constant value, an unknown field, a state
that is not a member of the status enum). Write it as a constant.

## Common mistakes

- Writing `public Guid Id { get; set; }`, `Status`, `TenantId`, `CreatedAt`, `CategoryId` or a child collection on
  the entity. They are generated; a duplicate fails to compile or changes the key type.
- Adding traits conditionally (`if (options.MultiTenant) products.MultiTenant();`). The chain must be static.
- Writing a controller, DTO or `IEntityTypeConfiguration` for a mapped entity.
- Setting `Status` in a request body or in `PUT`. Use a transition.
- Putting a status check in the handler. Declare `from:` on the transition and `EditableIn` on the resource.
- Calling `SaveChangesAsync` inside a transition, action or `Compute()`. Haldus saves after the logic returns.
- Using `IgnoreQueryFilters()` without arguments in application code. It also removes tenant and ownership filters;
  pass the filter name (`IgnoreQueryFilters([HaldusFilters.SoftDelete])`).
- Expecting `?name=foo` to filter a list. Write an `IFilter<T>` and declare `.Filterable<TFilter>()`.
- Forgetting `If-Match` on a `ConcurrencyChecked` resource (428), including for transitions and actions.
- Referencing only `Haldus.Abstractions` or `Haldus.EntityFrameworkCore` in a project with `Map*` calls. Reference
  `Haldus.AspNetCore`; it carries the generator.

## Snippet for a project that uses Haldus

Paste this into the `AGENTS.md` / `CLAUDE.md` of an application built on Haldus:

```markdown
## Haldus

This backend is built on Haldus, which generates entities, DTOs, Minimal API endpoints and EF Core configuration at
compile time from `Map*` calls. Read the guide before changing any resource:
https://raw.githubusercontent.com/haldusdev/haldus/main/docs/guide.md (the same file ships in the NuGet package:
`~/.nuget/packages/haldus.aspnetcore/<version>/docs/guide.md`).

- To add or change an endpoint, change the `Map*` chain or the entity's `partial class`; never hand-write DTOs,
  endpoints or EF configuration for a mapped entity, and never edit generated code.
- Entities are top-level partial classes with only their own fields; `Id`, `Status`, foreign keys and trait fields are
  generated.
- Traits are chained directly onto the `Map*` call (the chain is read at compile time).
- Business logic lives in `ITransition<T>`, `ITransitionHandler<,>`, `IEntityAction<T>`, `IComputed`, `IFilter<T>`
  and the job / action / query handlers.
- Generated code is under `obj/Generated`. `HALDUS0xx` build errors say what to change.
```
