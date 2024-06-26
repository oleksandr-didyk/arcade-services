// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Maestro.Common.AzureDevOpsTokens;

public class AzureDevOpsTokenProviderOptions
{
    public Dictionary<string, string> Tokens { get; } = [];

    public Dictionary<string, string> ManagedIdentities { get; } = [];
}
