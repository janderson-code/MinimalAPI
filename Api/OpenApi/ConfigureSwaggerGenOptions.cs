using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Options;

namespace Api.OpenApi;

public class ConfigureSwaggerGenOptions : IConfigureNamedOptions<SwaggerGenOptions>
{
    private readonly IApiVersionDescriptionProvider _provider;

    public ConfigureSwaggerGenOptions(IApiVersionDescriptionProvider provider)
    {
        _provider = provider;
    }

    public void Configure(SwaggerGenOptions options)
    {
        foreach (var description in _provider.ApiVersionDescriptions)
        {
            var openApiInfo = new OpenApiInfo
            {
                Description =
                    "ASP.NET Core 8.0 - Minimal API Example - Todo API implementation using ASP.NET Core Minimal API," +
                    "Entity Framework Core, Token authentication, Versioning, Unit Testing and Open API.",
                Title = $"Todo Api - v{description.ApiVersion}",
                Version = description.ApiVersion.ToString(),
                Contact = new OpenApiContact
                {
                    Name = "Janderson Gonçalves",
                    Url = new Uri("https://github.com/janderson-code")
                }

            };
            options.SwaggerDoc(description.GroupName,openApiInfo);
        }
    }

    public void Configure(string? name, SwaggerGenOptions options)
    {
        Configure(options);
    }
}