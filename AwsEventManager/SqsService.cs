using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace AwsEventManager;

public class SqsService
{
    public async Task<GetQueueAttributesResponse> GetQueueInfoAsync(string name, RegionEndpoint awsRegion, CancellationToken cancellationToken)
    {
        // Create the SQS client
        using var sqsClient = new AmazonSQSClient(awsRegion);
        try
        {
            // 1. Get the queue URL
            var getQueueUrlResponse = await sqsClient.GetQueueUrlAsync(new GetQueueUrlRequest
            {
                QueueName = name
            }, cancellationToken);

            string queueUrl = getQueueUrlResponse.QueueUrl;
            Console.WriteLine($"Queue URL: {queueUrl}");

            // 2. Get queue attributes
            var getAttributesResponse = await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = ["All"]
            }, cancellationToken);

            return getAttributesResponse;
        }
        catch (QueueDoesNotExistException)
        {
            Console.WriteLine($"Queue '{name}' does not exist.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving queue information: {ex.Message}");
            throw;
        }
    }
}