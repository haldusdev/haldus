# Haldus

Archetype ve trait'lerle tekrarlayan backend kodunu derleme zamanında üreten .NET 10 framework'ü.

```csharp
builder.Services.AddHaldus<AppDbContext>();

app.UseHaldus();
app.MapCrud<Product>();
```

```csharp
public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

Bu iki parçadan Haldus şunları üretir:

- `Product` sınıfının `Id` alanını taşıyan diğer yarısı,
- `ProductResponse`, `CreateProductRequest`, `UpdateProductRequest`,
- `GET /products`, `GET /products/{id}`, `POST /products`, `PUT /products/{id}`,
  `DELETE /products/{id}`,
- EF Core model kaydı.

## Archetype'lar

Her archetype bir kaynağın hangi işlemlere sahip olduğunu seçer:

| Archetype | Üretilen uçlar |
|---|---|
| `MapCrud<T>()` | Liste, tekil, oluştur, `PUT`, `PATCH`, sil |
| `MapReadOnly<T>()` | Liste, tekil |
| `MapAppendOnly<T>()` | Liste, tekil, oluştur |
| `MapStore<T>()` | Liste, tekil, `PUT /{key}` (ekle veya güncelle), sil |
| `MapLookup<T>()` | Sayfalamasız liste, tekil, `PUT /{code}`, sil; `Code` ve `Name` alanları |
| `MapSingleton<T>()` | Tek kayıt: `GET`, `PUT`, `PATCH` |
| `MapAction<TRequest, TResponse>()` | `POST`: entity'ye bağlı olmayan komut |
| `MapQuery<TRequest, TResponse>()` | `GET`: entity'ye bağlı olmayan hesap |

`MapAction` ve `MapQuery`'nin iş mantığı bir handler sınıfında durur; Haldus onu bulur ve kaydeder:

```csharp
app.MapQuery<ShippingQuoteRequest, ShippingQuote>();

public sealed class ShippingQuoteHandler(AppDbContext db)
    : IQueryHandler<ShippingQuoteRequest, ShippingQuote>
{
    public async Task<ShippingQuote> HandleAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        // hesap
    }
}
```

Kısa işler için lambda da verilebilir: `app.MapQuery<EchoRequest, EchoResponse>((request, _) => ...)`.

## Trait'ler ve süzme

Davranışlar `Map*` çağrısının devamına zincirlenir:

```csharp
app.MapCrud<Customer>()
    .MultiTenant()
    .Auditable(trackUser: true)
    .SoftDeletable()
    .ConcurrencyChecked()
    .Filterable<CustomerFilter>()
    .Searchable(customer => customer.Name, customer => customer.CreatedAt);
```

| Zincir metodu | Etkisi |
|---|---|
| `.MultiTenant()` | `TenantId` alanı; sorgular kiracıya göre süzülür |
| `.Auditable()` | Oluşturulma ve değiştirilme zamanı, isteğe bağlı kullanıcı |
| `.SoftDeletable()` | Silme geri alınabilir; `restore` ve `purge` uçları |
| `.ConcurrencyChecked()` | `ETag` / `If-Match` ile iyimser eşzamanlılık |
| `.Archivable()` | `archive` ve `unarchive` uçları; arşivlenenler listede gizlenir |
| `.Without(...)` / `.Only(...)` | Üretilecek standart işlemleri seçer |
| `.Filterable<TFilter>()` | Kendi yazdığın filtre sınıfını liste ucunun sorgu parametrelerine bağlar |
| `.Searchable(...)` | `POST /search`: sayılan alanlarda VE/VEYA koşulları ve sıralama |

## Durum

Geliştirme aşamasında. Tasarım kararları ve aşamalar için [CLAUDE.md](CLAUDE.md), kararların
dayandığı araştırma için [reports](reports/) klasörüne bakın.

## Geliştirme

```bash
dotnet build
```

```bash
dotnet test
```

Entegrasyon testleri Docker gerektirir.
