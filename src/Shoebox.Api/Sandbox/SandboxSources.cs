using System.Diagnostics;

namespace Shoebox.Api.Sandbox;

public static class SandboxSources
{
    public static readonly ActivitySource DefaultActivitySource = new ActivitySource("Sandbox");

    // Saga service ActivitySources - each represents a simulated microservice
    // These are used to create spans that appear as separate services in the APM topology
    public static readonly ActivitySource OrderServiceSource = new ActivitySource("order-service", "1.0.0");
    public static readonly ActivitySource InventoryServiceSource = new ActivitySource("inventory-service", "1.0.0");
    public static readonly ActivitySource PaymentServiceSource = new ActivitySource("payment-service", "1.0.0");
    public static readonly ActivitySource NotificationServiceSource = new ActivitySource("notification-service", "1.0.0");

    /// <summary>
    /// Gets the ActivitySource for a given service name
    /// </summary>
    public static ActivitySource GetServiceSource(string serviceName) => serviceName switch
    {
        "order-service" => OrderServiceSource,
        "inventory-service" => InventoryServiceSource,
        "payment-service" => PaymentServiceSource,
        "notification-service" => NotificationServiceSource,
        _ => DefaultActivitySource
    };

    /// <summary>
    /// All saga service source names for registration
    /// </summary>
    public static readonly string[] SagaServiceSourceNames = new[]
    {
        OrderServiceSource.Name,
        InventoryServiceSource.Name,
        PaymentServiceSource.Name,
        NotificationServiceSource.Name
    };
}