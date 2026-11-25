# Örnek uygulamalar

Depoda altı örnek vardır. Haldus'u ilk kez görüyorsanız eğitimin uygulamasıyla başlayın; bir bildirimin gerçek bir
kullanımını arıyorsanız örnek mağazaya bakın.

| Örnek | Ne için | Nasıl çalıştırılır |
|---|---|---|
| [Proje ve iş takibi](../samples/Haldus.Sample.Tracker) | [Eğitimin](getting-started/tutorial.md) bitmiş hali. Küçük, migration'larıyla birlikte. | Kendi README'sinde. |
| [Örnek mağaza](../samples/Haldus.Sample.Shop) | Bildirimlerin hemen hepsi, gerçek bir alanda; 80'i aşkın kaynak. Entegrasyon testleri buna karşı çalışır. | Kendi README'sinde. |
| [Modüler örnek](../samples/Haldus.Sample.Modular) | Özelliklerin ayrı sınıf kütüphaneleri olduğu yapı; ortak ve modül başına bağlam. | Kendi README'sinde. |
| [TypeScript istemcisi](../samples/Haldus.Sample.Shop.Client) | Mağazanın şemasından orval ile üretilen tipler ve TanStack Query hook'ları. Uygulama değildir. | `pnpm generate:api`, `pnpm typecheck` |
| [Yönetim paneli](../samples/Haldus.Sample.Shop.Web) | Vite + React + shadcn/ui ile mağazanın paneli: ürünler, kategoriler, siparişlerin iş akışı, giriş. | Kendi README'sinde. |
| [Mobil uygulama](../samples/Haldus.Sample.Shop.Mobile) | Expo + React Native ile ürünler, favoriler ve sepet. | Kendi README'sinde. |

Örneklerdeki bağlantı dizeleri ve parolalar kaynak kodda durmaz; ortam değişkenleriyle veya kullanıcı sırlarıyla
verilir.

## Bir özelliği örnekte bulmak

| Konu | Nerede |
|---|---|
| Durum makinesi, geçişler, numaralama | `samples/Haldus.Sample.Tracker/Program.cs`; daha geniş hali `samples/Haldus.Sample.Shop/Features/Sales` (siparişler). |
| Süzme, arama, sıralama | `samples/Haldus.Sample.Tracker/IssueFilter.cs`; `samples/Haldus.Sample.Shop/Features/Catalog` (ürünler). |
| Kullanıcıya ait kaynaklar ve favoriler | `samples/Haldus.Sample.Shop/Features/Account`. |
| Bakiyeler ve tutmalar | `samples/Haldus.Sample.Shop/Features/Stock`. |
| Onay | `samples/Haldus.Sample.Shop/Features/Supply` (satın alma talepleri). |
| Sırlar ve token ile erişim | `samples/Haldus.Sample.Shop/Features/Integrations`. |
| Tip, rol, alt tip | `samples/Haldus.Sample.Shop/Features/Engagement`. |
| Modüller arası referans | `samples/Haldus.Sample.Modular`. |
| Kimlik doğrulama ve yetki kuralı | `samples/Haldus.Sample.Shop/Features/Auth`. |

## Ayrıca bakın

- [Eğitim: ilk API'niz](getting-started/tutorial.md)
- [Frontend ve mobil istemciler](guides/frontend.md)
