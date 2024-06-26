// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System;
using Microsoft.DotNet.DarcLib.Helpers;

namespace Microsoft.DotNet.DarcLib;

public class RemoteTokenProvider
{
    public RemoteTokenProvider(string? gitHubToken = null, string? azureDevOpsToken = null)
    {
        GitHubToken = gitHubToken;
        AzureDevOpsToken = azureDevOpsToken;
    }

    public static string GitRemoteUser => Constants.GitHubBotUserName;

    public string? GitHubToken { get; }


    public string? AzureDevOpsToken { get; }

    public string? GetTokenForUri(string repoUri)
    {
        var repoType = GitRepoUrlParser.ParseTypeFromUri(repoUri);

        return repoType switch
        {
            GitRepoType.GitHub => GitHubToken,
            GitRepoType.AzureDevOps => AzureDevOpsToken,
            GitRepoType.Local => null,
            _ => throw new NotImplementedException($"Unsupported repository remote {repoUri}"),
        };
    }
}
