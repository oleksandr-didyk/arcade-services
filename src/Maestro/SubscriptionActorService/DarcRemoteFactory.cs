// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;
using Maestro.Common.AzureDevOpsTokens;
using Maestro.Data;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.DotNet.GitHub.Authentication;
using Microsoft.DotNet.Internal.Logging;
using Microsoft.DotNet.ServiceFabric.ServiceHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SubscriptionActorService;

public class DarcRemoteFactory : IRemoteFactory
{
    private readonly IConfiguration _configuration;
    private readonly IGitHubTokenProvider _gitHubTokenProvider;
    private readonly IAzureDevOpsTokenProvider _azureDevOpsTokenProvider;
    private readonly BuildAssetRegistryContext _context;
    private readonly DarcRemoteMemoryCache _cache;

    private readonly TemporaryFiles _tempFiles;
    private readonly ILocalGit _localGit;
    private readonly IVersionDetailsParser _versionDetailsParser;
    private readonly OperationManager _operations;

    public DarcRemoteFactory(
        IConfiguration configuration,
        IGitHubTokenProvider gitHubTokenProvider,
        IAzureDevOpsTokenProvider azureDevOpsTokenProvider,
        DarcRemoteMemoryCache memoryCache,
        BuildAssetRegistryContext context,
        TemporaryFiles tempFiles,
        ILocalGit localGit,
        IVersionDetailsParser versionDetailsParser,
        OperationManager operations)
    {
        _tempFiles = tempFiles;
        _localGit = localGit;
        _versionDetailsParser = versionDetailsParser;
        _operations = operations;
        _configuration = configuration;
        _gitHubTokenProvider = gitHubTokenProvider;
        _azureDevOpsTokenProvider = azureDevOpsTokenProvider;
        _cache = memoryCache;
        _context = context;
    }

    public async Task<IRemote> GetRemoteAsync(string repoUrl, ILogger logger)
    {
        using (_operations.BeginOperation($"Getting remote for repo {repoUrl}."))
        {
            IRemoteGitRepo remoteGitClient = await GetRemoteGitClient(repoUrl, logger);
            return new Remote(remoteGitClient, _versionDetailsParser, logger);
        }
    }

    public async Task<IDependencyFileManager> GetDependencyFileManagerAsync(string repoUrl, ILogger logger)
    {
        using (_operations.BeginOperation($"Getting remote file manager for repo {repoUrl}."))
        {
            IRemoteGitRepo remoteGitClient = await GetRemoteGitClient(repoUrl, logger);
            return new DependencyFileManager(remoteGitClient, _versionDetailsParser, logger);
        }
    }

    private async Task<IRemoteGitRepo> GetRemoteGitClient(string repoUrl, ILogger logger)
    {
        // Normalize the url with the AzDO client prior to attempting to
        // get a token. When we do coherency updates we build a repo graph and
        // may end up traversing links to classic azdo uris.
        string normalizedUrl = AzureDevOpsClient.NormalizeUrl(repoUrl);

        // Look up the setting for where the repo root should be held.  Default to empty,
        // which will use the temp directory.
        string temporaryRepositoryRoot = _configuration.GetValue<string>("DarcTemporaryRepoRoot", null);
        if (string.IsNullOrEmpty(temporaryRepositoryRoot))
        {
            temporaryRepositoryRoot = _tempFiles.GetFilePath("repos");
        }

        long installationId = await _context.GetInstallationId(normalizedUrl);
        var repoType = GitRepoUrlParser.ParseTypeFromUri(normalizedUrl);

        if (repoType == GitRepoType.GitHub && installationId == default)
        {
            throw new GithubApplicationInstallationException($"No installation is available for repository '{normalizedUrl}'");
        }

        var remoteConfiguration = repoType switch
        {
            GitRepoType.GitHub => new RemoteTokenProvider(
                gitHubToken: await _gitHubTokenProvider.GetTokenForInstallationAsync(installationId)),
            GitRepoType.AzureDevOps => new RemoteTokenProvider(
                azureDevOpsToken: await _azureDevOpsTokenProvider.GetTokenForRepository(normalizedUrl)),

            _ => throw new NotImplementedException($"Unknown repo url type {normalizedUrl}"),
        };

        var gitExe = _localGit.GetPathToLocalGit();

        return GitRepoUrlParser.ParseTypeFromUri(normalizedUrl) switch
        {
            GitRepoType.GitHub => installationId == default
                ? throw new GithubApplicationInstallationException($"No installation is available for repository '{normalizedUrl}'")
                : new GitHubClient(
                    gitExe,
                    remoteConfiguration.GitHubToken,
                    logger,
                    temporaryRepositoryRoot,
                    _cache.Cache),

            GitRepoType.AzureDevOps => new AzureDevOpsClient(
                gitExe,
                remoteConfiguration.AzureDevOpsToken,
                logger,
                temporaryRepositoryRoot),

            _ => throw new NotImplementedException($"Unknown repo url type {normalizedUrl}"),
        };
    }
}
