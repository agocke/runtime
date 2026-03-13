// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Helix.Client;
using Microsoft.DotNet.Helix.Client.Models;

return await HelixSubmit.Program.RunAsync(args);

namespace HelixSubmit
{
    internal static class Program
    {
        internal static async Task<int> RunAsync(string[] args)
        {
            string? queue = null;
            string? token = null;
            string? baseUrl = null;
            string? correlationPayloadDir = null;
            string? testPayloadDir = null;
            string? command = null;
            string? timeoutStr = null;
            string? workItemName = null;
            string? source = null;
            string? type = null;
            string? creator = null;
            string? resultsDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].TrimStart('\'').TrimEnd('\'');
                if (TryParseArg(arg, "--queue=", out string? value))
                {
                    queue = value;
                }
                else if (TryParseArg(arg, "--token=", out value))
                {
                    token = value;
                }
                else if (TryParseArg(arg, "--base-url=", out value))
                {
                    baseUrl = value;
                }
                else if (TryParseArg(arg, "--correlation-payload-dir=", out value))
                {
                    correlationPayloadDir = value;
                }
                else if (TryParseArg(arg, "--test-payload-dir=", out value))
                {
                    testPayloadDir = value;
                }
                else if (TryParseArg(arg, "--command=", out value))
                {
                    command = value;
                }
                else if (TryParseArg(arg, "--timeout=", out value))
                {
                    timeoutStr = value;
                }
                else if (TryParseArg(arg, "--work-item-name=", out value))
                {
                    workItemName = value;
                }
                else if (TryParseArg(arg, "--source=", out value))
                {
                    source = value;
                }
                else if (TryParseArg(arg, "--type=", out value))
                {
                    type = value;
                }
                else if (TryParseArg(arg, "--creator=", out value))
                {
                    creator = value;
                }
                else if (TryParseArg(arg, "--results-dir=", out value))
                {
                    resultsDir = value;
                }
                else
                {
                    Console.Error.WriteLine($"Unknown argument: {arg}");
                    PrintUsage();

                    return 1;
                }
            }

            if (queue is null || command is null || workItemName is null)
            {
                Console.Error.WriteLine("Missing required arguments.");
                PrintUsage();

                return 1;
            }

            TimeSpan timeout = TimeSpan.FromMinutes(15);
            if (timeoutStr is not null && !TimeSpan.TryParse(timeoutStr, out timeout))
            {
                Console.Error.WriteLine($"Invalid timeout format: {timeoutStr}");

                return 1;
            }

            try
            {
                return await SubmitAndWaitAsync(
                    queue, token, baseUrl, correlationPayloadDir,
                    testPayloadDir, command, timeout, workItemName,
                    source, type, creator, resultsDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Helix submission failed: {ex.Message}");
                Console.Error.WriteLine(ex.ToString());

                return 1;
            }
        }

        private static async Task<int> SubmitAndWaitAsync(
            string queue,
            string? token,
            string? baseUrl,
            string? correlationPayloadDir,
            string? testPayloadDir,
            string command,
            TimeSpan timeout,
            string workItemName,
            string? source,
            string? type,
            string? creator,
            string? resultsDir)
        {
            IHelixApi api = CreateApi(token, baseUrl);

            IJobDefinition job = api.Job
                .Define()
                .WithType(type ?? "test/bazel/")
                .WithTargetQueue(queue);

            if (source is not null)
            {
                job = job.WithSource(source);
            }
            else
            {
                job = job.WithSource($"runtime/bazel/{queue}");
            }

            if (creator is not null)
            {
                job = job.WithCreator(creator);
            }
            else if (token is null)
            {
                // Anonymous submissions require a creator
                job = job.WithCreator("bazel-helix");
            }

            if (correlationPayloadDir is not null)
            {
                job = job.WithCorrelationPayloadDirectory(correlationPayloadDir);
            }

            IWorkItemDefinitionWithCommand workItemBuilder = job.DefineWorkItem(workItemName);
            IWorkItemDefinitionWithPayload withCommand = workItemBuilder.WithCommand(command);

            IWorkItemDefinition workItem;
            if (testPayloadDir is not null)
            {
                workItem = withCommand.WithDirectoryPayload(testPayloadDir);
            }
            else
            {
                workItem = withCommand.WithEmptyPayload();
            }

            job = workItem
                .WithTimeout(timeout)
                .AttachToJob();

            Console.WriteLine($"Submitting Helix job to queue '{queue}'...");
            Console.WriteLine($"  Work item: {workItemName}");
            Console.WriteLine($"  Command:   {command}");
            Console.WriteLine($"  Timeout:   {timeout}");

            ISentJob sentJob = await job.SendAsync(log => Console.WriteLine($"  [Helix] {log}"));

            Console.WriteLine($"Job submitted. CorrelationId: {sentJob.CorrelationId}");
            Console.WriteLine("Waiting for job completion...");

            // Allow generous time for queue wait + execution. Bazel controls the outer timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(50));
            JobPassFail result;
            try
            {
                result = await sentJob.WaitAsync(pollingIntervalMs: 10_000, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"Timed out waiting for Helix job {sentJob.CorrelationId}.");
                Console.Error.WriteLine($"Check status at: https://helix.dot.net/api/jobs/{sentJob.CorrelationId}/details?api-version=2019-06-17");
                return 1;
            }

            // Helix reports Total as N+1 because it includes a synthetic
            // "HelixController Work Queueing" item for internal bookkeeping.
            int internalCount = 0;
            int userPassed = 0;
            int userFailed = 0;
            var workItemNames = new List<string>();

            if (result.Passed is not null)
            {
                foreach (string name in result.Passed)
                {
                    if (name.StartsWith("HelixController", StringComparison.Ordinal))
                        internalCount++;
                    else
                    {
                        userPassed++;
                        workItemNames.Add(name);
                    }
                }
            }

            if (result.Failed is not null)
            {
                foreach (string name in result.Failed)
                {
                    if (name.StartsWith("HelixController", StringComparison.Ordinal))
                        internalCount++;
                    else
                    {
                        userFailed++;
                        workItemNames.Add(name);
                    }
                }
            }

            Console.WriteLine($"Job completed. Work items: {userPassed + userFailed} total, {userPassed} passed, {userFailed} failed");

            // Fetch and display console output from work items to surface xUnit test results.
            // For passed jobs, show just the summary; for failed jobs, show full output + logs.
            bool hasFailed = userFailed > 0;
            if (hasFailed)
            {
                Console.Error.WriteLine("Failed work items:");
                foreach (string name in workItemNames)
                {
                    if (result.Failed!.Contains(name))
                        Console.Error.WriteLine($"  - {name}");
                }
            }

            // Display console output for all non-internal work items.
            await FetchAndDisplayWorkItemLogsAsync(
                baseUrl ?? "https://helix.dot.net/",
                sentJob.CorrelationId,
                workItemNames,
                verbose: hasFailed);

            if (resultsDir is not null)
            {
                Directory.CreateDirectory(resultsDir);
                string summaryPath = Path.Combine(resultsDir, "helix_summary.txt");
                await File.WriteAllTextAsync(summaryPath,
                    $"CorrelationId: {sentJob.CorrelationId}\n" +
                    $"Queue: {queue}\n" +
                    $"WorkItems: {userPassed + userFailed}\n" +
                    $"Passed: {userPassed}\n" +
                    $"Failed: {userFailed}\n");
            }

            return hasFailed ? 1 : 0;
        }

        private static async Task FetchAndDisplayWorkItemLogsAsync(
            string baseUrl,
            string correlationId,
            IReadOnlyList<string> workItemNames,
            bool verbose)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            string apiBase = baseUrl.TrimEnd('/');

            foreach (string workItemName in workItemNames)
            {
                // Skip internal Helix work items.
                if (workItemName.StartsWith("HelixController", StringComparison.Ordinal))
                    continue;

                try
                {
                    string encodedName = Uri.EscapeDataString(workItemName);
                    string detailsUrl =
                        $"{apiBase}/api/jobs/{correlationId}/workitems/{encodedName}?api-version=2019-06-17";

                    string detailsJson = await http.GetStringAsync(detailsUrl);
                    using JsonDocument doc = JsonDocument.Parse(detailsJson);
                    JsonElement root = doc.RootElement;

                    int? exitCode = root.TryGetProperty("ExitCode", out JsonElement exitCodeEl) ? exitCodeEl.GetInt32() : null;
                    bool passed = exitCode == 0;

                    // Fetch console output.
                    string? consoleContent = null;
                    if (root.TryGetProperty("ConsoleOutputUri", out JsonElement consoleUri) &&
                        consoleUri.GetString() is string consoleUrl)
                    {
                        consoleContent = await http.GetStringAsync(consoleUrl);
                    }

                    if (passed && !verbose)
                    {
                        // For passing work items, extract just the xUnit summary line.
                        if (consoleContent is not null)
                        {
                            string[] lines = consoleContent.Split('\n');
                            foreach (string line in lines)
                            {
                                string trimmed = line.Trim();
                                if (trimmed.Contains("Total:") && trimmed.Contains("Failed:"))
                                {
                                    Console.WriteLine($"  {trimmed}");
                                }
                            }
                        }
                    }
                    else
                    {
                        // For failed work items or verbose mode, show everything.
                        Console.Error.WriteLine($"\n=== {workItemName} (exit code: {exitCode}) ===");

                        if (!string.IsNullOrWhiteSpace(consoleContent))
                        {
                            Console.Error.WriteLine("--- Console Output ---");
                            Console.Error.WriteLine(consoleContent);
                        }
                        else
                        {
                            Console.Error.WriteLine("(console output is empty)");
                        }

                        if (root.TryGetProperty("Logs", out JsonElement logs) &&
                            logs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement log in logs.EnumerateArray())
                            {
                                string? module = log.TryGetProperty("Module", out JsonElement m) ? m.GetString() : null;
                                string? logUrl = log.TryGetProperty("Uri", out JsonElement u) ? u.GetString() : null;
                                if (logUrl is not null)
                                {
                                    string logContent = await http.GetStringAsync(logUrl);
                                    if (!string.IsNullOrWhiteSpace(logContent))
                                    {
                                        Console.Error.WriteLine($"--- Log: {module ?? "unknown"} ---");
                                        Console.Error.WriteLine(logContent);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  (failed to fetch logs for {workItemName}: {ex.Message})");
                }
            }
        }

        private static IHelixApi CreateApi(string? token, string? baseUrl)
        {
            if (token is not null && baseUrl is not null)
            {
                return ApiFactory.GetAuthenticated(baseUrl, token);
            }

            if (token is not null)
            {
                return ApiFactory.GetAuthenticated(token);
            }

            if (baseUrl is not null)
            {
                return ApiFactory.GetAnonymous(baseUrl);
            }

            return ApiFactory.GetAnonymous();
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("""
                Usage: HelixSubmit --queue=<queue> --command=<cmd> --work-item-name=<name>
                                   [--token=<token>] [--base-url=<url>]
                                   [--correlation-payload-dir=<dir>] [--test-payload-dir=<dir>]
                                   [--timeout=<HH:MM:SS>] [--source=<source>] [--type=<type>]
                                   [--creator=<creator>] [--results-dir=<dir>]

                Required:
                  --queue              Helix queue name (e.g., Ubuntu.2204.Amd64.Open)
                  --command            Command to execute on the Helix worker
                  --work-item-name     Name for the Helix work item

                Optional:
                  --token              Helix access token (omit for anonymous/.Open queues)
                  --base-url           Helix API base URL (default: https://helix.dot.net/)
                  --correlation-payload-dir  Shared payload directory (e.g., testhost)
                  --test-payload-dir   Work item payload directory (e.g., test binaries)
                  --timeout            Work item timeout (default: 00:15:00)
                  --source             Job source identifier
                  --type               Job type (default: test/bazel/)
                  --creator            Creator name (required for anonymous, auto-set if omitted)
                  --results-dir        Directory to write result summary files
                """);
        }

        private static bool TryParseArg(string s, string prefix, [NotNullWhen(true)] out string? value)
        {
            if (s.StartsWith(prefix))
            {
                value = s[prefix.Length..];

                return true;
            }

            value = null;

            return false;
        }
    }
}
