using System.Diagnostics;
using OpenTelemetry;

namespace Shoebox.Api.Session;

public class ShoeboxMiddleware : IMiddleware
{
    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var shoeboxId = context.Request.GetShoeboxId();
        if (string.IsNullOrWhiteSpace(shoeboxId))
        {
            return next(context);
        }

        Baggage.SetBaggage(ShoeboxConstants.TagKey, shoeboxId);
        Activity.Current?.SetTag(ShoeboxConstants.TagKey, shoeboxId);


        return next(context);
    }
}