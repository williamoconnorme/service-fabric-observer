using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aggregator;
using FabricObserver.Observers.Utilities;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Runtime;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace Collector
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class Collector : StatelessService
    {
        // CT: Don't create a new FabricClient each iteration. Just use one instance. This is important for memory use.
        private readonly FabricClient fabricClient;
        private readonly ObserverHealthReporter healthReporter;
        private readonly Logger logger;

        public Collector(StatelessServiceContext context)
            : base(context)
        {
            fabricClient = new FabricClient();
            
            // CT: you can use FO Logger to stuff to Collector folder.
            logger = new Logger("Collector");
            healthReporter = new ObserverHealthReporter(logger, fabricClient);
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new ServiceInstanceListener[0];
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {

            //Remote Procedure Call to Aggregator
            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                new Uri("fabric:/Internship/Aggregator"),
                new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                );
            string nodeName = this.Context.NodeContext.NodeName;
            // CT: if the runtime token is cancelled before the next iteration, we will escape the loop. And... not block the runtime from closing down the service.
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    //ServiceEventSource.Current.ServiceMessage(this.Context, "Working-{0}", ++iterations);
                 
                    var (_, delta) = SFUtilities.getTime();

                    // CT: This will throw when the cancellation token is cancelled if it happens after the iteration started.
                    // This will take down the replica (which is what we want here).
                    await Task.Delay(TimeSpan.FromMilliseconds(delta), cancellationToken);

                    NodeData data= await CollectBatching(SFUtilities.intervalMiliseconds / 10, 7, cancellationToken);

                    // CT: Add the runtime cancellationToken to this async call to have it return immediately when the runtime token is cancelled.
                    // You should await this call.
                    
                    await AggregatorProxy.PutDataRemote(nodeName, ByteSerialization.ObjectToByteArray(data));
                }
                // CT: Don't handle the exception thrown when an operation is cancelled. Here, cancellationToken here is the SF runtime cancellation token.
                // If you do not honor its cancellation, then SF will be blocked from progressing in replica/service Close.
                // We don't care about logging this type of exception here. So, let it pass through and take things down. For unexpected errors, handle them and log, then throw.
                catch (Exception e) when (!(e is OperationCanceledException)) 
                {
                    /*var healthInfo = new HealthInformation("99", "Collector", HealthState.Warning)
                    {
                        Description = e.ToString()
                    };*/

                    //var appHealthReport = new ApplicationHealthReport(new Uri("fabric:/Internship"), healthInfo);
                    var appHealthReport = new HealthReport
                    {
                        AppName = new Uri("fabric:/Internship"),
                        Code = "99",
                        HealthMessage = $"Unhandled exception collecting perf data: {e}",
                        NodeName = Context.NodeContext.NodeName,
                        PartitionId = Context.PartitionId,
                        ReplicaOrInstanceId = Context.ReplicaOrInstanceId,
                        ReportType = HealthReportType.Application,
                        ServiceName = Context.ServiceName,
                        SourceId = "Collector",
                        Property = "Data Collection Failure",
                        State = HealthState.Warning,
                        HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                        EmitLogEvent = true, // This info will also be written to C:\fabric_observer_logs\Collector folder
                    };

                    healthReporter.ReportHealthToServiceFabric(appHealthReport);
                    //fabricClient.HealthManager.ReportHealth(appHealthReport);

                    throw; // CT: Fix the bugs. This will take down the replica/service process. SF will then restart it, per usual.
                }
            }
        }

        private async Task<NodeData> CollectBatching(double intervalMiliseconds,int batchCount, CancellationToken cancellationToken)
        {
            List<NodeData> nodeDataList = new List<NodeData>(); 

            for(int j = 0; j < batchCount; j++)
            {
                // Collect Data
                float cpu = CpuUtilizationProvider.Instance.GetProcessorTimePercentage();
                var (TotalMemoryGb, MemoryInUseMb, PercentInUse) = OSInfoProvider.Instance.TupleGetMemoryInfo();
                DriveInfo[] allDrives = DriveInfo.GetDrives();
                string nodeName = this.Context.NodeContext.NodeName;

                
                var (totalMiliseconds, _) = SFUtilities.getTime();

                Dictionary<int, ProcessData> pids = await SFUtilities.Instance.GetDeployedProcesses(nodeName);
                List<ProcessData> processDataList = new List<ProcessData>();

                //for instead of Parallel.For because Parallel.For bursts the CPU before we load it and this is more lightweigh on the cpu even though timing isn't exact. This is more relevant.
                // CT: The problem with sequential here is that if there are a *lot* of service processes this could take a long time to complete. You can control the level
                // of parallelism (see what is done in AppObserver, for example). I do not see bursts of CPU in FO with Parallelism enabled (that is, at the default level of parallelism, which is 1/4 of available CPUs).
                // For now (MVP), this is fine.
                double totalSfCpu = 0, totalSfRamMB = 0, totalSfRamPercentage = 0;
                for (int i = 0; i < pids.Count; i++)
                {
                    int pid = pids.ElementAt(i).Key;
                    var processData = pids[pid];
                    var (cpuPercentage, ramMb) = await SFUtilities.Instance.TupleGetResourceUsageForProcess(pid);
                    processData.cpuPercentage = cpuPercentage;
                    processData.ramMb = ramMb;

                    float ramPercentage = 100;
                    if (TotalMemoryGb > 0) ramPercentage = (100 * ramMb) / (1024 * TotalMemoryGb);

                    processData.ramPercentage = ramPercentage;
                    processDataList.Add(processData);
                    totalSfCpu += cpuPercentage;
                    totalSfRamMB += ramMb;
                    totalSfRamPercentage += ramPercentage;

                }

                var data = new NodeData(totalMiliseconds, nodeName)
                {
                    hardware = new Hardware(cpu, TotalMemoryGb, MemoryInUseMb, PercentInUse, allDrives),
                    processList = processDataList,
                    sfHardware = new Hardware((float)totalSfCpu, TotalMemoryGb, (long)totalSfRamMB, totalSfRamPercentage, null)
                };

                nodeDataList.Add(data);
                await Task.Delay(TimeSpan.FromMilliseconds(intervalMiliseconds), cancellationToken);

            }

            return NodeData.AverageNodeData(nodeDataList);
        }
    }
}