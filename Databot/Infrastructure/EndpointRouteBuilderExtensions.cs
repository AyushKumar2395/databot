using System.Diagnostics.CodeAnalysis;

namespace Databot.Infrastructure;

public static class EndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder builder)
    {
        public RouteHandlerBuilder MapGet(Delegate handler, [StringSyntax("Route")] string pattern = "")
            => builder.MapGet(pattern, handler)
                .WithName(handler.Method.Name);

        public RouteHandlerBuilder MapPost(Delegate handler, [StringSyntax("Route")] string pattern = "")
            => builder.MapPost(pattern, handler)
                .WithName(handler.Method.Name);

        public RouteHandlerBuilder MapPut(Delegate handler, [StringSyntax("Route")] string pattern = "")
            => builder.MapPut(pattern, handler)
                .WithName(handler.Method.Name);

        public RouteHandlerBuilder MapDelete(Delegate handler, [StringSyntax("Route")] string pattern = "")
            => builder.MapDelete(pattern, handler)
                .WithName(handler.Method.Name);
    }
}