using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GlujDrive.Server.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HostOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (HostConnection.IsLocal(context.HttpContext))
        {
            return;
        }

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "This operation is available only on the host computer.",
            Detail = "Open Gluj Drive directly on the Windows PC to change host-only settings."
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
