﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Interpreter;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.PythonTools.Project {
    static class Pip {
        private static readonly Regex PackageNameRegex = new Regex(
            "^(?<name>[a-z0-9_]+)(-.+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly KeyValuePair<string, string>[] UnbufferedEnv = new[] { 
            new KeyValuePair<string, string>("PYTHONUNBUFFERED", "1")
        };

        private static ProcessOutput Run(IPythonInterpreterFactory factory, Redirector output, params string[] cmd) {
            string pipPath = Path.Combine(factory.Configuration.PrefixPath, "Scripts", "pip.exe");
            if (!File.Exists(pipPath)) {
                pipPath = Path.Combine(factory.Configuration.PrefixPath, "pip.exe");
                if (!File.Exists(pipPath)) {
                    pipPath = null;
                }
            }

            if (!string.IsNullOrEmpty(pipPath)) {
                return ProcessOutput.Run(pipPath, cmd, null, UnbufferedEnv, false, output, quoteArgs: false);
            } else {
                return ProcessOutput.Run(factory.Configuration.InterpreterPath,
                    new[] { "-m", "pip" }.Concat(cmd),
                    factory.Configuration.PrefixPath,
                    UnbufferedEnv,
                    false,
                    output,
                    quoteArgs: false);
            }
        }

        private static ProcessOutput Run(IPythonInterpreterFactory factory, params string[] cmd) {
            return Run(factory, null, cmd);
        }

        public static Task<HashSet<string>> Freeze(IPythonInterpreterFactory factory) {
            return Task.Factory.StartNew<HashSet<string>>((Func<HashSet<string>>)(() => {
                var lines = new HashSet<string>();
                using (var proc = Run(factory, "--version")) {
                    proc.Wait();
                    if (proc.ExitCode == 0) {
                        lines.UnionWith(proc.StandardOutputLines
                            .Select(line => Regex.Match(line, "pip (?<version>[0-9.]+)"))
                            .Where(match => match.Success && match.Groups["version"].Success)
                            .Select(match => "pip==" + match.Groups["version"].Value));
                    }
                }

                using (var proc = Run(factory, "freeze")) {
                    proc.Wait();
                    if (proc.ExitCode == 0) {
                        lines.UnionWith(proc.StandardOutputLines);
                        return lines;
                    }
                }

                // Pip failed, so clear out any entries that may have appeared
                lines.Clear();

                try {
                    var packagesPath = Path.Combine(factory.Configuration.LibraryPath, "site-packages");
                    lines.UnionWith(Directory.EnumerateDirectories(packagesPath)
                        .Select(name => Path.GetFileName(name))
                        .Select(name => PackageNameRegex.Match(name))
                        .Where(m => m.Success)
                        .Select(m => m.Groups["name"].Value));
                } catch (ArgumentException) {
                } catch (IOException) {
                }

                return lines;
            }));
        }

        public static Task Install(IPythonInterpreterFactory factory, string package, Redirector output = null) {
            return Task.Factory.StartNew((Action)(() => {
                using (var proc = Run(factory, output, "install", package)) {
                    proc.Wait();
                }
            }));
        }

        public static Task<bool> Install(IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            Redirector output = null) {

            Task task;
            if (site != null && !ModulePath.GetModulesInLib(factory).Any(mp => mp.ModuleName == "pip")) {
                task = QueryInstallPip(factory, site, SR.GetString(SR.InstallPip), output);
            } else {
                var tcs = new TaskCompletionSource<object>();
                tcs.SetResult(null);
                task = tcs.Task;
            }
            return task.ContinueWith(t => {
                if (output != null) {
                    output.WriteLine(SR.GetString(SR.PackageInstalling, package));
                    output.Show();
                }
                using (var proc = Run(factory, output, "install", package)) {
                    proc.Wait();

                    if (output != null) {
                        if (proc.ExitCode == 0) {
                            output.WriteLine(SR.GetString(SR.PackageInstallSucceeded, package));
                        } else {
                            output.WriteLine(SR.GetString(SR.PackageInstallFailedExitCode, package, proc.ExitCode ?? -1));
                        }
                        output.Show();
                    }
                    return proc.ExitCode == 0;
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        public static Task<bool> Uninstall(IPythonInterpreterFactory factory, string package, Redirector output = null) {
            return Task.Factory.StartNew((Func<bool>)(() => {
                if (output != null) {
                    output.WriteLine(SR.GetString(SR.PackageUninstalling, package));
                    output.Show();
                }
                using (var proc = Run(factory, output, "uninstall", "-y", package)) {
                    proc.Wait();

                    if (output != null) {
                        if (proc.ExitCode == 0) {
                            output.WriteLine(SR.GetString(SR.PackageUninstallSucceeded, package));
                        } else {
                            output.WriteLine(SR.GetString(SR.PackageUninstallFailedExitCode, package, proc.ExitCode ?? -1));
                        }
                        output.Show();
                    }
                    return proc.ExitCode == 0;
                }
            }));
        }

        public static Task InstallPip(IPythonInterpreterFactory factory, Redirector output = null) {
            var pipDownloaderPath = Path.Combine(PythonToolsPackage.GetPythonToolsInstallPath(), "pip_downloader.py");

            return Task.Factory.StartNew((Action)(() => {
                if (output != null) {
                    output.WriteLine(SR.GetString(SR.PipInstalling));
                    output.Show();
                }
                // TODO: Handle elevation
                using (var proc = ProcessOutput.Run(factory.Configuration.InterpreterPath,
                    new [] { pipDownloaderPath },
                    factory.Configuration.PrefixPath,
                    null,
                    false,
                    output)
                ) {
                    proc.Wait();
                    if (output != null) {
                        if (proc.ExitCode == 0) {
                            output.WriteLine(SR.GetString(SR.PipInstallSucceeded));
                        } else {
                            output.WriteLine(SR.GetString(SR.PipInstallFailedExitCode, proc.ExitCode ?? -1));
                        }
                        output.Show();
                    }
                }
            }));
        }

        public static Task QueryInstall(IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            string message,
            Redirector output = null) {
            if (Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(site,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == 2) {
                var tcs = new TaskCompletionSource<object>();
                tcs.SetCanceled();
                return tcs.Task;
            }

            return Install(factory, package, output);
        }

        public static Task QueryInstallPip(IPythonInterpreterFactory factory,
            IServiceProvider site,
            string message,
            Redirector output = null) {
            if (Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(site,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == 2) {
                var tcs = new TaskCompletionSource<object>();
                tcs.SetCanceled();
                return tcs.Task;
            }

            return InstallPip(factory, output);
        }
    }
}