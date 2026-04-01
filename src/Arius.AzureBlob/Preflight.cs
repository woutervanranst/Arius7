using Arius.Core.Shared.Storage;

namespace Arius.AzureBlob;

/// Classifies the type of error encountered during the preflight connectivity check.
/// </summary>
public enum PreflightErrorKind
{
    /// <summary>The container does not exist (HTTP 404).</summary>
    ContainerNotFound,

    /// <summary>The credential was valid but access was denied (HTTP 403).</summary>
    AccessDenied,

    /// <summary>The credential could not be obtained (e.g. Azure CLI not logged in).</summary>
    CredentialUnavailable,

    /// <summary>An unexpected Azure SDK error occurred.</summary>
    Other,
}

/// <summary>
/// Thrown by <see cref="IBlobService.GetContainerServiceAsync(string, PreflightMode, CancellationToken)"/> when a known
/// connectivity or authentication failure is detected during the preflight check.
/// Carries structured fields so each host can format its own user-facing messages.
/// </summary>
public sealed class PreflightException : Exception
{
    public PreflightException(
        PreflightErrorKind errorKind,
        string             authMode,
        string             accountName,
        string             containerName,
        int?               statusCode  = null,
        Exception?         inner       = null)
        : base(
            $"Preflight check failed: {errorKind} on account '{accountName}', container '{containerName}' (auth: {authMode})",
            inner)
    {
        ErrorKind     = errorKind;
        AuthMode      = authMode;
        AccountName   = accountName;
        ContainerName = containerName;
        StatusCode    = statusCode;
    }

    /// <summary>Structured classification of the error.</summary>
    public PreflightErrorKind ErrorKind { get; }

    /// <summary><c>"key"</c> when a shared-key credential was used; <c>"token"</c> for token credentials.</summary>
    public string AuthMode { get; }

    /// <summary>The storage account name passed to the factory.</summary>
    public string AccountName { get; }

    /// <summary>The container name passed to the factory.</summary>
    public string ContainerName { get; }

    /// <summary>HTTP status code from the underlying <c>RequestFailedException</c>, or <c>null</c> for credential errors.</summary>
    public int? StatusCode { get; }
}
