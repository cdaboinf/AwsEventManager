// See https://aka.ms/new-console-template for more information

using Amazon;
using Amazon.EventBridge.Model;
using Amazon.IdentityManagement.Model;
using Amazon.SQS.Model;
using AwsEventManager;
using CreateConnectionResponse = Amazon.EventBridge.Model.CreateConnectionResponse;

var createApiDestinationEvent = false;

// aws region
var region = RegionEndpoint.USEast2;
var queueName = "dlq-queue";

// sqs test connection

# region QUEUE

var queueService = new SqsService();
try
{
    var queueInfo = await queueService.GetQueueInfoAsync(queueName, region, default);
    Console.WriteLine("Queue Attributes:");
    foreach (var kvp in queueInfo.Attributes)
    {
        Console.WriteLine($" - {kvp.Key}: {kvp.Value}");
    }
}
catch (QueueDoesNotExistException ex)
{
    Console.WriteLine($"Queue '{queueName}' does not exist. {ex}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error retrieving queue information: {ex.Message}");
}

# endregion

//***** EventBridge Connection *****//
var awsEventBridgeService = new EventBridgeService(region);
var connectionResponse = new CreateConnectionResponse();
try
{
    connectionResponse =
        await awsEventBridgeService.GetOrCreateEventBridgeConnectionAsync("event-bridge-connection", region, default);
}
catch (Exception ex)
{
    Console.WriteLine($"Error creating or checking connection: {ex.Message}");
}

//***** EventBridge Destination *****//
CreateApiDestinationResponse apiDestinationResponse = null!;
var apiDestinationName = "event-bridge-api-dest";
try
{
    apiDestinationResponse = await awsEventBridgeService.GetOrCreateEventBridgeApiDestinationAsync(
        connectionResponse.ConnectionArn,
        apiDestinationName, default);
}
catch (Exception ex)
{
    Console.WriteLine($"Error creating or checking API Destination: {ex.Message}");
}

//***** IAM Role ARN *****//
GetRoleResponse iamEventSchedulerRole = null;
GetRoleResponse iamEventRuleRole = null;
try
{
    var roleService = new IdentityService(region);

    var roleRuleName = "scheduler-invoke-api-destination-role";
    iamEventRuleRole = await roleService.GetRoleAsync(roleRuleName, default);

    var roleEventName = "scheduler-putevents-sqs-role";
    iamEventSchedulerRole = await roleService.GetRoleAsync(roleEventName, default);
}
catch (Exception ex)
{
    Console.WriteLine($"Error retrieving role: {ex.Message}");
}

//***** EventBridge Scheduler *****//
if (createApiDestinationEvent)
{
//***** Event For Api Destination *****//
    try
    {
        await awsEventBridgeService.CreateSchedulerEventForDestinationApiAsync(apiDestinationName, iamEventSchedulerRole.Role.Arn);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating schedule: {ex.Message}");
    }
}
else
{
//***** Rule, Target, and Scheduler Service Bus *****//
    try
    {
        // creates service bus
        string eventBusName = "invoke-api-dest-rule-bus";
        var eventBus =
            await awsEventBridgeService.CreateEventBus(
                eventBusName,
                CancellationToken.None);
        
        // creates event bridge rule
        string ruleName = "invoke-api-dest-rule";
        var targetResponse =
            await awsEventBridgeService.CreateEventBridgeRuleAsync(
                ruleName,
                iamEventRuleRole.Role.Arn,
                apiDestinationResponse.ApiDestinationArn,
                eventBusName,
                CancellationToken.None);
        
        // creates event bridge scheduler
        var scheduleName = "event-schedule-bus";
        var eventSchedule = await awsEventBridgeService.CreateSchedulerEventForEventBus(
            scheduleName, 
            iamEventSchedulerRole.Role.Arn,
            eventBusName,
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating schedule: {ex.Message}");
    }
}