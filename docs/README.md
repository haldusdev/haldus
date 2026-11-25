# Haldus belgeleri

Haldus, ASP.NET Core ve EF Core için tekrarlayan backend kodunu derleme zamanında üreten bir .NET 10 framework'üdür.
Bir `partial class` ve tek satırlık bir bildirimden entity'nin geri kalanını, istek ve yanıt tiplerini, Minimal API
uçlarını, doğrulamayı ve EF Core yapılandırmasını üretir.

<!-- example: index -->
```csharp
app.MapCrud<Order>()
    .MultiTenant()
    .Auditable()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Numbered("ORD-{yyyy}-{seq:6}");

public enum OrderStatus { Draft, Placed, Cancelled }

public partial class Order
{
    [Required]
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}
```

## Nereden başlamalı

| İsteğiniz | Okuyun |
|---|---|
| Haldus'u hiç kullanmadım | [Kurulum](getting-started/installation.md), sonra [eğitim: ilk API'niz](getting-started/tutorial.md) |
| Nasıl çalıştığını anlamak istiyorum | [Haldus nasıl çalışır](concepts/how-it-works.md), [ortak kurallar](concepts/conventions.md) |
| Bir özelliği nasıl kullanacağımı arıyorum | Aşağıdaki rehberler veya [bildirim dizini](reference/declarations.md) |
| Bir derleme hatası aldım (`HALDUS0xx`) | [Tanılar](reference/diagnostics.md) |
| Bir durum kodunun nedenini arıyorum | [HTTP sözleşmesi](reference/http.md), [sık sorulan sorular](faq.md) |
| Bir kodlama ajanına Haldus'u anlatacağım | [docs/guide.md](guide.md) (İngilizce, tek dosyalık başvuru) |

## Başlarken

- [Kurulum](getting-started/installation.md): paketler, `AddHaldus`, `UseHaldus`, üretilen kodu görmek.
- [Eğitim: ilk API'niz](getting-started/tutorial.md): proje ve iş takibi API'sini adım adım kurmak.

## Kavramlar

- [Haldus nasıl çalışır](concepts/how-it-works.md): generator, dört katman, entity'nin iki yarısı, kuralların nerede uygulandığı.
- [Ortak kurallar](concepts/conventions.md): adresler, anahtarlar, istek ve yanıt tipleri, listeler, `PUT` ile `PATCH`, hatalar, dil.

## Rehberler

**Kaynağı tanımlamak**

- [Archetype'lar](guides/archetypes.md): `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapLookup`, `MapSingleton`.
- [İlişkiler](guides/relationships.md): referans, ebeveyn altında, ağaç, gömülü değer, çoktan-çoğa bağ, tip, rol, alt tip.
- [Detaylar ve hesaplanan alanlar](guides/details-and-computed-fields.md): sipariş satırları, `IComputed`.
- [Alanlar ve anahtarlar](guides/fields.md): denetim alanları, anahtar biçimi, numaralar, metadata, özel alanlar, çeviriler.
- [Hassas ve şifreli alanlar](guides/sensitive-data.md)
- [Toplanan alanlar](guides/rollups.md): alt kayıtların sayısı ve toplamı.

**Davranış ve iş mantığı**

- [Durum makineleri](guides/state-machines.md): durumlar, geçişler, ek eksenler.
- [İş mantığı](guides/business-logic.md): geçişlerin, aksiyonların ve sorguların mantığı; handler'lar.
- [Onay ve belge](guides/approvals.md)
- [Kaydın yaşam döngüsü](guides/lifecycle.md): geri alınabilir silme, arşiv, eşzamanlılık, süre dolumu, saklama, tekrar serisi.
- [Kayıt işlemleri](guides/record-operations.md): toplu işlemler, kopya, sıra, önizleme, ölçüte göre silme, değişiklik kümeleri.
- [Kayıtlar arası zincir](guides/record-chains.md): ters kayıt, yerine geçen kayıt, üretme, türetme.
- [Sürümler, yayın ve okundu bilgisi](guides/history-and-publishing.md)

**Okuma ve erişim**

- [Listeleme, süzme, sıralama ve arama](guides/querying.md)
- [Kiracılar, kullanıcılar ve yetkilendirme](guides/tenancy-and-ownership.md)
- [Sırlar ve erişim token'ları](guides/secrets.md)

**Altyapı**

- [Dosyalar ve takılabilir kaynaklar](guides/files.md)
- [Arka plan işleri](guides/jobs.md)
- [Bakiyeler](guides/balances.md)
- [Olay yayını](guides/events.md)
- [Modüler yapı](guides/modules.md)
- [Veritabanı ve migration'lar](guides/database.md)
- [Frontend ve mobil istemciler](guides/frontend.md)
- [Uygulamanızı test etmek](guides/testing.md)
- [Üretime hazırlık](guides/production.md)

## Başvuru

- [Bildirim dizini](reference/declarations.md): bütün `Map*`, zincir ve kayıt metotları.
- [Yapılandırma](reference/configuration.md): `HaldusOptions` ve değiştirilebilen servisler.
- [HTTP sözleşmesi](reference/http.md): durum kodları, başlıklar, hata gövdesi.
- [Tanılar](reference/diagnostics.md): `HALDUS001`–`HALDUS010`.
- [Paketler](reference/packages.md)

## Diğer

- [Örnek uygulamalar](samples.md)
- [Sık sorulan sorular](faq.md)
- [Katkıda bulunmak](../CONTRIBUTING.md)
- [Değişiklik günlüğü](../CHANGELOG.md)
