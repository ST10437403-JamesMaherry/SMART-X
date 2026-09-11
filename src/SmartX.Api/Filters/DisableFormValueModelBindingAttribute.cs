using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SmartX.Api.Filters;

/// <summary>
/// Prevents ASP.NET Core's default form model binding from consuming
/// multipart request bodies before a streaming controller can process them.
///
/// Streaming upload endpoints read Request.Body directly with MultipartReader.
/// If the normal form value providers remain enabled, MVC may read and buffer
/// the multipart body first, leaving the streaming reader with an exhausted
/// request stream.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = true)]
public sealed class DisableFormValueModelBindingAttribute
    : Attribute,
      IResourceFilter
{
    /// <summary>
    /// Removes form-based value providers before model binding occurs.
    ///
    /// Route, query-string and header model binding remain available,
    /// which allows the attachment endpoint to continue receiving the
    /// sensor ID and attachment category normally.
    /// </summary>
    public void OnResourceExecuting(
        ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var valueProviderFactories =
            context.ValueProviderFactories;

        valueProviderFactories.RemoveType<
            FormValueProviderFactory>();

        valueProviderFactories.RemoveType<
            FormFileValueProviderFactory>();

        valueProviderFactories.RemoveType<
            JQueryFormValueProviderFactory>();
    }

    /// <summary>
    /// No post-processing is required after the request has completed.
    /// </summary>
    public void OnResourceExecuted(
        ResourceExecutedContext context)
    {
        // Intentionally empty.
    }
}