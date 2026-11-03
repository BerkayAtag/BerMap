using Microsoft.AspNetCore.Mvc;

namespace BerMap.Api.Controllers;

public static class ControllerActorExtensions
{
    /// <summary>
    /// Resolves the acting principal for audit records. Until the on-premises
    /// identity provider is wired in, an explicit X-BerMap-Actor header is
    /// accepted for service accounts; otherwise the authenticated name or
    /// "api" is used.
    /// </summary>
    public static string GetActor(this ControllerBase controller)
    {
        if (controller.Request.Headers.TryGetValue("X-BerMap-Actor", out var header) &&
            !string.IsNullOrWhiteSpace(header.ToString()))
        {
            return header.ToString().Trim();
        }

        return controller.User?.Identity?.Name is { Length: > 0 } name ? name : "api";
    }
}
