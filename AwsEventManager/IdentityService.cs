using Amazon;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace AwsEventManager;

public class IdentityService
{
    private AmazonIdentityManagementServiceClient _client;
    private AmazonSecurityTokenServiceClient _stsClient;

    public IdentityService(RegionEndpoint awsRegion)
    {
        _client = new AmazonIdentityManagementServiceClient(awsRegion);
        _stsClient = new AmazonSecurityTokenServiceClient(awsRegion);
    }

    public async Task<GetRoleResponse> GetRoleAsync(string roleName,
        CancellationToken cancellationToken)
    {
        try
        {
            // Call GetRole to fetch details
            var response = await _client.GetRoleAsync(new GetRoleRequest
            {
                RoleName = roleName
            }, cancellationToken);

            Console.WriteLine($"Found IAM Role: {roleName}");
            Console.WriteLine($"Role ARN: {response.Role.Arn}");

            return response;
        }
        catch (NoSuchEntityException)
        {
            Console.WriteLine($"Role '{roleName}' does not exist in this account/region.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving role: {ex.Message}");
            throw;
        }
    }

    public async Task<string> GetAccountIdAsync()
    {
        var response = await _stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest());
        return response.Account;
    }

    public async Task<string> GetCallerArnAsync()
    {
        var response = await _stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest());
        return response.Arn;
    }

    public async Task<(SessionAWSCredentials Creds, AssumeRoleResponse Role)> GetAssumedRoleSessionCredentials(
        string role, string sessionName)
    {
        var assumeRoleResponse = await _stsClient.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = $"arn:aws:iam::848362861133:role/{role}",
            RoleSessionName = sessionName
        });

        var credentials = new Amazon.Runtime.SessionAWSCredentials(
            assumeRoleResponse.Credentials.AccessKeyId,
            assumeRoleResponse.Credentials.SecretAccessKey,
            assumeRoleResponse.Credentials.SessionToken
        );

        return (credentials, assumeRoleResponse);
    }
}