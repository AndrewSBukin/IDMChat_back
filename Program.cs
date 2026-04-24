
using Asp.Versioning;
using IDMChat.Domain;
using IDMChat.Hubs;
using IDMChat.Middleware;

//using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace IDMChat
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddDbContextPool<ChatDbContext>(options =>
            {
                var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                options.UseSqlServer(connectionString, sqlOptions =>
                {
                    sqlOptions.CommandTimeout(30);
                    sqlOptions.MaxBatchSize(100);
                    sqlOptions.EnableRetryOnFailure(3);
                });
                options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
                options.EnableSensitiveDataLogging(false);
                options.EnableDetailedErrors(false);
            }, poolSize: 256);

            builder.Services.AddControllers();
            builder.Services.AddSignalR(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            });
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "API v1", Version = "v1" });
                //options.SwaggerDoc("v2", new OpenApiInfo { Title = "API v2", Version = "v2" });

                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "Bearer"
                });

                options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                        },
                        Array.Empty<string>()
                    }
                });

                options.DocInclusionPredicate((version, desc) =>
                {
                    var versionAttr = desc.CustomAttributes()
                        .OfType<ApiVersionAttribute>()
                        .FirstOrDefault();

                    if (versionAttr == null) return true; // Методы без версии показывать

                    return versionAttr.Versions.Any(v => $"v{v.MajorVersion}" == version);
                });

                options.OperationFilter<AddVersionParameterFilter>();
            });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowClient",
                    policy => policy.WithOrigins("http://localhost:3000")
                                    .AllowAnyMethod()
                                    .AllowAnyHeader()
                                    .AllowCredentials());
            });

            builder.Services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;  // Возвращать версии в заголовках
                options.ApiVersionReader = new UrlSegmentApiVersionReader(); // Версия в URL
            })
            .AddMvc();


            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = builder.Configuration["Jwt:Issuer"],
                        ValidAudience = builder.Configuration["Jwt:Audience"],
                        RequireExpirationTime = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
                    };

                    // Для SignalR - получение токена из query string
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            var path = context.HttpContext.Request.Path;
                            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chatHub"))
                            {
                                context.Token = accessToken;
                            }
                            return Task.CompletedTask;
                        }
                    };
                });

            builder.Services.AddAuthorization();

            builder.Services.AddSingleton<IBackgroundLogQueue, BackgroundLogQueue>();
            builder.Services.AddHostedService<LogBatchProcessor>(); // background writer

            //builder.Services.Configure<HostOptions>(options =>
            //{
            //    options.ServicesStartConcurrently = true;
            //});
            //var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            //if (!Directory.Exists(webRoot))
            //    Directory.CreateDirectory(webRoot);

            //builder.Environment.WebRootPath = webRoot;


            var app = builder.Build();

            // Configure the HTTP request pipeline.
            //if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
                    //options.SwaggerEndpoint("/swagger/v2/swagger.json", "v2");
                });
            }

            app.UseMiddleware<LoggingMiddleware>();

            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

                    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                    logger.LogError(exception, "Unhandled exception");

                    if (exception is RateLimitException rateLimitEx)
                    {
                        context.Response.StatusCode = 429;
                        context.Response.Headers.RetryAfter = rateLimitEx.RetryAfterSeconds.ToString();

                        await context.Response.WriteAsJsonAsync(new
                        {
                            code = "RATE_LIMIT",
                            message = rateLimitEx.Message,
                            retryAfter = rateLimitEx.RetryAfterSeconds
                        });
                    }
                    else
                    {
                        var (statusCode, code, message) = exception switch
                        {
                            NotFoundException => (404, "NOT_FOUND", exception.Message),
                            ForbiddenException => (403, "FORBIDDEN", exception.Message),
                            ValidationException => (400, "VALIDATION_ERROR", exception.Message),
                            UnauthorizedException => (401, "UNAUTHORIZED", "Требуется авторизация"),
                            //_ => (500, "INTERNAL_ERROR", "Ошибка сервера")
                            _ => (500, "INTERNAL_ERROR", exception?.ToString())
                        };

                        context.Response.StatusCode = statusCode;
                        context.Response.ContentType = "application/json";
                                                
                        await context.Response.WriteAsJsonAsync(new { code, message });
                    }
                });
            });

            app.UseHttpsRedirection();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")),
                RequestPath = "",
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=86400");
                    ctx.Context.Response.Headers.Append("Expires", DateTime.UtcNow.AddDays(1).ToString("R"));
                }
            });

            app.UseCors("AllowClient");

            app.MapControllers();
            app.MapHub<ChatHub>("/chatHub");

            app.Run();
        }
    }

    public class AddVersionParameterFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var version = context.ApiDescription
                .CustomAttributes()
                .OfType<ApiVersionAttribute>()
                .FirstOrDefault()?
                .Versions
                .FirstOrDefault();
            if (version == null) return;

            operation.Parameters ??= new List<OpenApiParameter>();

            // Заменяем параметр версии в пути
            var versionParam = operation.Parameters.FirstOrDefault(p => p.Name == "version" && p.In == ParameterLocation.Path);
            if (versionParam != null)
            {
                versionParam.Example = new Microsoft.OpenApi.Any.OpenApiString(version.MajorVersion?.ToString() ?? "1");
                versionParam.Schema.Default = new OpenApiString(version.MajorVersion?.ToString() ?? "1");
            }
        }
    }
}
