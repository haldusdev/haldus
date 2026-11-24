# Frameworks, CRUD generators and headless CMSs: resource kinds and composable behaviors (state as of November 2026)

Reading conventions used in these notes:

- "Cited Findings" contain only facts taken from a page fetched (or a search result returned) in this research session. Prose is paraphrased, not quoted. API identifiers are reproduced as-is.
- A finding tagged **[search-level]** was confirmed only through a search-result summary, not by reading the page itself. Treat it as weaker evidence.
- Anything I know from prior knowledge but did not re-verify in this session is listed under "Gaps" and tagged **[unverified]**. In the two final tables such ecosystems carry the suffix `(u)`.
- "Our catalog" = archetypes Crud, ReadOnly, Singleton, Store, AppendOnly, Lookup, Association, Action, Job, Workflow, Detail; traits Auditable, SoftDeletable, MultiTenant, Versioned, Under<Parent>, Filterable, Batch, Import/Export, Revisioned, Expiring, Idempotent.

Versions observed in this session: ABP docs "latest" = 10.7 (URLs for 11.0 docs also exist); EF Core 10 (LTS, released Nov 2025); ASP.NET Core OData docs still titled "OData 8" with a 9.4.0 minimal-API announcement; Rails guides 8.1.4.1; API Platform docs 5.0 "current"; Strapi 5; Laravel 13.x current (12.x page carries an "old version" banner); PostgREST docs v14 exist; Hasura docs consulted are v2.x; Directus docs moved from directus.io to directus.com (301).

---

## Objective: per-tool inventory (kinds, built-in behaviors, relationships/nesting, custom operations and hooks, limits)

### Takeaway
Every tool splits the problem the same way we do (a small set of resource shapes plus opt-in behaviors), but they declare behaviors through four different API shapes: marker interfaces/base classes (ABP), mixins/traits on the model (Rails, Laravel, Doctrine, DRF), declarative schema flags (Strapi, Directus, JHipster JDL, Contentful) and database-level objects (PostgREST, Hasura, Supabase). The richest sources of things we lack are the CMSs (single types, components, dynamic zones, draft/publish, i18n, sort/archive fields) and the Doctrine/Laravel/Rails trait ecosystems (sluggable, sortable, tree, translatable, uploadable, prunable, UUID keys).

### Cited Findings

#### ABP Framework (.NET) — docs 10.7

(a) Kinds
- Entity base classes: `Entity<TKey>`, non-generic `Entity` (composite keys via overriding `GetKeys()`), `AggregateRoot<TKey>` (adds extra properties + concurrency stamp), `BasicAggregateRoot<TKey>` (aggregate root without those two) — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- Application-service shapes: `CrudAppService` (several generic arities: key type plus separate DTOs for get, list, create, update), `AbstractKeyCrudAppService` (no single-`Id` assumption), `ICrudAppService`; generated methods are `GetAsync`, `GetListAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`; default list input is `PagedAndSortedResultRequestDto` — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services)
- ABP Suite distinguishes entity type *Master* (default) and *Child*; a master can have several children — [ABP Suite master-detail](https://abp.io/docs/latest/suite/creating-master-detail-relationship)
- Feature system: named features (boolean or any string-stored value; value types toggle / free text / selection; child features selectable only when parent is on) defined in a `FeatureDefinitionProvider`; value resolved by an ordered provider chain Tenant → Edition → Configuration → Default; checked with `[RequiresFeature]` or `IFeatureChecker` — [ABP Features](https://abp.io/docs/latest/framework/infrastructure/features)
- Background jobs: built-in persistent job manager with retry after crash, plus Hangfire, RabbitMQ and Quartz integrations; BLOB storing abstraction with Azure, AWS, file-system and database providers; distributed event bus (`IDistributedEventBus`, `IDistributedEventHandler`) with RabbitMQ/Kafka/Rebus/Azure and inbox/outbox configured on `AbpDistributedEventBusOptions` **[search-level]** — [ABP Background Jobs](https://abp.io/docs/latest/framework/infrastructure/background-jobs)

(b) Built-in behaviors (all are marker interfaces or base classes)
- Audit ladder: `IHasCreationTime` → `IMayHaveCreator` → `ICreationAuditedObject`; `IHasModificationTime` → `IModificationAuditedObject`; `IAuditedObject`; `ISoftDelete` → `IHasDeletionTime` → `IDeletionAuditedObject`; `IFullAuditedObject`. Matching base classes `CreationAudited*`, `Audited*`, `FullAudited*` exist for both Entity and AggregateRoot, each with a `...WithUser<TUser>` variant that adds navigation to the user — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- `IHasConcurrencyStamp` (optimistic concurrency), `IHasEntityVersion` (integer auto-incremented by ABP on every update, starts at 0), `IHasExtraProperties` (dynamic `ExtraProperties` dictionary for extensibility), `IKeyedObject` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- Key generation: the docs warn against `Guid.NewGuid()` and point to `IGuidGenerator` (sequential GUIDs) — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- `EntityCache`: a read-only distributed cache of an entity that is invalidated on update/delete (a caching behavior, not a base class) — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- `IMultiTenant` only adds a nullable `TenantId`; null means host-owned. TenantId is set automatically from `ICurrentTenant` in the entity constructor; changing it later means moving the row to another tenant. `ICurrentTenant.Change(...)` scopes a tenant switch — [ABP Multi-Tenancy](https://abp.io/docs/latest/framework/architecture/multi-tenancy)
- Data filters: built-ins are `ISoftDelete` and `IMultiTenant`; `IDataFilter.Disable<T>()` / `Enable<T>()` in a `using` scope; custom filter = marker interface + `ShouldFilterEntity` + `CreateFilterExpression` in the DbContext (or `HasAbpQueryFilter`); default on/off state via `AbpDataFilterOptions.DefaultStates`; repository `HardDeleteAsync` bypasses soft delete — [ABP Data Filtering](https://abp.io/docs/latest/framework/infrastructure/data-filtering)
- Audit logging: one audit-log object per request (request/response data, executed actions with parameters, entity changes, exception, duration). Entity history is opt-in via `EntityHistorySelectors` or `[Audited]`; `[DisableAuditing]` on a class or a single property (e.g. password); each property change stores original and new value — [ABP Audit Logging](https://abp.io/docs/latest/framework/infrastructure/audit-logging)

(c) Relationships and nesting
- The entities doc advises against navigation properties between aggregate roots — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- Suite child entity: orchestration is delegated to the master; application services are still generated for the child; the child inherits multi-tenancy from the master; no separate UI or tests are generated; child rows are edited in grids/tabs inside the master page; many-to-many is disabled for children (one-to-many only) — [ABP Suite master-detail](https://abp.io/docs/latest/suite/creating-master-detail-relationship)

(d) Custom operations and hooks
- Per-operation authorization through `GetPolicyName`, `GetListPolicyName`, `CreatePolicyName`, `UpdatePolicyName`, `DeletePolicyName` or by overriding `CheckXxxPolicyAsync`; query hook `CreateFilteredQuery` plus `ApplySorting`/`ApplyPaging`/`ApplyDefaultSorting`; mapping hooks `MapToGetOutputDtoAsync`, `MapToGetListOutputDtoAsync`, `MapToEntityAsync` (two overloads: create and update); key hooks `GetEntityByIdAsync`, `DeleteByIdAsync`; any method can be overridden and custom methods added next to the CRUD ones — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services)

(e) Limits
- `CrudAppService` requires a single `Id`; composite keys need `AbstractKeyCrudAppService` with hand-written `GetEntityByIdAsync`/`DeleteByIdAsync` — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services)
- With the user-defined-function filter mapping, EF compiled queries that need soft-delete/tenant/custom filters throw; MongoDB filtering only works through the repositories; the doc warns to change global filter defaults carefully because pre-built modules assume soft-delete is on — [ABP Data Filtering](https://abp.io/docs/latest/framework/infrastructure/data-filtering)
- Disabling the tenant filter does not work across database-per-tenant setups; per-tenant connection-string UI is commercial-only — [ABP Multi-Tenancy](https://abp.io/docs/latest/framework/architecture/multi-tenancy)
- Entity-change logging is not supported on the MongoDB provider — [ABP Audit Logging](https://abp.io/docs/latest/framework/infrastructure/audit-logging)
- Support threads report: children of an aggregate all flagged deleted when the parent is updated through `CrudAppService` in a multi-tenant scenario **[search-level]** — [ABP support #1037](https://abp.io/support/questions/1037/CrudAppService-Child-Entity-in-MultiTenant-Scenario-missing-TenantId); ABP staff saying automatic child-collection update mapping is not planned **[search-level]** — [ABP support #1110](https://abp.io/QA/Questions/1110); since 8.3 updating a child through its parent no longer updates the parent's audit columns **[search-level]** — [ABP support #9642](https://abp.io/support/questions/9642/Audit-columns-for-parent-table-is-not-updating-when-child-table-is-updated-using-parent-and-child-data-in-same-object)

#### EF Core 10 features that behave like traits

- Named query filters: several filters per entity, each with a name (the docs' example pairs a soft-deletion filter with a tenant filter) and selectively ignorable via `IgnoreQueryFilters([names])`. Before EF 10 only one filter per entity type was possible and filters had to be AND-ed into one expression — [EF Core 10 What's New](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew), [EF Core Global Query Filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)
- The filters doc names soft deletion and multi-tenancy as the two mainstream uses, and shows soft delete implemented by rewriting Deleted entries to Modified in `SaveChangesAsync` — [EF Core Global Query Filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)
- Documented filter limits: filters only on the root type of an inheritance hierarchy; no cycle detection; a filtered entity reached through a *required* navigation silently removes parent rows (inner join) — [EF Core Global Query Filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)
- Complex types (`ComplexProperty`): identity-less value types mapped either to columns of the owner table or (new in EF 10) to one JSON column via `ToJson()`; optional complex types and structs supported; JSON mapping allows collections; `ExecuteUpdateAsync` works inside JSON only for complex types — [EF Core 10 What's New](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- Owned entity types (`OwnsOne`, `OwnsMany`, `[Owned]`): described as part of the owner and similar to aggregates; always loaded with the owner; collection default key is (owner FK, Id); can be nested and can live in a separate table. Limits: no `DbSet<T>`, no `Entity<T>()`, an instance cannot be shared by several owners, no inheritance hierarchies. Microsoft now advises complex types instead of owned types for value objects, JSON and table splitting — [EF Core Owned Entity Types](https://learn.microsoft.com/en-us/ef/core/modeling/owned-entities), [EF Core 10 What's New](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- Primitive collections (e.g. `string[] Tags`) stored as a JSON column; SQL Server 2025 native `json` and `vector` types with `EF.Functions.VectorDistance`; Cosmos full-text and hybrid search — [EF Core 10 What's New](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)

#### OData for ASP.NET Core, FastEndpoints

- OData EDM element kinds: entity types, complex types, enums, entity sets, singletons, functions, actions; type inheritance supported; entity sets, singletons, functions and actions are the things exposed as endpoints — [ASP.NET Core OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview)
- Declaration shape: `modelBuilder.EntitySet<Customer>("Customers")` on an `ODataConventionModelBuilder`; query options (`$filter`, `$select`, `$expand`, `$orderby`, `$top`, `$skip`, `$apply`) are off by default and enabled globally plus per action with `[EnableQuery]`; `$batch` is an opt-in endpoint that executes several operations in one request and orders dependent ones — [ASP.NET Core OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview)
- The overview page states routing is controller-based with no minimal-API support — [ASP.NET Core OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview); contradicted for newer versions by a Microsoft OData blog post (Oct 2025) saying OData features can be enabled on minimal APIs since 9.4.0 **[search-level]** — [OData devblog](https://devblogs.microsoft.com/odata/enable-odata-functionalities-on-asp-net-core-minimal-api/)
- FastEndpoints offers pre-processors and post-processors attached per endpoint, with global variants `IGlobalPreProcessor` / `IGlobalPostProcessor`; post-processors can observe unhandled exceptions **[search-level]** — [FastEndpoints pre/post processors](https://fast-endpoints.com/docs/pre-post-processors)

#### Django REST Framework and Django packages

(a) Kinds
- ViewSet bases: `ViewSet` (no actions), `GenericViewSet` (no actions, has `get_object`/`get_queryset`), `ModelViewSet` (list, retrieve, create, update, partial_update, destroy), `ReadOnlyModelViewSet` (list, retrieve) — [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/)
- Nine concrete generic views, one per useful combination: `CreateAPIView`, `ListAPIView`, `RetrieveAPIView`, `DestroyAPIView`, `UpdateAPIView`, `ListCreateAPIView`, `RetrieveUpdateAPIView`, `RetrieveDestroyAPIView`, `RetrieveUpdateDestroyAPIView` — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/)

(b) Behaviors
- Five operation mixins: `ListModelMixin`, `CreateModelMixin`, `RetrieveModelMixin`, `UpdateModelMixin` (update + partial_update), `DestroyModelMixin`; a custom archetype is built by inheriting `GenericViewSet` plus the mixins wanted (docs example: a create+list+retrieve viewset) — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/), [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/)
- Tree packages: the django-mptt README declares the project unmaintained (best-effort only), recommends django-tree-queries (recursive CTEs over a plain parent FK), and explains that precomputed left/right/level values cause write amplification, corruption under concurrent writes and inconsistency after bulk operations — [django-mptt README](https://github.com/django-mptt/django-mptt)
- State machines: the original django-fsm repository was archived in April 2024; successors are viewflow.fsm (different API), the drop-in fork django-fsm-2 (django-commons) and django-fsm-rx **[search-level]** — [django-fsm-2 on PyPI](https://pypi.org/project/django-fsm-2), [viewflow/django-fsm](https://www.github.com/viewflow/django-fsm)

(c) Relationships and nesting
- Relation field kinds: `StringRelatedField` (read-only), `PrimaryKeyRelatedField`, `HyperlinkedRelatedField`, `SlugRelatedField`, `HyperlinkedIdentityField` (read-only). Nested serializers are read-only by default; writable nesting requires hand-written `create()`/`update()`. `GenericForeignKey` targets need a custom field. Reverse relations are not included automatically. Listed third-party helpers: drf-nested-routers, rest-framework-generic-relations — [DRF Serializer relations](https://www.django-rest-framework.org/api-guide/relations/)
- drf-writable-nested supplies `WritableNestedModelSerializer`; known frictions are unique validators failing on nested update and multipart form data not being parsed for nested fields **[search-level]** — [drf-writable-nested](https://github.com/beda-software/drf-writable-nested)

(d) Custom operations and hooks
- `@action(detail=True|False, methods=[...], permission_classes=..., serializer_class=..., url_path=..., url_name=...)`: `detail=True` yields a per-object route (pk in URL), `detail=False` a collection route; extra HTTP methods on the same URL are mapped with `@<action>.mapping.delete`; the current action name is available as `self.action` for per-action branching — [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/)
- Override hooks: `get_queryset`, `get_object`, `get_serializer_class`, `filter_queryset`, `perform_create`, `perform_update`, `perform_destroy`. The perform hooks are documented as the place to set attributes implied by the request but absent from the payload, to run logic around save, or to raise validation errors — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/)

(e) Limits — see the pain-points question below.

#### Ruby on Rails — guides 8.1.4.1

(a) Kinds
- `resources :photos` generates seven routes; singular `resource :geocoder` generates six (no index, no id in the URL) — [Rails Routing](https://guides.rubyonrails.org/routing.html)
- `only:` / `except:` restrict the generated routes (e.g. a read-only resource is `only: [:index, :show]`) — [Rails Routing](https://guides.rubyonrails.org/routing.html)
- Model-shape kinds: single-table inheritance (one table, `type` column), delegated types (`delegated_type :entryable, types: [...]` on a superclass record that owns shared columns, with per-type tables), join models (`has_many :through`) versus payload-less join tables (`has_and_belongs_to_many`) — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html)

(b) Behaviors declared as association options
- `counter_cache` (denormalized child count on the parent, kept current by Rails, with an inactive mode for backfills), `touch` (bump parent timestamp when the child is saved/destroyed), `dependent` (`:destroy`, `:delete_all`, `:destroy_async`, nullify, `:restrict_with_exception`, `:restrict_with_error`), `inverse_of`, `strict_loading`, `optional` — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html)
- Routing concerns: `concern :commentable do resources :comments end`, then `resources :messages, concerns: :commentable` — a reusable bundle of nested routes — [Rails Routing](https://guides.rubyonrails.org/routing.html)

(c) Relationships and nesting
- Nested resources put the parent id in the URL; rule of thumb in the guide is one level of nesting only. Shallow nesting (`shallow: true` or a `shallow do` block) keeps index/new/create under the parent and moves show/edit/update/destroy to the top level — [Rails Routing](https://guides.rubyonrails.org/routing.html)
- Polymorphic association: `belongs_to :imageable, polymorphic: true` with `has_many :pictures, as: :imageable`; self joins for hierarchies (manager/subordinates); composite primary keys on join tables — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html)

(d) Custom operations and hooks
- Member routes (`on: :member`, act on one record) versus collection routes (`on: :collection`), plus `on: :new` — [Rails Routing](https://guides.rubyonrails.org/routing.html)
- Association callbacks `before_add`, `after_add`, `before_remove`, `after_remove` (a `before_add` can veto adding a child) and association extensions (custom finder methods on the collection proxy) — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html)

#### API Platform (PHP) — docs 5.0

(a) Kinds / operations
- Default operations: collection `GET` and `POST`, item `GET`, `PATCH`, `DELETE`; `PUT` is supported but not registered by default; a `Query` operation (HTTP QUERY: safe, idempotent, criteria in the body) exists but is off by default — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- Declaring any operation explicitly turns off the automatic CRUD set, so a read-only resource is declared by listing only `Get` and `GetCollection` — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- A class can be a resource with `operations: []` (no routes, non-persisted) and item access can be blocked while keeping a valid IRI through a not-exposed `Get` — [API Platform Operations](https://api-platform.com/docs/core/operations/)

(b) Behaviors
- Parameter-based filters (recommended): `SortFilter`, `DateFilter`, `ExactFilter`, `PartialSearchFilter`, `StartSearchFilter`, `EndSearchFilter`, `WordStartSearchFilter`, `IriFilter`, `ComparisonFilter` (gt/gte/lt/lte/ne/between), `FreeTextQueryFilter` (one parameter over several properties), `OrFilter`, `ChainFilter`, `ExistsFilter`; declared per resource or per operation as `'name' => new QueryParameter(filter: ExactFilter::class)`. The older `#[ApiFilter]` attribute is described as not recommended; `PropertyFilter` gives sparse fieldsets — [API Platform Filters](https://api-platform.com/docs/core/filters/)
- Doctrine Extensions (Gedmo) 3.x list: Blameable, Loggable (change history with versions), Sluggable, Timestampable, Translatable, Tree (closure, nested set, materialized path), IpTraceable, SoftDeleteable, Sortable, Uploadable, plus ODM-only References and ReferenceIntegrity — [DoctrineExtensions README](https://github.com/doctrine-extensions/DoctrineExtensions)

(c) Relationships and nesting
- A subresource is an additional `#[ApiResource]` on the child class with its own `uriTemplate` (e.g. `/companies/{companyId}/employees`) and `uriVariables` mapping each variable to `new Link(fromClass: Company::class, toProperty: 'company')`; authorization can be attached to the link with a `security` expression that sees the parent object, always enabled since 5.0 — [API Platform Subresources](https://api-platform.com/docs/core/subresources/)

(d) Custom operations and hooks
- Per-operation options: `uriTemplate`, `requirements`, `status`, `controller`, `provider`, `processor`, `input`, `output: false`, and the pipeline switches `read`, `write`, `deserialize`; state providers (read side) and processors (write side) are the per-operation extension points — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- Custom controllers are officially discouraged and unsupported for GraphQL; providers/processors are the recommended extension point **[search-level]** — [API Platform Controllers](https://api-platform.com/docs/core/controllers)

#### JHipster JDL

(a)/(b) Options (entity-level switches)
- Unary: `skipClient`, `skipServer`, `noFluentMethod`, `filter` (entity filtering; forces a service class), `readOnly`. Binary: `dto` (mapstruct | no), `service` (serviceClass | serviceImpl | no), `paginate` (pagination | infinite-scroll | no), `search` (elasticsearch | no), `microservice`, `angularSuffix`, `clientRootFolder`. Syntax forms: `paginate A with pagination`, `dto * with mapstruct except C`, annotation `@dto(mapstruct)`, and free-form custom annotations that land in an `options` bag — [JDL Options](https://www.jhipster.tech/jdl/options)
- Field types include `Enum`, `UUID`, `Duration`, `Blob`, `AnyBlob`, `ImageBlob`, `TextBlob`; validations `required`, `unique`, `min`, `max`, `minlength`, `maxlength`, `pattern`, `minbytes`, `maxbytes`; field options `defaultValue`, `defaultValueComputed` — [JDL Entities & fields](https://www.jhipster.tech/jdl/entities-fields), [JDL Options](https://www.jhipster.tech/jdl/options)

(c) Relationships
- Four types (`OneToOne`, `OneToMany`, `ManyToOne`, `ManyToMany`); syntax `A{b(name) required} to B{a}` (injected field, display field, required side); naming one side only makes it unidirectional; `with builtInEntity` when the target is a built-in entity such as `User`; `@OnDelete("SET NULL")` / `@OnUpdate("CASCADE")` with values NO ACTION, RESTRICT, CASCADE, SET NULL, SET DEFAULT; an `Id` relationship option is listed without explanation — [JDL Relationships](https://www.jhipster.tech/jdl/relationships)

(e) Limits
- Required reflexive (self) relationships are not supported; without cascade, deleting a parent with children fails on the FK — [JDL Relationships](https://www.jhipster.tech/jdl/relationships)
- Regenerating an entity overwrites hand-written changes; the community answer is the "side-by-side" pattern (custom classes that extend/replace generated ones, custom repository methods in a separate package, sometimes a separate API prefix) **[search-level]** — [JHipster tip 035](https://www.jhipster.tech/tips/035_tip_combine_generation_and_custom_code.html)

#### Strapi 5

(a) Kinds
- `kind: collectionType | singleType`; components (stored under `src/components/<category>/`, used via `type: "component"`, optionally `repeatable: true`); dynamic zones (`type: "dynamiczone"` with a `components` array) — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)

(b) Behaviors
- Model options `draftAndPublish`, `privateAttributes`, `populateCreatorFields`; `pluginOptions.i18n.localized`; attribute types include `uid`, `enumeration`, `media`, `json`, `password`, `richtext`, `customField`; attribute flags `private` (stripped from responses), `required`, `unique`, `configurable` — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- Draft & Publish: three states (draft = never published, modified = published with unsaved-to-live draft changes, published = no pending changes); a document holds a draft version and a published version; actions publish, unpublish (keep or replace last draft), discard changes; bulk publish/unpublish; scheduling only through Releases (paid plans); API `status` parameter selects draft or published — [Strapi Draft & Publish](https://docs.strapi.io/cms/features/draft-and-publish)

(c) Relationships
- `oneToOne`, `oneToMany`, `manyToOne`, `manyToMany`, uni- or bidirectional with `inversedBy` / `mappedBy` — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- Polymorphic `morphToMany` / `morphMany` exist in schema files but I only found v4-era migration documentation, which requires `configurable: false` and creates one morph table per relation **[search-level]** — [Strapi v3→v4 SQL relations](https://docs.strapi.io/dev-docs/migration/v3-to-v4/data/sql-relations)

(d) Hooks
- Lifecycle hooks: before/after for create, update, delete, findOne, findMany, count, plus `*Many` bulk variants — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- Document Service middlewares: `strapi.documents.use((context, next) => ...)` with `context.uid`, `context.action`, `context.params`; registered in `register()`; must return `next()` — [Strapi Document Service middlewares](https://docs.strapi.io/cms/api/document-service/middlewares)

(e) Limits
- In v5 one Document Service call triggers several DB hooks: `publish()` fires create (per locale) plus delete of previous published versions; `create({status:'published'})` fires create twice; `discardDraft()` fires create and delete; bulk hooks never fire from the Document Service; hooks do not fire for direct knex access — [Strapi lifecycle-hooks breaking change](https://docs.strapi.io/cms/migration/v4-to-v5/breaking-changes/lifecycle-hooks-document-service), [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- With Draft & Publish on, `unique` is not checked for drafts, so duplicates fail only at publish — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)

#### Directus

- Relationship kinds: M2O (real FK column), O2M (virtual alias field, no column), M2M (junction collection; self-referencing allowed), M2A "many to any" (alias + junction that also stores the related collection name; also called matrix/replicator), Translations (O2M alias + `languages` collection + junction holding all translated text; a wizard generates the whole setup) — [Directus Relationships](https://directus.com/docs/guides/data-model/relationships)
- Collection settings: singleton toggle (skip the list, go straight to the single item); sort field (integer, drives manual ordering in the app and API; relational fields need their own sort field); archive field with configurable archive/unarchive values (archived items are hidden in the app but still returned by the API unless filtered); accountability level (activity + revisions, activity only, or none); content versioning per collection; optional system fields for created/updated user and timestamp; primary key choices auto-increment int, big int, generated UUID, or manually entered string (not changeable later); "Save as Copy" with configurable duplication fields (related items are not copied) — [Directus Collections](https://directus.com/docs/guides/data-model/collections)

#### Contentful

- Model elements: space → content types (max 50 fields each, with a `displayField`) → entries; assets are a separate fixed-shape kind (name, description, file); field types Symbol, Text, RichText, Integer, Number, Date, Location, Boolean, Link (to entry or asset), Array (of symbols or links), Object (free JSON); fields can be hidden from the API output; values are keyed by locale — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/)
- Entry/asset states: draft, changed (published with pending changes), published, archived (not published, not editable) **[search-level]** — [Contentful: determine entry/asset state](https://contentful.com/developers/docs/tutorials/general/determine-entry-asset-state)

#### Supabase / PostgREST

- Ownership is a row-level-security policy per operation, e.g. a select policy for role `authenticated` whose `USING` clause compares `auth.uid()` with the row's `user_id`; `WITH CHECK` validates rows produced by insert/update; views bypass RLS unless created with `security_invoker = true` (Postgres 15+); the service role bypasses RLS; `security definer` functions are the escape hatch for cross-table checks; recommended to wrap `auth.uid()` in a sub-select and index policy columns — [Supabase Row Level Security](https://supabase.com/docs/guides/database/postgres/row-level-security)
- PostgREST exposes functions under `/rpc/<name>` (prefix avoids collisions with tables/views); POST maps JSON keys to parameters, GET maps query-string parameters; GET runs read-only and a volatile function called with GET returns 405; a function wrongly marked STABLE/IMMUTABLE fails because of the read-only transaction **[search-level]** — [PostgREST Functions as RPC](https://postgrest.org/en/v14/references/api/functions.html), [PostgREST Transactions](https://docs.postgrest.org/en/v14/references/transactions.html)

#### Hasura (v2 docs)

- Actions: custom queries/mutations backed by a REST handler for business logic (validation, enrichment, calling other APIs); asynchronous actions let clients subscribe to the result of a long-running action — [Hasura Actions](https://hasura.io/docs/2.0/actions/overview/)
- Computed fields: virtual fields backed by a SQL function over the row; scalar (usable in where/order_by when no extra args) or table-valued (`SETOF` a tracked table); may receive session variables (e.g. "liked by current user"); function must be STABLE/IMMUTABLE — [Hasura Computed fields](https://hasura.io/docs/2.0/schema/postgres/computed-fields/)
- Schema feature kinds listed in the docs navigation: tables, relationships, remote relationships, views, SQL functions, computed fields, logical models, native queries, default field values, enum types, data validations, input validations — [Hasura Computed fields](https://hasura.io/docs/2.0/schema/postgres/computed-fields/)
- Scheduled triggers: cron triggers (in metadata) and one-off scheduled events (created through the API, not part of metadata); delivery is at-least-once like event triggers **[search-level]** — [Hasura Scheduled Triggers](https://hasura.io/docs/2.0/scheduled-triggers/overview/), [Hasura blog](https://hasura.io/blog/p/ece9dd7c-b18e-494a-891d-2eabc3dd711f/)

#### Laravel 13.x (Eloquent) and Spatie

- Model traits/attributes: `SoftDeletes` (with `restore()`, `forceDelete()`, `withTrashed()`), `HasUuids`, `HasUlids`, `Prunable` (a `prunable()` query plus the scheduled `model:prune` command, `--pretend` dry run; soft-deleted matches are force-deleted), `MassPrunable` (bulk delete, no events), global scopes via `#[ScopedBy(...)]`, `replicate()` to clone a model, `upsert()` / `updateOrCreate()` / `firstOrCreate()` — [Laravel Eloquent (12.x page, flagged as superseded by 13.x)](https://laravel.com/docs/12.x/eloquent)
- Mass updates and mass deletes do not fire model events — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent)
- Relationships: `hasOne`, `hasMany`, `belongsTo`, `hasOneThrough`, `hasManyThrough`, `belongsToMany` with pivot payload (`withPivot`, `withTimestamps`, custom pivot model via `using()`), "one of many" (`latestOfMany`, `ofMany`), polymorphic `morphOne`/`morphMany`/`morphTo`/`morphToMany`/`morphedByMany` with `Relation::enforceMorphMap`, query-time aggregates `withCount`/`withSum`/`withExists`, `withDefault` (null-object), `chaperone()`, scoped relations with `withAttributes` — [Laravel Eloquent Relationships 13.x](https://laravel.com/docs/13.x/eloquent-relationships)
- Spatie packages confirmed to exist via a dependency listing: laravel-schemaless-attributes, laravel-tags, laravel-translatable, laravel-sluggable **[search-level]** — [Packagist: spatie](https://packagist.org/packages/spatie/)

### Inferences
- The same behavior is expressed at three "heights": a column convention (Directus sort/archive field, Strapi `uid`), a model mixin (Doctrine, Laravel, Rails), or an interface that a framework-level interceptor reacts to (ABP). Our fluent `.MultiTenant()` call is closest to the ABP shape but is resolved at compile time; ABP's ladder (`IHasCreationTime` → `IFullAuditedObject`) suggests traits should be decomposable into smaller facets (time only, time+user, deletion audit).
- Both ABP (`AbstractKeyCrudAppService`) and Rails/EF (composite keys) treat "key shape" as orthogonal to the resource kind. Our Store archetype (client-supplied key) partly mixes key strategy into the archetype; Directus' four PK options and Laravel's `HasUuids`/`HasUlids` suggest a separate key-strategy trait.
- ABP's `ICrudAppService` generic arities and API Platform's per-operation `input`/`output` show that per-operation DTO separation is a first-class knob in the mature tools.

### Gaps
- Ardalis.Specification and Ardalis.ApiEndpoints: not fetched; no verified facts.
- "EasyCaching-style CRUD generators": I found no reliable source for this category in the session and did not research it further.
- ABP `ReadOnlyAppService`, settings system, permission system and `MultiTenancySides`: named in the brief but not present on the pages fetched; not verified here.
- EF Core temporal tables, TPH/TPT/TPC inheritance mapping, HierarchyId, SaveChanges/command interceptors: docs not fetched this session. **[unverified]** From prior knowledge they exist (temporal tables as SQL Server system-versioned history, three inheritance mapping strategies, HierarchyId since EF 8 on SQL Server, interceptors as the hook for audit/soft-delete).
- JHipster `embedded` option: not on the current JDL options page I fetched; a search result points to a separate "embedded entities for Couchbase and MongoDB" page that I could not read. Composite keys and `@Id`/derived identifiers are also undocumented on the pages fetched.
- Strapi 5 polymorphic relation documentation: only v4-era material found.
- Directus Flows, hooks, one-to-one relations and on-delete behavior: the Flows page returned empty content; not verified.
- Contentful environments, tags, scheduled publishing/releases, reference validations (`linkContentType`): not on the pages fetched.
- Hasura event triggers page and Hasura DDN (v3): not fetched; only v2 docs were consulted.
- Django packages django-simple-history, django-polymorphic, contenttypes, django-modeltranslation, django-guardian, django-ordered-model, django-treebeard: not fetched. Rails gems acts_as_list/positioning, ancestry, closure_tree, AASM, paper_trail, Active Storage, `enum`, Active Record Encryption: not fetched. Spatie eloquent-sortable, activitylog, medialibrary, model-states and Laravel API Resources: not fetched. All are **[unverified]** here and appear in the final tables only with the `(u)` suffix.
- DRF and JHipster current version numbers were not captured.

---

## Key question 1: Which behaviors appear as reusable traits/mixins in 3+ ecosystems?

### Takeaway
Verified in three or more ecosystems and absent from our catalog: translatable/localized, manual ordering (sortable/position), sluggable/unique human key, tree/hierarchy, draft-and-publish, attachments/media, schemaless extra properties, private/sensitive fields, row ownership, and key strategy (UUID/ULID/manual). Timestamps, blame, soft delete, tenancy, history and optimistic concurrency also clear the bar but are already covered by Auditable, SoftDeletable, MultiTenant, Revisioned and Versioned.

### Cited Findings
- Timestamps: ABP `IHasCreationTime`/`IHasModificationTime` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities); Doctrine Timestampable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Directus optional created/updated system fields — [Directus Collections](https://directus.com/docs/guides/data-model/collections); Laravel timestamps section — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent)
- Blameable (who created/modified): ABP `IMayHaveCreator`, `IModificationAuditedObject` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities); Doctrine Blameable (and IpTraceable) — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Directus user-created/user-updated fields — [Directus Collections](https://directus.com/docs/guides/data-model/collections); Strapi `populateCreatorFields` — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- Soft delete / archive: ABP `ISoftDelete` with deletion audit — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities); Doctrine SoftDeleteable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Laravel `SoftDeletes` with restore/forceDelete/withTrashed — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent); EF Core query-filter pattern — [EF Core filters](https://learn.microsoft.com/en-us/ef/core/querying/filters); Directus archive field with custom values — [Directus Collections](https://directus.com/docs/guides/data-model/collections); Contentful archived state **[search-level]** — [Contentful states](https://contentful.com/developers/docs/tutorials/general/determine-entry-asset-state)
- Multi-tenancy / row scoping: ABP `IMultiTenant` — [ABP Multi-Tenancy](https://abp.io/docs/latest/framework/architecture/multi-tenancy); EF Core tenant filter — [EF Core filters](https://learn.microsoft.com/en-us/ef/core/querying/filters); Supabase RLS — [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security)
- Row ownership / object-level authorization: Supabase ownership policies — [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security); DRF `perform_create` for request-implied attributes — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/); API Platform `security` on `Link` — [API Platform Subresources](https://api-platform.com/docs/core/subresources/); ABP per-operation policy names — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services)
- Change history / revisions: ABP entity history — [ABP Audit Logging](https://abp.io/docs/latest/framework/infrastructure/audit-logging); Doctrine Loggable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Directus revisions + activity — [Directus Collections](https://directus.com/docs/guides/data-model/collections)
- Optimistic concurrency / version counter: ABP `IHasConcurrencyStamp`, `IHasEntityVersion` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- Translatable / localized: Doctrine Translatable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Strapi i18n `localized` — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); Directus Translations — [Directus Relationships](https://directus.com/docs/guides/data-model/relationships); Contentful locale-keyed fields — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/); Spatie laravel-translatable **[search-level]** — [Packagist: spatie](https://packagist.org/packages/spatie/)
- Sortable / manual position: Doctrine Sortable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Directus sort field (collection-level and per relation) — [Directus Collections](https://directus.com/docs/guides/data-model/collections)
- Sluggable / unique readable key: Doctrine Sluggable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); Strapi `uid` attribute type — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); Spatie laravel-sluggable **[search-level]** — [Packagist: spatie](https://packagist.org/packages/spatie/)
- Tree / hierarchy: Doctrine Tree (three storage strategies) — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); django-mptt and django-tree-queries — [django-mptt README](https://github.com/django-mptt/django-mptt); Rails self joins — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html); JHipster reflexive relationships (required ones unsupported) — [JDL Relationships](https://www.jhipster.tech/jdl/relationships)
- Draft / publish: Strapi — [Strapi Draft & Publish](https://docs.strapi.io/cms/features/draft-and-publish); Directus content versioning — [Directus Collections](https://directus.com/docs/guides/data-model/collections); Contentful states **[search-level]** — [Contentful states](https://contentful.com/developers/docs/tutorials/general/determine-entry-asset-state)
- Attachments / media: Strapi `media` attribute — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); Contentful assets — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/); Doctrine Uploadable — [DoctrineExtensions](https://github.com/doctrine-extensions/DoctrineExtensions); JHipster `Blob`/`ImageBlob` field types — [JDL Entities & fields](https://www.jhipster.tech/jdl/entities-fields); ABP BLOB storing **[search-level]** — [ABP Background Jobs search result](https://abp.io/docs/latest/framework/infrastructure/background-jobs)
- Extra / schemaless properties: ABP `IHasExtraProperties` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities); Strapi `json` attribute — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); Contentful `Object` field — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/); EF Core JSON-mapped complex types — [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew); Spatie schemaless-attributes **[search-level]** — [Packagist: spatie](https://packagist.org/packages/spatie/)
- Private / sensitive fields: Strapi `private` flag and `password` type — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); ABP property-level `[DisableAuditing]` — [ABP Audit Logging](https://abp.io/docs/latest/framework/infrastructure/audit-logging); Contentful hide-from-output field setting — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/)
- Key strategy: Laravel `HasUuids`/`HasUlids` — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent); ABP `IGuidGenerator` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities); Directus four PK types — [Directus Collections](https://directus.com/docs/guides/data-model/collections); JHipster `UUID` type — [JDL Entities & fields](https://www.jhipster.tech/jdl/entities-fields)
- Full-text / search: JHipster `search ... with elasticsearch` — [JDL Options](https://www.jhipster.tech/jdl/options); API Platform `FreeTextQueryFilter`, `PartialSearchFilter` — [API Platform Filters](https://api-platform.com/docs/core/filters/); Contentful Text fields support full-text search — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/); EF Core 10 Cosmos full-text and SQL Server vector search — [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- Derived values (counter cache / computed): Rails `counter_cache` — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html); Laravel `withCount`/`withSum` — [Laravel Relationships](https://laravel.com/docs/13.x/eloquent-relationships); Hasura computed fields — [Hasura Computed fields](https://hasura.io/docs/2.0/schema/postgres/computed-fields/)
- Parent touch: Rails `touch` — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html); ABP users expect it and report its absence since 8.3 **[search-level]** — [ABP support #9642](https://abp.io/support/questions/9642/Audit-columns-for-parent-table-is-not-updating-when-child-table-is-updated-using-parent-and-child-data-in-same-object)
- Prunable / retention: Laravel `Prunable`, `MassPrunable` — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent)
- Clone / duplicate: Laravel `replicate()` — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent); Directus "Save as Copy" with duplication-field settings — [Directus Collections](https://directus.com/docs/guides/data-model/collections)
- Pagination mode as an option: JHipster `paginate` (pagination | infinite-scroll) — [JDL Options](https://www.jhipster.tech/jdl/options); ABP `PagedAndSortedResultRequestDto` — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services); OData `$top`/`$skip` with a max-top limit — [OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview)
- Entity cache: ABP `EntityCache` — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities)
- Tags: Spatie laravel-tags **[search-level]** — [Packagist: spatie](https://packagist.org/packages/spatie/); EF Core primitive collection `string[] Tags` as a JSON column — [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- Commentable as reusable nested resource: Rails routing concern example — [Rails Routing](https://guides.rubyonrails.org/routing.html)

### Inferences
- "Archive" is not the same as soft delete in the CMSs: archived rows stay queryable through the API (Directus) or are a lifecycle state (Contentful), while soft delete hides rows and pairs with restore/force-delete. Our SoftDeletable should make explicit whether it generates restore, purge and include-deleted surfaces (Laravel and ABP both ship all three).
- Draft/publish is not a Workflow state machine in Strapi 5: it is two stored versions of the same document plus publish/unpublish/discard operations. That makes "Publishable" a distinct trait (dual version + read-side `status` switch), with scheduled publishing as an add-on.
- Translatable has two storage shapes in the wild: a side/junction table per entity (Directus, Doctrine) and locale-keyed field values in the same document (Contentful). A source generator would need to pick one or expose both.
- Sortable appears at two scopes: whole-collection order and order within a parent relation (Directus needs a separate sort field per relational field). For us that implies position scoped by `Under<Parent>` / Detail.
- Prunable (Laravel) is close to our Expiring but differs: it is an arbitrary query predicate evaluated by a scheduled job with a dry-run mode, not a per-row expiry timestamp.
- Timestamps-only and blame-only are separable facets in ABP and Doctrine; our single Auditable may be too coarse for AppendOnly or Lookup resources.

### Gaps
- Sequence / document-number generation: no first-party trait found on any page fetched; not verified in any ecosystem.
- Encrypted-at-rest fields: only "private/hidden" was verified. **[unverified]** Rails `encrypts` and Laravel's `encrypted` cast exist from prior knowledge.
- Taggable and commentable as packaged traits were verified in only one or two ecosystems (Spatie tags at search level; Rails routing concern). **[unverified]** acts-as-taggable-on, django-taggit.
- Sortable in Rails (acts_as_list, positioning), Django (django-ordered-model) and Laravel (spatie/eloquent-sortable); tree in Rails (ancestry, closure_tree), EF Core (HierarchyId), django-treebeard; history in django-simple-history, paper_trail, spatie/laravel-activitylog, EF temporal tables; state machines in AASM and spatie/laravel-model-states; attachments in Active Storage and spatie/laravel-medialibrary; object permissions in django-guardian: all **[unverified]** in this session.

---

## Key question 2: Which resource kinds exist beyond plain CRUD?

### Takeaway
Beyond list/create/read/update/delete, the tools consistently distinguish: single-record resources, embedded identity-less components, polymorphic/union-typed links and zones, inheritance families, views/read models and non-persisted resources, RPC functions split into safe "functions" and side-effecting "actions" (each bound or unbound), event/schedule-driven triggers, and assets/media as their own kind. Our catalog has no equivalent for components, polymorphic links, inheritance, safe parameterized functions, triggers/scheduled events, or assets.

### Cited Findings
- Single record: Strapi `singleType` — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); Directus singleton — [Directus Collections](https://directus.com/docs/guides/data-model/collections); Rails singular `resource` — [Rails Routing](https://guides.rubyonrails.org/routing.html); OData singleton — [OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview)
- Embedded component / value type: Strapi components, optionally repeatable — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); EF Core complex types and owned types — [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew), [EF Core Owned types](https://learn.microsoft.com/en-us/ef/core/modeling/owned-entities); OData complex types — [OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview); Contentful `Object` field — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/)
- Polymorphic link / union-typed children: Strapi dynamic zones — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models); Directus M2A — [Directus Relationships](https://directus.com/docs/guides/data-model/relationships); Rails polymorphic associations — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html); Laravel morph relations with an enforced morph map — [Laravel Relationships](https://laravel.com/docs/13.x/eloquent-relationships); DRF `GenericForeignKey` handling — [DRF relations](https://www.django-rest-framework.org/api-guide/relations/)
- Inheritance families: Rails STI and delegated types — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html); OData type inheritance — [OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview)
- Views / read models / non-persisted resources: Hasura views, logical models and native queries — [Hasura Computed fields (navigation)](https://hasura.io/docs/2.0/schema/postgres/computed-fields/); Supabase views and their RLS caveat — [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security); API Platform resource with `operations: []` or a custom provider — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- RPC / custom operations: PostgREST `/rpc/<fn>` with GET for read-only and POST for volatile **[search-level]** — [PostgREST Functions as RPC](https://postgrest.org/en/v14/references/api/functions.html); OData functions and actions — [OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview); Hasura Actions (sync and async) — [Hasura Actions](https://hasura.io/docs/2.0/actions/overview/); DRF `@action(detail=...)` — [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/); Rails member/collection routes — [Rails Routing](https://guides.rubyonrails.org/routing.html); API Platform custom operations and the body-carrying `Query` operation — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- Events and schedules: Hasura cron triggers and one-off scheduled events with at-least-once delivery **[search-level]** — [Hasura Scheduled Triggers](https://hasura.io/docs/2.0/scheduled-triggers/overview/); ABP distributed event bus with outbox/inbox and background jobs **[search-level]** — [ABP Background Jobs](https://abp.io/docs/latest/framework/infrastructure/background-jobs); Contentful webhooks on publish/unpublish **[search-level]** — [Contentful content events](https://contentful.com/developers/docs/extensibility/webhooks/content-events.md)
- Long-running operations: Hasura asynchronous actions with result subscription — [Hasura Actions](https://hasura.io/docs/2.0/actions/overview/); ABP background jobs **[search-level]** — [ABP Background Jobs](https://abp.io/docs/latest/framework/infrastructure/background-jobs)
- Assets / media as a kind: Contentful assets — [Contentful data model](https://www.contentful.com/developers/docs/concepts/data-model/); Strapi `media` — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- Join entity with payload vs bare link: Rails `has_many :through` vs HABTM — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html); Laravel pivot with `withPivot` or a custom pivot model — [Laravel Relationships](https://laravel.com/docs/13.x/eloquent-relationships); Directus junction collections, including self-referencing M2M — [Directus Relationships](https://directus.com/docs/guides/data-model/relationships)
- Scoped settings / feature values: ABP features resolved Tenant → Edition → Configuration → Default — [ABP Features](https://abp.io/docs/latest/framework/infrastructure/features)
- Enum / reference data: JHipster `Enum`, Strapi `enumeration`, Hasura enum types — [JDL Entities & fields](https://www.jhipster.tech/jdl/entities-fields), [Strapi Models](https://docs.strapi.io/cms/backend-customization/models), [Hasura Computed fields (navigation)](https://hasura.io/docs/2.0/schema/postgres/computed-fields/)
- Upsert: Laravel `upsert`/`updateOrCreate` — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent); API Platform `PUT` with `allowCreate` — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- Batch endpoint: OData `$batch` — [OData overview](https://learn.microsoft.com/en-us/odata/webapi-8/fundamentals/overview)
- Operation-subset shapes as named kinds: DRF's nine generic views and `ReadOnlyModelViewSet` — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/); JHipster `readOnly` — [JDL Options](https://www.jhipster.tech/jdl/options)

### Inferences
- OData and PostgREST both separate *safe* custom operations (function / STABLE via GET, cacheable, composable) from *unsafe* ones (action / VOLATILE via POST). Our single Action archetype ("custom command on a resource") covers bound commands only; a "Function/Query" kind (parameterized read) and unbound (resource-less) operations are not represented.
- Rails member/collection, DRF `detail=True/False` and OData binding all encode the same binary: instance-bound versus collection-bound. Our Action should state which of the two it generates, and both are needed.
- Component (identity-less, reusable across several entities, no endpoints, optionally repeatable) is distinct from our Detail (child with identity owned by one parent). EF complex type ↔ Strapi component; EF `OwnsMany` ↔ our Detail.
- Dynamic zone / M2A is an *ordered, heterogeneous* child list; it combines polymorphism with Sortable. Nothing in our catalog expresses "one of N types".
- API Platform's option to run the same resource class with `operations: []` suggests a "model only, no endpoints" archetype (entity + EF config without API), useful for entities reached only through a parent.
- ABP features and Strapi/Directus singletons are two different things both loosely called "settings": a singleton is one editable record; a feature/setting system is a keyed value resolved through a scope chain (tenant → edition → default). Our Singleton covers only the first.

### Gaps
- OData containment navigation properties and bound-vs-unbound declaration syntax (`.Action(...)`, `.Collection.Function(...)`) were not on the page fetched; not verified.
- PostgREST views, computed/virtual columns and embedding: docs returned HTTP 429; only the RPC behavior is confirmed at search level.
- Hasura event triggers (per-table insert/update/delete webhooks): confirmed only indirectly through the scheduled-trigger sources.
- Directus Flows and Contentful webhooks details not verified.

---

## Key question 3: "Child edited through the parent" versus "independent nested resource"

### Takeaway
The tools use three separate mechanisms that we should keep apart: (1) ownership in the data model (EF owned/complex types, Strapi components, ABP Suite Child type) where the child has no life outside the parent; (2) URL nesting of an otherwise independent resource (Rails nested/shallow routes, API Platform subresource `Link`, drf-nested-routers); (3) nested writes in the parent payload, which almost nobody generates automatically (DRF is read-only by default, ABP declines to automate child-collection updates). Our Detail maps to (1), Under<Parent> to (2); mechanism (3) and the "shallow" variant of (2) are the open design choices.

### Cited Findings
- EF Core: owned types are declared on the owner (`OwnsOne`/`OwnsMany`), have no `DbSet`, load automatically with the owner, and by default key a collection as (owner FK, Id) so the relationship is immutable; the docs note a surrogate key would allow moving a child between parents, which goes against aggregate semantics — [EF Core Owned types](https://learn.microsoft.com/en-us/ef/core/modeling/owned-entities)
- EF Core: complex types are for identity-less values and are now preferred over owned types for that purpose — [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- ABP Suite: declare the child with entity type *Child* and pick its master; the master orchestrates it, but app services are still generated for the child; child inherits tenancy; edited in sub-grids/tabs of the master page; one-to-many only — [ABP Suite master-detail](https://abp.io/docs/latest/suite/creating-master-detail-relationship)
- ABP: guidance against navigation properties between aggregate roots — [ABP Entities](https://abp.io/docs/latest/framework/architecture/domain-driven-design/entities); automatic mapping of child collections on update is not planned according to ABP staff **[search-level]** — [ABP support #1110](https://abp.io/QA/Questions/1110)
- Rails: URL nesting via nested `resources`; guide's rule of thumb is one level; `shallow: true` nests only the collection-scoped actions (index, new, create) and exposes member actions at top level — [Rails Routing](https://guides.rubyonrails.org/routing.html)
- Rails: parent/child coupling is declared with association options — `dependent` (destroy, delete_all, nullify, restrict), `touch`, `counter_cache`, and `before_add`/`before_remove` callbacks that can veto changes to the child collection — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html)
- API Platform: independent nested resource = extra `#[ApiResource]` on the child with a parent-prefixed `uriTemplate` and a `Link(fromClass, toProperty)`; parent-based authorization lives on the link — [API Platform Subresources](https://api-platform.com/docs/core/subresources/)
- DRF: nested serializers are read-only unless `create()`/`update()` are written by hand; URL nesting comes from the third-party drf-nested-routers — [DRF relations](https://www.django-rest-framework.org/api-guide/relations/)
- Strapi: component attribute (optionally `repeatable`) = data embedded in and edited with the parent entry; relation attribute = link to an independent content type — [Strapi Models](https://docs.strapi.io/cms/backend-customization/models)
- Directus: O2M is a virtual alias on the parent that surfaces independent child items in the parent's item page; ordering of children requires a sort field on the relation; duplicating a parent does not copy related items — [Directus Relationships](https://directus.com/docs/guides/data-model/relationships), [Directus Collections](https://directus.com/docs/guides/data-model/collections)
- JHipster: only plain relationships; child lifetime is expressed with `@OnDelete("CASCADE")` on the relationship — [JDL Relationships](https://www.jhipster.tech/jdl/relationships)
- Laravel: children created through a scoped relationship can receive fixed attributes (`withAttributes`), and `chaperone()` back-links children to the parent — [Laravel Relationships](https://laravel.com/docs/13.x/eloquent-relationships)

### Inferences
- Even when a child is "owned", ABP Suite still emits child app services; the ownership shows up in tenancy inheritance, UI placement and delegated orchestration, not in the absence of endpoints. A Detail archetype may reasonably offer both a nested-write surface on the parent and optional direct child endpoints.
- The EF default composite key (parent FK + local Id) is a concrete, documented way to encode "cannot be re-parented"; a generator can use it to distinguish Detail (FK in key, immutable) from Under<Parent> (FK is an ordinary, possibly mutable column).
- Rails shallow nesting is the mainstream answer to "independent nested resource": list/create under the parent, item operations by the child's own id. Under<Parent> likely needs a `shallow` switch.
- Cascade policy (`dependent` / `@OnDelete`) is a per-relationship option everywhere, with at least four values (cascade, set null, restrict, no action); it should be an explicit parameter of both Detail and Under<Parent> rather than implied by the archetype.
- Authorization inherited from the parent (API Platform link security, Suite tenancy inheritance) is a recurring requirement for both mechanisms.

### Gaps
- Rails `accepts_nested_attributes_for` (nested writes through the parent) is not covered in the guide sections fetched; not verified.
- OData containment (the closest protocol-level notion of "child addressed only through parent") not verified.
- Hasura nested inserts through relationships and PostgREST resource embedding not verified.
- Multi-level master-detail (child of child) in ABP Suite is not described on the page.

---

## Key question 4: What do users complain about most with generated CRUD, and which escape hatches are treated as essential?

### Takeaway
The recurring leak is the same everywhere: generated CRUD works for the plain single-table case and breaks at (a) nested/child writes, (b) business logic that does not fit create/update, (c) implicit global filters interacting with joins and bulk paths, and (d) regeneration overwriting hand edits. The escape hatches every mature tool ships are: restrict the operation set, override or replace a single operation, per-operation authorization and DTOs, a query-customization hook, pre/post hooks around persistence, a scoped way to bypass a behavior's filter, and first-class custom actions.

### Cited Findings

Pain points
- HackSoft's Django styleguide advises plain `APIView`/`GenericAPIView`, one API per operation (four APIs for CRUD), dedicated input and output serializers per API, minimal serializer reuse and no business logic in APIs or serializers; it says generic views plus serializers suit the straightforward CRUD case but that case rarely occurs in practice and logic becomes fragmented — [HackSoft Django-Styleguide](https://github.com/HackSoftware/Django-Styleguide)
- DRF nested serializers are read-only by default and writes must be hand-coded — [DRF relations](https://www.django-rest-framework.org/api-guide/relations/); helper libraries still struggle with unique validators on nested update and multipart payloads **[search-level]** — [drf-writable-nested](https://github.com/beda-software/drf-writable-nested)
- ABP: `CrudAppService` cannot be used with composite keys — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services); users report child entities marked deleted or not updated when updating an aggregate through it, and staff state automatic child-collection mapping is not planned **[search-level]** — [ABP support #1037](https://abp.io/support/questions/1037/CrudAppService-Child-Entity-in-MultiTenant-Scenario-missing-TenantId), [ABP support #1110](https://abp.io/QA/Questions/1110); mapping into a new object instead of the tracked entity breaks child collections **[search-level]** — [ABP support #10282](https://abp.io/support/questions/10282/Mapperly-Cannot-Map-Id-properly)
- ABP data filters: compiled queries throw when filters use the UDF mapping; changing global filter defaults can break pre-built modules — [ABP Data Filtering](https://abp.io/docs/latest/framework/infrastructure/data-filtering)
- EF Core global filters: a filter on a required navigation's target silently drops parent rows; filters only on hierarchy roots; before EF 10 a second `HasQueryFilter` overwrote the first and filters could not be disabled individually — [EF Core filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)
- EF Core owned types: reference semantics cause failures when assigning one owned instance to two properties, no bulk update, identity-based comparison; Microsoft recommends switching to complex types — [EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)
- Strapi 5: a single publish/update call fires multiple create/delete hooks (also once per locale), bulk hooks never fire from the Document Service, and direct DB access skips hooks — [Strapi lifecycle-hooks breaking change](https://docs.strapi.io/cms/migration/v4-to-v5/breaking-changes/lifecycle-hooks-document-service)
- Laravel: bulk update/delete and `MassPrunable` skip model events, so event-based behaviors silently do not run — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent)
- django-mptt: denormalized tree columns cause write amplification, corruption under concurrency and breakage with bulk operations; project unmaintained — [django-mptt README](https://github.com/django-mptt/django-mptt)
- API Platform: declaring one explicit operation removes all default ones — [API Platform Operations](https://api-platform.com/docs/core/operations/); custom controllers are discouraged and do not work with GraphQL **[search-level]** — [API Platform Controllers](https://api-platform.com/docs/core/controllers); a developer critique titled "Why I stopped using API Platform" and community threads report DTO/serialization behavior not matching expectations **[search-level, anecdotal]** — [dev.to: Why I stopped using API Platform](https://dev.to/alekitto/why-i-stopped-using-api-platform-2eki)
- JHipster: regeneration overwrites custom code; teams adopt the side-by-side pattern, at the cost of duplicated files **[search-level]** — [JHipster tip 035](https://www.jhipster.tech/tips/035_tip_combine_generation_and_custom_code.html)
- Database-as-API tools: critics focus on where business logic goes (stored procedures, edge functions, or the client) and on exposing the schema publicly; much of this criticism comes from competing vendors **[search-level, partly biased sources]** — [dev.to comparison](https://dev.to/ainiro/a-comparison-of-supabase-hasura-and-aista-4cjo), [Supabase blog response](https://supabase.com/blog/simplify-backend-with-data-api)
- Supabase: views bypass RLS by default; per-row function evaluation and missing indexes on policy columns are documented performance traps — [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security)
- Rails: deep nesting produces unwieldy helpers that need every ancestor — [Rails Routing](https://guides.rubyonrails.org/routing.html)
- Directus: primary key type cannot be changed after creation; archived items are still returned by the API unless filtered — [Directus Collections](https://directus.com/docs/guides/data-model/collections)
- ABP Suite: no many-to-many for child entities; no UI/tests generated for children — [ABP Suite master-detail](https://abp.io/docs/latest/suite/creating-master-detail-relationship)

Escape hatches that are shipped as first-class features
- Restrict the operation set: Rails `only:`/`except:` — [Rails Routing](https://guides.rubyonrails.org/routing.html); DRF mixin composition and `ReadOnlyModelViewSet` — [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/); API Platform explicit `operations` list — [API Platform Operations](https://api-platform.com/docs/core/operations/); JHipster `readOnly` — [JDL Options](https://www.jhipster.tech/jdl/options)
- Override one operation or its persistence step: ABP overridable CRUD methods and mapping hooks — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services); DRF `perform_create`/`perform_update`/`perform_destroy` — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/); API Platform per-operation `provider`/`processor` — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- Query hook: ABP `CreateFilteredQuery`, `ApplySorting`, `ApplyPaging` — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services); DRF `get_queryset`, `filter_queryset` — [DRF Generic views](https://www.django-rest-framework.org/api-guide/generic-views/)
- Per-operation authorization: ABP `XxxPolicyName` — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services); DRF `permission_classes` on `@action` — [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/); Supabase per-operation policies — [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security)
- Per-operation DTO/serializer: ABP generic DTO arguments — [ABP Application Services](https://abp.io/docs/latest/framework/architecture/domain-driven-design/application-services); DRF `get_serializer_class` and `serializer_class` on `@action` — [DRF ViewSets](https://www.django-rest-framework.org/api-guide/viewsets/); API Platform `input`/`output` — [API Platform Operations](https://api-platform.com/docs/core/operations/)
- Scoped bypass of a behavior: ABP `IDataFilter.Disable<ISoftDelete>()` and `HardDeleteAsync` — [ABP Data Filtering](https://abp.io/docs/latest/framework/infrastructure/data-filtering); EF `IgnoreQueryFilters([name])` — [EF Core filters](https://learn.microsoft.com/en-us/ef/core/querying/filters); Laravel `withTrashed()`/`forceDelete()` — [Laravel Eloquent](https://laravel.com/docs/12.x/eloquent); Supabase service role — [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security)
- Cross-cutting pre/post hooks: FastEndpoints pre/post processors **[search-level]** — [FastEndpoints](https://fast-endpoints.com/docs/pre-post-processors); Strapi Document Service middlewares — [Strapi middlewares](https://docs.strapi.io/cms/api/document-service/middlewares); Rails association callbacks — [Rails Association Basics](https://guides.rubyonrails.org/association_basics.html)
- Custom actions next to CRUD: DRF `@action`, Rails member/collection routes, API Platform custom operations, OData actions/functions, Hasura Actions, PostgREST RPC — sources as listed under key question 2
- Keeping generated and custom code apart: JHipster side-by-side **[search-level]** — [JHipster tip 035](https://www.jhipster.tech/tips/035_tip_combine_generation_and_custom_code.html)

### Inferences
- A source generator avoids JHipster's overwrite problem by construction only if every extension point is reachable without editing generated files; the equivalent of "side-by-side" in C# is partial classes/partial methods plus user-supplied delegates in the fluent call. The list of hooks above (query, map-in, map-out, before/after persist, authorize, per-operation DTO) is the minimum set the other tools converged on.
- Behaviors implemented through per-entity events (Laravel observers, Strapi DB hooks) silently fail on bulk paths; behaviors implemented as query filters fail on raw SQL and compiled queries. Our Batch and Import/Export traits are exactly the paths where Auditable, SoftDeletable, Revisioned and MultiTenant are most likely to be skipped, so trait × Batch interaction needs explicit design.
- Named, individually ignorable EF 10 query filters remove the main historical obstacle to composing SoftDeletable + MultiTenant (+ Publishable, Expiring) on one entity; targeting EF 10 makes one filter per trait feasible, but the "required navigation" pitfall still applies when a filtered entity is a required parent.
- Strapi's experience shows that when a trait changes storage shape (draft and published rows per document), low-level hooks stop meaning what users expect; hooks should be defined at the logical-operation level (publish, unpublish), not the row level.
- API Platform's "declare one, lose the defaults" rule is a documented surprise; an additive/subtractive API (`.Without(Delete)` / `.Only(...)`) avoids it.
- The HackSoft position (one endpoint per operation, separate input/output types) matches what Minimal API generation naturally produces, which is a point in favor of generating discrete endpoints rather than a generic CRUD service.

### Gaps
- I did not find a first-party or widely cited critique specific to ABP's `CrudAppService`; evidence is limited to support-forum threads read at search-summary level.
- No GitHub-issue-level evidence was collected for JHipster, API Platform or Hasura; the API Platform and database-as-API critiques are anecdotal and, in the latter case, partly from competing vendors.
- I found no reliable source ranking these complaints by frequency; "most complained about" is my synthesis, not a measured result.
- Rails "concerns" criticism and Rails-specific scaffold critiques were not researched.

---

## Final tables: catalog gap analysis

Legend: `(u)` = ecosystem known from prior knowledge only, not verified in this session; `(s)` = verified at search-summary level only. "Closest item" is my mapping (inference), not a sourced fact. Sources for every verified cell are in the sections above.

### Table 1 — behavior/trait | ecosystems that ship it | closest item in our catalog or MISSING

| Behavior / trait | Ecosystems that ship it | Closest item in our catalog |
|---|---|---|
| Timestamps (created/updated time only) | ABP, Doctrine Timestampable, Directus, Laravel, Rails (u), Django (u) | Auditable (coarser; no time-only facet) |
| Blameable (created/updated by) | ABP, Doctrine Blameable, Directus, Strapi creator fields | Auditable |
| Deletion audit (deleted at / by) | ABP `IDeletionAuditedObject` | SoftDeletable + Auditable (verify combination) |
| Soft delete with restore / purge / include-deleted | ABP, Doctrine SoftDeleteable, Laravel SoftDeletes, EF Core filter pattern | SoftDeletable |
| Archivable (hidden by default, still addressable, custom flag values) | Directus archive field, Contentful archived state (s) | MISSING (SoftDeletable is nearest) |
| Multi-tenant row scoping | ABP, EF Core filter pattern, Supabase RLS, Spatie multitenancy (u) | MultiTenant |
| Ownable / per-row owner authorization | Supabase RLS, DRF perform_create pattern, API Platform link security, django-guardian (u) | MISSING |
| Per-operation authorization policy | ABP, DRF, Supabase | MISSING (not a listed trait) |
| Optimistic concurrency token | ABP `IHasConcurrencyStamp` | Versioned |
| Auto-incremented entity version counter | ABP `IHasEntityVersion` | Versioned (verify counter vs token) |
| Change history / revisions | ABP entity history, Doctrine Loggable, Directus revisions, django-simple-history (u), paper_trail (u), EF temporal tables (u), Spatie activitylog (u) | Revisioned |
| Request/activity audit log | ABP audit log, Directus activity | MISSING (Auditable/Revisioned cover row data, not request log) |
| Translatable / localized fields | Doctrine Translatable, Strapi i18n, Directus Translations, Contentful locales, Spatie translatable (s), django-modeltranslation (u) | MISSING |
| Sortable / manual position (global or within parent) | Doctrine Sortable, Directus sort field, acts_as_list/positioning (u), django-ordered-model (u), Spatie eloquent-sortable (u) | MISSING |
| Sluggable / unique readable identifier | Doctrine Sluggable, Strapi `uid`, Spatie sluggable (s), friendly_id (u) | MISSING (Lookup has `code` only) |
| Tree / hierarchy | Doctrine Tree, django-mptt / django-tree-queries, Rails self-join, ancestry/closure_tree (u), EF HierarchyId (u) | MISSING |
| Publishable (draft / published dual version) | Strapi, Directus content versioning, Contentful (s) | MISSING (Workflow is nearest, not equivalent) |
| Scheduled publish / release bundle | Strapi Releases | MISSING |
| Attachable (files/media on an entity) | Strapi media, Contentful assets, Doctrine Uploadable, JHipster Blob types, ABP BLOB storing (s), Active Storage (u), Spatie medialibrary (u) | MISSING |
| Extra / schemaless properties | ABP `IHasExtraProperties`, Strapi json, Contentful Object, EF JSON complex types, Spatie schemaless (s) | MISSING |
| Private / sensitive field (hidden from output, excluded from audit) | Strapi `private`/`password`, ABP `[DisableAuditing]`, Contentful hidden field | MISSING |
| Encrypted field | Rails `encrypts` (u), Laravel encrypted cast (u) | MISSING |
| Key strategy (UUID / ULID / sequential GUID / manual string) | Laravel HasUuids/HasUlids, ABP IGuidGenerator, Directus PK types, JHipster UUID | Store covers client-supplied key only; otherwise MISSING |
| Composite key | ABP AbstractKeyCrudAppService, EF owned collections, Rails | Association / Detail implicitly; MISSING as option |
| Searchable (full-text / free-text) | JHipster search, API Platform FreeTextQueryFilter, Contentful Text, EF Core 10 Cosmos FTS | MISSING (Filterable is field filtering) |
| Declarative filters / sorting / paging | API Platform filters, JHipster filter + paginate, OData query options, ABP paged request | Filterable |
| Sparse fieldsets / expand related | API Platform PropertyFilter, OData `$select`/`$expand` | MISSING |
| Counter cache / derived aggregate | Rails counter_cache, Laravel withCount, Hasura computed fields | MISSING |
| Computed (virtual) field, possibly per-user | Hasura computed fields | MISSING |
| Touch parent on child change | Rails touch, Laravel touches (TOC only) | MISSING (belongs with Detail / Under<Parent>) |
| Cascade policy on parent delete | Rails dependent, JHipster @OnDelete | MISSING as explicit option |
| Prunable / retention by predicate | Laravel Prunable / MassPrunable | Expiring (nearest; differs: predicate + scheduled job + dry run) |
| Cloneable / duplicate | Laravel replicate, Directus Save as Copy | MISSING |
| Taggable | Spatie tags (s), EF primitive collection, acts-as-taggable-on (u) | MISSING |
| Commentable (reusable polymorphic child bundle) | Rails routing concern, Laravel morphMany | MISSING |
| Entity cache | ABP EntityCache | MISSING |
| Events / outbox on change | ABP event bus + outbox (s), Hasura event triggers (s), Contentful webhooks (s), Laravel model events | MISSING |
| State machine | django-fsm family (s), AASM (u), Spatie model-states (u) | Workflow |
| Idempotent requests | none verified in the tools surveyed | Idempotent (ours; no counterpart found) |
| Batch operations | OData `$batch`, Strapi bulk publish, Laravel upsert | Batch |
| Import / Export | none verified in the tools surveyed | Import/Export (ours; no counterpart found) |
| Feature-gated resource | ABP `[RequiresFeature]` | MISSING |
| Default values / presets from context | JHipster defaultValue, Hasura default field values, Laravel withAttributes | MISSING |
| Sequence / document number generation | none verified | MISSING (no evidence either way) |

### Table 2 — resource kind | ecosystems | closest item in our catalog or MISSING

| Resource kind | Ecosystems | Closest item in our catalog |
|---|---|---|
| Full CRUD collection | all | Crud |
| Read-only collection | DRF ReadOnlyModelViewSet, JHipster readOnly, Rails `only:`, API Platform Get+GetCollection | ReadOnly |
| Arbitrary operation subset (e.g. create+list+retrieve, no update) | DRF mixins / nine generic views, Rails only/except, API Platform operations list | Partly AppendOnly; general subset MISSING |
| Single record | Strapi singleType, Directus singleton, Rails singular resource, OData singleton | Singleton |
| Keyed upsert store | Laravel upsert/updateOrCreate, API Platform PUT allowCreate, Directus manual string PK | Store |
| Immutable / append-only | none verified as a named kind | AppendOnly (ours) |
| Reference data / enum | JHipster Enum, Strapi enumeration, Hasura enum types | Lookup |
| Many-to-many link, bare | Rails HABTM, Directus M2M junction, JHipster ManyToMany | Association |
| Many-to-many link with payload / join entity | Rails has_many :through, Laravel custom pivot | Association (verify payload support) |
| Owned child with identity (master-detail) | ABP Suite Child, EF OwnsMany | Detail |
| Independent nested resource (URL nesting, incl. shallow) | Rails nested/shallow, API Platform subresource, drf-nested-routers | Under<Parent> (shallow variant MISSING) |
| Embedded component / value type (no identity, reusable, repeatable) | Strapi component, EF complex type, OData complex type, Contentful Object | MISSING |
| Polymorphic link (belongs to one of N types) | Rails polymorphic, Laravel morphTo, DRF GenericForeignKey, Strapi morph (s) | MISSING |
| Heterogeneous ordered child list | Strapi dynamic zone, Directus M2A | MISSING |
| Inheritance family (shared base + subtypes) | Rails STI, Rails delegated types, OData inheritance, EF TPH/TPT/TPC (u) | MISSING |
| Self-referential hierarchy | Rails self-join, Directus self M2M, Doctrine Tree | MISSING |
| View / projection / read model (query-backed, no own table) | Hasura views / logical models / native queries, Supabase views, API Platform custom provider | ReadOnly (nearest; projection source MISSING) |
| Model without endpoints | API Platform `operations: []` | MISSING |
| Instance-bound command | DRF `@action(detail=True)`, Rails member route, OData bound action | Action |
| Collection-bound command | DRF `@action(detail=False)`, Rails collection route | Action (verify collection binding) |
| Unbound command (no resource) | Hasura Action, PostgREST volatile RPC, OData unbound action | MISSING |
| Safe function / parameterized query (GET, cacheable) | OData function, PostgREST stable RPC, API Platform Query operation | MISSING |
| Long-running operation | Hasura async action, ABP background job (s) | Job |
| State machine resource | django-fsm family (s), Contentful/Strapi publication states | Workflow |
| Event trigger / webhook subscription | Hasura event triggers (s), Contentful webhooks (s), ABP event bus (s) | MISSING |
| Scheduled / cron operation and one-off scheduled event | Hasura scheduled triggers (s), Laravel scheduled prune | MISSING (Job is on-demand) |
| Asset / media resource | Contentful assets, Strapi media, Doctrine Uploadable | MISSING |
| Scoped settings / feature values (tenant → edition → default) | ABP features | MISSING (Singleton is a single record, not a scope chain) |
| Translations side-collection | Directus Translations (languages + junction) | MISSING |
| Batch endpoint across resources | OData `$batch` | Batch (per-resource; cross-resource MISSING) |
