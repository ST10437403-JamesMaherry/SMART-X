using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Attachments;
using SmartX.Application.Commands;
using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Domain.Exceptions;

namespace SmartX.Api.ExceptionHandling;

/// <summary>
/// Converts expected Smart-X Application and Domain exceptions into
/// consistent RFC-style ProblemDetails HTTP responses.
///
/// Central error handling avoids duplicate controller-specific mappings
/// and keeps internal exception details away from external clients.
/// </summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<ApiExceptionHandler> logger)
    {
        _problemDetailsService =
            problemDetailsService ??
            throw new ArgumentNullException(
                nameof(problemDetailsService));

        _logger =
            logger ??
            throw new ArgumentNullException(
                nameof(logger));
    }

    /// <summary>
    /// Handles known application failures using safe ProblemDetails.
    ///
    /// Unexpected failures are logged server-side without exposing
    /// their private exception messages to HTTP clients.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // Do not attempt to write a new response after the HTTP client
        // has explicitly cancelled or disconnected.
        if (exception is OperationCanceledException &&
            httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        ExceptionMapping mapping =
            MapException(exception);

        if (mapping.StatusCode >=
            StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Unhandled Smart-X API exception. TraceId: {TraceId}",
                httpContext.TraceIdentifier);
        }

        var problemDetails =
            new ProblemDetails
            {
                Status = mapping.StatusCode,
                Title = mapping.Title,
                Detail =
                    mapping.ExposeExceptionMessage
                        ? exception.Message
                        : "An unexpected error occurred while processing " +
                          "the request.",
                Instance = httpContext.Request.Path
            };

        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ??
            httpContext.TraceIdentifier;

        httpContext.Response.StatusCode =
            mapping.StatusCode;

        return await _problemDetailsService.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails
            });
    }

    /// <summary>
    /// Preserves the established API error contract.
    ///
    /// P2-05 adds only InvalidRealtimeHistoryQueryException -> 400.
    /// Unknown registered devices continue to produce 404.
    /// Invalid command/Undo operations retain their existing mappings.
    /// </summary>
    private static ExceptionMapping MapException(
        Exception exception)
    {
        return exception switch
        {
            RealtimeDeviceNotFoundException =>
                new ExceptionMapping(
                    StatusCodes.Status404NotFound,
                    "Device not found",
                    true),

            SensorNotFoundException =>
                new ExceptionMapping(
                    StatusCodes.Status404NotFound,
                    "Sensor not found",
                    true),

            InvalidRealtimeHistoryQueryException =>
                new ExceptionMapping(
                    StatusCodes.Status400BadRequest,
                    "Invalid history query",
                    true),

            ActuatorCommandValidationException =>
                new ExceptionMapping(
                    StatusCodes.Status400BadRequest,
                    "Invalid actuator command",
                    true),

            ActuatorCommandConflictException =>
                new ExceptionMapping(
                    StatusCodes.Status409Conflict,
                    "Actuator command conflict",
                    true),

            DuplicateSensorIdentifierException =>
                new ExceptionMapping(
                    StatusCodes.Status409Conflict,
                    "Duplicate sensor identifier",
                    true),

            SensorRegistrationValidationException =>
                new ExceptionMapping(
                    StatusCodes.Status400BadRequest,
                    "Invalid sensor request",
                    true),

            InvalidTelemetryRequestException =>
                new ExceptionMapping(
                    StatusCodes.Status400BadRequest,
                    "Invalid telemetry request",
                    true),

            InvalidSensorAttachmentException =>
                new ExceptionMapping(
                    StatusCodes.Status400BadRequest,
                    "Invalid sensor attachment",
                    true),

            DashboardIssueNotFoundException =>
                new ExceptionMapping(
                    StatusCodes.Status404NotFound,
                    "Dashboard issue not found",
                    true),

            DashboardIssueOperationException =>
                new ExceptionMapping(
                    StatusCodes.Status409Conflict,
                    "Dashboard issue state conflict",
                    true),

            DomainValidationException =>
                new ExceptionMapping(
                    StatusCodes.Status400BadRequest,
                    "Domain validation failed",
                    true),

            _ =>
                new ExceptionMapping(
                    StatusCodes.Status500InternalServerError,
                    "Internal server error",
                    false)
        };
    }

    private sealed record ExceptionMapping(
        int StatusCode,
        string Title,
        bool ExposeExceptionMessage);
}