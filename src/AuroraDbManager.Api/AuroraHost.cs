using System.Text.Json;
using System.Text.Json.Serialization;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Application.Jobs.BackupDatabase;
using AuroraDbManager.Api.Application.Jobs.CreateDatabase;
using AuroraDbManager.Api.Application.Jobs.DeleteDatabase;
using AuroraDbManager.Api.Application.Jobs.ProvisionInstance;
using AuroraDbManager.Api.Application.Jobs.RestoreDatabase;
using AuroraDbManager.Api.Application.Monitoring;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Application.Users;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Errors;
using AuroraDbManager.Api.Infrastructure.Auth;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using AuroraDbManager.Api.Infrastructure.Databases;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Monitoring;
using AuroraDbManager.Api.Infrastructure.Persistence;
using AuroraDbManager.Api.Infrastructure.Restores;
using AuroraDbManager.Api.Infrastructure.Scheduling;
using AuroraDbManager.Api.Infrastructure.Secrets;
using AuroraDbManager.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace AuroraDbManager.Api;

/// <summary>
/// How an Aurora host is put together. There are two hosts: this project, which serves the REST
/// API alone, and <c>AuroraDbManager.Web</c>, which serves the Razor Pages UI and the same API.
/// Both are built from the pieces here, so there is one definition of what Aurora's services
/// are, how the API is exposed and how a request travels through it.
/// </summary>
public static class AuroraHost
{
    /// <summary>
    /// Everything that is not about HTTP: the system database, the application services, jobs,
    /// Docker, backups, scheduling, monitoring, users, and the background workers. A host that
    /// calls this is a complete Aurora node, whatever it serves on top.
    /// </summary>
    public static IServiceCollection AddAuroraCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("SystemDatabase")
                ?? throw new InvalidOperationException("Connection string 'SystemDatabase' is not configured.")));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<InstanceService>();
        services.AddScoped<InstanceHealthService>();
        services.AddScoped<DatabaseService>();
        services.AddScoped<BackupService>();
        services.AddScoped<RestoreService>();

        services.AddOptions<JobOptions>()
            .Bind(configuration.GetSection(JobOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.LeaseRenewalIntervalSeconds < options.LeaseDurationSeconds,
                "Jobs:LeaseRenewalIntervalSeconds must be less than Jobs:LeaseDurationSeconds.")
            .ValidateOnStart();
        services.AddSingleton<JobQueue>();
        services.AddScoped<JobService>();
        services.AddScoped<JobProcessor>();
        services.AddScoped<JobRecovery>();
        services.AddSingleton<InstanceReconciler>();
        services.AddScoped<IJobHandler, ProvisionInstanceHandler>();
        services.AddScoped<IJobHandler, CreateDatabaseHandler>();
        services.AddScoped<IJobHandler, DeleteDatabaseHandler>();
        services.AddScoped<IJobHandler, BackupDatabaseHandler>();
        services.AddScoped<IJobHandler, RestoreDatabaseHandler>();

        services.AddOptions<DockerOptions>()
            .Bind(configuration.GetSection(DockerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IDockerEngine, DockerEngine>();
        services.AddSingleton<DockerImageResolver>();
        services.AddScoped<IInstanceProvisioner, DockerInstanceProvisioner>();
        services.AddSingleton<IInstanceRuntimeProbe, DockerInstanceRuntimeProbe>();

        // Database managers reach an instance's server on the Docker network; see DockerInstanceEndpointResolver.
        services.AddOptions<DatabaseManagerOptions>()
            .Bind(configuration.GetSection(DatabaseManagerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IInstanceEndpointResolver, DockerInstanceEndpointResolver>();
        services.AddScoped<IDatabaseManager, PostgreSqlDatabaseManager>();
        services.AddScoped<IDatabaseManager, MySqlDatabaseManager>();

        // Backups run the engines' dump programs on this machine and keep the artifacts on its filesystem.
        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection(BackupOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>();

        // Both storages exist. Backups:StorageType alone decides which one new backups go to: that
        // default is what IBackupStorage resolves to. An existing backup is always handled in the storage
        // its own record names, through the resolver. The S3 client is created on first use, so a server
        // that never touches an S3 backup needs no S3 settings and no AWS credentials.
        services.AddSingleton<IArtifactHasher, Sha256ArtifactHasher>();
        services.AddSingleton<LocalBackupStorage>();
        services.AddSingleton<IS3ObjectClient, AwsS3ObjectClient>();
        services.AddSingleton<S3BackupStorage>();
        services.AddSingleton<IBackupStorage>(services =>
            services.GetRequiredService<IOptions<BackupOptions>>().Value.StorageType switch
            {
                BackupStorageType.S3 => services.GetRequiredService<S3BackupStorage>(),
                _ => services.GetRequiredService<LocalBackupStorage>()
            });
        services.AddSingleton<IBackupStorageResolver, BackupStorageResolver>();
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddScoped<IBackupManager, PostgreSqlBackupManager>();
        services.AddScoped<IBackupManager, MySqlBackupManager>();

        // Restores read a backup back from that storage and load it with the engines' own programs.
        services.AddScoped<IRestoreManager, PostgreSqlRestoreManager>();
        services.AddScoped<IRestoreManager, MySqlRestoreManager>();

        // Encrypts instance passwords at rest; see ProtectedInstanceSecretStore.
        services.AddDataProtection().SetApplicationName("AuroraDbManager");
        services.AddScoped<IInstanceSecretStore, ProtectedInstanceSecretStore>();

        // Scheduled backups: a schedule only decides when; the backup itself is the ordinary job above.
        services.AddSingleton<IScheduleCalculator, CronosScheduleCalculator>();
        services.AddScoped<BackupScheduleService>();
        services.AddScoped<BackupScheduler>();
        services.AddSingleton<SchedulerHeartbeat>();

        // Monitoring observes the services above and runs none of their work: health checks, standard
        // .NET metrics on one meter, and a summary read from the system database. See docs/monitoring.md.
        services.AddSingleton<AuroraMetrics>();
        services.AddScoped<MonitoringSummaryService>();
        services.AddAuroraHealthChecks();

        // Users and signing in, for whichever front end asks: credentials are checked in one place.
        // Background work, the job worker and the scheduler, is the application's own and involves no user.
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // How the host is exposed: trusted proxies, the login rate limit, the request size limit
        // and the response headers. CORS is deliberately not enabled. See docs/security.md.
        services.AddAuroraSecurity(configuration);

        // Before the workers: the first administrator is there, if it can be, when the application starts serving.
        services.AddHostedService<BootstrapAdminInitializer>();
        services.AddHostedService<JobWorker>();
        services.AddHostedService<ScheduledBackupWorker>();

        return services;
    }

    /// <summary>
    /// The REST API: its controllers, its JSON conventions, its OpenAPI document, and JWT bearer
    /// authentication with the role policies. Every endpoint needs a signed-in user unless it
    /// says otherwise; which role may do what is in <see cref="AuroraPolicies"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="defaultScheme">
    /// The scheme requests are authenticated with unless something says otherwise: bearer tokens
    /// for a host that is only the API. A host with more than the API passes a scheme of its own.
    /// </param>
    /// <returns>The authentication builder, for a host that has further schemes to add.</returns>
    public static AuthenticationBuilder AddAuroraApi(this IServiceCollection services, string defaultScheme = JwtBearerDefaults.AuthenticationScheme)
    {
        var mvc = services
            .AddControllers(options =>
            {
                // Report validation errors under JSON property names ("memoryMb") rather than CLR names.
                options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
            })
            .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions))
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = ErrorHandling.ValidationFailed;
                // Leave bodiless 4xx results (e.g. 415) to the status code pages instead of ProblemDetails.
                options.SuppressMapClientErrors = true;
            });

        // The controllers are in this assembly, also when another project is the host.
        var api = typeof(AuroraHost).Assembly;
        if (!mvc.PartManager.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == api))
        {
            mvc.AddApplicationPart(api);
        }

        // The OpenAPI document and non-MVC responses read these options rather than MVC's.
        services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        services.AddOpenApi(options => options.AddBearerAuthentication());

        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerSetup>();
        services.AddAuthorization(AuroraPolicies.Configure);
        return services.AddAuthentication(defaultScheme).AddJwtBearer();
    }

    /// <summary>
    /// The start of every host's pipeline: the client a trusted proxy reports, the request id,
    /// and the security headers. What a host does about errors comes after this, and differs.
    /// </summary>
    /// <param name="app">The host.</param>
    /// <param name="contentSecurityPolicy">
    /// The policy for a response, for a host that serves more than JSON; null for the API's own,
    /// which allows nothing.
    /// </param>
    public static void UseAuroraEdge(this WebApplication app, Func<HttpContext, string>? contentSecurityPolicy = null)
    {
        // Created now rather than with the first job, so the gauges of pending and running jobs are there from the start.
        app.Services.GetRequiredService<AuroraMetrics>();

        // First of all, so that what follows, logging and rate limiting included, sees the client a trusted proxy reports.
        app.UseAuroraForwardedHeaders();
        // Then, so every request has its id before anything is logged for it, errors included.
        app.UseMiddleware<RequestCorrelationMiddleware>();
        app.UseAuroraSecurityHeaders(contentSecurityPolicy);
    }

    /// <summary>Answers the API's failures with its standard JSON error body: unhandled exceptions and bodiless error statuses alike.</summary>
    public static void UseAuroraApiErrors(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(errorApp => errorApp.Run(ErrorHandling.WriteExceptionBodyAsync));
        app.UseStatusCodePages(statusCodeContext => ErrorHandling.WriteStatusCodeBodyAsync(statusCodeContext.HttpContext));
    }

    /// <summary>The request size limit, HTTPS, and then who the caller is and what they may do.</summary>
    public static void UseAuroraRequestPipeline(this WebApplication app)
    {
        app.UseAuroraRequestLimits();
        app.UseAuroraHttps();

        app.UseAuthentication();
        // After routing has chosen the endpoint: only signing in has a limit, and a bucket of its own.
        app.UseRateLimiter();
        app.UseAuthorization();
    }

    /// <summary>The API's endpoints: the health checks, the controllers and, in development, the OpenAPI document.</summary>
    public static void MapAuroraApi(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
        }

        app.MapAuroraHealthChecks();
        app.MapControllers();
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        // Enums are exposed as snake_case strings: "postgres", "provisioning", "provision_instance", ...
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        // Numbers must be JSON numbers; "1" is not accepted for cpu.
        options.NumberHandling = JsonNumberHandling.Strict;
    }
}
