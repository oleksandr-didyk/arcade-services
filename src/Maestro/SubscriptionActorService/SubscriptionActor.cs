// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Maestro.Contracts;
using Maestro.Data;
using Maestro.Data.Models;
using Microsoft.DotNet.ServiceFabric.ServiceHost;
using Microsoft.DotNet.ServiceFabric.ServiceHost.Actors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Asset = Maestro.Contracts.Asset;

namespace SubscriptionActorService
{
    namespace unused
    {
        // class needed to appease service fabric build time generation of actor code
        [StatePersistence(StatePersistence.Persisted)]
        public class SubscriptionActor : Actor, ISubscriptionActor
        {
            public SubscriptionActor(ActorService actorService, ActorId actorId) : base(actorService, actorId)
            {
            }

            public Task<string> RunActionAsync(string method, string arguments)
            {
                throw new NotImplementedException();
            }

            public Task UpdateAsync(int buildId)
            {
                throw new NotImplementedException();
            }

            public Task<bool> UpdateForMergedPullRequestAsync(int updateBuildId)
            {
                throw new NotImplementedException();
            }

            public Task<bool> AddDependencyFlowEventAsync(
                int updateBuildId, 
                DependencyFlowEventType flowEvent, 
                DependencyFlowEventReason reason, 
                MergePolicyCheckResult policy,
                string flowType,
                string url)
            {
                throw new NotImplementedException();
            }
        }
    }

    public class SubscriptionActor : ISubscriptionActor, IActionTracker, IActorImplementation
    {
        public SubscriptionActor(
            BuildAssetRegistryContext context,
            ILogger<SubscriptionActor> logger,
            IActionRunner actionRunner,
            IActorProxyFactory<IPullRequestActor> pullRequestActorFactory)
        {
            Context = context;
            Logger = logger;
            ActionRunner = actionRunner;
            PullRequestActorFactory = pullRequestActorFactory;
        }

        public ActorId Id { get; private set; }
        public BuildAssetRegistryContext Context { get; }
        public ILogger<SubscriptionActor> Logger { get; }
        public IActionRunner ActionRunner { get; }
        public IActorProxyFactory<IPullRequestActor> PullRequestActorFactory { get; }

        public Guid SubscriptionId => Id.GetGuidId();

        public void Initialize(ActorId actorId, IActorStateManager stateManager, IReminderManager reminderManager)
        {
            Id = actorId;
        }

        public async Task TrackSuccessfulAction(string action, string result)
        {
            SubscriptionUpdate subscriptionUpdate = await GetSubscriptionUpdate();

            subscriptionUpdate.Action = action;
            subscriptionUpdate.ErrorMessage = result;
            subscriptionUpdate.Method = null;
            subscriptionUpdate.Arguments = null;
            subscriptionUpdate.Success = true;
            await Context.SaveChangesAsync();
        }

        public async Task TrackFailedAction(string action, string result, string method, string arguments)
        {
            SubscriptionUpdate subscriptionUpdate = await GetSubscriptionUpdate();

            subscriptionUpdate.Action = action;
            subscriptionUpdate.ErrorMessage = result;
            subscriptionUpdate.Method = method;
            subscriptionUpdate.Arguments = arguments;
            subscriptionUpdate.Success = false;
            await Context.SaveChangesAsync();
        }

        public Task<string> RunActionAsync(string method, string arguments)
        {
            return ActionRunner.RunAction(this, method, arguments);
        }

        Task ISubscriptionActor.UpdateAsync(int buildId)
        {
            return ActionRunner.ExecuteAction(() => UpdateAsync(buildId));
        }

        public async Task<bool> UpdateForMergedPullRequestAsync(int updateBuildId)
        {
            Logger.LogInformation("Updating {subscriptionId} with latest build id {buildId}", SubscriptionId, updateBuildId);
            Subscription subscription = await Context.Subscriptions.FindAsync(SubscriptionId);
            
            if (subscription != null)
            {
                subscription.LastAppliedBuildId = updateBuildId;
                Context.Subscriptions.Update(subscription);
                await Context.SaveChangesAsync();
                return true;
            }
            else
            {
                Logger.LogInformation("Could not find subscription with ID {subscriptionId}. Skipping latestBuild update.", SubscriptionId);
                return false;
            }
        }

        public async Task<bool> AddDependencyFlowEventAsync(
            int updateBuildId, 
            DependencyFlowEventType flowEvent, 
            DependencyFlowEventReason reason, 
            MergePolicyCheckResult policy,
            string flowType,
            string url)
        {
            string updateReason = reason == DependencyFlowEventReason.New || 
                                  reason == DependencyFlowEventReason.AutomaticallyMerged ? 
                                 reason.ToString() : $"{reason}{policy}";

            Logger.LogInformation($"Adding dependency flow event for {SubscriptionId} with {flowEvent} {updateReason} {flowType}");
            Subscription subscription = await Context.Subscriptions.FindAsync(SubscriptionId);
            if (subscription != null)
            {
                var dfe = new DependencyFlowEvent
                {
                    SourceRepository = subscription.SourceRepository,
                    TargetRepository = subscription.TargetRepository,
                    ChannelId = subscription.ChannelId,
                    BuildId = updateBuildId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Event = flowEvent.ToString(),
                    Reason = updateReason,
                    FlowType = flowType,
                    Url = url,
                };
                Context.DependencyFlowEvents.Add(dfe);
                await Context.SaveChangesAsync();
                return true;
            }
            else
            {
                Logger.LogInformation("Could not find subscription with ID {subscriptionId}. Skipping adding dependency flow event.", SubscriptionId);
                return false;
            }
        }

        [ActionMethod("Updating subscription for build {buildId}")]
        public async Task<ActionResult<bool>> UpdateAsync(int buildId)
        {
            Subscription subscription = await Context.Subscriptions.FindAsync(SubscriptionId);

            await AddDependencyFlowEventAsync(
                buildId, 
                DependencyFlowEventType.Fired, 
                DependencyFlowEventReason.New, 
                MergePolicyCheckResult.PendingPolicies, 
                "PR",
                null);

            Logger.LogInformation("Looking up build {buildId}", buildId);

            Build build = await Context.Builds.Include(b => b.Assets)
                .ThenInclude(a => a.Locations)
                .FirstAsync(b => b.Id == buildId);

            ActorId pullRequestActorId;

            if (subscription.PolicyObject.Batchable)
            {
                pullRequestActorId = PullRequestActorId.Create(
                    subscription.TargetRepository,
                    subscription.TargetBranch);
            }
            else
            {
                pullRequestActorId = PullRequestActorId.Create(SubscriptionId);
            }

            Logger.LogInformation("Creating pull request actor for '{pullRequestActorId}'", pullRequestActorId);

            IPullRequestActor pullRequestActor = PullRequestActorFactory.Lookup(pullRequestActorId);

            List<Asset> assets = build.Assets.Select(
                    a => new Asset
                    {
                        Name = a.Name,
                        Version = a.Version
                    })
                .ToList();

            Logger.LogInformation("Running asset update for {subscriptionId}", SubscriptionId);

            await pullRequestActor.UpdateAssetsAsync(
                SubscriptionId,
                subscription.SourceEnabled ? SubscriptionType.DependenciesAndSources : SubscriptionType.Dependencies,
                build.Id, 
                build.GitHubRepository ?? build.AzureDevOpsRepository, 
                build.Commit, 
                assets);

            Logger.LogInformation("Asset update complete for {subscriptionId}", SubscriptionId);

            return ActionResult.Create(true, "Update Sent");
        }

        private async Task<SubscriptionUpdate> GetSubscriptionUpdate()
        {
            SubscriptionUpdate subscriptionUpdate = await Context.SubscriptionUpdates.FindAsync(SubscriptionId);
            if (subscriptionUpdate == null)
            {
                Context.SubscriptionUpdates.Add(
                    subscriptionUpdate = new SubscriptionUpdate {SubscriptionId = SubscriptionId});
            }
            else
            {
                Context.SubscriptionUpdates.Update(subscriptionUpdate);
            }

            return subscriptionUpdate;
        }
    }
}
