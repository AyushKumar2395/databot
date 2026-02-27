using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Databot.Infrastructure;

public sealed class ApiDocumentationTransformer(IAuthenticationSchemeProvider authenticationSchemeProvider)
    : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var authenticationSchemes = await authenticationSchemeProvider.GetAllSchemesAsync();

        if (authenticationSchemes.Any(x => x.Name == IdentityConstants.BearerScheme))
        {
            Dictionary<string, IOpenApiSecurityScheme> requirements = new()
            {
                ["Bearer"] = new OpenApiSecurityScheme()
                {
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "JWT Authorization header using the Bearer scheme."
                }
            };

            document.Components ??= new();
            document.Components.SecuritySchemes = requirements;
        }

        document.Info = new()
        {
            Title = "Databot API",
            Version = "v1",
            Description =
                "Databot API, with multi model selection, to give the response of metrics from collected data from database.",
            Contact = new OpenApiContact
            {
                Name = "SQL GIG",
                Email = "sales@sqlgig.io",
            },
        };
    }
}