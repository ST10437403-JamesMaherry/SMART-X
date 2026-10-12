using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;


using SmartX.Application.Attachments;
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
/// Keeping this translation in one location prevents controllers from
/// duplicating exception handling and ensures clients receive predictable
/// status codes throughout the API.
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
    /// Maps known Smart-X failures to safe client-facing ProblemDetails.
    ///
    /// Unexpected failures are logged but their internal exception messages
    /// are deliberately hidden from API clients.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // A disconnected request should not be transformed into another HTTP
        // response after the caller has already abandoned the connection.
        if (exception is OperationCanceledException &&
            httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var mapping =
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