using Api.Endpoints.Todo;
using Api.Filters.Todo;
using Api.Middlewares;
using Api.OpenApi;
using Api.Validation;
using Api.ViewModels.Todo;
using Asp.Versioning.ApiExplorer;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders().AddConsole();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(setup =>
{
    setup.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer"
    });

    setup.OperationFilter<AuthorizationHeaderOperationHeader>();
    setup.OperationFilter<ApiVersionOperationFilter>();
});

builder.Services.AddCarter();
builder.Services.AddHealthChecks();

//Global Exception Handler
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

builder.Services.AddProblemDetails();
builder.Services.AddMediatR(c => { c.RegisterServicesFromAssembly(typeof(Program).Assembly); });
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));


var jwtPolicyName = "jwt";

builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiterOptions.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Microsoft.AspNetCore.RateLimitingMiddleware")
            .LogWarning("OnRejected: {GetUserEndPoint}", GetUserEndPoint(context.HttpContext));

        return new ValueTask();
    };

    limiterOptions.AddPolicy(jwtPolicyName, httpContext =>
    {
        var tokenValue = string.Empty;
        if (AuthenticationHeaderValue.TryParse(httpContext.Request.Headers["Authorization"], out var authHeader))
            tokenValue = authHeader.Parameter;

        var email = string.Empty;
        var rateLimitWindowInMinutes = 5;
        var permitLimitAuthorized = 60;
        var permitLimitAnonymous = 30;
        if (!string.IsNullOrEmpty(tokenValue))
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(tokenValue);
            email = token.Claims.First(claim => claim.Type == "Email").Value;
            var dbContext = httpContext.RequestServices.GetRequiredService<TodoDbContext>();
            var user = dbContext.Users.FirstOrDefault(u => u.Email == email);
            if (user != null)
            {
                permitLimitAuthorized = user.PermitLimit;
                rateLimitWindowInMinutes = user.RateLimitWindowInMinutes;
            }
        }

        return RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = string.IsNullOrEmpty(email) ? permitLimitAnonymous : permitLimitAuthorized,
            Window = TimeSpan.FromMinutes(rateLimitWindowInMinutes),
            QueueLimit = 0
        });
    });
});

static string GetUserEndPoint(HttpContext context)
{
    var tokenValue = string.Empty;
    if (AuthenticationHeaderValue.TryParse(context.Request.Headers["Authorization"], out var authHeader))
        tokenValue = authHeader.Parameter;

    var email = "";
    if (!string.IsNullOrEmpty(tokenValue))
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(tokenValue);
        email = token.Claims.First(claim => claim.Type == "Email").Value;
    }

    return $"User {email ?? "Anonymous"} endpoint:{context.Request.Path}"
           + $" {context.Connection.RemoteIpAddress}";
}

builder.WebHost.UseKestrel(options => options.AddServerHeader = false);
builder.Services.AddHttpContextAccessor();

//Version
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1);
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
    // options.AssumeDefaultVersionWhenUnspecified = true;
    // options.ApiVersionReader = new HeaderApiVersionReader("api-version");
}).AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'V";
    options.SubstituteApiVersionInUrl = true;
});

builder.Services.ConfigureOptions<ConfigureSwaggerGenOptions>();


builder.Services.AddAuthorization();
builder.Services.AddAuthentication("Bearer").AddJwtBearer();

builder.Services.AddDbContextFactory<TodoDbContext>(options =>
    options.UseInMemoryDatabase($"MinimalApiDb-{Guid.NewGuid()}"));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHealthChecks().AddDbContextCheck<TodoDbContext>();
builder.Services.AddScoped<IValidator<TodoItemInput>, TodoItemInput.TodoItemInputValidator>();
builder.Services.AddScoped<IValidator<UserInput>, UserInput.UserInputValidator>();


var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

var versionSet = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1))
    .HasApiVersion(new ApiVersion(2))
    .ReportApiVersions()
    .Build();


var scope = app.Services.CreateScope();
var databaseContext = scope.ServiceProvider.GetService<TodoDbContext>();
if (databaseContext != null) databaseContext.Database.EnsureCreated();

app.MapGroup("api/v{apiVersion:apiVersion}/todoitems/")
    .WithApiVersionSet(versionSet)
    .WithTags("Todo Items")
    .MapApiEndpoints()
    .RequireAuthorization()
    .RequireRateLimiting(jwtPolicyName)
    .WithOpenApi()
    .WithMetadata()
    .AddEndpointFilter(async (efiContext, next) =>
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await next(efiContext);
        stopwatch.Stop();
        var elapsed = stopwatch.ElapsedMilliseconds;
        var response = efiContext.HttpContext.Response;
        response.Headers.TryAdd("X-Response-Time", $"{elapsed} milliseconds");
        return result;
    });

app.MapGet("/health", async (HealthCheckService healthCheckService) =>
    {
        var report = await healthCheckService.CheckHealthAsync();
        return report.Status == HealthStatus.Healthy
            ? Results.Ok(report)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }).WithOpenApi()
    .WithTags("Health")
    .RequireRateLimiting(jwtPolicyName)
    .Produces(200)
    .ProducesProblem(503)
    .Produces(429);

app.UseSwagger();

app.UseSwaggerUI(options =>
{
    IReadOnlyList<ApiVersionDescription> descriptions = app.DescribeApiVersions();
    foreach (var description in descriptions)
    {
        string url = $"/swagger/{description.GroupName}/swagger.json";
        string name = description.GroupName.ToUpperInvariant();

        options.SwaggerEndpoint(url, name);
    }
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapCarter();
app.UseHttpsRedirection();
app.UseExceptionHandler();
app.Run();

//For integration testing
public partial class Program
{
}