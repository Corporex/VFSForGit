﻿using GVFS.Tests.Should;
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace GVFS.FunctionalTests.Tools
{
    public static class GVFSServiceProcess
    {
        public const string TestServiceName = "Test.GVFS.Service";
        private const string ServiceNameArgument = "--servicename=" + TestServiceName;

        public static void InstallService(string pathToService)
        {
            UninstallService();

            // Wait for delete to complete. If the services control panel is open, this will never complete.
            while (RunScCommand("query", TestServiceName).ExitCode == 0)
            {
                Thread.Sleep(1000);
            }

            // Install service
            string createServiceArguments = string.Format(
                "{0} binPath= \"{1}\"",
                TestServiceName,
                pathToService);

            ProcessResult result = RunScCommand("create", createServiceArguments);
            result.ExitCode.ShouldEqual(0, "Failure while running sc create " + createServiceArguments + "\r\n" + result.Output);
        }

        public static void StartService()
        {
            using (ServiceController controller = new ServiceController(TestServiceName))
            {
                controller.Start(new[] { ServiceNameArgument });
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                controller.Status.ShouldEqual(ServiceControllerStatus.Running);
            }
        }

        public static void StopService()
        {
            try
            {
                using (ServiceController controller = new ServiceController(TestServiceName))
                {
                    if (controller.Status == ServiceControllerStatus.Running)
                    {
                        controller.Stop();
                    }

                    controller.WaitForStatus(ServiceControllerStatus.Stopped);
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        public static void UninstallService()
        {
            StopService();
            
            RunScCommand("delete", TestServiceName);

            // Make sure to delete any test service data state
            string serviceData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GVFS", TestServiceName);
            DirectoryInfo serviceDataDir = new DirectoryInfo(serviceData);
            if (serviceDataDir.Exists)
            {
                serviceDataDir.Delete(true);
            }
        }

        private static ProcessResult RunScCommand(string command, string parameters)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo("sc");
            processInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardOutput = true;

            processInfo.Arguments = command + " " + parameters;

            return ProcessHelper.Run(processInfo);
        }
    }
}
