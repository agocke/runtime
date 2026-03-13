// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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

            JobPassFail result = await sentJob.WaitAsync(pollingIntervalMs: 10_000);

            Console.WriteLine($"Job completed. Total={result.Total} Passed={result.Passed?.Count ?? 0} Failed={result.Failed?.Count ?? 0}");

            if (result.Failed is { Count: > 0 })
            {
                Console.Error.WriteLine("Failed work items:");
                foreach (string failed in result.Failed)
                {
                    Console.Error.WriteLine($"  - {failed}");
                }
            }

            if (resultsDir is not null)
            {
                Directory.CreateDirectory(resultsDir);
                string summaryPath = Path.Combine(resultsDir, "helix_summary.txt");
                await File.WriteAllTextAsync(summaryPath,
                    $"CorrelationId: {sentJob.CorrelationId}\n" +
                    $"Queue: {queue}\n" +
                    $"Total: {result.Total}\n" +
                    $"Passed: {result.Passed?.Count ?? 0}\n" +
                    $"Failed: {result.Failed?.Count ?? 0}\n");
            }

            bool success = result.Failed is null || result.Failed.Count == 0;

            return success ? 0 : 1;
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
