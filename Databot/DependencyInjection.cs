namespace Databot;

public static class DependencyInjection
{
    public static void AddWebServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer<ApiDocumentationTransformer>());
    }
}