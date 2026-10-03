using System.Net;
using System.Net.Sockets;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using AuroraDbManager.Api.Application.Backups;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups.S3;

/// <summary>
/// <see cref="IS3ObjectClient"/> over the AWS SDK for .NET. Everything is the SDK's own: request
/// signing, endpoint resolution, credential resolution, transient retries and multipart transfer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Endpoint.</b> With <c>Backups:S3:Endpoint</c> set, requests go to that endpoint and are
/// signed for the configured region; this is how any S3-compatible service is used. Without it,
/// the SDK resolves the AWS endpoint from the region.
/// </para>
/// <para>
/// <b>Credentials.</b> With an access key and secret key configured, those are used. Otherwise
/// the SDK's standard resolution applies: environment variables, the shared credentials file, web
/// identity, or the role of the container or machine.
/// </para>
/// <para>
/// The client is created on first use, so an application that stores backups locally never
/// creates one and needs neither S3 settings nor credentials.
/// </para>
/// </remarks>
public sealed class AwsS3ObjectClient(IOptions<BackupOptions> options) : IS3ObjectClient, IDisposable
{
    private const string MetadataPrefix = "x-amz-meta-";

    private readonly Lazy<AmazonS3Client> _client = new(() => CreateClient(options.Value.S3));

    public async Task<S3ObjectInfo?> FindObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _client.Value.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = key }, cancellationToken);
            return new S3ObjectInfo(
                metadata.ContentLength,
                metadata.Headers.ContentType,
                metadata.Metadata.Keys.ToDictionary(
                    name => name.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase) ? name[MetadataPrefix.Length..] : name,
                    name => metadata.Metadata[name],
                    StringComparer.OrdinalIgnoreCase));
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // A HEAD request has no error body, so a missing bucket looks the same as a missing
            // object here. A missing bucket is reported by the upload that follows.
            return null;
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw S3Errors.Translate(exception);
        }
    }

    public async Task UploadFileAsync(S3Upload upload, CancellationToken cancellationToken)
    {
        var request = new TransferUtilityUploadRequest
        {
            BucketName = upload.Bucket,
            Key = upload.Key,
            // Streamed from the file; large files are sent in parts by the SDK.
            FilePath = upload.FilePath,
            ContentType = upload.ContentType
        };

        foreach (var (name, value) in upload.Metadata)
        {
            request.Metadata.Add(name, value);
        }

        try
        {
            using var transfer = new TransferUtility(_client.Value);
            await transfer.UploadAsync(request, cancellationToken);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw S3Errors.Translate(exception);
        }
    }

    public async Task<bool> DownloadFileAsync(string bucket, string key, string filePath, CancellationToken cancellationToken)
    {
        try
        {
            // A plain GET: the object is read and nothing else.
            using var response = await _client.Value.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken);
            await response.WriteResponseStreamToFileAsync(filePath, append: false, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchKey")
        {
            return false;
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw S3Errors.Translate(exception);
        }
    }

    public async Task<bool> ReadObjectAsync(
        string bucket, string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.Value.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken);
            await read(response.ResponseStream, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchKey")
        {
            return false;
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw S3Errors.Translate(exception);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static AmazonS3Client CreateClient(S3BackupOptions settings)
    {
        var config = new AmazonS3Config { ForcePathStyle = settings.PathStyle };

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        else
        {
            config.ServiceURL = settings.Endpoint;
            config.AuthenticationRegion = settings.Region;
        }

        return string.IsNullOrWhiteSpace(settings.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), config);
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}

/// <summary>Turns what the AWS SDK throws into client-safe backup errors.</summary>
public static class S3Errors
{
    private static readonly HashSet<string> AuthErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch", "ExpiredToken", "InvalidToken",
        "AuthorizationHeaderMalformed", "RequestTimeTooSkewed", "AccountProblem", "AllAccessDisabled"
    };

    /// <summary>
    /// The SDK's exception is kept as the inner exception, for logs. Its message can name the
    /// endpoint, the bucket and request ids, so none of it goes into the message for clients.
    /// </summary>
    public static BackupOperationException Translate(Exception exception) => exception switch
    {
        BackupOperationException known => known,

        AmazonS3Exception { ErrorCode: "NoSuchBucket" } => new BackupOperationException(
            BackupErrorCodes.BackupStorageBucketNotFound, "The backup storage bucket does not exist.", exception),

        AmazonS3Exception s3 when AuthErrorCodes.Contains(s3.ErrorCode ?? string.Empty)
            || s3.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new BackupOperationException(
            BackupErrorCodes.BackupStorageAuthFailed, "The backup storage rejected the server's credentials.", exception),

        _ when IsTimeout(exception) => new BackupOperationException(
            BackupErrorCodes.BackupStorageTimeout, "The backup storage did not respond in time.", exception),

        AmazonServiceException service when (int)service.StatusCode >= 500 => Unavailable(exception),

        // An answer from the store that is neither of the above: it was reached and said no.
        AmazonServiceException service when (int)service.StatusCode >= 400 => new BackupOperationException(
            BackupErrorCodes.BackupStorageUploadFailed, "The backup storage rejected the backup.", exception),

        _ when IsNetworkFailure(exception) => Unavailable(exception),

        // No credentials could be resolved at all: the SDK says so without having sent a request.
        AmazonClientException => new BackupOperationException(
            BackupErrorCodes.BackupStorageAuthFailed, "The server has no usable credentials for the backup storage.", exception),

        _ => new BackupOperationException(
            BackupErrorCodes.BackupStorageUploadFailed, "The backup could not be uploaded to the backup storage.", exception)
    };

    private static BackupOperationException Unavailable(Exception exception) =>
        new(BackupErrorCodes.BackupStorageUnavailable, "The backup storage is not available.", exception);

    private static bool IsTimeout(Exception exception) =>
        Chain(exception).Any(wrapped => wrapped is TimeoutException or OperationCanceledException);

    private static bool IsNetworkFailure(Exception exception) =>
        Chain(exception).Any(wrapped => wrapped is HttpRequestException or SocketException or IOException or WebException);

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
