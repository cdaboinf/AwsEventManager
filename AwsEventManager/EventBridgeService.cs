using Amazon;
using Amazon.ApiGatewayV2;
using Amazon.ApiGatewayV2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using ResourceNotFoundException = Amazon.SQS.Model.ResourceNotFoundException;
using Target = Amazon.EventBridge.Model.Target;

namespace AwsEventManager;

public class EventBridgeService
{
    private AmazonEventBridgeClient _eventBridgeClient;
    private AmazonSchedulerClient _schedulerClient;
    private AmazonApiGatewayV2Client _apiGatewayClient;
    private IdentityService _identityService;

    public EventBridgeService(RegionEndpoint awsRegion, AWSCredentials credentials)
    {
        _eventBridgeClient = new AmazonEventBridgeClient(credentials,awsRegion);
        _schedulerClient = new AmazonSchedulerClient(credentials, awsRegion);
        _apiGatewayClient = new AmazonApiGatewayV2Client(credentials, awsRegion);
        _identityService = new IdentityService(awsRegion);
    }

    public async Task<Amazon.EventBridge.Model.CreateConnectionResponse> GetOrCreateEventBridgeConnectionAsync(
        string connectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            // check if connection already exists
            var listConnectionsResponse =
                await _eventBridgeClient.ListConnectionsAsync(new ListConnectionsRequest(), cancellationToken);
            var existingConnection = listConnectionsResponse.Connections
                .Find(c => c.Name.Equals(connectionName, StringComparison.OrdinalIgnoreCase));

            Amazon.EventBridge.Model.CreateConnectionResponse connectionResponse;
            if (existingConnection != null)
            {
                Console.WriteLine($"Connection '{connectionName}' already exists.");
                Console.WriteLine($"Existing Connection ARN: {existingConnection.ConnectionArn}");
                Console.WriteLine($"State: {existingConnection.ConnectionState}");

                // get connection details
                var describeResponse = await _eventBridgeClient.DescribeConnectionAsync(
                    new DescribeConnectionRequest { Name = existingConnection.Name }, cancellationToken);

                connectionResponse = new Amazon.EventBridge.Model.CreateConnectionResponse
                {
                    ConnectionArn = describeResponse.ConnectionArn,
                    ConnectionState = describeResponse.ConnectionState,
                    ResponseMetadata = new Amazon.Runtime.ResponseMetadata()
                };
            }
            else
            {
                // create a new connection
                var request = new CreateConnectionRequest
                {
                    Name = connectionName,
                    AuthorizationType = ConnectionAuthorizationType.API_KEY,
                    AuthParameters = new CreateConnectionAuthRequestParameters
                    {
                        ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters
                        {
                            ApiKeyName = "x-api-key",
                            ApiKeyValue = Guid.NewGuid().ToString()
                        }
                    },
                    Description = "Connection for invoking API destinations"
                };

                connectionResponse = await _eventBridgeClient.CreateConnectionAsync(request, cancellationToken);

                Console.WriteLine($"✅ Created new connection");
                Console.WriteLine($"Connection ARN: {connectionResponse.ConnectionArn}");
                Console.WriteLine($"Connection State: {connectionResponse.ConnectionState}");
            }

            return connectionResponse;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating or checking connection: {ex.Message}");
            throw;
        }
    }

    public async Task<CreateApiDestinationResponse> GetOrCreateEventBridgeApiDestinationAsync(
        string connectionArn,
        string apiDestinationName,
        CancellationToken cancellationToken)
    {
        try
        {
            // API Destination details
            string endpointUrl = await GetApiGatewayUrlAsync("transfers-api", cancellationToken);

            // Step 1: Check if API Destination already exists
            var existingDestinations =
                await _eventBridgeClient.ListApiDestinationsAsync(new ListApiDestinationsRequest(), cancellationToken);

            var existingDestination = existingDestinations.ApiDestinations
                .Find(d => d.Name.Equals(apiDestinationName, StringComparison.OrdinalIgnoreCase));

            CreateApiDestinationResponse apiDestinationResponse;
            if (existingDestination != null)
            {
                Console.WriteLine($"API Destination '{apiDestinationName}' already exists.");
                Console.WriteLine($"Existing ARN: {existingDestination.ApiDestinationArn}");
                apiDestinationResponse = new CreateApiDestinationResponse
                {
                    ApiDestinationArn = existingDestination.ApiDestinationArn,
                    ApiDestinationState = existingDestination.ApiDestinationState,
                    ResponseMetadata = new Amazon.Runtime.ResponseMetadata()
                };
            }
            else
            {
                // Step 2: Create API Destination if it doesn't exist
                var createRequest = new CreateApiDestinationRequest
                {
                    Name = apiDestinationName,
                    ConnectionArn = connectionArn,
                    InvocationEndpoint = endpointUrl,
                    HttpMethod = ApiDestinationHttpMethod.POST,
                    Description = "API Destination to call external API",
                    InvocationRateLimitPerSecond = 5 // max calls per second
                };

                apiDestinationResponse =
                    await _eventBridgeClient.CreateApiDestinationAsync(createRequest, cancellationToken);

                Console.WriteLine($"Created API Destination ARN: {apiDestinationResponse.ApiDestinationArn}");
                Console.WriteLine($"State: {apiDestinationResponse.ApiDestinationState}");
            }

            return apiDestinationResponse;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating or checking API Destination: {ex.Message}");
            throw;
        }
    }

    public async Task<PutTargetsResponse> CreateEventBridgeRuleAsync(
        string ruleName,
        string roleArn,
        string apiDestinationArn,
        string eventBusName,
        CancellationToken cancellationToken)
    {
        await _eventBridgeClient.PutRuleAsync(
            new PutRuleRequest
            {
                Name = ruleName, 
                EventPattern = @"{""source"": [""com.app.scheduler""]}", State = RuleState.ENABLED,
                EventBusName = eventBusName
            }, cancellationToken);
        
        // Attach API Destination as target
        var target = await _eventBridgeClient.PutTargetsAsync(
            new PutTargetsRequest
            {
                Rule = ruleName, 
                EventBusName = eventBusName,
                Targets =
                [
                    new Target
                    {
                        Id = "ApiDestinationTarget", 
                        Arn = apiDestinationArn, 
                        RoleArn = GetRoleArnFromAssumedRoleArn(roleArn), 
                        InputPath = "$.detail"
                    }
                ]
            }, cancellationToken);
        return target;
    }

    public async Task<Amazon.Scheduler.Model.CreateScheduleResponse> CreateSchedulerEventForEventBus(
        string scheduleName,
        string schedulerRoleArn,
        string eventBusName,
        CancellationToken cancellationToken)
    {
        var identity = await _identityService.GetAccountIdAsync();

        var eventBridgeParams = new EventBridgeParameters
        {
            DetailType = "ScheduledEvent",
            Source = "com.app.scheduler",
        };

        var scheduleRequest = new CreateScheduleRequest
        {
            Name = scheduleName,
            ScheduleExpression = "rate(1 minutes)", // or cron(...)
            FlexibleTimeWindow = new FlexibleTimeWindow
            {
                Mode = FlexibleTimeWindowMode.OFF
            },
            Target = new Amazon.Scheduler.Model.Target
            {
                Arn = $"arn:aws:events:us-east-2:{identity}:event-bus/{eventBusName}", // Event bus ARN
                RoleArn = GetRoleArnFromAssumedRoleArn(schedulerRoleArn), // scheduler specific role
                EventBridgeParameters = eventBridgeParams,
                Input = @"{
                    ""message"": ""Scheduler Calling API""
                }"
            }
        };

        var schedule = await _schedulerClient.CreateScheduleAsync(scheduleRequest, cancellationToken);
        return schedule;
    }

    public async Task<CreateEventBusResponse> CreateEventBus(string busName, CancellationToken cancellationToken)
    {
        var identity = await _identityService.GetAccountIdAsync();
        try
        {
            // 1. List all event buses and check if it exists
            var listResponse =
                await _eventBridgeClient.ListEventBusesAsync(new ListEventBusesRequest(), cancellationToken);
            var existingBus = listResponse.EventBuses.FirstOrDefault(b => b.Name == busName);
            if (existingBus != null)
            {
                Console.WriteLine($"✅ EventBus '{busName}' already exists.");
                return new CreateEventBusResponse
                {
                    EventBusArn = existingBus.Arn
                };
            }

            // 2. If not found, create a new one
            Console.WriteLine($"ℹ️ EventBus '{busName}' not found. Creating...");
            var request = new CreateEventBusRequest
            {
                Name = busName
            };
            var createResponse = await _eventBridgeClient.CreateEventBusAsync(request);

            Console.WriteLine($"✅ EventBus '{busName}' created.");
            return createResponse;
        }
        catch (ResourceAlreadyExistsException)
        {
            Console.WriteLine($"⚠️ EventBus '{busName}' already exists.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error creating EventBus: {ex.Message}");
            throw;
        }
    }

    public async Task<string> GetApiGatewayUrlAsync(string apiName, CancellationToken cancellationToken = default)
    {
        try
        {
            var apis = await _apiGatewayClient.GetApisAsync(new GetApisRequest(), cancellationToken);

            foreach (var api in apis.Items)
            {
                Console.WriteLine($"API Name: {api.Name}, API ID: {api.ApiId}");
            }

            var gatewayApi = apis.Items.First(a => a.Name.Equals(apiName, StringComparison.OrdinalIgnoreCase));

            var stage = "dev";
            var route = "log";

            var apiUrl = $"{gatewayApi.ApiEndpoint}/{stage}/{route}";

            return apiUrl;
        }
        catch (ResourceNotFoundException ex)
        {
            Console.WriteLine($"No Function URL found for Lambda: {apiName}, {ex}");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetApisAsync not defined exception, {ex}");
            throw;
        }
    }
    
    public static string GetRoleArnFromAssumedRoleArn(string assumedRoleArn)
    {
        // Example: arn:aws:sts::848362861133:assumed-role/csharp-app-role/CSharpAppSession
        var parts = assumedRoleArn.Split(':');

        if (parts.Length < 6 || parts[2] != "sts" || !parts[5].StartsWith("assumed-role/"))
            throw new ArgumentException("Invalid assumed-role ARN", nameof(assumedRoleArn));

        var accountId = parts[4];
        var rolePart = parts[5]; // "assumed-role/csharp-app-role/CSharpAppSession"

        var roleName = rolePart.Split('/')[1]; // "csharp-app-role"

        return $"arn:aws:iam::{accountId}:role/{roleName}";
    }
}