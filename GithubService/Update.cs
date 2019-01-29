using GithubService.Models;
using GithubService.Models.Webhooks;
using GithubService.Repository;
using GithubService.Services;
using GithubService.Services.Clients;
using GithubService.Services.Converters;
using GithubService.Services.Interfaces;
using GithubService.Services.Parsers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GithubService
{
    public static class Update
    {
        [FunctionName("kcd-github-service-update")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest request,
            ILogger logger)
        {
            logger.LogInformation("Update called.");

            var fileParser = new FileParser();

            // Get all the files from GitHub
            var githubClient = new GithubClient(
                Environment.GetEnvironmentVariable("Github.RepositoryName"),
                Environment.GetEnvironmentVariable("Github.RepositoryOwner"),
                Environment.GetEnvironmentVariable("Github.AccessToken"));
            var githubService = new Services.GithubService(githubClient, fileParser);

            // Read Webhook message from GitHub
            WebhookMessage webhookMessage;
            using (var streamReader = new StreamReader(request.Body, Encoding.UTF8))
            {
                var requestBody = streamReader.ReadToEnd();
                webhookMessage = JsonConvert.DeserializeObject<WebhookMessage>(requestBody);
            }

            // Get paths to added/modified/deleted files
            var parser = new WebhookParser();
            var (addedFiles, modifiedFiles, removedFiles) = parser.ExtractFiles(webhookMessage);

            var connectionString = Environment.GetEnvironmentVariable("Repository.ConnectionString");
            var codeFileRepository = await CodeFileRepository.CreateInstance(connectionString);

            var codeConverter = new CodeConverter();
            var kenticoCloudClient = new KenticoCloudClient(
                Environment.GetEnvironmentVariable("KenticoCloud.ProjectId"),
                Environment.GetEnvironmentVariable("KenticoCloud.ContentManagementApiKey"),
                Environment.GetEnvironmentVariable("KenticoCloud.InternalApiKey")
            );

            var kenticoCloudService = new KenticoCloudService(kenticoCloudClient, codeConverter);

            ProcessAddedFiles(addedFiles, codeFileRepository, githubService, kenticoCloudService);
            ProcessModifiedFiles(modifiedFiles, codeFileRepository, githubService, kenticoCloudService, logger);
            ProcessRemovedFiles(removedFiles.ToArray(), codeFileRepository, kenticoCloudService);

            return new OkObjectResult("Updated.");
        }

        private static async void ProcessAddedFiles(
            ICollection<string> addedFiles,
            ICodeFileRepository codeFileRepository,
            IGithubService githubService,
            IKenticoCloudService kenticoCloudService)
        {
            if (!addedFiles.Any())
                return;

            var codeFiles = new List<CodeFile>();

            foreach (var filePath in addedFiles)
            {
                var codeFile = await githubService.GetCodeFileAsync(filePath);

                await codeFileRepository.StoreAsync(codeFile);
                codeFiles.Add(codeFile);
            }

            var codeConverter = new CodeConverter();
            var fragmentsByCodename = codeConverter.ConvertToCodenameCodeFragments(codeFiles.SelectMany(file => file.CodeFragments));

            foreach (var fragments in fragmentsByCodename)
            {
                await kenticoCloudService.UpsertCodeFragmentsAsync(fragments);
            }
        }

        private static async void ProcessModifiedFiles(
            ICollection<string> modifiedFiles,
            ICodeFileRepository codeFileRepository,
            IGithubService githubService,
            IKenticoCloudService kenticoCloudService,
            ILogger logger)
        {
            if (!modifiedFiles.Any())
                return;

            var fragmentsToRemove = new List<CodeFragment>();
            var fragmentsToUpsert = new List<CodeFragment>();

            var codeConverter = new CodeConverter();

            foreach (var filePath in modifiedFiles)
            {
                var oldCodeFile = await codeFileRepository.GetAsync(filePath);

                var newCodeFile = await githubService.GetCodeFileAsync(filePath);
                await codeFileRepository.StoreAsync(newCodeFile);

                if (oldCodeFile == null)
                {
                    logger.LogWarning($"Trying to modify code file {filePath} might result in inconsistent content in KC because there is no known previous version of the code file.");

                    fragmentsToUpsert.AddRange(newCodeFile.CodeFragments);
                }
                else
                {
                    var (newFragments, modifiedFragments, removedFragments) = codeConverter.CompareFragmentLists(oldCodeFile.CodeFragments, newCodeFile.CodeFragments);
                    fragmentsToUpsert.AddRange(newFragments);
                    fragmentsToUpsert.AddRange(modifiedFragments);
                    fragmentsToRemove.AddRange(removedFragments);
                }
            }

            codeConverter.ConvertToCodenameCodeFragments(fragmentsToRemove)
                .Select(async fragments => await kenticoCloudService.RemoveCodeFragmentsAsync(fragments));

            codeConverter.ConvertToCodenameCodeFragments(fragmentsToUpsert)
                .Select(async fragments => await kenticoCloudService.UpsertCodeFragmentsAsync(fragments));
        }

        private static async void ProcessRemovedFiles(
            ICollection<string> removedFiles,
            ICodeFileRepository codeFileRepository,
            IKenticoCloudService kenticoCloudService)
        {
            if (!removedFiles.Any())
                return;

            var codeFiles = new List<CodeFile>();

            foreach (var removedFile in removedFiles)
            {
                var archivedFile = await codeFileRepository.ArchiveAsync(removedFile);

                if (archivedFile != null)
                {
                    codeFiles.Add(archivedFile);
                }
            }

            var codeConverter = new CodeConverter();
            var fragmentsByCodename = codeConverter.ConvertToCodenameCodeFragments(codeFiles.SelectMany(file => file.CodeFragments));

            foreach (var fragments in fragmentsByCodename)
            {
                await kenticoCloudService.RemoveCodeFragmentsAsync(fragments);
            }
        }
    }
}
