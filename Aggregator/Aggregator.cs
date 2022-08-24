using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Query;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using StatefulService = Microsoft.ServiceFabric.Services.Runtime.StatefulService;

namespace Aggregator
{
    /// <summary>
    /// This service implements the aggregation logic. It forms Snapshots periodically.
    /// It exposes an API (RPC) to fetch and put data.
    /// </summary>
    internal class Aggregator : StatefulService, IMyCommunication
    {
        private CancellationToken Token 
        { 
            get; set; 
        }

        public Aggregator(StatefulServiceContext context)
            : base(context)
        { }

        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(SFUtilities.intervalMiliseconds), cancellationToken);

                while (await GetMinQueueCountAsync() > 1)
                {
                    await ProduceSnapshotAsync();
                    await TryPruneAsync();
                }
            }
        }

        public async Task PutDataRemote(string queueName,byte[] data)
        {
            await AddDataAsync(queueName, data);
        }

        public async Task<List<byte[]>> GetDataRemote(string queueName)
        {
            return await GetDataAsync(queueName);
        }

        public async Task<List<Snapshot>> GetSnapshotsRemote(double milisecondsLow, double milisecondsHigh)
        {
            List<Snapshot> list = new List<Snapshot>();
            var stateManager = StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(Snapshot.queueName);
            try
            {
                using var tx = stateManager.CreateTransaction();
                var iterator = (await reliableQueue.CreateEnumerableAsync(tx)).GetAsyncEnumerator();

                byte[] data = null;
                while (await iterator.MoveNextAsync(Token))
                {
                    data = iterator.Current;

                    if (data != null)
                    {
                        Snapshot s = (Snapshot)ByteSerialization.ByteArrayToObject(data);
                        if (s.Miliseconds >= milisecondsLow && s.Miliseconds <= milisecondsHigh)
                        { 
                            list.Add(s); 
                        }
                        else
                        {
                            s = null;
                            data = null;
                        }
                    }
                }
            }
            catch
            {
               
            }

            return list;
        }

        /// <summary>
        /// ClearAsync in not implemented for the RealiableQueue?!
        /// Workaround would be to dequeue each element as implemented for pruning
        /// </summary>
        /// <returns></returns>
        public async Task DeleteAllSnapshotsRemote()
        {
            using var tx = StateManager.CreateTransaction();
            try
            {
                await (await StateManager.GetOrAddAsync<IReliableQueue<byte[]>>(Snapshot.queueName)).ClearAsync();
                await tx.CommitAsync();
            }
            catch
            {
                
            }
        }

        private async Task<Snapshot> CreateSnapshot()
        {
            NodeList nodeList = await SFUtilities.Instance.GetNodeListAsync();
            List<NodeData> nodeDataList = new List<NodeData>();
            ClusterData clusterData = null;
            bool checkTime;
            double minTime = await MinTimeStampInQueueAsync(nodeList);
            bool success = true;

            if (minTime == -1)
            {
                // queues empty
                return null;
            }

            byte[] clusterDataBytes = await PeekFirstAsync(ClusterData.QueueName);

            if (clusterDataBytes != null)
            {
                clusterData = (ClusterData)ByteSerialization.ByteArrayToObject(clusterDataBytes);
                checkTime = Snapshot.CheckTime(minTime, clusterData.Milliseconds);

                if (!checkTime)
                {
                    success = false; //Snapshot must contain SFData
                }
                else
                {
                    await DequeueAsync(ClusterData.QueueName);
                }
            }
            else
            {
                success = false; //QUEUE is empty
            }

            foreach(var node in nodeList)
            {
                byte[] nodeDataBytes = await PeekFirstAsync(node.NodeName);

                if (nodeDataBytes == null)
                {
                    continue; // QUEUE in empty
                }

                NodeData nodeData = (NodeData)ByteSerialization.ByteArrayToObject(nodeDataBytes);
                checkTime = Snapshot.CheckTime(minTime, nodeData.Milliseconds);

                if (checkTime)
                {
                    nodeDataList.Add(nodeData);
                    await DequeueAsync(node.NodeName);
                }
            }

            if (nodeDataList.Count == 0)
            {
                success = false; //Snapshot must have at least 1 NodeData
            }

            if (success)
            {
                return new Snapshot(minTime, clusterData, nodeDataList);
            }

            return null; //Something failed
        }
        private async Task ProduceSnapshotAsync() 
        {
            try
            {
                Snapshot snap = await CreateSnapshot();

                if (snap != null)
                {
                    await AddDataAsync(Snapshot.queueName, ByteSerialization.ObjectToByteArray(snap));
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
            }
        }

        private async Task<long> GetMinQueueCountAsync()
        {
            long min_count = long.MaxValue;
            NodeList nodeList = await SFUtilities.Instance.GetNodeListAsync();

            using (var tx = StateManager.CreateTransaction())
            {
                long count;

                foreach (Node node in nodeList)
                {
                    var x = await StateManager.GetOrAddAsync<IReliableQueue<byte[]>>(node.NodeName);
                    count = await x.GetCountAsync(tx);

                    if (count < min_count)
                    {
                        min_count = count;
                    }
                }

                var y = await StateManager.GetOrAddAsync<IReliableQueue<byte[]>>(ClusterData.QueueName);
                count = await y.GetCountAsync(tx);

                if (count < min_count)
                {
                    min_count = count;
                }
            }

            return min_count;
        }
        /// <summary>
        /// returns the min timestamp at the beginning of all queues
        /// if none return -1
        /// </summary>
        /// <param name="nodeList"></param>
        /// <returns></returns>
        private async Task<double> MinTimeStampInQueueAsync(System.Fabric.Query.NodeList nodeList)
        {
            double timeStamp = -1;

            foreach (var node in nodeList)
            {
                var hw = await PeekFirstAsync(node.NodeName);

                if (hw != null)
                {
                    var data = (NodeData)ByteSerialization.ByteArrayToObject(hw);

                    if (data.Milliseconds < timeStamp || timeStamp == -1)
                    {
                        timeStamp = data.Milliseconds;
                    }
                }
            }

            var sf = await PeekFirstAsync(ClusterData.QueueName);

            if (sf != null)
            {
                var data = (ClusterData)ByteSerialization.ByteArrayToObject(sf);

                if (data.Milliseconds < timeStamp || timeStamp == -1)
                {
                    timeStamp = data.Milliseconds;
                }
            }

            return timeStamp;
        }

        
        private async Task<long> GetQueueCountAsync(string queueName)
        {
            long count = 0;

            using (var tx = StateManager.CreateTransaction())
            {
                var x = await StateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);
                count = await x.GetCountAsync(tx);
            }

            return count;
        }

        private async Task TryPruneAsync()
        {
            long count = await GetQueueCountAsync(Snapshot.queueName);

            if (count > Snapshot.queueCapacity)
            {
                await DequeueAsync(Snapshot.queueName);
            }
        }


        private async Task AddDataAsync(string queueName,byte[] data)
        {
            var stateManager = StateManager;
            IReliableQueue<byte[]> reliableQueue = null;

            while (reliableQueue == null) 
            {
                try
                {
                    reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);
                }
                catch
                { 
                
                }
            }

            using var tx = stateManager.CreateTransaction();
            await reliableQueue.EnqueueAsync(tx, data);
            await tx.CommitAsync();
        }

        private async Task<byte[]> PeekFirstAsync(string queueName)
        {
            var stateManager = StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);

            using var tx = stateManager.CreateTransaction();
            var conditional = await reliableQueue.TryPeekAsync(tx);

            if (conditional.HasValue)
            {
                return conditional.Value;
            }
            
            return null;
        }

        private async Task<byte[]> DequeueAsync(string queueName)
        {
            var stateManager = StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);

            using var tx = stateManager.CreateTransaction();
            var conditional = await reliableQueue.TryDequeueAsync(tx);
            await tx.CommitAsync();

            if (conditional.HasValue)
            {
                return conditional.Value;
            }

           return null;
        }

        public async Task<List<byte[]>> GetDataAsync(string queueName)
        {
            if (queueName == null) return null;
            List<byte[]> list= new List<byte[]>();

            var stateManager = StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);

            try 
            {
                using var tx = stateManager.CreateTransaction();
                var iterator = (await reliableQueue.CreateEnumerableAsync(tx)).GetAsyncEnumerator();
                byte[] data = null;

                while (await iterator.MoveNextAsync(Token))
                {
                    data = iterator.Current;

                    if (data != null)
                    {
                        list.Add(data);
                    }
                }
            }
            catch
            {
                
            }

            return list;
        }
        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see https://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return this.CreateServiceRemotingReplicaListeners();
        }
    }
}