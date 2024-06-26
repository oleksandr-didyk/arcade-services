// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Maestro.Common.AzureDevOpsTokens;

public interface IAzureDevOpsTokenProvider
{
    Task<string> GetTokenForAccount(string account);
}

public static class AzureDevOpsTokenProviderExtensions
{
    private static readonly Regex AccountNameRegex = new(@"^https://dev\.azure\.com/(?<account>[a-zA-Z0-9]+)/");

    public static Task<string> GetTokenForRepository(this IAzureDevOpsTokenProvider that, string repositoryUrl)
    {
        Match m = AccountNameRegex.Match(repositoryUrl);
        if (!m.Success)
        {
            throw new ArgumentException($"{repositoryUrl} is not a valid Azure DevOps repository URL");
        }
        var account = m.Groups["account"].Value;
        return that.GetTokenForAccount(account);
    }
}
