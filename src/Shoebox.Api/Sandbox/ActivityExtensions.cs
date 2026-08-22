using System.Diagnostics;
using OpenTelemetry;

namespace Shoebox.Api.Sandbox;

public static class ActivityExtensions
{
    public static Activity? AddSandboxTag(this Activity? activity)
    {
        if (activity == null)
        {
            return null;
        }

        var sandboxId = Baggage.GetBaggage(SandboxConstants.TagKey);
        activity.SetTag(SandboxConstants.TagKey, sandboxId);

        return activity;
    }
}