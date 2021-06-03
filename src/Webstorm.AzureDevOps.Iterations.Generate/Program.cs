using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.Core.WebApi.Types;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace Webstorm.AzureDevOps.Iterations.Generate
{
    internal class Program
    {
        private const string AppSettingsFile = "appsettings.json";
        private const string FinishDateKey = "finishDate";
        private const string StartDateKey = "startDate";

        private static IConfiguration _configuration;
        private static string _currentProject;
        private static List<WebApiTeam> _currentTeams;
        private static int _iterationLength;
        private static TeamHttpClient _teamHttpClient;
        private static DateTime _today;
        private static VssConnection _vssConnection;
        private static WorkHttpClient _workHttpClient;
        private static WorkItemTrackingHttpClient _workItemTrackingHttpClient;

        private static void AssignIterationToTeams(WorkItemClassificationNode workItemClassificationNode)
        {
            var teamSettingsIteration = new TeamSettingsIteration { Id = workItemClassificationNode.Identifier };

            foreach (var teamContext in _currentTeams.Select(webApiTeam => new TeamContext(_currentProject, webApiTeam.Name)))
                _workHttpClient.PostTeamIterationAsync(teamSettingsIteration, teamContext).SyncResult();
        }

        private static void CreateIteration(string iterationNamePrefix, int iterationNumber, DateTime startDate)
        {
            var workItemClassificationNode = new WorkItemClassificationNode
            {
                Name = $"{iterationNamePrefix} {iterationNumber}",
                StructureType = TreeNodeStructureType.Iteration,
                Attributes = new Dictionary<string, object>
                {
                    { "startDate", startDate },
                    // Need to count the first day when calculating the finish date 
                    { "finishDate", startDate.AddDays(_iterationLength - 1) }
                }
            };

            var createdWorkItemClassificationNode = _workItemTrackingHttpClient.CreateOrUpdateClassificationNodeAsync(
                workItemClassificationNode,
                _currentProject,
                TreeStructureGroup.Iterations).Result;

            if (createdWorkItemClassificationNode == null)
                throw new Exception($"Unable to create the iteration `{_currentProject} - {iterationNamePrefix}`");

            AssignIterationToTeams(createdWorkItemClassificationNode);
        }

        private static void CreateIterations(string iterationNamePrefix, int iterationsToCreate, int iterationNumber, DateTime startDate)
        {
            var iterationsCreated = 0;

            while (iterationsCreated < iterationsToCreate)
            {
                CreateIteration(iterationNamePrefix, iterationNumber, startDate);

                iterationsCreated++;
                iterationNumber++;
                startDate = startDate.AddDays(_iterationLength);
            }
        }

        private static WorkItemClassificationNode GetCurrentIterationWorkItemClassificationNode(WorkItemClassificationNode workItemClassificationNode)
        {
            return workItemClassificationNode.Children
                .SingleOrDefault(wicn => (DateTime)wicn.Attributes[StartDateKey] < _today && _today < (DateTime)wicn.Attributes[FinishDateKey]);
        }

        private static WorkItemClassificationNode GetRootIterationWorkItemClassificationNode(string project)
        {
            return _workItemTrackingHttpClient.GetClassificationNodeAsync(
                project,
                TreeStructureGroup.Iterations,
                null,
                1).Result;
        }

        private static void Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .AddCommandLine(args)
                .AddEnvironmentVariables()
                .AddJsonFile(AppSettingsFile, true, true)
#if DEBUG
                .AddUserSecrets<Program>()
#endif
                .Build();

            _today = DateTime.Now;

            var azureDevOpsPat = _configuration.GetSection("AzureDevOps:PersonalAccessToken").Value;
            var azureDevOpsUri = _configuration.GetSection("AzureDevOps:Uri").Value;
            var iterationsToCreate = int.Parse(_configuration.GetSection("AzureDevOps:IterationsToCreate").Value);
            var iterationNamePrefix = _configuration.GetSection("AzureDevOps:IterationNamePrefix").Value;

            _iterationLength = int.Parse(_configuration.GetSection("AzureDevOps:IterationLength").Value);

            var projects = _configuration
                .GetSection("AzureDevOps:Projects")
                .GetChildren()
                .AsEnumerable()
                .Select(configurationSection => configurationSection.Value);

            _vssConnection = new VssConnection(new Uri(azureDevOpsUri), new VssBasicCredential(string.Empty, azureDevOpsPat));

            _workHttpClient = _vssConnection.GetClient<WorkHttpClient>();
            _workItemTrackingHttpClient = _vssConnection.GetClient<WorkItemTrackingHttpClient>();
            _teamHttpClient = _vssConnection.GetClient<TeamHttpClient>();

            foreach (var project in projects)
            {
                _currentProject = project;

                _currentTeams = _teamHttpClient.GetTeamsAsync(project).Result;

                var azureDevOpsIterations = GetRootIterationWorkItemClassificationNode(project);

                //TODO: Check for default iterations

                var hasIterations = azureDevOpsIterations.HasChildren ?? false;
                var hasDatesSet = azureDevOpsIterations.Children?
                                      .All(workItemClassificationNode =>
                                          workItemClassificationNode.Attributes != null &&
                                          workItemClassificationNode.Attributes.ContainsKey(StartDateKey) &&
                                          workItemClassificationNode.Attributes.ContainsKey(FinishDateKey))
                                  ?? false;

                if (!hasIterations)
                {
                    var startDate = DateTime.Parse(_configuration.GetSection("AzureDevOps:IterationOneDate").Value);

                    CreateIterations(iterationNamePrefix, iterationsToCreate, 1, startDate);
                }
                else if (hasDatesSet)
                {
                    int iterationNumber;
                    DateTime iterationStartDate;

                    var currentIteration = GetCurrentIterationWorkItemClassificationNode(azureDevOpsIterations);

                    while (currentIteration == null)
                    {
                        var latestIteration = azureDevOpsIterations.Children.OrderByDescending(wicn => (DateTime)wicn.Attributes[FinishDateKey]).First();

                        iterationNumber = int.Parse(latestIteration.Name.Replace($"{iterationNamePrefix} ", string.Empty)) + 1;
                        iterationStartDate = ((DateTime)latestIteration.Attributes[FinishDateKey]).AddDays(1);

                        CreateIteration(iterationNamePrefix, iterationNumber, iterationStartDate);

                        azureDevOpsIterations = GetRootIterationWorkItemClassificationNode(project);
                        currentIteration = GetCurrentIterationWorkItemClassificationNode(azureDevOpsIterations);
                    }

                    iterationNumber = int.Parse(currentIteration.Name.Replace($"{iterationNamePrefix} ", string.Empty)) + 1;
                    iterationStartDate = ((DateTime)currentIteration.Attributes[FinishDateKey]).AddDays(1);

                    for (var index = 0; index < iterationsToCreate; index++)
                    {
                        var iterationName = $"{iterationNamePrefix} {iterationNumber}";

                        if (azureDevOpsIterations.Children.All(wicn => wicn.Name != iterationName))
                            CreateIteration(iterationNamePrefix, iterationNumber, iterationStartDate);

                        iterationNumber++;
                        iterationStartDate = iterationStartDate.AddDays(_iterationLength);
                    }
                }
                else
                {
                    throw new NotImplementedException("Logic for the current state of Azure DevOps has not been implemented.");
                }
            }
        }
    }
}
