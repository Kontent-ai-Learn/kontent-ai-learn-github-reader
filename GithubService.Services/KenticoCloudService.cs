﻿using GithubService.Models.KenticoCloud;
using GithubService.Services.Interfaces;
using KenticoCloud.ContentManagement.Exceptions;
using KenticoCloud.ContentManagement.Models.Items;
using System;
using System.Net;
using System.Threading.Tasks;
using GithubService.Models;

namespace GithubService.Services
{
    public class KenticoCloudService : IKenticoCloudService
    {
        private readonly IKenticoCloudClient _kcClient;
        private readonly ICodeConverter _codeConverter;

        public KenticoCloudService(IKenticoCloudClient kcClient, ICodeConverter codeConverter)
        {
            _kcClient = kcClient;
            _codeConverter = codeConverter;
        }

        public async Task<CodeSamples> UpsertCodeFragmentsAsync(CodenameCodeFragments fragment)
        {
            var codeSamples = _codeConverter.ConvertToCodeSamples(fragment);
            var contentItem = await EnsureCodeSamplesItemAsync(fragment.Codename);

            return await EnsureCodeSamplesVariantAsync(contentItem, codeSamples);
        }

        public Task DeleteCodeFragmentsAsync(CodenameCodeFragments fragment)
        {
            throw new NotImplementedException();
        }

        private async Task<ContentItemModel> EnsureCodeSamplesItemAsync(string codename)
        {
            try
            {
                // Try to get the content item from KC using codename
                return await _kcClient.GetContentItemAsync(codename);
            }
            catch (ContentManagementException exception)
            {
                if (exception.StatusCode != HttpStatusCode.NotFound)
                    throw;

                // Content item doesn't exist in KC -> create it
                var codeSamplesItem = new ContentItemCreateModel
                {
                    Type = ContentTypeIdentifier.ByCodename("code_samples"),
                    Name = codename
                };
                return await _kcClient.CreateContentItemAsync(codeSamplesItem);
            }
        }

        private async Task<CodeSamples> EnsureCodeSamplesVariantAsync(ContentItemModel contentItem, CodeSamples codeSamples)
        {
            try
            {
                // Try to update the content variant in KC
                return await _kcClient.UpsertCodeSamplesVariantAsync(contentItem, codeSamples);
            }
            catch (ContentManagementException exception)
            {
                if (!exception.Message.Contains("Cannot update published content"))
                    throw;

                // The variant seems to be published -> create new version in KC
                await _kcClient.CreateNewVersionOfDefaultVariantAsync(contentItem);

                // The content variant should be updated correctly now
                return await _kcClient.UpsertCodeSamplesVariantAsync(contentItem, codeSamples);
            }
        }
    }
}
