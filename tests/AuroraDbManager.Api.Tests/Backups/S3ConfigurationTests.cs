using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>Storage selection, S3 settings and the translation of AWS SDK errors; nothing here touches a network.</summary>
public sealed class S3ConfigurationTests
{
    private static BackupOptions Local() => new() { Local = new LocalBackupOptions { RootPath = "/var/lib/aurora/backups" } };

    private static BackupOptions S3(Action<S3BackupOptions>? configure = null)
    {
        var options = new BackupOptions
        {
            StorageType = BackupStorageType.S3,
            S3 = new S3BackupOptions { Bucket = "aurora-backups", Region = "eu-central-1" }
        };
        configure?.Invoke(options.S3);
        return options;
    }

    private static BackupOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => $"Backups:{setting.Key}", setting => (string?)setting.Value))
            .Build();
        var options = new BackupOptions();
        configuration.GetSection(BackupOptions.SectionName).Bind(options);
        return options;
    }

    // --- Selection ----------------------------------------------------------------------------

    [Fact]
    public void StorageType_DefaultsToLocal_WhenNothingIsConfigured()
    {
        Assert.Equal(BackupStorageType.Local, new BackupOptions().StorageType);
        Assert.Equal(BackupStorageType.Local, Bind(("Local:RootPath", "/backups")).StorageType);
        // Having S3 settings around does not select S3.
        Assert.Equal(BackupStorageType.Local, Bind(("S3:Bucket", "b"), ("S3:Region", "us-east-1"), ("S3:Endpoint", "http://localstack:4566")).StorageType);
    }

    [Theory]
    [InlineData("local", BackupStorageType.Local)]
    [InlineData("s3", BackupStorageType.S3)]
    [InlineData("S3", BackupStorageType.S3)]
    public void StorageType_IsReadFromConfiguration(string value, BackupStorageType expected)
    {
        Assert.Equal(expected, Bind(("StorageType", value)).StorageType);
    }

    [Fact]
    public void S3Settings_AreReadFromConfiguration()
    {
        var options = Bind(
            ("StorageType", "s3"), ("S3:Bucket", "aurora-backups"), ("S3:Region", "us-east-1"),
            ("S3:Endpoint", "http://localstack:4566"), ("S3:AccessKey", "test"), ("S3:SecretKey", "test"),
            ("S3:PathStyle", "true"), ("S3:Prefix", "aurora"));

        Assert.Null(options.Validate());
        Assert.Equal("aurora-backups", options.S3.Bucket);
        Assert.Equal("us-east-1", options.S3.Region);
        Assert.Equal("http://localstack:4566", options.S3.Endpoint);
        Assert.True(options.S3.PathStyle);
        Assert.Equal("aurora", options.S3.NormalizedPrefix);
    }

    // --- Validation ---------------------------------------------------------------------------

    [Fact]
    public void LocalStorage_NeedsNoS3Settings()
    {
        Assert.Null(Local().Validate());
    }

    [Fact]
    public void S3Storage_NeedsNoLocalRoot_NoEndpoint_AndNoStaticCredentials()
    {
        // Plain AWS: a bucket, a region, and the SDK's own credential resolution.
        var options = S3();

        Assert.Null(options.Validate());
        Assert.False(options.S3.PathStyle);
    }

    [Theory]
    [InlineData("", "eu-central-1", "Backups:S3:Bucket")]
    [InlineData("  ", "eu-central-1", "Backups:S3:Bucket")]
    [InlineData("aurora-backups", "", "Backups:S3:Region")]
    public void S3Storage_WithoutBucketOrRegion_IsInvalid_AndSaysWhichSettingIsMissing(string bucket, string region, string expectedSetting)
    {
        var error = S3(s3 =>
        {
            s3.Bucket = bucket;
            s3.Region = region;
        }).Validate();

        Assert.NotNull(error);
        Assert.Contains(expectedSetting, error);
    }

    [Theory]
    [InlineData("AKIAEXAMPLE", null)]
    [InlineData(null, "secret")]
    [InlineData("AKIAEXAMPLE", "")]
    public void S3Storage_WithHalfACredentialPair_IsInvalid_WithoutEchoingIt(string? accessKey, string? secretKey)
    {
        var error = S3(s3 =>
        {
            s3.AccessKey = accessKey;
            s3.SecretKey = secretKey;
        }).Validate();

        Assert.NotNull(error);
        Assert.DoesNotContain("AKIAEXAMPLE", error);
        Assert.DoesNotContain("secret", error);
    }

    [Theory]
    [InlineData("localstack:4566")]
    [InlineData("ftp://storage.example")]
    [InlineData("not a url")]
    public void S3Storage_WithAnEndpointThatIsNotAnHttpUrl_IsInvalid(string endpoint)
    {
        Assert.NotNull(S3(s3 => s3.Endpoint = endpoint).Validate());
    }

    [Theory]
    [InlineData("http://localstack:4566")]
    [InlineData("https://s3.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void S3Storage_EndpointIsOptional(string? endpoint)
    {
        Assert.Null(S3(s3 => s3.Endpoint = endpoint).Validate());
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("./a")]
    public void S3Storage_WithAPrefixThatIsNotAPlainKeyPrefix_IsInvalid(string prefix)
    {
        Assert.NotNull(S3(s3 => s3.Prefix = prefix).Validate());
    }

    [Fact]
    public void LocalStorage_IgnoresBrokenS3Settings_AndS3StorageIgnoresAMissingLocalRoot()
    {
        var local = Local();
        local.S3.Endpoint = "not a url";
        Assert.Null(local.Validate());

        var s3 = S3();
        s3.Local.RootPath = "";
        Assert.Null(s3.Validate());
    }

    [Fact]
    public void Startup_WithS3SelectedButNoBucket_FailsAndNamesTheSetting()
    {
        using var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                options.StorageType = BackupStorageType.S3;
                options.S3.Region = "us-east-1";
            }
        };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Backups:S3:Bucket is required", exception.Message);
    }

    [Theory]
    [InlineData(BackupStorageType.Local, typeof(LocalBackupStorage))]
    [InlineData(BackupStorageType.S3, typeof(S3BackupStorage))]
    public void ConfiguredStorageType_AloneDecidesWhichStorageIsUsed(BackupStorageType storageType, Type expected)
    {
        using var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                options.StorageType = storageType;
                // Fully configured either way: what is configured does not decide, the type does.
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
                options.S3.Endpoint = "http://localstack:4566";
            }
        };

        var storage = factory.Services.GetRequiredService<IBackupStorage>();

        Assert.IsType(expected, storage);
        Assert.Equal(storageType, storage.Type);
    }

    // --- The AWS SDK stays in Infrastructure --------------------------------------------------

    [Fact]
    public void ApplicationAndDomain_DoNotMentionAwsSdkTypes()
    {
        var apiAssembly = typeof(BackupOptions).Assembly;
        var inner = apiAssembly.GetTypes()
            .Where(type => type.Namespace is { } ns
                && (ns.StartsWith("AuroraDbManager.Api.Application", StringComparison.Ordinal)
                    || ns.StartsWith("AuroraDbManager.Api.Domain", StringComparison.Ordinal)
                    || ns.StartsWith("AuroraDbManager.Api.Controllers", StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(inner);

        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;

        var mentioned = inner.SelectMany(type => new[] { type.BaseType }
                .Concat(type.GetInterfaces())
                .Concat(type.GetFields(all).Select(field => field.FieldType))
                .Concat(type.GetProperties(all).Select(property => property.PropertyType))
                .Concat(type.GetConstructors(all).SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
                .Concat(type.GetMethods(all).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))))
            .Where(type => type is not null)
            .SelectMany(type => type!.IsGenericType ? type.GetGenericArguments().Append(type) : [type]);

        Assert.DoesNotContain(mentioned, type => (type.Namespace ?? string.Empty).StartsWith("Amazon", StringComparison.Ordinal));
        // And the SDK is really there to be mentioned, in Infrastructure.
        Assert.StartsWith("Amazon", typeof(AmazonS3Exception).Namespace);
    }

    // --- Error translation --------------------------------------------------------------------

    private static AmazonS3Exception S3Exception(string errorCode, HttpStatusCode statusCode) =>
        new("raw-sdk-detail: bucket aurora-prod at https://s3.internal.example, key AKIAEXAMPLE", ErrorType.Sender, errorCode, "REQ123", statusCode);

    public static TheoryData<Exception, string> SdkFailures => new()
    {
        { S3Exception("NoSuchBucket", HttpStatusCode.NotFound), "BACKUP_STORAGE_BUCKET_NOT_FOUND" },
        { S3Exception("AccessDenied", HttpStatusCode.Forbidden), "BACKUP_STORAGE_AUTH_FAILED" },
        { S3Exception("InvalidAccessKeyId", HttpStatusCode.Forbidden), "BACKUP_STORAGE_AUTH_FAILED" },
        { S3Exception("SignatureDoesNotMatch", HttpStatusCode.Forbidden), "BACKUP_STORAGE_AUTH_FAILED" },
        { S3Exception("ExpiredToken", HttpStatusCode.BadRequest), "BACKUP_STORAGE_AUTH_FAILED" },
        { S3Exception("Unknown", HttpStatusCode.Unauthorized), "BACKUP_STORAGE_AUTH_FAILED" },
        { S3Exception("InternalError", HttpStatusCode.InternalServerError), "BACKUP_STORAGE_UNAVAILABLE" },
        { S3Exception("SlowDown", HttpStatusCode.ServiceUnavailable), "BACKUP_STORAGE_UNAVAILABLE" },
        { S3Exception("EntityTooLarge", HttpStatusCode.BadRequest), "BACKUP_STORAGE_UPLOAD_FAILED" },
        { new AmazonServiceException("raw-sdk-detail", new HttpRequestException("Connection refused (s3.internal.example:443)", new SocketException(61))), "BACKUP_STORAGE_UNAVAILABLE" },
        { new HttpRequestException("raw-sdk-detail: name or service not known"), "BACKUP_STORAGE_UNAVAILABLE" },
        { new IOException("raw-sdk-detail: the response ended prematurely"), "BACKUP_STORAGE_UNAVAILABLE" },
        { new AmazonServiceException("raw-sdk-detail", new TimeoutException("raw-sdk-detail")), "BACKUP_STORAGE_TIMEOUT" },
        { new TaskCanceledException("raw-sdk-detail: the request was canceled due to the configured HttpClient.Timeout"), "BACKUP_STORAGE_TIMEOUT" },
        { new AmazonClientException("raw-sdk-detail: Failed to resolve AWS credentials"), "BACKUP_STORAGE_AUTH_FAILED" },
        { new InvalidOperationException("raw-sdk-detail"), "BACKUP_STORAGE_UPLOAD_FAILED" }
    };

    [Theory]
    [MemberData(nameof(SdkFailures))]
    public void SdkFailure_IsTranslatedToASafeCode_WithNothingOfTheSdksMessageForTheClient(Exception sdkFailure, string expectedCode)
    {
        var translated = S3Errors.Translate(sdkFailure);

        Assert.Equal(expectedCode, translated.Code);
        // The SDK's exception is kept for the log...
        Assert.Same(sdkFailure, translated.InnerException);
        // ...and none of what it says reaches the client.
        Assert.DoesNotContain("raw-sdk-detail", translated.Message);
        Assert.DoesNotContain("example", translated.Message);
        Assert.DoesNotContain("AKIAEXAMPLE", translated.Message);
        Assert.DoesNotContain("aurora-prod", translated.Message);
    }

    [Fact]
    public void AlreadyTranslatedFailure_IsPassedThrough()
    {
        var known = new BackupOperationException(BackupErrorCodes.BackupStorageVerificationFailed, "safe");

        Assert.Same(known, S3Errors.Translate(known));
    }
}
