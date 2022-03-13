﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliWrap;
using DotNetReleaser.Helpers;
using DotNetReleaser.Logging;
using LibGit2Sharp;
using Spectre.Console;

namespace DotNetReleaser;

public partial class ReleaserApp
{
    private async Task<(BuildInformation? buildInformation, IDevHosting? devHosting, IDevHosting? devHostingExtra)?> Configuring(string configurationFile, BuildKind buildKind, string githubApiToken, string? githubApiTokenExtra, string? nugetApiToken, bool forceArtifactsFolder, IReadOnlyList<string> webAppProfiles)
    {
        // ------------------------------------------------------------------
        // Load Configuration
        // ------------------------------------------------------------------
        var gitHubInfo = GitHubActionHelper.GetInfo();
        if (gitHubInfo is not null)
        {
            Info($"Running from GitHub: {gitHubInfo}");
        }

        
        if (!await LoadConfiguration(configurationFile)) return null; // return false;

        if (!EnsureArtifactsFolders(forceArtifactsFolder)) return null; // return false;

        // ------------------------------------------------------------------
        // Load Package Information from MSBuild project
        // ------------------------------------------------------------------
        var buildInformation = await LoadProjects();
        if (buildInformation is null || HasErrors) return null; // return false;
        
        // Find and assign PublishProfile for WebApp publishing
        AssignWebAppProfiles(buildInformation, webAppProfiles);
        
        // ------------------------------------------------------------------
        // Validate Publish parameters
        // ------------------------------------------------------------------
        var hostingConfiguration = _config.GitHub;

        IDevHosting? devHosting = null;
        IDevHosting? devHostingExtra = null;

        // Connect to GitHub if we have a token
        if (!string.IsNullOrEmpty(githubApiToken))
        {
            devHosting = await ConnectToDevHosting(hostingConfiguration, githubApiToken);
            if (devHosting is null)
            {
                return null; // return false;
            }
        }

        // Connet to GitHub for extra access
        if (!string.IsNullOrEmpty(githubApiTokenExtra))
        {
            devHostingExtra = await ConnectToDevHosting(hostingConfiguration, githubApiTokenExtra);
            if (devHostingExtra is null)
            {
                return null; // return false;
            }
        }

        // We require a branch name if we need to publish a changelog (for the draft version)
        // or we need to do an automatic run on GitHub Action.
        var requiresDraftForBuild = (buildKind == BuildKind.Run || buildKind == BuildKind.Build) && !string.IsNullOrEmpty(githubApiToken) && !_config.Changelog.DisableDraftForBuild;
        var requiringBranchNameAndCommitSha = buildKind == BuildKind.Publish && hostingConfiguration.Publish
                                  || requiresDraftForBuild
                                  || buildKind == BuildKind.Run;
        bool validateBranchName = false;

        buildInformation.GitInformation = GitInformation.Create(_logger, _config.ConfigurationFilePath, hostingConfiguration.Branches);
        if (requiringBranchNameAndCommitSha && buildInformation.GitInformation is null)
        {
            Error($"Unable to find a git repository from the folder {Path.GetDirectoryName(_config.ConfigurationFilePath)}. This is required by the current action.");
            return null;
        }

        // Fetch current branch name
        if (buildKind == BuildKind.Run)
        {
            if (devHosting is null)
            {
                Error("Missing GitHub API token. The command `run` is only supported to run from a GitHub Action with a valid --github-api-token.");
                return null;
            }

            if (gitHubInfo is null)
            {
                Error("Invalid usage of command `run`. This command is only supported to run from a GitHub Action");
                return null;
            }

            // Automatically convert a run into a publish if we have a release tag
            if (gitHubInfo.EventName == "push" && gitHubInfo.RefType == GitHubActionRefType.Tag)
            {
                var regexVersion = new Regex(@$"^{hostingConfiguration.VersionPrefix}\d+(\.\d+)*");
                if (regexVersion.IsMatch(gitHubInfo.RefName))
                {
                    _logger.InfoMarkup($"The tag `{Markup.Escape(gitHubInfo.RefName)}` is identified as a release tag. [green on black]Publish mode[/] selected.");
                    buildKind = BuildKind.Publish;
                }
                else
                {
                    _logger.WarnMarkup($"The tag {Markup.Escape(gitHubInfo.RefName)} is not identified as a release tag. [green on black]Build only mode[/] selected.");
                    buildKind = BuildKind.Build;
                }
            }
            else
            {
                _logger.InfoMarkup(gitHubInfo.EventName == "push"
                    ? $"The trigger event is `{Markup.Escape(gitHubInfo.EventName)}` and the branch `{Markup.Escape(gitHubInfo.RefName)}`. [green on black]Build only mode[/] selected."
                    : $"The trigger event is `{Markup.Escape(gitHubInfo.EventName)}`. [green on black]Build only mode[/] selected.");
                buildKind = BuildKind.Build;
            }
        }

        if (buildKind == BuildKind.Publish)
        {
            if (hostingConfiguration.Publish)
            {
                if (string.IsNullOrEmpty(githubApiToken))
                {
                    Error($"Publishing to {hostingConfiguration.Provider} requires to pass --github-token");
                    return null; // return false;
                }

                validateBranchName = true;
                buildInformation.AllowPublishDraft = true;
            }

            if (_config.NuGet.Publish && string.IsNullOrEmpty(nugetApiToken) && buildInformation.GetAllPackableProjects().Count > 0)
            {
                Error("Publishing to NuGet requires to pass --nuget-token");
                return null; // return false;
            }
        }

        // Verifies that the branch is a supported branch for releases
        if (validateBranchName)
        {
            var branchName = buildInformation.GitInformation!.BranchName;
            if (!_config.GitHub.Branches.Contains(branchName))
            {
                Error($"The current git branch `{branchName}` is not listed in the authorized release branches from the configuration `github.branches = [{string.Join(", ", _config.GitHub.Branches)}]`");
                return null;
            }
        }

        // Allow to generate a draft only when building from release branches
        if (requiresDraftForBuild)
        {
            var branchName = buildInformation.GitInformation!.BranchName;
            if (_config.GitHub.Branches.Contains(branchName))
            {
                buildInformation.AllowPublishDraft = true;
            }
        }

        // Store the build kind
        buildInformation.BuildKind = buildKind;

        return (buildInformation, devHosting, devHostingExtra);
    }

    private void AssignWebAppProfiles(BuildInformation buildInformation, IReadOnlyList<string> webAppProfiles)
    {
        for (var i = 0; i < webAppProfiles.Count; i++)
        {
            var webAppProfile = webAppProfiles[i];
            var indexOfEqual = webAppProfile.IndexOf('=');
            if (indexOfEqual < 0)
            {
                Error($"Invalid publish profile at {i}. Expecting an equal `=` between <ProjectName>=<PublishProfile>.");
                continue;
            }

            var projectName = webAppProfile.Substring(0, indexOfEqual).Trim();
            var publishProfile = webAppProfile.Substring(indexOfEqual + 1).Trim();
            if (publishProfile == "")
            {
                Error($"Invalid empty publish profile at {i}. Expecting a publish profile after the ProjectName: <ProjectName>=<PublishProfile>.");
                continue;
            }

            var projectPackageInfo = buildInformation.ProjectPackageInfoCollections.SelectMany(x => x.Packages).FirstOrDefault(x => x.ProjectName == projectName);
            if (projectPackageInfo is null)
            {
                Error($"Invalid publish profile at {i}. The ProjectName `{projectName}` was not found in the MSBuild project name list [{string.Join(", ", buildInformation.ProjectPackageInfoCollections.SelectMany(x => x.Packages).Select(x => x.ProjectName))}]");
                continue;
            }

            projectPackageInfo.WebAppPublishProfile = publishProfile;
        }
    }
}