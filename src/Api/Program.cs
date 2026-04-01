using StackExchange.Redis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using FluentValidation.AspNetCore;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.OpenApi.Models;
using Dapper;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ════════════════════════════════════════════════════════════════════════════
// CONFIGURATION & SERVICES
// ════════════════════════════════════════════════════════════════════════════

builder.Logging.SetMinimumLevel(LogLevel.Information);
if (builder.Environment.IsDevelopment())
    builder.Logging.AddConsole();

// Controllers & JSON
builder.Services.AddControllers()
    .AddJsonOptions(opt =>
    {
        opt.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        opt.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    })
    .AddFluentValidation(fv =>
    {
        fv.RegisterValidatorsFromAssembly(typeof(Program).Assembly);
    });

// Authentication — JWT Bearer or disabled for dev
var disableAuth = builder.Configuration.GetValue<bool>("DisableAuthentication");
if (!disableAuth)
{
    builder.Services.AddAuthentication("Bearer")
        .AddJwtBearer("Bearer", options =>
        {
            options.Authority = builder.Configuration["JwtAuthority"] ?? "https://your-auth-server.example.com";
            options.Audience = builder.Configuration["JwtAudience"] ?? "chat-api";
            options.TokenValidationParameters = new()
            {
                ValidateAudience = true,
                ValidateIssuer = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };
        });
}
else
{
    builder.Services.AddAuthentication("Development")
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>("Development", _ => { });
}

builder.Services.AddAuthorization();

// Swagger / OpenAPI
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Real-Time Chat API", Version = "v1" });
    if (!disableAuth)
    {
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT"
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                []
            }
        });
    }
});

// Rate limiting
builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.ChatPolicies());

// Redis (singleton multiplexer)
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));

// Cache services
// builder.Services.AddSingleton<ChatCacheService>();
// builder.Services.AddSingleton<ChatCacheServiceV2>();

// Repositories
// builder.Services.AddScoped<ChatRepository>();

// Application services (interfaces defined in ChatServices.cs)
// builder.Services.AddScoped<IMessageService, MessageService>();
// builder.Services.AddScoped<IConversationService, ConversationService>();
// builder.Services.AddScoped<IPresenceService, PresenceService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Degraded, tags: ["cache"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);
    // .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!)
    // .AddRedis(builder.Configuration.GetConnectionString("Redis")!);

builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));

builder.Services.AddScoped<HealthCheckService>();

// CORS (for WebSocket support)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// ════════════════════════════════════════════════════════════════════════════
// BUILD & MIDDLEWARE PIPELINE
// ════════════════════════════════════════════════════════════════════════════

var app = builder.Build();

// Request logging & correlation ID
app.Use(async (context, next) =>
{
    var correlationId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
    context.Items["CorrelationId"] = correlationId;
    await next();
});

// Exception handling
app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

// HTTPS redirect (only in production)
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Swagger (only in development)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS
app.UseCors("AllowAll");

// Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// Rate limiting
app.UseMiddleware<RedisRateLimitMiddleware>();

// Health checks
app.MapHealthEndpoints();

// Controllers
app.MapControllers();

// ════════════════════════════════════════════════════════════════════════════
// RUN
// ════════════════════════════════════════════════════════════════════════════

app.Run();

// ════════════════════════════════════════════════════════════════════════════
// ═══════════════════════ TYPE DECLARATIONS (AFTER app.Run()) ════════════════
// ════════════════════════════════════════════════════════════════════════════

#pragma warning disable CA1050 // Declare types in namespaces (required for top-level statements)

// ════════════════════════════════════════════════════════════════════════════
// EXTENSION METHODS
// ════════════════════════════════════════════════════════════════════════════

public static class ClaimsPrincipalExt
{
    public static string GetUserId(this ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(id)) throw new UnauthorizedAccessException("User ID claim missing.");
        return id;
    }
}

public static class HealthCheckExt
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => true,
            AllowCachingResponses = false
        });
        
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("startup")
        });
        
        app.MapHealthChecks("/health/detail", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = async (ctx, report) => {
                ctx.Response.ContentType = "application/json";
                var json = System.Text.Json.JsonSerializer.Serialize(new { report.Status, report.Entries });
                await ctx.Response.WriteAsync(json);
            }
        });
        
        return app;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// DEVELOPMENT AUTHENTICATION HANDLER
// ════════════════════════════════════════════════════════════════════════════

public class DevelopmentAuthenticationHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    public DevelopmentAuthenticationHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock)
        : base(options, logger, encoder, clock) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers["X-User-Id"].FirstOrDefault() ?? "dev-user-123";
        var userName = Request.Headers["X-User-Name"].FirstOrDefault() ?? "Dev User";

        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, userName),
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, Scheme.Name);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

// ════════════════════════════════════════════════════════════════════════════
// GLOBAL EXCEPTION HANDLER MIDDLEWARE
// ════════════════════════════════════════════════════════════════════════════

public class GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex, logger);
        }
    }

    private static async Task HandleExceptionAsync(
        HttpContext context, Exception exception, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        var correlationId = context.Items["CorrelationId"]?.ToString() ?? context.TraceIdentifier;

        var response = context.Response;
        response.ContentType = "application/json";

        var (statusCode, message, details) = exception switch
        {
            NotConversationMemberException ex => (403, "Access Denied", ex.Message),
            MessageNotFoundException ex => (404, "Not Found", ex.Message),
            MessageDeleteException ex => (400, "Invalid Operation", ex.Message),
            UnauthorizedAccessException ex => (401, "Unauthorized", ex.Message),
            ArgumentException ex => (400, "Invalid Request", ex.Message),
            InvalidOperationException ex => (500, "Server Error", ex.Message),
            _ => (500, "Internal Server Error", "An unexpected error occurred.")
        };

        logger.LogError(exception, "Unhandled {Type}: {Message} | CorrelationId: {CorrelationId}",
            exception.GetType().Name, exception.Message, correlationId);

        response.StatusCode = statusCode;

        var errorResponse = new
        {
            status = statusCode,
            error = new
            {
                message = message,
                details = details,
                timestamp = DateTimeOffset.UtcNow,
                correlationId = correlationId
            }
        };

        await response.WriteAsJsonAsync(errorResponse);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// CUSTOM CHAT EXCEPTIONS
// ════════════════════════════════════════════════════════════════════════════

public class NotConversationMemberException(string userId, string convId)
    : Exception($"User '{userId}' is not a member of conversation '{convId}'.");

public class MessageNotFoundException(string messageId)
    : Exception($"Message '{messageId}' not found.");

public class MessageDeleteException(string messageId)
    : Exception($"Message '{messageId}' could not be deleted or not owned by you.");

// ════════════════════════════════════════════════════════════════════════════
// REDIS RATE LIMITING MIDDLEWARE
// ════════════════════════════════════════════════════════════════════════════

public record RateLimitRule(string Method, string PathPattern, int RequestsPerMinute);

public class RedisRateLimitMiddleware(RequestDelegate next, IConnectionMultiplexer redis, IEnumerable<RateLimitRule> rules, ILogger<RedisRateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var rule = MatchRule(context.Request);

        if (rule is not null)
        {
            var userId = context.User?.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "anonymous";
            var key = $"rate_limit:{userId}:{rule.PathPattern}";
            var db = redis.GetDatabase();

            var count = db.StringIncrement(key);
            if (count == 1)
                db.KeyExpire(key, TimeSpan.FromMinutes(1));

            if (count > rule.RequestsPerMinute)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Rate limit exceeded",
                    limit = rule.RequestsPerMinute,
                    remaining = 0
                });
                return;
            }

            context.Items["RateLimitRemaining"] = rule.RequestsPerMinute - count;
        }

        await next(context);
    }

    private RateLimitRule? MatchRule(HttpRequest request)
    {
        return rules.FirstOrDefault(r => 
            r.Method == request.Method && 
            request.Path.StartsWithSegments(r.PathPattern));
    }
}

public static class RateLimitPolicies
{
    public static IEnumerable<RateLimitRule> ChatPolicies() =>
    [
        new RateLimitRule("POST", "/api/v1/conversations", 10),
        new RateLimitRule("POST", "/api/v1/conversations", 60),
        new RateLimitRule("GET", "/api/v1", 120)
    ];
}

// ════════════════════════════════════════════════════════════════════════════
// CUSTOM HEALTH CHECKS
// ════════════════════════════════════════════════════════════════════════════

public class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.PingAsync();
            return HealthCheckResult.Healthy("Redis is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"Redis degraded: {ex.Message}");
        }
    }
}

public class PostgreSqlHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL unhealthy: {ex.Message}");
        }
    }
}

#pragma warning restore CA1050
