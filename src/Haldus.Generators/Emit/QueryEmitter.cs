using System.Text;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Bir <c>MapQuery</c> isteğinin endpoint'ini üretir: istek tipinin özellikleri ayrı sorgu parametreleri
/// olarak bağlanır (camelCase ve isteğe bağlı), istek doğrulanır ve iş mantığına verilir.
/// </summary>
internal static class QueryEmitter
{
    public static string Emit(QueryModel query)
    {
        const string Results = "global::Microsoft.AspNetCore.Http.HttpResults";

        var parameters = string.Concat(query.Parameters.Select(parameter =>
            $"            [global::Microsoft.AspNetCore.Mvc.FromQuery(Name = \"{ResourceEmitter.CamelCase(parameter.Name)}\")] "
            + $"{parameter.NullableType} query{parameter.Name},\n"));

        var assignments = new StringBuilder();
        foreach (var parameter in query.Parameters)
        {
            // Gönderilmeyen dizi parametresi boş dizi olarak bağlanır; o da "gönderilmedi" sayılır.
            var pattern = parameter.IsArray ? "{ Length: > 0 }" : "{ }";
            var local = ResourceEmitter.CamelCase(parameter.Name) + "Value";

            assignments.Append($$"""

                            if (query{{parameter.Name}} is {{pattern}} {{local}})
                            {
                                request.{{parameter.Name}} = {{local}};
                            }

                """);
        }

        return $$"""
            {{ResourceEmitter.Header}}
            using Microsoft.AspNetCore.Builder;

            {{ResourceEmitter.NamespaceLine(query.RequestNamespace)}}
            internal static class {{query.RequestName}}QueryEndpoint
            {
                public static global::Microsoft.AspNetCore.Builder.RouteHandlerBuilder Map(
                    global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,
                    string route,
                    global::System.Func<{{query.RequestType}}, global::System.IServiceProvider, global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<{{query.ResponseType}}>> handler) =>
                    endpoints.MapGet(route, async global::System.Threading.Tasks.Task<{{Results}}.Results<{{Results}}.Ok<{{query.ResponseType}}>, {{Results}}.ValidationProblem>> (
            {{parameters}}            global::Microsoft.AspNetCore.Http.HttpContext httpContext,
                        global::Haldus.IHaldusMessages messages,
                        global::System.Threading.CancellationToken cancellationToken) =>
                    {
                        var request = new {{query.RequestType}}();
            {{assignments}}
                        if (global::Haldus.HaldusValidation.Validate(request, messages) is { } problem)
                        {
                            return problem;
                        }

                        return global::Microsoft.AspNetCore.Http.TypedResults.Ok(
                            await handler(request, httpContext.RequestServices, cancellationToken));
                    });
            }

            """;
    }
}
