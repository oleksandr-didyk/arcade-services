using Maestro.Data.Models;
using Microsoft.DotNet.DarcLib;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static SubscriptionActorService.PullRequestActorImplementation;

namespace SubscriptionActorService
{
    public class PullRequestDescriptionBuilder
    {
        public PullRequestDescriptionBuilder(ILoggerFactory loggerFactory, StringBuilder description)
        {
            _logger = loggerFactory.CreateLogger(GetType());
            _description = description;
            _startingReferenceId = GetStartingReferenceId();
        }

        private ILogger _logger;

        private StringBuilder _description;

        private int _startingReferenceId;

        /// <summary>
        ///     Calculate the PR description for an update.
        /// </summary>
        /// <param name="update">Update</param>
        /// <param name="deps">Dependencies updated</param>
        /// <returns>Task</returns>
        /// <remarks>
        ///     Because PRs tend to be live for short periods of time, we can put more information
        ///     in the description than the commit message without worrying that links will go stale.
        /// </remarks>
        public void CalculatePRDescription(UpdateAssetsParameters update, List<DependencyUpdate> deps, List<GitFile> committedFiles, Build build)
        {
            var changesLinks = new List<string>();

            //Find the Coherency section of the PR description
            if (update.IsCoherencyUpdate)
            {
                string sectionStartMarker = $"[marker]: <> (Begin:Coherency Updates)";
                string sectionEndMarker = $"[marker]: <> (End:Coherency Updates)";
                int sectionStartIndex = RemovePRDescriptionSection(sectionStartMarker, sectionEndMarker);

                var coherencySection = new StringBuilder();
                coherencySection.AppendLine(sectionStartMarker);
                coherencySection.AppendLine("## Coherency Updates");
                coherencySection.AppendLine();
                coherencySection.AppendLine("The following updates ensure that dependencies with a *CoherentParentDependency*");
                coherencySection.AppendLine("attribute were produced in a build used as input to the parent dependency's build.");
                coherencySection.AppendLine("See [Dependency Description Format](https://github.com/dotnet/arcade/blob/master/Documentation/DependencyDescriptionFormat.md#dependency-description-overview)");
                coherencySection.AppendLine();
                coherencySection.AppendLine(DependencyUpdateBegin);
                coherencySection.AppendLine();
                coherencySection.AppendLine("- **Coherency Updates**:");
                foreach (DependencyUpdate dep in deps)
                {
                    coherencySection.AppendLine($"  - **{dep.To.Name}**: from {dep.From.Version} to {dep.To.Version} (parent: {dep.To.CoherentParentDependencyName})");
                }
                coherencySection.AppendLine();
                coherencySection.AppendLine(DependencyUpdateEnd);
                coherencySection.AppendLine();
                coherencySection.AppendLine(sectionEndMarker);
                _description.Insert(sectionStartIndex, coherencySection.ToString());
            }
            else
            {
                string sourceRepository = update.SourceRepo;
                Guid updateSubscriptionId = update.SubscriptionId;
                string sectionStartMarker = $"[marker]: <> (Begin:{updateSubscriptionId})";
                string sectionEndMarker = $"[marker]: <> (End:{updateSubscriptionId})";
                int sectionStartIndex = RemovePRDescriptionSection(sectionStartMarker, sectionEndMarker);

                var subscriptionSection = new StringBuilder();
                subscriptionSection.AppendLine(sectionStartMarker);
                subscriptionSection.AppendLine($"## From {sourceRepository}");
                subscriptionSection.AppendLine($"- **Subscription**: {updateSubscriptionId}");
                subscriptionSection.AppendLine($"- **Build**: {build.AzureDevOpsBuildNumber}");
                subscriptionSection.AppendLine($"- **Date Produced**: {build.DateProduced.ToUniversalTime():MMMM d, yyyy h:mm:ss tt UTC}");
                // This is duplicated from the files changed, but is easier to read here.
                subscriptionSection.AppendLine($"- **Commit**: {build.Commit}");
                string branch = build.AzureDevOpsBranch ?? build.GitHubBranch;
                if (!string.IsNullOrEmpty(branch))
                {
                    subscriptionSection.AppendLine($"- **Branch**: {branch}");
                }
                subscriptionSection.AppendLine();
                subscriptionSection.AppendLine(DependencyUpdateBegin);
                subscriptionSection.AppendLine();
                subscriptionSection.AppendLine($"- **Updates**:");

                var shaRangeToLinkId = new Dictionary<(string from, string to), int>();

                foreach (DependencyUpdate dep in deps)
                {
                    if (!shaRangeToLinkId.ContainsKey((dep.From.Commit, dep.To.Commit)))
                    {
                        string changesUri = string.Empty;
                        try
                        {
                            changesUri = GetChangesURI(dep.To.RepoUri, dep.From.Commit, dep.To.Commit);
                        }
                        catch (ArgumentNullException e)
                        {
                            _logger.LogError(e, $"Failed to create SHA comparison link for dependency {dep.To.Name} during asset update for subscription {update.SubscriptionId}");
                        }
                        shaRangeToLinkId.Add((dep.From.Commit, dep.To.Commit), _startingReferenceId + changesLinks.Count);
                        changesLinks.Add(changesUri);
                    }
                    subscriptionSection.AppendLine($"  - **{dep.To.Name}**: [from {dep.From.Version} to {dep.To.Version}][{shaRangeToLinkId[(dep.From.Commit, dep.To.Commit)]}]");
                }

                subscriptionSection.AppendLine();
                for (int i = 0; i < changesLinks.Count; i++)
                {
                    subscriptionSection.AppendLine($"[{i + _startingReferenceId}]: {changesLinks[i]}");
                }

                subscriptionSection.AppendLine();
                subscriptionSection.AppendLine(DependencyUpdateEnd);
                subscriptionSection.AppendLine();
                UpdatePRDescriptionDueConfigFiles(committedFiles, subscriptionSection);

                subscriptionSection.AppendLine();
                subscriptionSection.AppendLine(sectionEndMarker);
                _description.Insert(sectionStartIndex, subscriptionSection.ToString());

            }
            _description.AppendLine();
            _startingReferenceId += changesLinks.Count;
        }

        private int RemovePRDescriptionSection(string sectionStartMarker, string sectionEndMarker)
        {
            string descriptionString = _description.ToString();
            int sectionStartIndex = descriptionString.IndexOf(sectionStartMarker);
            int sectionEndIndex = descriptionString.IndexOf(sectionEndMarker);

            if (sectionStartIndex != -1 && sectionEndIndex != -1)
            {
                sectionEndIndex += sectionEndMarker.Length;
                _description.Remove(sectionStartIndex, sectionEndIndex - sectionStartIndex);
                return sectionStartIndex;
            }
            // if either marker is missing, just append at end and don't remove anything
            // from the description
            return _description.Length;
        }

        /// <summary>
        /// Goes through the description and finds the biggest reference id. This is needed when updating an exsiting PR.
        /// </summary>
        /// <returns></returns>
        private int GetStartingReferenceId()
        {
            Regex regex = new Regex("(?<=^\\[)\\d+(?=\\]:.+)", RegexOptions.Multiline);
            int maxIndex = 0;

            foreach(var match in regex.Matches(_description.ToString()))
            {
                int currentIndex = Int32.Parse(match.ToString());
                maxIndex = (currentIndex > maxIndex) ? currentIndex : maxIndex;
            }

            return maxIndex + 1;
        }

        public static string GetChangesURI(string repoURI, string from, string to)
        {
            if (repoURI == null)
            {
                throw new ArgumentNullException(nameof(repoURI));
            }
            if (from == null)
            {
                throw new ArgumentNullException(nameof(from));
            }
            if (to == null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            string fromSha = from.Length > 7 ? from.Substring(0, 7) : from;
            string toSha = to.Length > 7 ? to.Substring(0, 7) : to;

            if (repoURI.Contains("github.com"))
            {
                return $"{repoURI}/compare/{fromSha}...{toSha}";
            }
            return $"{repoURI}/branches?baseVersion=GC{fromSha}&targetVersion=GC{toSha}&_a=files";
        }
    }
}
