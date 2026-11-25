---
name: Hata bildirimi
about: Haldus'un beklendiği gibi çalışmadığı bir durum
labels: bug
---

## Ne oldu

<!-- Kısaca: ne yaptınız, ne bekliyordunuz, ne oldu. -->

## Yeniden üretmek

<!-- Sorunu gösteren en küçük bildirim. Entity sınıfları ve Map* zinciri yeterlidir. -->

```csharp
app.MapCrud<Order>()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft);
```

<!-- Çalışma zamanı hatasıysa: gönderilen istek ve alınan yanıt (durum kodu ve gövde). -->

```http
POST /orders
```

<!-- Derleme hatasıysa: tanının kodu ve mesajı (HALDUS0xx). Üretilen kodla ilgiliyse obj/Generated altındaki ilgili dosyanın parçası. -->

## Ortam

- Haldus sürümü veya commit:
- .NET SDK (`dotnet --version`):
- PostgreSQL sürümü:
- İşletim sistemi:
