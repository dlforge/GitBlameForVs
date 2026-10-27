using Microsoft.VisualStudio.Shell;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace GitBlameForVs.GitBlame
{
    internal static class VisualStudioCommitDetailsOpener
    {
        public static async Task OpenAsync(string repositoryPath, string commitHash)
        {
            if (!IsObjectId(commitHash))
                return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var controlsAssembly = Assembly.Load("Microsoft.TeamFoundation.Controls");
                var gitControlsAssembly = Assembly.Load("Microsoft.TeamFoundation.Git.Controls");
                var gitContractsAssembly = Assembly.Load("Microsoft.TeamFoundation.Git.Contracts");
                var teamExplorerType = controlsAssembly.GetType(
                    "Microsoft.TeamFoundation.Controls.ITeamExplorer", throwOnError: true);
                var globalServiceProvider = ServiceProvider.GlobalProvider;
                var teamExplorer = globalServiceProvider.GetService(teamExplorerType);
                if (teamExplorer == null)
                    throw new InvalidOperationException("Visual Studio Git repository service is unavailable.");

                var gitIdType = gitContractsAssembly.GetType(
                    "Microsoft.TeamFoundation.Git.Contracts.GitId", throwOnError: true);
                var gitInterfaceType = gitContractsAssembly.GetType(
                    "Microsoft.TeamFoundation.Git.Contracts.IGitId", throwOnError: true);
                var gitId = Activator.CreateInstance(gitIdType, commitHash)
                    ?? throw new InvalidOperationException("Could not create the Git commit identifier.");

                var providerType = gitControlsAssembly.GetType(
                    "Microsoft.TeamFoundation.Git.Controls.CommitDetails.CommitDetailsProvider",
                    throwOnError: true);
                var providerConstructor = providerType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    new[] { typeof(string), gitInterfaceType, typeof(IServiceProvider) },
                    modifiers: null)
                    ?? throw new MissingMethodException(providerType.FullName, ".ctor");
                var provider = providerConstructor.Invoke(
                    new object[] { repositoryPath, gitId, globalServiceProvider });

                var contextType = gitControlsAssembly.GetType(
                    "Microsoft.TeamFoundation.Git.Controls.CommitDetails.CommitDetailsContext",
                    throwOnError: true);
                var context = Activator.CreateInstance(contextType, provider)
                    ?? throw new InvalidOperationException("Could not create the Git commit details context.");

                var pageIdType = controlsAssembly.GetType(
                    "Microsoft.TeamFoundation.Controls.TeamExplorerPageIds", throwOnError: true);
                string? pageIdText = pageIdType.GetField(
                    "GitCommitDetails", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
                if (!Guid.TryParse(pageIdText, out var pageId))
                    throw new InvalidOperationException("Visual Studio Git commit details page is unavailable.");

                var navigateToPage = teamExplorerType.GetMethod(
                    "NavigateToPage", new[] { typeof(Guid), typeof(object) })
                    ?? throw new MissingMethodException(teamExplorerType.FullName, "NavigateToPage");
                navigateToPage.Invoke(teamExplorer, new[] { (object)pageId, context });
            }
            catch (Exception error)
            {
                Trace.TraceError($"Failed to open Visual Studio commit details: {error}");
            }
        }

        private static bool IsObjectId(string commitHash)
        {
            if (commitHash.Length != 40 && commitHash.Length != 64)
                return false;

            foreach (char character in commitHash)
            {
                if (!Uri.IsHexDigit(character))
                    return false;
            }

            return true;
        }
    }
}
