// See https://aka.ms/new-console-template for more information

using Amazon;
using Amazon.EventBridge.Model;
using AwsEventManager;
using CreateConnectionResponse = Amazon.EventBridge.Model.CreateConnectionResponse;

/*var stsClient = new AmazonSecurityTokenServiceClient();
var assumeRoleResponse = await stsClient.AssumeRoleAsync(new AssumeRoleRequest
{
    RoleArn = "arn:aws:iam::848362861133:role/csharp-app-role",
    RoleSessionName = "CSharpAppSession"
});
var credentials = new Amazon.Runtime.SessionAWSCredentials(
    assumeRoleResponse.Credentials.AccessKeyId,
    assumeRoleResponse.Credentials.SecretAccessKey,
    assumeRoleResponse.Credentials.SessionToken
);*/

var createEventBridgeConnection = true;
var createEventBridgeDestination = true;
var createEventBridgeEventBus = true;
var createEventBridgeEventBusRule = true;
var createEventBridgeScheduler = true;

// aws region
var region = RegionEndpoint.USEast2;

var identityService = new IdentityService(region);
var credentials = await identityService.GetAssumedRoleSessionCredentials("csharp-app-role", "CSharpAppSession");

//***** EventBridge Connection *****//
var awsEventBridgeService = new EventBridgeService(region, credentials.Creds);
var connectionResponse = new CreateConnectionResponse();
if (createEventBridgeConnection)
{
    try
    {
        connectionResponse =
            await awsEventBridgeService.GetOrCreateEventBridgeConnectionAsync(
                "event-bridge-connection",
                CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating or checking connection: {ex.Message}");
    }
}

//***** EventBridge Destination *****//
CreateApiDestinationResponse apiDestinationResponse = null!;
var apiDestinationName = "event-bridge-api-dest";
if (createEventBridgeDestination)
{
    try
    {
        apiDestinationResponse = await awsEventBridgeService.GetOrCreateEventBridgeApiDestinationAsync(
            connectionResponse.ConnectionArn,
            apiDestinationName, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating or checking API Destination: {ex.Message}");
    }
}

//***** Event For Api Destination -- Rule, Target, and Scheduler Service Bus *****//
try
{
    // creates service bus
    string eventBusName = "invoke-api-dest-rule-bus";
    if (createEventBridgeEventBus)
    {
        await awsEventBridgeService.CreateEventBus(
            eventBusName,
            CancellationToken.None);
    }

    // creates event bridge rule
    string ruleName = "invoke-api-dest-rule";
    if (createEventBridgeEventBusRule)
    {
        await awsEventBridgeService.CreateEventBridgeRuleAsync(
            ruleName,
            credentials.Role.AssumedRoleUser.Arn,
            apiDestinationResponse.ApiDestinationArn,
            eventBusName,
            CancellationToken.None);
    }

    // creates event bridge scheduler
    if (createEventBridgeScheduler)
    {
        var scheduleName = $"event-schedule-bus-{Guid.NewGuid().ToString().Split('-')[0]}";
        await awsEventBridgeService.CreateSchedulerEventForEventBus(
            scheduleName,
            credentials.Role.AssumedRoleUser.Arn,
            eventBusName,
            CancellationToken.None);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error creating schedule: {ex.Message}");
}