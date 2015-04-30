﻿using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
using Microsoft.Research.DecisionService.Common;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using MultiWorldTesting;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClientDecisionService
{
    /// <summary>
    /// Encapsulates logic for recorder with async server communications & policy update.
    /// </summary>
    public class DecisionService<TContext> : IDisposable
    {
        public DecisionService(DecisionServiceConfiguration<TContext> config)
        {
            explorer = config.Explorer;

            if (!config.OfflineMode)
            {
                this.logger = config.Logger ?? new DecisionServiceLogger<TContext>(
                    config.BatchConfig,
                    config.ContextJsonSerializer,
                    config.AuthorizationToken,
                    config.LoggingServiceAddress);

                this.commandCenterBaseAddress = config.CommandCenterAddress ?? DecisionServiceConstants.CommandCenterAddress;
                this.DownloadSettings(config.AuthorizationToken);

                this.blobPollDelay = config.PollingPeriod == TimeSpan.Zero ? DecisionServiceConstants.PollDelay : config.PollingPeriod;

                this.blobUpdater = new AzureBlobUpdater(
                    "settings",
                    this.applicationSettingsBlobUri,
                    this.applicationConnectionString,
                    config.BlobOutputDir,
                    this.blobPollDelay,
                    this.UpdateSettings,
                    config.SettingsPollFailureCallback);

                this.policy = new DecisionServicePolicy<TContext>(
                    this.applicationModelBlobUri, this.applicationConnectionString,
                    config.BlobOutputDir,
                    this.blobPollDelay,
                    this.UpdatePolicy,
                    config.ModelPollFailureCallback);
            }
            else
            {
                this.logger = config.Logger;
                if (this.logger == null)
                {
                    throw new ArgumentException("A custom logger must be defined when operating in offline mode.", "Logger");
                }
            }

            mwt = new MwtExplorer<TContext>(config.AuthorizationToken, this.logger);
        }

        /*ReportSimpleReward*/
        public void ReportReward(float reward, string uniqueKey)
        {
            logger.ReportReward(reward, uniqueKey);
        }

        public void ReportOutcome(string outcomeJson, string uniqueKey)
        {
            logger.ReportOutcome(outcomeJson, uniqueKey);
        }

        public uint ChooseAction(string uniqueKey, TContext context)
        {
            return mwt.ChooseAction(explorer, uniqueKey, context);
        }

        public void Flush()
        {
            if (blobUpdater != null)
            {
                blobUpdater.StopPolling();
            }

            if (policy != null)
            {
                policy.StopPolling();
            }

            if (logger != null)
            {
                logger.Flush();
            }
        }

        public void Dispose() { }

        private void DownloadSettings(string token)
        {
            string serviceConnectionString = System.Configuration.ConfigurationManager.ConnectionStrings["AzureStorageConnectionString"].ConnectionString;
            
            CloudStorageAccount storageAccount = null;
            bool accountFound = CloudStorageAccount.TryParse(serviceConnectionString, out storageAccount);
            if (!accountFound || storageAccount == null)
            {
                throw new Exception("Could not connect to Azure storage for the service.");
            }

            try
            {
                CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
                blobClient.DefaultRequestOptions.RetryPolicy = new ExponentialRetry(DecisionServiceConstants.RetryDeltaBackoff, DecisionServiceConstants.RetryCount);

                CloudBlobContainer settingsContainer = blobClient.GetContainerReference(string.Format(DecisionServiceConstants.SettingsContainerName, token));
                CloudBlockBlob settingsBlob = settingsContainer.GetBlockBlobReference(DecisionServiceConstants.LatestSettingsBlobName);

                using (var ms = new MemoryStream())
                {
                    settingsBlob.DownloadToStream(ms);
                    string metadataJson = Encoding.UTF8.GetString(ms.ToArray());

                    var metadata = JsonConvert.DeserializeObject<ApplicationTransferMetadata>(metadataJson);
                    this.applicationConnectionString = metadata.ConnectionString;
                    this.applicationSettingsBlobUri = metadata.SettingsBlobUri;
                    this.applicationModelBlobUri = metadata.ModelBlobUri;

                    this.explorer.EnableExplore(metadata.IsExplorationEnabled);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Unable to retrieve application settings from Azure blob storage: " + ex.ToString());
            }
        }

        private void UpdateSettings(string settingsFile)
        {
            try
            {
                string metadataJson = File.ReadAllText(settingsFile);
                var metadata = JsonConvert.DeserializeObject<ApplicationTransferMetadata>(metadataJson);

                this.explorer.EnableExplore(metadata.IsExplorationEnabled);
            }
            catch (Exception ex)
            {
                if (ex is JsonReaderException)
                {
                    Trace.TraceWarning("Cannot read new settings.");
                }
                else
                {
                    throw;
                }
            }
            
        }

        private void UpdatePolicy()
        {
            IConsumePolicy<TContext> consumePolicy = explorer as IConsumePolicy<TContext>;
            if (consumePolicy != null)
            {
                consumePolicy.UpdatePolicy(policy);
                Trace.TraceInformation("Model update succeeded.");
            }
            else
            {
                // TODO: how to handle updating policies for Bootstrap explorers?
                throw new NotSupportedException("This type of explorer does not currently support updating policy functions.");
            }
        }

        public IRecorder<TContext> Recorder { get { return logger; } }
        public IPolicy<TContext> Policy { get { return policy; } }

        private readonly string commandCenterBaseAddress;
        private readonly TimeSpan blobPollDelay;

        AzureBlobUpdater blobUpdater;

        private string applicationConnectionString;
        private string applicationSettingsBlobUri;
        private string applicationModelBlobUri;

        private readonly IExplorer<TContext> explorer;
        private readonly ILogger<TContext> logger;
        private readonly DecisionServicePolicy<TContext> policy;
        private readonly MwtExplorer<TContext> mwt;
    }
}
