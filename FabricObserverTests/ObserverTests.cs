﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver;
using FabricObserver.Observers;
using FabricObserver.Observers.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;
using ServiceFabric.Mocks;
using static ServiceFabric.Mocks.MockConfigurationPackage;
using System.Fabric.Description;
using System.Xml;
using FabricObserver.Observers.Utilities.Telemetry;
using Polly;

/***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***/

namespace FabricObserverTests
{
    [TestClass]
    public class ObserverTests
    {
        private const string NodeName = "_Node_0";
        private static readonly Uri ServiceName = new Uri("fabric:/app/service");
        private static readonly bool isSFRuntimePresentOnTestMachine = IsLocalSFRuntimePresent();
        private static readonly CancellationToken token = new CancellationToken();
        private static readonly FabricClient fabricClient = new FabricClient();
        private static readonly ICodePackageActivationContext CodePackageContext = null;
        private static readonly StatelessServiceContext _context = null;

        static ObserverTests()
        {
            /* SF runtime mocking care of ServiceFabric.Mocks by loekd.
               https://github.com/loekd/ServiceFabric.Mocks */

            string configPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "Settings.xml");
            ConfigurationPackage configPackage = BuildConfigurationPackageFromSettingsFile(configPath);

            CodePackageContext =
                new MockCodePackageActivationContext(
                        ServiceName.AbsoluteUri,
                        "applicationType",
                        "Code",
                        "1.0.0.0",
                        Guid.NewGuid().ToString(),
                        @"C:\Log",
                        @"C:\Temp",
                        @"C:\Work",
                        "ServiceManifest",
                        "1.0.0.0")
                {
                    ConfigurationPackage = configPackage
                };

            _context =
                new StatelessServiceContext(
                            new NodeContext(NodeName, new NodeId(0, 1), 0, "NodeType0", "TEST.MACHINE"),
                            CodePackageContext,
                            "FabricObserver.FabricObserverType",
                            ServiceName,
                            null,
                            Guid.NewGuid(),
                            long.MaxValue);
        }

        /* Helpers */

        private static ConfigurationPackage BuildConfigurationPackageFromSettingsFile(string configPath)
        {
            StringReader sreader = null;
            XmlReader xreader = null;

            try
            {
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    return null;
                }

                string configXml = File.ReadAllText(configPath);

                // Safe XML pattern - *Do not use LoadXml*.
                XmlDocument xdoc = new XmlDocument { XmlResolver = null };
                sreader = new StringReader(configXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null });
                xdoc.Load(xreader);

                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");
                var sectionNodes = xdoc.SelectNodes("//sf:Section", nsmgr);
                var configSections = new ConfigurationSectionCollection();

                if (sectionNodes != null)
                {
                    foreach (XmlNode node in sectionNodes)
                    {
                        ConfigurationSection configSection = CreateConfigurationSection(node?.Attributes?.Item(0).Value);
                        var sectionParams = xdoc.SelectNodes($"//sf:Section[@Name='{configSection.Name}']//sf:Parameter", nsmgr);
                        
                        if (sectionParams != null)
                        {
                            foreach (XmlNode node2 in sectionParams)
                            {
                                ConfigurationProperty parameter = CreateConfigurationSectionParameters(node2?.Attributes?.Item(0).Value, node2?.Attributes?.Item(1).Value);
                                configSection.Parameters.Add(parameter);
                            }
                        }

                        configSections.Add(configSection);
                    }

                    var configSettings = CreateConfigurationSettings(configSections);
                    ConfigurationPackage configPackage = CreateConfigurationPackage(configSettings, configPath.Replace("\\Settings.xml", ""));
                    return configPackage;
                }
            }
            finally
            {
                sreader.Dispose();
                xreader.Dispose();
            }

            return null;
        }

        private static bool IsLocalSFRuntimePresent()
        {
            try
            {
                var ps = Process.GetProcessesByName("Fabric");
                return ps.Length != 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task CleanupTestHealthReportsAsync(ObserverBase obs = null)
        {
            // Polly retry policy and async execution. Any other type of exception shall bubble up to caller as they are no-ops.
            await Policy.Handle<HealthReportNotFoundException>()
                            .Or<FabricException>()
                            .Or<TimeoutException>()
                            .WaitAndRetryAsync(
                                new[]
                                {
                                    TimeSpan.FromSeconds(1),
                                    TimeSpan.FromSeconds(5),
                                    TimeSpan.FromSeconds(10),
                                    TimeSpan.FromSeconds(15)
                                }).ExecuteAsync(() => CleanupTestHealthReportsAsyncInternal(obs)).ConfigureAwait(false);
        }

        private static async Task CleanupTestHealthReportsAsyncInternal(ObserverBase obs = null)
        {
            // Clear any existing user app, node or fabric:/System app Test Health Reports.
            var healthReport = new HealthReport
            {
                Code = FOErrorWarningCodes.Ok,
                HealthMessage = $"Clearing existing Test health reports.",
                State = HealthState.Ok,
                EntityType = EntityType.Service,
                NodeName = NodeName,
                EmitLogEvent = false
            };

            Logger logger = new Logger("TestLogger");
            using var fabricClient = new FabricClient();

            // Service reports.
            if (obs is { HasActiveFabricErrorOrWarning: true } && obs.ObserverName != ObserverConstants.NetworkObserverName)
            {
                if (obs?.ServiceNames?.Count(a => !string.IsNullOrWhiteSpace(a) && a.Contains("fabric:/")) > 0)
                {
                    foreach (var service in obs.ServiceNames)
                    {
                        Uri serviceName = new Uri(service);
                        IEnumerable<HealthEvent> fabricObserverServiceHealthEvents = null;

                        var serviceHealth = await fabricClient.HealthManager.GetServiceHealthAsync(serviceName).ConfigureAwait(false);
                        fabricObserverServiceHealthEvents = serviceHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId.Contains(obs.ObserverName)
                              && s.HealthInformation.HealthState == HealthState.Error
                              || s.HealthInformation.HealthState == HealthState.Warning);

                        foreach (var evt in fabricObserverServiceHealthEvents)
                        {
                            healthReport.ServiceName = serviceName;
                            healthReport.Property = evt.HealthInformation.Property;
                            healthReport.SourceId = evt.HealthInformation.SourceId;
                            var healthReporter = new ObserverHealthReporter(logger, fabricClient);
                            healthReporter.ReportHealthToServiceFabric(healthReport);
                            await Task.Delay(150);
                            await HealthReportNotExistsThrowAsync(healthReport.EntityType, service, evt, fabricClient);
                        }
                    }
                }
            }
           
            // System app reports.
            var sysAppHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(new Uri(ObserverConstants.SystemAppName)).ConfigureAwait(false);

            if (sysAppHealth != null)
            {
                foreach (var evt in sysAppHealth.HealthEvents.Where(
                                s => s.HealthInformation.SourceId.Contains(ObserverConstants.FabricSystemObserverName)
                                  && s.HealthInformation.HealthState == HealthState.Error 
                                  || s.HealthInformation.HealthState == HealthState.Warning))
                {
                    
                    healthReport.AppName = new Uri(ObserverConstants.SystemAppName);
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.EntityType = EntityType.Application;
                    var healthReporter = new ObserverHealthReporter(logger, fabricClient);
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    await Task.Delay(150);
                    await HealthReportNotExistsThrowAsync(healthReport.EntityType, ObserverConstants.SystemAppName, evt, fabricClient);
                }
            }

            // Node reports.
            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName).ConfigureAwait(false);
            var fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(
                    s => (s.HealthInformation.SourceId.Contains(ObserverConstants.NodeObserverName)
                          || s.HealthInformation.SourceId.Contains(ObserverConstants.DiskObserverName))
                      && s.HealthInformation.HealthState == HealthState.Error
                      || s.HealthInformation.HealthState == HealthState.Warning);

            healthReport.EntityType = EntityType.Machine;

            foreach (var evt in fabricObserverNodeHealthEvents)
            {
                healthReport.Property = evt.HealthInformation.Property;
                healthReport.SourceId = evt.HealthInformation.SourceId;
                var healthReporter = new ObserverHealthReporter(logger, fabricClient);
                healthReporter.ReportHealthToServiceFabric(healthReport);
                await Task.Delay(150);
                await HealthReportNotExistsThrowAsync(healthReport.EntityType, NodeName, evt, fabricClient);
            }
        }

        private static async Task HealthReportNotExistsThrowAsync(EntityType entityType, string name, HealthEvent evt, FabricClient fabricClient)
        {
            if (entityType == EntityType.Application && name == ObserverConstants.SystemAppName)
            {
                var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(new Uri(ObserverConstants.SystemAppName)).ConfigureAwait(false);
                var healthyEvents =
                    appHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId == evt.HealthInformation.SourceId
                          && s.HealthInformation.Property == evt.HealthInformation.Property
                          && s.HealthInformation.HealthState == HealthState.Ok);

                if (healthyEvents?.Count() == 0)
                {
                    throw new HealthReportNotFoundException($"OK clear for {ObserverConstants.SystemAppName} not found.");
                }
            }
            else if (entityType == EntityType.Service)
            {   
                var serviceHealth = await fabricClient.HealthManager.GetServiceHealthAsync(new Uri(name)).ConfigureAwait(false);
                var healthyEvents =
                    serviceHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId == evt.HealthInformation.SourceId 
                          && s.HealthInformation.Property == evt.HealthInformation.Property
                          && s.HealthInformation.HealthState == HealthState.Ok);

                if (healthyEvents?.Count() == 0)
                {
                    throw new HealthReportNotFoundException($"OK clear for {name} not found.");
                }
            }
            else if (entityType == EntityType.Node || entityType == EntityType.Machine || entityType == EntityType.Disk)
            {
                // Node reports
                var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(name).ConfigureAwait(false);

                var healthyEvents =
                    nodeHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId == evt.HealthInformation.SourceId
                          && s.HealthInformation.Property == evt.HealthInformation.Property
                          && s.HealthInformation.HealthState == HealthState.Ok);

                if (healthyEvents?.Count() == 0)
                {
                    throw new HealthReportNotFoundException($"OK clear for {NodeName} not found.");
                }
            }
        }

        private static bool InstallCerts()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // We cannot install certs into local machine store on Linux
                return false;
            }

            var validCert = new X509Certificate2("MyValidCert.p12");
            var expiredCert = new X509Certificate2("MyExpiredCert.p12");

            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(validCert);
                store.Add(expiredCert);
                return true;
            }
            catch (CryptographicException ex) when (ex.HResult == 5) // access denied
            {
                return false;
            }
        }

        private static void UnInstallCerts()
        {
            var validCert = new X509Certificate2("MyValidCert.p12");
            var expiredCert = new X509Certificate2("MyExpiredCert.p12");

            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(validCert);
            store.Remove(expiredCert);
        }

        [ClassCleanup]
        public static async Task TestClassCleanupAsync()
        {
            // Remove any files generated.
            try
            {
                var outputFolder = Path.Combine(Environment.CurrentDirectory, "observer_logs");

                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
            }
            catch (IOException)
            {

            }

            await CleanupTestHealthReportsAsync();
        }

        /* End Helpers */

        /* Simple Tests (no SF runtime required) */

        [TestMethod]
        public void AppObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AppObserverName);
        }

        [TestMethod]
        public void AzureStorageUploadObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AzureStorageUploadObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AzureStorageUploadObserverName);
        }

        [TestMethod]
        public void CertificateObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.CertificateObserverName);
        }

        [TestMethod]
        public void ContainerObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new ContainerObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.ContainerObserverName);
        }

        [TestMethod]
        public void DiskObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new DiskObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.DiskObserverName);
        }

        [TestMethod]
        public void FabricSystemObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new FabricSystemObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.FabricSystemObserverName);
        }

        [TestMethod]
        public void NetworkObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new NetworkObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null); 
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NetworkObserverName);
        }

        [TestMethod]
        public void NodeObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NodeObserverName);
        }

        [TestMethod]
        public void OSObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new OSObserver(fabricClient, _context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.OSObserverName);
        }

        [TestMethod]
        public void SFConfigurationObserver_Constructor_Test()
        {
            using var client = new FabricClient();
            
            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new SFConfigurationObserver(client, _context);

            // These are set in derived ObserverBase.
            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.SFConfigurationObserverName);
        }

        /* End Simple Tests */


        /****** 
          NOTE: These real tests below do NOT work without a running local SF cluster. This is because they exercise the exact code that will run on 
                a target machine. Health Reports will be generated (and cleaned up) - an important test in its own right. 
                The idea here is to exercise code just as it will be used in a real environment. Observers are just classes, concrete types. FO instantiates and manages them. 
                The mocking that is done (_context) is to make it possible to test various observers via a Stateless service host (so, FO) that doesn't actually exist when the tests run. 
        ******/

        /* AppObserver Initialization */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_MalformedTargetAppValue_GeneratesWarning()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.targetAppMalformed.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to malformed targetApp value.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_InvalidJson_GeneratesWarning()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.invalid.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to invalid json.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_NoConfigFound_GeneratesWarning()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.empty.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to malformed targetApp value.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        /* Single serviceInclude/serviceExclude real tests. 
         
           Note: All of these include/exclude service configuration tests are utilizing a deployed application on Charles' test machine: 
           https://github.com/azure-samples/service-fabric-dotnet-data-aggregation/tree/master/ In this case, only 3 of its services are deployed. 
           You can change the code below (and the related config files) to suit your own needs. There was no other way to conduct these real tests without 
           utilizing a real application and its services. So, if you have a deployed app on your dev machine's local cluster, then just use that 
           if you don't want to deploy the above app to your local machine. You will need to change the related AppObserver.config.*.json files in this Test Project's
           PackageRoot\Config folder. */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_ServiceExcludeList_EnsureExcluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());

            // Note: You must modify AppObserver.config.apptype.exclude.json to exclude a service for an app type that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services and we excluded only DoctorActorServiceType service.
            Assert.IsFalse(deployedTargets.Any(t => t.ServiceName.OriginalString.Contains("DoctorActorServiceType")));
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_ServiceExcludeList_EnsureExcluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());

            // Note: You must modify AppObserver.config.app.exclude.json to exclude a service for an app that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services and we excluded only DoctorActorServiceType service.
            Assert.IsFalse(deployedTargets.Any(t => t.ServiceName.OriginalString.Contains("DoctorActorServiceType")));
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_ServiceIncludeList_EnsureIncluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());

            // Note: You must modify AppObserver.config.apptype.include.json to include a service for an app type that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services and we included only DoctorActorServiceType.
            Assert.IsTrue(deployedTargets.All(t => t.ServiceName.OriginalString.Contains("DoctorActorServiceType")));
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_ServiceIncludeList_EnsureIncluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());

            // Note: You must modify AppObserver.config.app.include.json to include a service for an app that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services and we included only DoctorActorServiceType.
            Assert.IsTrue(deployedTargets.All(t => t.ServiceName.OriginalString.Contains("DoctorActorServiceType")));
        }

        /* Multiple exclude/include service settings for single targetApp/Type tests */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_MultiServiceExcludeList_EnsureNotExcluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.multi-exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());

            // Note: You must modify AppObserver.config.apptype.multi-exclude.json to exclude multiple services for a single app type that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services.

            // You can't supply multiple EXclude lists for the same target app/type.
            Assert.IsTrue(serviceReplicas.Count == 3);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_MultiServiceExcludeList_EnsureNotExcluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.multi-exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());

            // Note: You must modify AppObserver.config.app.multi-exclude.json to specify exclusion of multiple services for a single app that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services.

            // You can't supply multiple EXclude lists for the same target app/type.
            Assert.IsTrue(serviceReplicas.Count == 3);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_MultiServiceIncludeList_EnsureIncluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.multi-include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());

            // Note: You must modify AppObserver.config.apptype.multi-include.json to include services for an app type that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services, but we included only two, by specifying each one in two separate config items.
            Assert.IsTrue(serviceReplicas.Count == 2);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_MultiServiceIncludeList_EnsureIncluded()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.multi-include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());

            // Note: You must modify AppObserver.config.app.multi-include.json to include services for an app that is actually
            // deployed to your local dev machine. Here, HealthMetrics app has 3 services.
            Assert.IsTrue(serviceReplicas.Count == 2);
        }

        /* End InitializeAsync tests. */

        /* ObserveAsync/ReportAsync */

        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            
            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_WarningsGenerated()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_warnings.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_Observer_WarningsGenerated()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.oldstyle_warnings.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_Observer_NoWarningsGenerated()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.oldstyle_nowarnings.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task ContainerObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new ContainerObserver(client, _context)
            {
                ConfigurationFilePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "ContainerObserver.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task ClusterObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            var client = new FabricClient();

            ClusterObserverManager.FabricServiceContext = _context;
            ClusterObserverManager.FabricClientInstance = client;
            ClusterObserverManager.EtwEnabled = true;
            ClusterObserverManager.TelemetryEnabled = true;

            // On a one-node cluster like your dev machine, pass true for ignoreDefaultQueryTimeout otherwise each FabricClient query will take 2 minutes 
            // to timeout in ClusterObserver.
            var obs = new ClusterObserver.ClusterObserver(null, ignoreDefaultQueryTimeout: true)
            {
                ConfigSettings = new ClusterObserver.Utilities.ConfigSettings(null, null)
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task CertificateObserver_validCerts()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!InstallCerts())
            {
                Assert.Inconclusive("This test can only be run on Windows as an admin.");
            }

            try
            {
                var startDateTime = DateTime.Now;
                using var client = new FabricClient();

                ObserverManager.FabricServiceContext = _context;
                ObserverManager.FabricClientInstance = client;
                ObserverManager.TelemetryEnabled = false;
                ObserverManager.EtwEnabled = false;

                using var obs = new CertificateObserver(client, _context);

                var commonNamesToObserve = new List<string>
                {
                    "MyValidCert" // Common name of valid cert
                };

                var thumbprintsToObserve = new List<string>
                {
                    "1fda27a2923505e47de37db48ff685b049642c25" // thumbprint of valid cert
                };

                obs.DaysUntilAppExpireWarningThreshold = 14;
                obs.DaysUntilClusterExpireWarningThreshold = 14;
                obs.AppCertificateCommonNamesToObserve = commonNamesToObserve;
                obs.AppCertificateThumbprintsToObserve = thumbprintsToObserve;
                obs.SecurityConfiguration = new SecurityConfiguration
                {
                    SecurityType = SecurityType.None,
                    ClusterCertThumbprintOrCommonName = string.Empty,
                    ClusterCertSecondaryThumbprint = string.Empty
                };

                await obs.ObserveAsync(token);

                // observer ran to completion with no errors.
                Assert.IsTrue(obs.LastRunDateTime > startDateTime);

                // observer detected no error conditions.
                Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

                // observer did not have any internal errors during run.
                Assert.IsFalse(obs.IsUnhealthy);
            }
            finally
            {
                UnInstallCerts();
            }
        }

        [TestMethod]
        public async Task CertificateObserver_expiredAndexpiringCerts()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(client, _context);

            using var obsMgr = new ObserverManager(obs, client)
            {
                ApplicationName = "fabric:/TestApp0"
            };

            var commonNamesToObserve = new List<string>
            {
                "MyExpiredCert" // common name of expired cert
            };

            var thumbprintsToObserve = new List<string>
            {
                "1fda27a2923505e47de37db48ff685b049642c25" // thumbprint of valid cert, but warning threshold causes expiring
            };

            obs.DaysUntilAppExpireWarningThreshold = int.MaxValue;
            obs.DaysUntilClusterExpireWarningThreshold = 14;
            obs.AppCertificateCommonNamesToObserve = commonNamesToObserve;
            obs.AppCertificateThumbprintsToObserve = thumbprintsToObserve;
            obs.SecurityConfiguration = new SecurityConfiguration
            {
                SecurityType = SecurityType.None,
                ClusterCertThumbprintOrCommonName = string.Empty,
                ClusterCertSecondaryThumbprint = string.Empty
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // stop clears health warning
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NodeObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Fail()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, _context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = 10000
            };

            await obs.ObserveAsync(token);

            // observer ran to completion.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions (so, it ignored meaningless percentage value).
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task NodeObserver_Negative_Integer_CPU_Mem_Ports_Firewalls_Values_No_Exceptions_In_Intialize()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, _context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsRawErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100
            };

            await obs.ObserveAsync(token);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task NodeObserver_Negative_Integer_Thresholds_CPU_Mem_Ports_Firewalls_All_Data_Containers_Are_Null()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, _context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsRawErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100
            };

            await obs.ObserveAsync(token);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // Data containers are null.
            Assert.IsTrue(obs.CpuTimeData == null);
            Assert.IsTrue(obs.MemDataInUse == null);
            Assert.IsTrue(obs.MemDataPercent == null);
            Assert.IsTrue(obs.ActivePortsData == null);
            Assert.IsTrue(obs.EphemeralPortsDataRaw == null);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task OSObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new OSObserver(client, _context)
            {
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"),
                IsObserverWebApiAppDeployed = true
            };

            // This is required since output files are only created if fo api app is also deployed to cluster..

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "SysInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var warningDictionary = new Dictionary<string, double>
            {
                { @"C:\SFDevCluster\Log\Traces", 50000 }
            };

            using var obs = new DiskObserver(client, _context)
            {
                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                FolderSizeMonitoringEnabled = true,
                FolderSizeConfigDataWarning = warningDictionary
            };


            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "disks.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            var warningDictionary = new Dictionary<string, double>
            {
                /* Windows paths.. */

                { @"%USERPROFILE%\AppData\Local\Temp", 50 },
                
                // This should be rather large.
                { "%USERPROFILE%", 50 }
            };

            using var obs = new DiskObserver(client, _context)
            {
                // This should cause a Warning on most dev machines.
                DiskSpacePercentWarningThreshold = 10,
                FolderSizeMonitoringEnabled = true,

                // Folder size monitoring. This will most likely generate a warning.
                FolderSizeConfigDataWarning = warningDictionary,

                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                MonitorDuration = TimeSpan.FromSeconds(5)
            };

            using var obsMgr = new ObserverManager(obs, client)
            {
                ApplicationName = "fabric:/TestApp0"
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected issues with disk/folder size.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // Disk consumption and folder size warnings were generated.
            Assert.IsTrue(obs.CurrentWarningCount == 3);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "disks.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);

            // Stop clears health warning
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(client, _context);
            await obs.ObserveAsync(token);

            // Observer ran to completion with no errors.
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_WritesLocalFile_ObsWebDeployed()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(client, _context)
            {
                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true
            };

            await obs.ObserveAsync(token);

            // Observer ran to completion with no errors.
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "NetInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        [TestMethod]
        public async Task NodeObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                DataCapacity = 5,
                UseCircularBuffer = false,
                CpuWarningUsageThresholdPct = 0.01F, // This will generate Warning for sure.
                MemWarningUsageThresholdMb = 1, // This will generate Warning for sure.
                ActivePortsWarningThreshold = 100, // This will generate Warning for sure.
                EphemeralPortsPercentWarningThreshold = 0.01 // This will generate Warning for sure.
            };

            using var obsMgr = new ObserverManager(obs, client);
            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // Stop clears health warning
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task SFConfigurationObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new SFConfigurationObserver(client, _context)
            {
                IsEnabled = true,

                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml")
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "SFInfraInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(false);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, _context)
            {
                IsEnabled = true,
                DataCapacity = 5,
                MonitorDuration = TimeSpan.FromSeconds(1)
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Adjust defaults in FabricObserver project's Observers/FabricSystemObserver.cs
            // file to experiment with err/warn detection/reporting behavior.
            // observer did not detect any errors or warnings for supplied thresholds.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_MemoryWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
            
            using var obs = new FabricSystemObserver(client, _context)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                MemWarnUsageThresholdMb = 5
            };

            await obs.ObserveAsync(token);

            // observer ran to completion.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_ActiveTcpPortsWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(false);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveTcpPortCountWarning = 5
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_EphemeralPortsWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(false);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveEphemeralPortCountWarning = 1
            };

            await obs.ObserveAsync(token).ConfigureAwait(false);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_HandlesWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(false);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                AllocatedHandlesWarning = 100
            };

            await obs.ObserveAsync(token).ConfigureAwait(false);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_Negative_Integer_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(false);
            
            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
            
            using var obs = new FabricSystemObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = -42
            };

            await obs.ObserveAsync(token).ConfigureAwait(false);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(false);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = _context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, _context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = 420
            };

            await obs.ObserveAsync(token).ConfigureAwait(false);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }
    }
}