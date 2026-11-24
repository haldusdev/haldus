namespace Haldus;

// Bir entity'ye bağlı olmayan işlemlerin iş mantığı. Haldus bu arayüzleri uygulayan sınıfları derleme
// zamanında bulur ve bağımlılık enjeksiyonuna kendisi kaydeder.

/// <summary>Yan etkili, sonuç döndüren komut: <c>app.MapAction&lt;TRequest, TResponse&gt;()</c>.</summary>
public interface IActionHandler<in TRequest, TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Yan etkili, sonuç döndürmeyen komut: <c>app.MapAction&lt;TRequest&gt;()</c>.</summary>
public interface IActionHandler<in TRequest>
{
    Task HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Yan etkisiz hesap: <c>app.MapQuery&lt;TRequest, TResponse&gt;()</c>.</summary>
public interface IQueryHandler<in TRequest, TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
