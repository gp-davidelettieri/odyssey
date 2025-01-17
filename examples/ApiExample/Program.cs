using ApiExample.Onboarding;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Azure.Cosmos;
using Minid;
using O9d.Json.Formatting;
using Odyssey;
using Odyssey.Model;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(
    opt => opt.SerializerOptions.PropertyNamingPolicy = new JsonSnakeCaseNamingPolicy());

builder.Services.AddOdyssey(cosmosClientFactory: _ => CreateClient(builder.Configuration));

var app = builder.Build();

IEventStore eventStore = app.Services.GetRequiredService<IEventStore>();

await eventStore.Initialize();

var repository = new AggregateRepository<Id>(eventStore);

Task<IResult> NotFoundTask = Task.FromResult(Results.NotFound());

app.MapPost("/payments", async (PaymentRequest payment) =>
{
    var initiated = new PaymentInitiated(Id.NewId("pay"), payment.Amount, payment.Currency, payment.Reference);

    var result = await eventStore.AppendToStream(initiated.Id.ToString(), new[] { Map(initiated) }, StreamState.NoStream);

    return result.Match(
        success => Results.Ok(new
        {
            initiated.Id,
            Status = "initiated"
        }),
        unexpected => Results.Conflict()
    );
});

app.MapPost("/payments/{id}/authorize", async (Id id) =>
{
    var authorized = new PaymentAuthorized(id, DateTime.UtcNow);
    // Pass the expected stream revision
    var result = await eventStore.AppendToStream(authorized.Id.ToString(), new[] { Map(authorized) }, StreamState.AtVersion(0));

    return result.Match(
        success => Results.Ok(new
        {
            authorized.Id,
            Status = "authorized"
        }),
        unexpected => Results.Conflict()
    );
});

app.MapPost("/payments/{id}/refunds", async (Id id) =>
{
    var refunded = new PaymentRefunded(id, DateTime.UtcNow);
    // Add the event, regardless of the state/revision of stream
    var result = await eventStore.AppendToStream(refunded.Id.ToString(), new[] { Map(refunded) }, StreamState.StreamExists);

    return result.Match(
        success => Results.Ok(new
        {
            refunded.Id,
            Status = "refunded"
        }),
        unexpected => Results.Conflict()
    );
});

app.MapGet("/events/{id}", async (string id, [Microsoft.AspNetCore.Mvc.FromQuery] ReadDirection? direction) =>
{
    var events = await eventStore.ReadStream(id, direction ?? ReadDirection.Forwards, StreamPosition.Start);
    return Results.Ok(events);
});

app.MapGet("/events/{id}/{version}", async (string id, long version) =>
{
    var eventResult = await eventStore.ReadStreamEvent(id, version);

    return eventResult.Match(
        e => Results.Ok(e),
        notFound => Results.NotFound()
    );
});

var platformId = Id.NewId("acc");

app.MapPost("onboarding/applications", async (InitiateApplicationRequest applicationRequest) =>
{
    var application =
        Application.Initiate(platformId, applicationRequest.Email, applicationRequest.FirstName, applicationRequest.LastName);

    var result = await repository.Save(application);

    return result.Match(success => Results.Ok(new { application.Id }), unexpected => Results.Conflict());
});

app.MapPost("onboarding/applications/{id}/start", async (Id id, HttpContext httpContext) =>
{
    var result = await repository.GetById<Application>(id);

    return await result.Match(
        async application =>
        {
            application.Start(httpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown");
            var result = await repository.Save(application);

            return result.Match(success => Results.Accepted(), unexpected => Results.Conflict());
        },
        _ => NotFoundTask
    );
});

app.MapPost("onboarding/applications/{id}/ubos", async (Id id, InviteUboRequest uboRequest) =>
{
    var result = await repository.GetById<Application>(id);

    return await result.Match(
        async application =>
        {
            application.InviteUbo(uboRequest.FirstName, uboRequest.LastName, uboRequest.Email);
            var result = await repository.Save(application);

            return result.Match(success => Results.Accepted(), unexpected => Results.Conflict());
        },
        _ => NotFoundTask
    );
});

app.Run();

static EventData Map<TEvent>(TEvent @event)
    => new(Guid.NewGuid(), @event!.GetType().Name.ToSnakeCase(), @event);

static CosmosClient CreateClient(IConfiguration configuration)
{
    return new(
        accountEndpoint: configuration["Cosmos:Endpoint"],
        authKeyOrResourceToken: configuration["Cosmos:Token"]
    );
}

record PaymentRequest(int Amount, string Currency, string Reference);
record PaymentInitiated(Id Id, int Amount, string Currency, string Reference);
record PaymentAuthorized(Id Id, DateTime AuthorizedOn);
record PaymentRefunded(Id Id, DateTime RefundedOn);

record InitiateApplicationRequest(string FirstName, string LastName, string Email);
record InviteUboRequest(string FirstName, string LastName, string Email);