﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.Common.Configurations;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.DeviceAdmin.Infrastructure.BusinessLogic;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.DeviceAdmin.Infrastructure.Models;
using Microsoft.ServiceBus.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

//MDS bae 2017.0626
using Microsoft.WindowsAzure.Storage.Blob;
using System.Linq;
using System.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.DeviceAdmin.Web.Controllers;

namespace Microsoft.Azure.Devices.Applications.RemoteMonitoring.EventProcessor.WebJob.Processors
{
    public class ActionProcessor : IEventProcessor
    {
        //MDS bae 2017.0626
        public static CloudBlobClient blobClient;
        public const string blobContainerName = "flirimage";
        public static CloudBlobContainer blobContainer;
        public string flirimageurl;
        public static ServiceClient iotHubServiceClient { get; private set; }

        private readonly IActionLogic _actionLogic;
        private readonly IActionMappingLogic _actionMappingLogic;
        private readonly IConfigurationProvider _configurationProvider;

        private int _totalMessages = 0;
        private Stopwatch _checkpointStopwatch;

        public ActionProcessor(
            IActionLogic actionLogic,
            IActionMappingLogic actionMappingLogic,
            IConfigurationProvider configurationProvider)
        {
            this.LastMessageOffset = "-1";
            _actionLogic = actionLogic;
            _actionMappingLogic = actionMappingLogic;
            _configurationProvider = configurationProvider;
        }

        public event EventHandler ProcessorClosed;

        public bool IsInitialized { get; private set; }

        public bool IsClosed { get; private set; }

        public bool IsReceivedMessageAfterClose { get; set; }

        public int TotalMessages
        {
            get { return _totalMessages; }
        }

        public CloseReason CloseReason { get; private set; }

        public PartitionContext Context { get; private set; }

        public string LastMessageOffset { get; private set; }

        public Task OpenAsync(PartitionContext context)
        {
            Trace.TraceInformation("ActionProcessor: Open : Partition : {0}", context.Lease.PartitionId);
            this.Context = context;
            _checkpointStopwatch = new Stopwatch();
            _checkpointStopwatch.Start();

            this.IsInitialized = true;

            return Task.Delay(0);
        }


        public async Task ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> messages)
        {
            Trace.TraceInformation("ActionProcessor: In ProcessEventsAsync");
            foreach (EventData message in messages)
            {
                try
                {
                    Trace.TraceInformation("ActionProcessor: {0} - Partition {1}", message.Offset, context.Lease.PartitionId);
                    this.LastMessageOffset = message.Offset;

                    string jsonString = Encoding.UTF8.GetString(message.GetBytes());
                    IList<ActionModel> results = JsonConvert.DeserializeObject<List<ActionModel>>(jsonString);
                    if (results != null)
                    {
                        foreach (ActionModel item in results)
                        {                            
                            await ProcessAction(item);
                        }
                    }

                    ++_totalMessages;
                }
                catch (Exception e)
                {
                    Trace.TraceError("ActionProcessor: Error in ProcessEventAsync -- " + e.ToString());
                }
            }

            // checkpoint after processing batch
            try
            {
                await context.CheckpointAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceError(
                    "{0}{0}*** CheckpointAsync Exception - ActionProcessor.ProcessEventsAsync ***{0}{0}{1}{0}{0}",
                    Console.Out.NewLine,
                    ex);
            }

            if (this.IsClosed)
            {
                this.IsReceivedMessageAfterClose = true;
            }
        }

        private async Task ProcessAction(ActionModel eventData)
        {
            if (eventData == null)
            {
                Trace.TraceWarning("Action event is null");
                return;
            }
            
            try
            {
                // NOTE: all column names from ASA come out as lowercase; see 
                // https://social.msdn.microsoft.com/Forums/office/en-US/c79a662b-5db1-4775-ba1a-23df1310091d/azure-table-storage-account-output-property-names-are-lowercase?forum=AzureStreamAnalytics 

                string deviceId = eventData.DeviceID;
                string ruleOutput = eventData.RuleOutput;

                if (ruleOutput.Equals("AlarmTemp", StringComparison.OrdinalIgnoreCase))
                {
                    //MDS bae 2017.0626
                    string iotHubConnectionString = "HostName=soulbrainsuite03c80.azure-devices.net;SharedAccessKeyName=iothubowner;SharedAccessKey=imdKYODaeMsTQVqE3Z/rrHUC4Lne8Azg8I7Grf4fBU8=";
                    iotHubServiceClient = ServiceClient.CreateFromConnectionString(iotHubConnectionString);
                    string commandParameterNew = "{\"Name\":\"CaptureImage\",\"Parameters\":{}}";
                    await iotHubServiceClient.SendAsync(deviceId, new Message(Encoding.UTF8.GetBytes(commandParameterNew)));

                    //MDS bae 2017.0626
                    var appSettingsReader = new AppSettingsReader();
                    var connectionString = "DefaultEndpointsProtocol=https;AccountName=soulbrainsuite;AccountKey=40ke5Jv9rIwl+FXeJ6XHseP9xTwe6+0Dz6x1WlVwoitCm1nY05UUEwhne4WRcOT6RtFcPf4YWvZisdL9gjsKZA==;EndpointSuffix=core.windows.net";
                    CloudStorageAccount storageaccount = CloudStorageAccount.Parse(connectionString);
                    blobClient = storageaccount.CreateCloudBlobClient();
                    blobContainer = blobClient.GetContainerReference(blobContainerName);
                    await blobContainer.CreateIfNotExistsAsync();
                    await blobContainer.SetPermissionsAsync(new BlobContainerPermissions { PublicAccess = BlobContainerPublicAccessType.Blob });
                    CloudBlobDirectory blobDirectory = blobContainer.GetDirectoryReference(deviceId);
                    foreach (IListBlobItem blob in blobDirectory.ListBlobs())
                    {
                        if (blob.GetType() == typeof(CloudBlockBlob))
                        {
                            flirimageurl = blob.Uri.ToString();
                        }
                    }

                    Trace.TraceInformation("ProcessAction: temperature rule triggered!");
                    double tempReading = eventData.Reading;

                    string tempActionId = await _actionMappingLogic.GetActionIdFromRuleOutputAsync(ruleOutput);
                    
                    if (!string.IsNullOrWhiteSpace(tempActionId))
                    {
                        await _actionLogic.ExecuteLogicAppAsync(
                        tempActionId,
                        deviceId,
                        flirimageurl, //MDS bae 2017.0626 add imageurl parameter
                        "Temperature",
                        tempReading);                        
                    }
                    else
                    {
                        Trace.TraceError("ActionProcessor: tempActionId value is empty for temperatureRuleOutput '{0}'", ruleOutput);
                    }
                }

                if (ruleOutput.Equals("AlarmHumidity", StringComparison.OrdinalIgnoreCase))
                {
                    Trace.TraceInformation("ProcessAction: humidity rule triggered!");
                    double humidityReading = eventData.Reading;

                    string humidityActionId = await _actionMappingLogic.GetActionIdFromRuleOutputAsync(ruleOutput);

                    if (!string.IsNullOrWhiteSpace(humidityActionId))
                    {
                        await _actionLogic.ExecuteLogicAppAsync(
                            humidityActionId,
                            deviceId,
                            flirimageurl, //MDS bae 2017.0626 add imageurl parameter
                            "Humidity",
                            humidityReading);
                    }
                    else
                    {
                        Trace.TraceError("ActionProcessor: humidityActionId value is empty for humidityRuleOutput '{0}'", ruleOutput);
                    }
                }
            }
            catch (Exception e)
            {
                Trace.TraceError("ActionProcessor: exception in ProcessAction:");
                Trace.TraceError(e.ToString());
            }
        }

        public Task CloseAsync(PartitionContext context, CloseReason reason)
        {
            Trace.TraceInformation("ActionProcessor: Close : Partition : " + context.Lease.PartitionId);
            this.IsClosed = true;
            _checkpointStopwatch.Stop();
            this.CloseReason = reason;
            this.OnProcessorClosed();

            try
            {
                return context.CheckpointAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceError(
                    "{0}{0}*** CheckpointAsync Exception - ActionProcessor.CloseAsync ***{0}{0}{1}{0}{0}",
                    Console.Out.NewLine,
                    ex);

                return Task.Run(() => { });
            }
        }

        public virtual void OnProcessorClosed()
        {
            EventHandler handler = this.ProcessorClosed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
