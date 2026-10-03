using System.Text.Json;
using System.Text.Json.Serialization;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Application.Jobs.BackupDatabase;
using AuroraDbManager.Api.Application.Jobs.CreateDatabase;
using AuroraDbManager.Api.Application.Jobs.DeleteDatabase;
using AuroraDbManager.Api.Application.Jobs.ProvisionInstance;
using AuroraDbManager.Api.Application.Jobs.RestoreDatabase;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Errors;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using AuroraDbManager.Api.Infrastructure.Databases;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Persistence;
using AuroraDbManager.Api.Infrastructure.Restores;
using AuroraDbManager.Api.Infrastructure.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services
    .AddControllers(options =>
    {
        // Report validation errors under JSON property names ("memoryMb") rather than CLR names.
        options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
    })
    .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = ErrorHandling.ValidationFailed;
        // Leave bodiless 4xx results (e.g. 415) to the status code pages below instead of ProblemDetails.
        options.SuppressMapClientErrors = true;
    });

// The OpenAPI document and non-MVC responses read these options rather than MVC's.
builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("SystemDatabase")
        ?? throw new InvalidOperationException("Connection string 'SystemDatabase' is not configured.")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InstanceService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<RestoreService>();

builder.Services.AddOptions<JobOptions>()
    .Bind(builder.Configuration.GetSection(JobOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.LeaseRenewalIntervalSeconds < options.LeaseDurationSeconds,
        "Jobs:LeaseRenewalIntervalSeconds must be less than Jobs:LeaseDurationSeconds.")
    .ValidateOnStart();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<JobProcessor>();
builder.Services.AddScoped<JobRecovery>();
builder.Services.AddSingleton<InstanceReconciler>();
builder.Services.AddScoped<IJobHandler, ProvisionInstanceHandler>();
builder.Services.AddScoped<IJobHandler, CreateDatabaseHandler>();
builder.Services.AddScoped<IJobHandler, DeleteDatabaseHandler>();
builder.Services.AddScoped<IJobHandler, BackupDatabaseHandler>();
builder.Services.AddScoped<IJobHandler, RestoreDatabaseHandler>();

builder.Services.AddOptions<DockerOptions>()
    .Bind(builder.Configuration.GetSection(DockerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IDockerEngine, DockerEngine>();
builder.Services.AddSingleton<DockerImageResolver>();
builder.Services.AddScoped<IInstanceProvisioner, DockerInstanceProvisioner>();

// Database managers reach an instance's server on the Docker network; see DockerInstanceEndpointResolver.
builder.Services.AddOptions<DatabaseManagerOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseManagerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IInstanceEndpointResolver, DockerInstanceEndpointResolver>();
builder.Services.AddScoped<IDatabaseManager, PostgreSqlDatabaseManager>();
builder.Services.AddScoped<IDatabaseManager, MySqlDatabaseManager>();

// Backups run the engines' dump programs on this machine and keep the artifacts on its filesystem.
builder.Services.AddOptions<BackupOptions>()
    .Bind(builder.Configuration.GetSection(BackupOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>();

// Both storages exist; Backups:StorageType alone decides which one new backups go to. The S3
// client is created on first use, so local storage needs no S3 settings and no AWS credentials.
builder.Services.AddSingleton<IArtifactHasher, Sha256ArtifactHasher>();
builder.Services.AddSingleton<LocalBackupStorage>();
builder.Services.AddSingleton<IS3ObjectClient, AwsS3ObjectClient>();
builder.Services.AddSingleton<S3BackupStorage>();
builder.Services.AddSingleton<IBackupStorage>(services =>
    services.GetRequiredService<IOptions<BackupOptions>>().Value.StorageType switch
    {
        BackupStorageType.S3 => services.GetRequiredService<S3BackupStorage>(),
        _ => services.GetRequiredService<LocalBackupStorage>()
    });
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddScoped<IBackupManager, PostgreSqlBackupManager>();
builder.Services.AddScoped<IBackupManager, MySqlBackupManager>();

// Restores read a backup back from that storage and load it with the engines' own programs.
builder.Services.AddScoped<IRestoreManager, PostgreSqlRestoreManager>();
builder.Services.AddScoped<IRestoreManager, MySqlRestoreManager>();

// Encrypts instance passwords at rest; see ProtectedInstanceSecretStore.
builder.Services.AddDataProtection().SetApplicationName("AuroraDbManager");
builder.Services.AddScoped<IInstanceSecretStore, ProtectedInstanceSecretStore>();

builder.Services.AddHostedService<JobWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler(errorApp => errorApp.Run(ErrorHandling.WriteStatusCodeBodyAsync));
app.UseStatusCodePages(statusCodeContext => ErrorHandling.WriteStatusCodeBodyAsync(statusCodeContext.HttpContext));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    // Enums are exposed as snake_case strings: "postgres", "provisioning", "provision_instance", ...
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    // Numbers must be JSON numbers; "1" is not accepted for cpu.
    options.NumberHandling = JsonNumberHandling.Strict;
}
