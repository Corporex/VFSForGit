﻿using CommandLine;
using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;

namespace GVFS.CommandLine
{
    public abstract class GVFSVerb
    {
        public GVFSVerb()
        {
            this.Output = Console.Out;
            this.ReturnCode = ReturnCode.Success;
            this.ServiceName = GVFSConstants.Service.ServiceName;
            
            this.InitializeDefaultParameterValues();
        }

        public abstract string EnlistmentRootPath { get; set; }
        
        [Option(
            MountParameters.ServiceName,
            Default = GVFSConstants.Service.ServiceName,
            Required = false,
            HelpText = "This parameter is reserved for internal use.")]
        public string ServiceName { get; set; }

        public string ServicePipeName
        {
            get
            {
                return this.ServiceName + ".Pipe";
            }
        }

        public TextWriter Output { get; set; }

        public ReturnCode ReturnCode { get; private set; }

        protected abstract string VerbName { get; }

        public static bool TrySetGitConfigSettings(GitProcess git)
        {
            Dictionary<string, string> expectedConfigSettings = new Dictionary<string, string>
            {
                { "core.autocrlf", "false" },
                { "core.fscache", "true" },
                { "core.gvfs", "true" },
                { "core.preloadIndex", "true" },
                { "core.safecrlf", "false" },
                { "core.sparseCheckout", "true" },
                { "core.untrackedCache", "false" },
                { GitConfigSetting.VirtualizeObjectsGitConfigName, "true" },
                { "credential.validate", "false" },
                { "diff.autoRefreshIndex", "false" },
                { "gc.auto", "0" },
                { "gui.gcwarning", "false" },
                { "index.version", "4" },
                { "merge.stat", "false" },
                { "receive.autogc", "false" },
            };

            Dictionary<string, GitConfigSetting> actualConfigSettings;
            if (!git.TryGetAllLocalConfig(out actualConfigSettings))
            {
                return false;
            }

            foreach (string key in expectedConfigSettings.Keys)
            {
                GitConfigSetting actualSetting;
                if (!actualConfigSettings.TryGetValue(key, out actualSetting) ||
                    !actualSetting.HasValue(expectedConfigSettings[key]))
                {
                    GitProcess.Result setConfigResult = git.SetInLocalConfig(key, expectedConfigSettings[key]);
                    if (setConfigResult.HasErrors)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public static ReturnCode Execute<TVerb>(
            string enlistmentRootPath,
            Action<TVerb> configureVerb = null)
            where TVerb : GVFSVerb, new()
        {
            TVerb verb = new TVerb();
            verb.EnlistmentRootPath = enlistmentRootPath;
            if (configureVerb != null)
            {
                configureVerb(verb);
            }

            try
            {
                verb.Execute();
            }
            catch (VerbAbortedException)
            {
            }

            return verb.ReturnCode;
        }

        public abstract void Execute();

        public virtual void InitializeDefaultParameterValues()
        {
        }

        protected bool ShowStatusWhileRunning(Func<bool> action, string message, bool suppressGvfsLogMessage = false)
        {
            return ConsoleHelper.ShowStatusWhileRunning(
                action,
                message,
                this.Output,
                showSpinner: this.Output == Console.Out && !ConsoleHelper.IsConsoleOutputRedirectedToFile(),
                suppressGvfsLogMessage: suppressGvfsLogMessage);
        }

        protected void ReportErrorAndExit(ReturnCode exitCode, string error, params object[] args)
        {
            if (error != null)
            {
                if (args == null || args.Length == 0)
                {
                    this.Output.WriteLine(error);
                }
                else
                {
                    this.Output.WriteLine(error, args);
                }
            }

            this.ReturnCode = exitCode;
            throw new VerbAbortedException(this);
        }

        protected void ReportErrorAndExit(string error, params object[] args)
        {
            this.ReportErrorAndExit(ReturnCode.GenericError, error, args);
        }

        protected void CheckGVFltRunning()
        {
            bool gvfltServiceRunning = false;
            try
            {
                ServiceController controller = new ServiceController("gvflt");
                gvfltServiceRunning = controller.Status.Equals(ServiceControllerStatus.Running);
            }
            catch (InvalidOperationException)
            {
                this.ReportErrorAndExit("Error: GVFlt Service was not found. To resolve, re-install GVFS");
            }

            if (!gvfltServiceRunning)
            {
                this.ReportErrorAndExit("Error: GVFlt Service is not running. To resolve, run \"sc start gvflt\" from an elevated command prompt");
            }
        }

        protected void CheckVolumeSupportsDeleteNotifications(ITracer tracer, Enlistment enlistment)
        {
            try
            {
                if (!NativeMethods.IsFeatureSupportedByVolume(Directory.GetDirectoryRoot(enlistment.EnlistmentRoot), NativeMethods.FileSystemFlags.FILE_RETURNS_CLEANUP_RESULT_INFO))
                {
                    this.ReportErrorAndExit("Error: File system does not support features required by GVFS. Confirm that Windows version is at or beyond that required by GVFS");
                }
            }
            catch (VerbAbortedException)
            {
                // ReportErrorAndExit throws VerbAbortedException.  Catch and re-throw here so that GVFS does not report that
                // it failed to determine if file system supports required features
                throw;
            }
            catch (Exception e)
            {
                if (tracer != null)
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("ErrorMessage", "Failed to determine if file system supports features required by GVFS");
                    metadata.Add("Exception", e.ToString());
                    tracer.RelatedError(metadata);
                }

                this.ReportErrorAndExit("Error: Failed to determine if file system supports features required by GVFS.");
            }
        }

        protected void CheckGitVersion(Enlistment enlistment)
        {
            GitProcess.Result versionResult = GitProcess.Version(enlistment);
            if (versionResult.HasErrors)
            {
                this.ReportErrorAndExit("Error: Unable to retrieve the git version");
            }

            GitVersion gitVersion;
            string version = versionResult.Output;
            if (version.StartsWith("git version "))
            {
                version = version.Substring(12);
            }

            if (!GitVersion.TryParse(version, out gitVersion))
            {
                this.ReportErrorAndExit("Error: Unable to parse the git version. {0}", version);
            }

            if (gitVersion.Platform != GVFSConstants.MinimumGitVersion.Platform)
            {
                this.ReportErrorAndExit("Error: Invalid version of git {0}.  Must use gvfs version.", version);
            }

            if (gitVersion.IsLessThan(GVFSConstants.MinimumGitVersion))
            {
                this.ReportErrorAndExit(
                    "Error: Installed git version {0} is less than the minimum version of {1}.",
                    gitVersion,
                    GVFSConstants.MinimumGitVersion);
            }
        }

        protected void CheckGVFSHooksVersion(Enlistment enlistment, string hooksPath)
        {
            if (hooksPath == null)
            {
                hooksPath = ProcessHelper.WhereDirectory(GVFSConstants.GVFSHooksExecutableName);
                if (hooksPath == null)
                {
                    this.ReportErrorAndExit("Could not find " + GVFSConstants.GVFSHooksExecutableName);
                }
            }

            FileVersionInfo myFileVersionInfo = FileVersionInfo.GetVersionInfo(hooksPath + "\\" + GVFSConstants.GVFSHooksExecutableName);
            string gvfsVersion = ProcessHelper.GetCurrentProcessVersion();
            if (myFileVersionInfo.ProductVersion != gvfsVersion)
            {
                this.ReportErrorAndExit("GVFS.Hooks version ({0}) does not match GVFS version ({1}).", myFileVersionInfo.ProductVersion, gvfsVersion);
            }
        }

        protected void ValidateGVFSVersion(GVFSEnlistment enlistment, GVFSConfig config, ITracer tracer)
        {
            using (ITracer activity = tracer.StartActivity("ValidateGVFSVersion", EventLevel.Informational))
            {
                Version currentVersion = new Version(ProcessHelper.GetCurrentProcessVersion());

                IEnumerable<GVFSConfig.VersionRange> allowedGvfsClientVersions =
                    config != null
                    ? config.AllowedGVFSClientVersions
                    : null;

                if (allowedGvfsClientVersions == null || !allowedGvfsClientVersions.Any())
                {
                    string errorMessage = string.Empty;
                    if (config == null)
                    {
                        errorMessage = "Could not query valid GVFS versions from: " + Uri.EscapeUriString(enlistment.RepoUrl);
                    }
                    else
                    {
                        errorMessage = "Server not configured to provide supported GVFS versions";
                    }

                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("ErrorMessage", errorMessage);
                    tracer.RelatedError(metadata, Keywords.Network);

                    this.Output.WriteLine();
                    this.Output.WriteLine("WARNING: Unable to validate your GVFS version");
                    this.Output.WriteLine();
                    return;
                }

                foreach (GVFSConfig.VersionRange versionRange in config.AllowedGVFSClientVersions)
                {
                    if (currentVersion >= versionRange.Min &&
                        (versionRange.Max == null || currentVersion <= versionRange.Max))
                    {
                        activity.RelatedEvent(
                            EventLevel.Informational,
                            "GVFSVersionValidated",
                            new EventMetadata
                            {
                                { "SupportedVersionRange", versionRange },
                            });
                        return;
                    }
                }

                activity.RelatedError("GVFS version {0} is not supported", currentVersion);
            }

            this.ReportErrorAndExit("\r\nERROR: Your GVFS version is no longer supported.  Install the latest and try again.\r\n");
        }

        public abstract class ForExistingEnlistment : GVFSVerb
        {
            [Value(
                0,
                Required = false,
                Default = "",
                MetaName = "Enlistment Root Path",
                HelpText = "Full or relative path to the GVFS enlistment root")]
            public override string EnlistmentRootPath { get; set; }

            public sealed override void Execute()
            {
                this.PreExecute(this.EnlistmentRootPath);
                GVFSEnlistment enlistment = this.CreateEnlistment(this.EnlistmentRootPath);
                this.Execute(enlistment);
            }

            protected virtual void PreExecute(string enlistmentRootPath)
            {
            }

            protected abstract void Execute(GVFSEnlistment enlistment);

            private GVFSEnlistment CreateEnlistment(string enlistmentRootPath)
            {
                string gitBinPath = GitProcess.GetInstalledGitBinPath();
                if (string.IsNullOrWhiteSpace(gitBinPath))
                {
                    this.ReportErrorAndExit("Error: " + GVFSConstants.GitIsNotInstalledError);
                }

                if (string.IsNullOrWhiteSpace(enlistmentRootPath))
                {
                    enlistmentRootPath = Environment.CurrentDirectory;
                }

                string hooksPath = ProcessHelper.WhereDirectory(GVFSConstants.GVFSHooksExecutableName);
                if (hooksPath == null)
                {
                    this.ReportErrorAndExit("Could not find " + GVFSConstants.GVFSHooksExecutableName);
                }

                GVFSEnlistment enlistment = null;
                try
                {
                    enlistment = GVFSEnlistment.CreateFromDirectory(enlistmentRootPath, null, gitBinPath, hooksPath);
                    if (enlistment == null)
                    {
                        this.ReportErrorAndExit(
                            "Error: '{0}' is not a valid GVFS enlistment",
                            enlistmentRootPath);
                    }
                }
                catch (InvalidRepoException e)
                {
                    this.ReportErrorAndExit(
                        "Error: '{0}' is not a valid GVFS enlistment. {1}",
                        enlistmentRootPath,
                        e.Message);
                }

                return enlistment;
            }
        }

        public class VerbAbortedException : Exception
        {
            public VerbAbortedException(GVFSVerb verb)
            {
                this.Verb = verb;
            }

            public GVFSVerb Verb { get; }
        }
    }
}