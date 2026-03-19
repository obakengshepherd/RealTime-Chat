using RealtimeChat.Infrastructure.Cache;
using RealtimeChat.Infrastructure.Persistence;
using StackExchange.Redis;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.ChatPolicies());

builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",       failureStatus: HealthStatus.Degraded,  tags: ["cache"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);

builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));

// ── Middleware pipeline ──
// app.UseAuthentication();
// app.UseAuthorization();
// app.UseMiddleware<RedisRateLimitMiddleware>();
// app.MapControllers();
// app.MapHealthEndpoints();

// Redis (singleton multiplexer — shared by pub/sub and presence)
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));

// Use Phase 5 extended cache service (replaces Phase 4 ChatCacheService)
builder.Services.AddSingleton<ChatCacheService>();       // keeps Phase 4 compatibility
builder.Services.AddSingleton<ChatCacheServiceV2>();     // Phase 5 extended version

// Repositories
builder.Services.AddScoped<ChatRepository>();

// Application services
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IPresenceService, PresenceService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!)
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!);