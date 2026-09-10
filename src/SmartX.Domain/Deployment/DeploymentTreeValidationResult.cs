namespace SmartX.Domain.Deployment;

/// <summary>
/// Contains the outcome of deployment-tree validation.
///
/// FailedPath records the exact route through the deployment tree
/// at which validation failed, making configuration problems easier
/// to diagnose.
/// </summary>
/// <param name="IsValid">Whether the deployment hierarchy is valid.</param>
/// <param name="ErrorCode">Typed reason for a validation failure.</param>
/// <param name="Message">Human-readable validation information.</param>
/// <param name="FailedPath">Hierarchy path at which failure occurred.</param>
/// <param name="FailedDepth">Depth at which failure occurred.</param>
public sealed record DeploymentTreeValidationResult(
    bool IsValid,
    DeploymentValidationErrorCode ErrorCode,
    string Message,
    string? FailedPath,
    int? FailedDepth)
{
    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static DeploymentTreeValidationResult Success()
    {
        return new DeploymentTreeValidationResult(
            true,
            DeploymentValidationErrorCode.None,
            "Deployment tree is valid.",
            null,
            null);
    }

    /// <summary>
    /// Creates a failed validation result with diagnostic information.
    /// </summary>
    public static DeploymentTreeValidationResult Failure(
        DeploymentValidationErrorCode errorCode,
        string message,
        string? failedPath,
        int? failedDepth)
    {
        return new DeploymentTreeValidationResult(
            false,
            errorCode,
            message,
            failedPath,
            failedDepth);
    }
}
