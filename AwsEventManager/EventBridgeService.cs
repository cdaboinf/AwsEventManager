using System.Net;
using Amazon;
using Amazon.ApiGatewayV2;
using Amazon.ApiGatewayV2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
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

    public EventBridgeService(RegionEndpoint awsRegion)
    {
        _eventBridgeClient = new AmazonEventBridgeClient(awsRegion);
        _schedulerClient = new AmazonSchedulerClient(awsRegion);
        _apiGatewayClient = new AmazonApiGatewayV2Client(awsRegion);
        _identityService = new IdentityService(awsRegion);
    }

    public async Task<Amazon.EventBridge.Model.CreateConnectionResponse> GetOrCreateEventBridgeConnectionAsync(
        string connectionName,
        RegionEndpoint awsRegion,
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

    public async Task<CreateApiDestinationResponse> GetOrCreateEventBridgeApiDestinationAsync(string connectionArn,
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

    public async Task<Amazon.Scheduler.Model.CreateScheduleResponse> CreateSchedulerEventForDestinationApiAsync(
        string apiDestinationName, string roleArn)
    {
        //arn:aws:events:us-east-2:848362861133:api-destination/event-bridge-api-dest/b419f733-0dfa-48c6-9917-806b9a9f731a
        try
        {
            var identity = await _identityService.GetAccountIdAsync();
            // Schedule name
            string scheduleName = "MyApiDestinationSchedule";
            var request = new CreateScheduleRequest
            {
                Name = scheduleName,
                Description = "Schedule to call my API Destination every 5 minutes",

                // Target configuration
                Target = new Amazon.Scheduler.Model.Target
                {
                    //Arn = $"arn:aws:scheduler:::api-destination/{apiDestinationName}",
                    //Arn = $"arn:aws:events:us-east-2:{identity}:api-destination/event-bridge-api-dest",
                    Arn = "arn:aws:scheduler:::aws-sdk/EventBridge/CreateApiDestination",
                    RoleArn = roleArn,
                    Input = "{\"Name\": \"" + apiDestinationName + "\"}",
                    //Input = "{ \"message\": \"app message\"}",
                    RetryPolicy = new Amazon.Scheduler.Model.RetryPolicy
                    {
                        MaximumRetryAttempts = 2,
                        MaximumEventAgeInSeconds = 3600
                    }
                },

                // Schedule expression (rate or cron)
                ScheduleExpression = "rate(1 minutes)", // or cron(0/1 * * * ? *) for every 1 minutes
                FlexibleTimeWindow = new FlexibleTimeWindow
                {
                    Mode = FlexibleTimeWindowMode.FLEXIBLE
                },

                State = ScheduleState.ENABLED
            };

            var response = await _schedulerClient.CreateScheduleAsync(request);

            Console.WriteLine($"Schedule ARN: {response.ScheduleArn}");
            return response;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating schedule: {ex.Message}");
            throw;
        }
    }

    public async Task<PutTargetsResponse> CreateEventBridgeRuleAsync1(
        string ruleName,
        string roleArn,
        string apiDestinationArn,
        string eventBusName,
        CancellationToken cancellationToken)
    {
        // 1. Check if the rule already exists using ListRulesAsync
        var listResponse = await _eventBridgeClient.ListRulesAsync(new ListRulesRequest
        {
            EventBusName = eventBusName,
            NamePrefix = ruleName
        }, cancellationToken);

        var existingRule = listResponse.Rules.FirstOrDefault(r => r.Name == ruleName);

        var targetResponse = new PutTargetsResponse();

        if (existingRule == null)
        {
            // 2. Create the rule if it doesn't exist
            await _eventBridgeClient.PutRuleAsync(new PutRuleRequest
            {
                Name = ruleName,
                EventPattern = @"{""source"": [""com.app.scheduler""]}",
                State = RuleState.ENABLED,
                EventBusName = eventBusName
            }, cancellationToken);

            // 3. Attach API Destination as target
            targetResponse = await _eventBridgeClient.PutTargetsAsync(new PutTargetsRequest
            {
                Rule = ruleName,
                EventBusName = eventBusName,
                Targets =
                {
                    new Target
                    {
                        Id = "ApiDestinationTarget",
                        Arn = apiDestinationArn,
                        RoleArn = roleArn,
                        InputPath = "$.detail"
                    }
                }
            }, cancellationToken);

            Console.WriteLine($"✅ Rule '{ruleName}' created on bus '{eventBusName}'.");
        }
        else
        {
            // Return a synthetic response if rule already exists
            targetResponse = new PutTargetsResponse
            {
                HttpStatusCode = HttpStatusCode.Created
            };

            Console.WriteLine($"ℹ️ Rule '{ruleName}' already exists on bus '{eventBusName}'.");
        }

        return targetResponse;
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
            ScheduleExpression = "rate(2 minutes)", // or cron(...)
            FlexibleTimeWindow = new FlexibleTimeWindow
            {
                Mode = FlexibleTimeWindowMode.OFF
            },
            Target = new Amazon.Scheduler.Model.Target
            {
                Arn = $"arn:aws:events:us-east-2:{identity}:event-bus/{eventBusName}", // Event bus ARN
                RoleArn = schedulerRoleArn, // scheduler specific role
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
    }

    public async Task<PutTargetsResponse> CreateEventBridgeRuleAsync(
        string ruleName,
        string roleArn,
        string apiDestinationArn,
        string eventBusName,
        CancellationToken cancellationToken)
    {
        var ruleResponse = await _eventBridgeClient.PutRuleAsync(
            new PutRuleRequest
            {
                Name = ruleName, EventPattern = @"{""source"": [""com.app.scheduler""]}", State = RuleState.ENABLED,
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
                        RoleArn = roleArn, 
                        InputPath = "$.detail"
                    }
                ]
            }, cancellationToken);
        return target;
    }
}