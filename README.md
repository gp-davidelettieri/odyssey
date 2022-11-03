# Odyssey

Odyssey enables Azure Cosmos DB to be used as an Event Store.

## Quick Start

### Register Odyssey at startup:

```c#
builder.Services.AddOdyssey(cosmosClientFactory: _ => CreateClient(builder.Configuration));

static CosmosClient CreateClient(IConfiguration configuration)
{
    return new(
        accountEndpoint: configuration["Cosmos:Endpoint"],
        authKeyOrResourceToken: configuration["Cosmos:Token"]
    );
}
```

You can provide a factory to create and register the underlying `CosmosClient` instance as per the above example, otherwise you must register this yourself.

### Take a dependency on `IEventStore`

```c#
app.MapPost("/payments", async (IEventStore eventStore, PaymentRequest payment) =>
{
    var initiated = new PaymentInitiated(Id.NewId("pay"), payment.Amount, payment.Currency, payment.Reference);
    await eventStore.AppendToStream(initiated.Id.ToString(), new[] { Map(initiated) }, StreamState.NoStream);

    return Results.Ok(new
    {
        initiated.Id,
        Status = "initiated"
    });
});
```