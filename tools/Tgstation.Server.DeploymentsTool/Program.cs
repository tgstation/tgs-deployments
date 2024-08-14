using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.IdentityModel.Tokens;

using Octokit;

namespace Tgstation.Server.DeploymentsTool
{
    internal class Program
    {
        const int InstallationExpiryDays = 7;

        const long DataCacheRepoId = 841149827;
        const long DeploymentsRepoId = 92952846;

        // json in working dir
        const string InstallationsFilePath = "../installations.json";

        static async Task<int> Main(string[] args)
        {
            try
            {
                var githubAppSerializedKey = args[0];
                var mode = args[1];

                var now = DateTimeOffset.UtcNow;
                if (mode == "token")
                {
                    var tokenOutputPath = args[2];
                    await File.WriteAllTextAsync(tokenOutputPath, (await CreateClientForRepo(DataCacheRepoId, githubAppSerializedKey)).Credentials.GetToken());
                    return 0;
                }

                if (mode == "telemetry")
                {
                    var telemetryIdStr = Environment.GetEnvironmentVariable("TELEMETRY_ID");
                    var semver = Environment.GetEnvironmentVariable("TGS_SEMVER");
                    var shutdown = Environment.GetEnvironmentVariable("SHUTDOWN");
                    var friendlyName = Environment.GetEnvironmentVariable("SERVER_FRIENDLY_NAME");

                    if (String.IsNullOrWhiteSpace(friendlyName))
                        friendlyName = null;

                    if (!Guid.TryParse(telemetryIdStr, out var telemetryId))
                    {
                        Console.WriteLine($"Invalid telemetry_id: {telemetryIdStr}");
                        return 1;
                    }

                    if (!Regex.IsMatch(semver, @"^[0-9]+\.[0-9]+\.[0-9]+$"))
                    {
                        Console.WriteLine($"Invalid tgs_version: {semver}");
                        return 2;
                    }

                    if (friendlyName != null && friendlyName.Length > 255)
                    {
                        Console.WriteLine($"Friendly name is too long: {friendlyName}");
                        return 3;
                    }

                    DataCache data;
                    if (File.Exists(InstallationsFilePath))
                    {
                        var json = await File.ReadAllTextAsync(InstallationsFilePath);
                        data = JsonSerializer.Deserialize<DataCache>(json)!;
                    }
                    else
                    {
                        data = new DataCache();
                    }

                    data.Installations ??= new Dictionary<string, TelemetryEntry>();

                    var isShutdown = Boolean.Parse(shutdown);
                    if (data.Installations.TryGetValue(telemetryId.ToString(), out var oldEntry))
                    {
                        if (!oldEntry.ActiveDeploymentId.HasValue && isShutdown)
                        {
                            return 0;
                        }

                        data.Installations.Remove(telemetryIdStr);
                    }

                    if (friendlyName != null && data.Installations.Any(x => x.Value.FriendlyName?.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) == true))
                    {
                        Console.WriteLine($"There already exists a different telemetry entry for server friendly name: {friendlyName}");
                        return 4;
                    }

                    var telemetryClient = await CreateClientForRepo(DeploymentsRepoId, githubAppSerializedKey);
                    long? deploymentId;
                    if (oldEntry?.ActiveDeploymentId.HasValue != true)
                    {
                        var deployment = await telemetryClient.Repository.Deployment.Create(
                             DeploymentsRepoId,
                             new NewDeployment($"tgstation-server-v{semver}")
                             {
                                 AutoMerge = false,
                                 Description = friendlyName == null ? "(Anonymous Installation)" : telemetryIdStr,
                                 Environment = friendlyName ?? telemetryId.ToString().ToUpperInvariant(),
                                 ProductionEnvironment = true,
                                 TransientEnvironment = true,
                                 RequiredContexts = new System.Collections.ObjectModel.Collection<string>(),
                             });

                        await telemetryClient.Repository.Deployment.Status.Create(
                            DeploymentsRepoId,
                            deployment.Id,
                            new NewDeploymentStatus(DeploymentState.Success));
                        deploymentId = deployment.Id;
                    }
                    else
                    {
                        deploymentId = oldEntry.ActiveDeploymentId!.Value;
                        if (isShutdown)
                        {
                            await telemetryClient.Repository.Deployment.Status.Create(
                                DeploymentsRepoId,
                                deploymentId.Value,
                                new NewDeploymentStatus(DeploymentState.Inactive));
                            deploymentId = null;
                        }
                    }

                    data.Installations[telemetryId.ToString()] = new TelemetryEntry
                    {
                        UpdatedAt = now,
                        FriendlyName = friendlyName,
                        Version = semver,
                        ActiveDeploymentId = deploymentId
                    };

                    var newJson = JsonSerializer.Serialize(
                        data,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true,
                        });

                    await File.WriteAllTextAsync(InstallationsFilePath, newJson);
                    return 0;
                }

                var client = await CreateClientForRepo(DeploymentsRepoId, githubAppSerializedKey);
                var sendingJson = await File.ReadAllTextAsync(InstallationsFilePath);
                var sendingData = JsonSerializer.Deserialize<DataCache>(sendingJson)!;

                async Task UpdateCacheEntry(TelemetryEntry entry)
                {
                    if (!entry.ActiveDeploymentId.HasValue
                        // || (now - entry.UpdatedAt) < TimeSpan.FromDays(InstallationExpiryDays) // TODO: ENABLE
                        )
                        return;

                    await client.Repository.Deployment.Status.Create(
                        DeploymentsRepoId,
                        entry.ActiveDeploymentId.Value,
                        new NewDeploymentStatus(DeploymentState.Inactive));

                    entry.ActiveDeploymentId = null;
                    entry.UpdatedAt = now;
                }

                await Task.WhenAll(sendingData.Installations!.Select(kvp => UpdateCacheEntry(kvp.Value)));

                var newJson2 = JsonSerializer.Serialize(
                    sendingData,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    });

                await File.WriteAllTextAsync(InstallationsFilePath, newJson2);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return 6;
            }
        }

        static async ValueTask<GitHubClient> CreateClientForRepo(long repositoryId, string githubAppSerializedKey)
        {
            var splits = githubAppSerializedKey.Split(':');

            var pemBytes = Convert.FromBase64String(splits[1]);
            var pem = Encoding.UTF8.GetString(pemBytes);

            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
            var jwtSecurityTokenHandler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };

            var now = DateTime.UtcNow;

            var jwt = jwtSecurityTokenHandler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = splits[0],
                Expires = now.AddMinutes(10),
                IssuedAt = now,
                SigningCredentials = signingCredentials
            });
            var jwtStr = jwtSecurityTokenHandler.WriteToken(jwt);

            var client = new GitHubClient(new ProductHeaderValue("tgs_deployments_tool"));
            client.Credentials = new Credentials(jwtStr, AuthenticationType.Bearer);

            var installation = await client.GitHubApps.GetRepositoryInstallationForCurrent(repositoryId);
            var installToken = await client.GitHubApps.CreateInstallationToken(installation.Id);

            client.Credentials = new Credentials(installToken.Token);
            return client;
        }
    }
}
