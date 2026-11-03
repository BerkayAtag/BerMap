using Microsoft.AspNetCore.Mvc;

namespace BerMap.Api.Controllers;

public static class ControllerActorExtensions
{
    /// <summary>
    /// Resolves the acting principal for audit records from the authenticated
    /// identity. Client-supplied headers are never trusted for this, otherwise
    /// any caller could write audit entries in someone else's name.
    /// </summary>
    public static string GetActor(this ControllerBase controller)
    {
        return controller.User?.Identity?.Name is { Length: > 0 } name ? name : "anonymous";
    }
}
