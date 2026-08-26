using Keyfactor.Orchestrators.Common.Enums;
using Keyfactor.Orchestrators.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MatadorTech.Keyfactor.EndpointValidationCustomJob
{
    public class EndpointValidationJobExtension : ICustomJobExtension
    {
        private const string ExtensionNameValue = "Custom.EndpointValidation";

        private const string DefaultValidationScriptPath =
            @"C:\KeyfactorScripts\EndpointValidation\Invoke-EndpointValidationProfile.ps1";

        private const string DefaultLogDirectory =
            @"C:\KeyfactorScripts\EndpointValidation\Logs";

        private const int DefaultTimeoutSeconds = 300;

        public string ExtensionName
        {
            get
            {
                return ExtensionNameValue;
            }
        }

        public JobResult ProcessJob(JobConfiguration jobConfiguration, SubmitCustomUpdate submitCustomUpdate)
        {
            JobContext jobContext = new JobContext();

            try
            {
                WriteLog("EndpointValidation custom job entered.");

                if (jobConfiguration == null)
                {
                    return NewJobResult(null, OrchestratorJobStatusJobResult.Failure, "JobConfiguration was null.");
                }

                WriteLog("JobHistoryId: " + jobConfiguration.JobHistoryId);
                WriteLog("JobId: " + jobConfiguration.JobId);
                WriteLog("Capability: " + jobConfiguration.Capability);

                Dictionary<string, object> properties = jobConfiguration.JobProperties ?? new Dictionary<string, object>();

                jobContext.ValidationProfileName = GetRequiredString(properties, "ValidationProfileName");
                jobContext.WorkflowInstanceId = GetRequiredString(properties, "WorkflowInstanceId");
                jobContext.WorkflowDefinitionId = GetRequiredString(properties, "WorkflowDefinitionId");
                jobContext.GateStepUniqueName = GetRequiredStringWithFallback(properties, "GateStepUniqueName", "WaitStepUniqueName");
                jobContext.WaitStepUniqueName = GetOptionalString(properties, "WaitStepUniqueName", jobContext.GateStepUniqueName);
                jobContext.CertificateStoreId = GetRequiredString(properties, "CertificateStoreId");
                jobContext.ExpectedCertificateId = GetRequiredInt(properties, "ExpectedCertificateId");
                jobContext.CorrelationId = GetRequiredString(properties, "CorrelationId");
                jobContext.ValidationScriptPath = GetOptionalString(properties, "ValidationScriptPath", DefaultValidationScriptPath);
                jobContext.CaptureMode = NormalizeCaptureMode(GetOptionalString(properties, "CaptureMode", "PostRenewalValidation"));
                jobContext.TimeoutSeconds = GetOptionalInt(properties, "TimeoutSeconds", DefaultTimeoutSeconds);

                if (jobContext.TimeoutSeconds < 30)
                {
                    jobContext.TimeoutSeconds = 30;
                }

                WriteLog("ValidationProfileName: " + jobContext.ValidationProfileName);
                WriteLog("WorkflowInstanceId: " + jobContext.WorkflowInstanceId);
                WriteLog("WorkflowDefinitionId: " + jobContext.WorkflowDefinitionId);
                WriteLog("GateStepUniqueName: " + jobContext.GateStepUniqueName);
                WriteLog("WaitStepUniqueName: " + jobContext.WaitStepUniqueName);
                WriteLog("CertificateStoreId: " + jobContext.CertificateStoreId);
                WriteLog("ExpectedCertificateId: " + jobContext.ExpectedCertificateId);
                WriteLog("CorrelationId: " + jobContext.CorrelationId);
                WriteLog("ValidationScriptPath: " + jobContext.ValidationScriptPath);
                WriteLog("CaptureMode: " + jobContext.CaptureMode);
                WriteLog("TimeoutSeconds: " + jobContext.TimeoutSeconds);

                if (!File.Exists(jobContext.ValidationScriptPath))
                {
                    string message = "Validation script was not found: " + jobContext.ValidationScriptPath;
                    SubmitFailureData(submitCustomUpdate, jobContext, message);
                    return NewJobResult(jobConfiguration, OrchestratorJobStatusJobResult.Failure, message);
                }

                string scriptJson = RunValidationScript(jobContext);

                if (string.IsNullOrWhiteSpace(scriptJson))
                {
                    string message = "Validation script returned an empty response.";
                    SubmitFailureData(submitCustomUpdate, jobContext, message);
                    return NewJobResult(jobConfiguration, OrchestratorJobStatusJobResult.Failure, message);
                }

                WriteLog("Validation script JSON response length: " + scriptJson.Length);

                EndpointValidationResponse validationResponse = ParseValidationResponse(scriptJson);

                CustomJobReturnData returnData = new CustomJobReturnData
                {
                    CustomJobExtension = ExtensionNameValue,
                    CorrelationId = jobContext.CorrelationId,
                    WorkflowInstanceId = jobContext.WorkflowInstanceId,
                    WorkflowDefinitionId = jobContext.WorkflowDefinitionId,
                    GateStepUniqueName = jobContext.GateStepUniqueName,
                    WaitStepUniqueName = jobContext.WaitStepUniqueName,
                    ExpectedCertificateId = jobContext.ExpectedCertificateId,
                    ExpectedCertificateStoreId = jobContext.CertificateStoreId,
                    ValidationProfileName = jobContext.ValidationProfileName,
                    CaptureMode = jobContext.CaptureMode,
                    Validation = validationResponse,
                    AssuranceEvidence = BuildAssuranceEvidence(jobContext, validationResponse),
                    RawValidationJson = scriptJson,
                    CompletedAtUtc = DateTimeOffset.UtcNow.ToString("o")
                };

                string returnJson = JsonSerializer.Serialize(
                    returnData,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                bool updateSubmitted = submitCustomUpdate.Invoke(returnJson);

                if (!updateSubmitted)
                {
                    return NewJobResult(jobConfiguration, OrchestratorJobStatusJobResult.Failure, "submitCustomUpdate.Invoke returned false.");
                }

                string validationError = ValidateResponse(validationResponse, jobContext);

                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    WriteLog("Validation failed: " + validationError);
                    return NewJobResult(jobConfiguration, OrchestratorJobStatusJobResult.Failure, validationError);
                }

                WriteLog("EndpointValidation custom job completed successfully.");
                return NewJobResult(jobConfiguration, OrchestratorJobStatusJobResult.Success, null);
            }
            catch (Exception ex)
            {
                WriteLog("EndpointValidation custom job failed: " + ex);

                try
                {
                    SubmitExceptionData(submitCustomUpdate, jobContext, ex);
                }
                catch
                {
                    // Do not mask the original failure.
                }

                return NewJobResult(jobConfiguration, OrchestratorJobStatusJobResult.Failure, ex.Message);
            }
        }

        private static JobResult NewJobResult(JobConfiguration? jobConfiguration, OrchestratorJobStatusJobResult jobResult, string? failureMessage)
        {
            JobResult result = new JobResult
            {
                Result = jobResult,
                FailureMessage = failureMessage ?? string.Empty
            };

            if (jobConfiguration != null)
            {
                result.JobHistoryId = jobConfiguration.JobHistoryId;
            }

            return result;
        }

        private static string RunValidationScript(JobContext jobContext)
        {
            string powerShellExe = FindPowerShellExecutable();

            Directory.CreateDirectory(DefaultLogDirectory);

            string command =
                "$ErrorActionPreference = 'Stop'; " +
                "$ProgressPreference = 'SilentlyContinue'; " +
                "$VerbosePreference = 'SilentlyContinue'; " +
                "$DebugPreference = 'SilentlyContinue'; " +
                "$InformationPreference = 'SilentlyContinue'; " +
                "$WarningPreference = 'SilentlyContinue'; " +
                "$result = & " + SingleQuote(jobContext.ValidationScriptPath) +
                " -ValidationProfileName " + SingleQuote(jobContext.ValidationProfileName) +
                " -CaptureMode " + SingleQuote(jobContext.CaptureMode) +
                " 3>$null 4>$null 5>$null 6>$null; " +
                "if ($result -is [array]) { $result = $result[-1] }; " +
                "$result | ConvertTo-Json -Depth 80 -Compress";

            string arguments =
                "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command " + Quote(command);

            WriteLog("Starting validation script process.");
            WriteLog("PowerShellExe: " + powerShellExe);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powerShellExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(jobContext.ValidationScriptPath) ?? @"C:\KeyfactorScripts\EndpointValidation"
            };

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();

            using Process process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool exited = process.WaitForExit(jobContext.TimeoutSeconds * 1000);

            if (!exited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Best effort cleanup.
                }

                string timeoutMessage =
                    "Validation script timed out after " + jobContext.TimeoutSeconds + " seconds." +
                    Environment.NewLine +
                    "STDOUT so far: " + stdout +
                    Environment.NewLine +
                    "STDERR so far: " + stderr;

                WriteLog(timeoutMessage);
                throw new TimeoutException(timeoutMessage);
            }

            process.WaitForExit();

            string stdoutText = stdout.ToString();
            string stderrText = stderr.ToString();

            WriteLog("Validation script exit code: " + process.ExitCode);
            WriteLog("Validation script stdout length: " + stdoutText.Length);
            WriteLog("Validation script stderr length: " + stderrText.Length);

            if (process.ExitCode != 0)
            {
                throw new Exception(
                    "Validation script exited with code " + process.ExitCode + "." +
                    Environment.NewLine +
                    "STDERR: " + stderrText +
                    Environment.NewLine +
                    "STDOUT: " + stdoutText);
            }

            string json = ExtractJsonObject(stdoutText);

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new Exception(
                    "Validation script did not emit a JSON object." +
                    Environment.NewLine +
                    "STDERR: " + stderrText +
                    Environment.NewLine +
                    "STDOUT: " + stdoutText);
            }

            return json;
        }

        private static string FindPowerShellExecutable()
        {
            string pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

            if (File.Exists(pwsh))
            {
                return pwsh;
            }

            return @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string SingleQuote(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private static string ExtractJsonObject(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return string.Empty;
            }

            string trimmed = stdout.Trim();

            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                return trimmed;
            }

            int firstBrace = trimmed.IndexOf('{');
            int lastBrace = trimmed.LastIndexOf('}');

            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return string.Empty;
        }

        private static EndpointValidationResponse ParseValidationResponse(string json)
        {
            EndpointValidationResponse? response =
                JsonSerializer.Deserialize<EndpointValidationResponse>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (response == null)
            {
                throw new Exception("Failed to deserialize endpoint validation response.");
            }

            return response;
        }


        private static Dictionary<string, object?> BuildAssuranceEvidence(JobContext jobContext, EndpointValidationResponse response)
        {
            if (IsPreRenewalCapture(jobContext))
            {
                return BuildPreRenewalCaptureAssuranceEvidence(jobContext, response);
            }

            HttpHealthResult health = TestApplicationHttpHealth(response.WebsiteUrl);
            string hostName = GetHostName(response.WebsiteUrl, response.ServerName);
            string pathTested = GetPathAndQuery(response.WebsiteUrl);
            string port = GetPort(response.WebsiteUrl);
            string hostnameSanMatch = DetermineHostnameSanMatch(hostName, response.Subject, response.San);

            string outcomeStatus = string.Equals(response.ValidationStatus, "PASS", StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL";
            string actionRequired = outcomeStatus == "PASS" ? "None" : "Review required";
            string recommendedAction = outcomeStatus == "PASS"
                ? "No application-owner action is required."
                : "Review endpoint validation details and certificate deployment status before approving the workflow gate.";
            string headline = outcomeStatus == "PASS"
                ? "Application TLS Renewal Verified - No Action Required"
                : "Application TLS Renewal Validation Requires Review";
            string summary = outcomeStatus == "PASS"
                ? "The application endpoint is serving the renewed certificate successfully. TLS validation passed, the served certificate matches Keyfactor Command inventory, and no application-owner action is required."
                : response.ValidationMessage ?? "Endpoint validation did not pass.";

            List<Dictionary<string, object?>> checklist = new List<Dictionary<string, object?>>
            {
                NewCheck("Application endpoint reachable", health.EndpointReachable),
                NewCheck("TLS handshake completed", health.TlsHandshake),
                NewCheck("Served certificate captured from live endpoint", IsPresent(response.SerialNumber) ? "PASS" : "FAIL"),
                NewCheck("Served certificate matches Keyfactor inventory", outcomeStatus),
                NewCheck("Certificate is not expired", CertificateNotExpired(response.ValidatedAtUtc, null)),
                NewCheck("CN/SAN matches application hostname", hostnameSanMatch),
                NewCheck("Issuer captured", IsPresent(response.Issuer) ? "PASS" : "FAIL"),
                NewCheck("Certificate chain is trusted", health.CertificateChainValidation),
                NewCheck("Weak signature algorithm check", "NotPerformed"),
                NewCheck("Key size / algorithm policy check", "NotPerformed"),
                NewCheck("Revocation check", "NotPerformed"),
                NewCheck("Application HTTP health check", health.ApplicationHttpHealthCheck),
                NewCheck("Application content check", "NotPerformed"),
                NewCheck("Load-balanced endpoint consistency", "NotApplicable")
            };

            return new Dictionary<string, object?>
            {
                ["EvidenceModelVersion"] = "v10",
                ["GeneratedAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["Outcome"] = new Dictionary<string, object?>
                {
                    ["Headline"] = headline,
                    ["Status"] = outcomeStatus,
                    ["ActionRequired"] = actionRequired,
                    ["Summary"] = summary,
                    ["RecommendedAction"] = recommendedAction
                },
                ["ApplicationHealth"] = new Dictionary<string, object?>
                {
                    ["ApplicationUrlTested"] = response.WebsiteUrl,
                    ["HttpResponse"] = health.HttpResponse,
                    ["HttpStatusCode"] = health.HttpStatusCode,
                    ["TlsHandshake"] = health.TlsHandshake,
                    ["HostnameSanMatch"] = hostnameSanMatch,
                    ["CertificateChainValidation"] = health.CertificateChainValidation,
                    ["ApplicationContentCheck"] = "NotPerformed",
                    ["ResponseTimeMs"] = health.ResponseTimeMs,
                    ["TestedFrom"] = Environment.MachineName,
                    ["HealthCheckError"] = health.ErrorMessage
                },
                ["RenewalVerification"] = new Dictionary<string, object?>
                {
                    ["PreviousCertificate"] = new Dictionary<string, object?>
                    {
                        ["Status"] = "NotCaptured",
                        ["Reason"] = "Before-renewal endpoint evidence is not captured by the v8 validation job. This will be added in a later evidence model version."
                    },
                    ["RenewedCertificate"] = new Dictionary<string, object?>
                    {
                        ["CertificateId"] = response.CertificateId,
                        ["InventoryItemId"] = response.CertStoreInventoryItemId,
                        ["SerialNumber"] = response.SerialNumber,
                        ["Sha1Thumbprint"] = response.Sha1Thumbprint,
                        ["Sha256Thumbprint"] = response.Sha256Thumbprint,
                        ["Subject"] = response.Subject,
                        ["Issuer"] = response.Issuer,
                        ["SAN"] = response.San,
                        ["ServedByApplication"] = IsPresent(response.SerialNumber) ? "After renewal" : "Unknown"
                    },
                    ["CommandInventoryMatch"] = outcomeStatus
                },
                ["ValidationChecklist"] = checklist,
                ["ValidationScope"] = new Dictionary<string, object?>
                {
                    ["EndpointTested"] = "Direct server/profile target",
                    ["ApplicationUrlTested"] = response.WebsiteUrl,
                    ["Host"] = hostName,
                    ["Port"] = port,
                    ["Protocol"] = GetProtocol(response.WebsiteUrl),
                    ["SniUsed"] = hostName,
                    ["PathTested"] = pathTested,
                    ["LoadBalancerTested"] = "NotValidated",
                    ["BackendNodesTested"] = "One/profile target",
                    ["ClientPerspective"] = "Internal orchestrator perspective",
                    ["ValidationSource"] = "Keyfactor Endpoint Validation Framework"
                },
                ["AutomationContext"] = new Dictionary<string, object?>
                {
                    ["WorkflowInstanceId"] = jobContext.WorkflowInstanceId,
                    ["WorkflowDefinitionId"] = jobContext.WorkflowDefinitionId,
                    ["ValidationJobCorrelationId"] = jobContext.CorrelationId,
                    ["ValidationProfile"] = jobContext.ValidationProfileName,
                    ["CustomJobExtension"] = ExtensionNameValue,
                    ["GateStepUniqueName"] = jobContext.GateStepUniqueName,
                    ["ExpectedCertificateId"] = jobContext.ExpectedCertificateId,
                    ["ExpectedCertificateStoreId"] = jobContext.CertificateStoreId,
                    ["ValidationScriptPath"] = jobContext.ValidationScriptPath,
                    ["TimeoutSeconds"] = jobContext.TimeoutSeconds,
                    ["TestedFromOrchestrator"] = Environment.MachineName
                },
                ["CertificateEvidence"] = new Dictionary<string, object?>
                {
                    ["CertificateId"] = response.CertificateId,
                    ["InventoryItemId"] = response.CertStoreInventoryItemId,
                    ["CertificateStoreId"] = response.CertificateStoreId,
                    ["SerialNumber"] = response.SerialNumber,
                    ["Sha1Thumbprint"] = response.Sha1Thumbprint,
                    ["Sha256Thumbprint"] = response.Sha256Thumbprint,
                    ["Subject"] = response.Subject,
                    ["Issuer"] = response.Issuer,
                    ["SAN"] = response.San,
                    ["ValidatedAtUtc"] = response.ValidatedAtUtc
                },
                ["RecommendedAction"] = recommendedAction
            };
        }


        private static Dictionary<string, object?> BuildPreRenewalCaptureAssuranceEvidence(JobContext jobContext, EndpointValidationResponse response)
        {
            Dictionary<string, object?> previous = response.PreviousCertificateEvidence ?? new Dictionary<string, object?>();

            string serialNumber = GetDictionaryValue(previous, "SerialNumber") ?? response.SerialNumber ?? string.Empty;
            string sha1 = GetDictionaryValue(previous, "Sha1Thumbprint") ?? response.Sha1Thumbprint ?? string.Empty;
            string sha256 = GetDictionaryValue(previous, "Sha256Thumbprint") ?? response.Sha256Thumbprint ?? string.Empty;
            string capturedAtUtc = GetDictionaryValue(previous, "CapturedAtUtc") ?? response.ValidatedAtUtc ?? DateTimeOffset.UtcNow.ToString("o");
            string url = GetDictionaryValue(previous, "Url") ?? response.WebsiteUrl ?? string.Empty;
            string host = GetDictionaryValue(previous, "TargetHost") ?? response.ServerName ?? GetHostName(url, response.ServerName) ?? string.Empty;
            string port = GetDictionaryValue(previous, "Port") ?? GetPort(url);
            string path = GetDictionaryValue(previous, "Path") ?? GetPathAndQuery(url);
            string protocol = GetProtocol(url);

            return new Dictionary<string, object?>
            {
                ["EvidenceModelVersion"] = "v10",
                ["GeneratedAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["Outcome"] = new Dictionary<string, object?>
                {
                    ["Headline"] = "Pre-renewal Certificate Evidence Captured",
                    ["Status"] = response.ValidationStatus,
                    ["ActionRequired"] = "None",
                    ["Summary"] = response.ValidationMessage,
                    ["RecommendedAction"] = "Continue certificate renewal workflow. This evidence will be used for the before/after certificate change comparison."
                },
                ["RenewalVerification"] = new Dictionary<string, object?>
                {
                    ["PreviousCertificate"] = previous,
                    ["RenewedCertificate"] = null,
                    ["CommandInventoryMatch"] = "NotApplicableBeforeRenewal"
                },
                ["ValidationChecklist"] = new List<Dictionary<string, object?>>
                {
                    NewCheck("Previous certificate captured from live endpoint", IsPresent(serialNumber) ? "PASS" : "FAIL"),
                    NewCheck("Previous certificate SHA1 captured", IsPresent(sha1) ? "PASS" : "FAIL"),
                    NewCheck("Previous certificate SHA256 captured", IsPresent(sha256) ? "PASS" : "FAIL"),
                    NewCheck("Previous certificate capture timestamp recorded", IsPresent(capturedAtUtc) ? "PASS" : "FAIL")
                },
                ["ValidationScope"] = new Dictionary<string, object?>
                {
                    ["EndpointTested"] = "Direct server/profile target",
                    ["ApplicationUrlTested"] = url,
                    ["Host"] = host,
                    ["Port"] = port,
                    ["Protocol"] = protocol,
                    ["SniUsed"] = GetDictionaryValue(previous, "SniHost") ?? host,
                    ["PathTested"] = path,
                    ["ClientPerspective"] = "Internal Keyfactor Universal Orchestrator perspective",
                    ["ValidationSource"] = "Keyfactor Endpoint Validation Framework"
                },
                ["AutomationContext"] = new Dictionary<string, object?>
                {
                    ["WorkflowInstanceId"] = jobContext.WorkflowInstanceId,
                    ["WorkflowDefinitionId"] = jobContext.WorkflowDefinitionId,
                    ["ValidationJobCorrelationId"] = jobContext.CorrelationId,
                    ["ValidationProfile"] = jobContext.ValidationProfileName,
                    ["CaptureMode"] = jobContext.CaptureMode,
                    ["CustomJobExtension"] = ExtensionNameValue,
                    ["GateStepUniqueName"] = jobContext.GateStepUniqueName,
                    ["ExpectedCertificateId"] = jobContext.ExpectedCertificateId,
                    ["ExpectedCertificateStoreId"] = jobContext.CertificateStoreId,
                    ["ValidationScriptPath"] = jobContext.ValidationScriptPath,
                    ["TimeoutSeconds"] = jobContext.TimeoutSeconds,
                    ["TestedFromOrchestrator"] = Environment.MachineName
                },
                ["CertificateEvidence"] = new Dictionary<string, object?>
                {
                    ["SerialNumber"] = serialNumber,
                    ["Sha1Thumbprint"] = sha1,
                    ["Sha256Thumbprint"] = sha256,
                    ["Subject"] = GetDictionaryValue(previous, "Subject") ?? response.Subject,
                    ["Issuer"] = GetDictionaryValue(previous, "Issuer") ?? response.Issuer,
                    ["SAN"] = GetDictionaryValue(previous, "San") ?? response.San,
                    ["CapturedAtUtc"] = capturedAtUtc
                },
                ["RecommendedAction"] = "Continue certificate renewal workflow."
            };
        }

        private static string? GetDictionaryValue(Dictionary<string, object?> values, string name)
        {
            if (!values.TryGetValue(name, out object? value) || value == null)
            {
                return null;
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString();
                }

                return element.ToString();
            }

            return value.ToString();
        }

        private static Dictionary<string, object?> BuildFailureAssuranceEvidence(JobContext jobContext, string failureMessage)
        {
            return new Dictionary<string, object?>
            {
                ["EvidenceModelVersion"] = "v8",
                ["GeneratedAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["Outcome"] = new Dictionary<string, object?>
                {
                    ["Headline"] = "Application TLS Renewal Validation Requires Review",
                    ["Status"] = "FAIL",
                    ["ActionRequired"] = "Review required",
                    ["Summary"] = failureMessage,
                    ["RecommendedAction"] = "Review the validation job failure and do not release the workflow gate until endpoint validation succeeds."
                },
                ["AutomationContext"] = new Dictionary<string, object?>
                {
                    ["WorkflowInstanceId"] = jobContext.WorkflowInstanceId,
                    ["WorkflowDefinitionId"] = jobContext.WorkflowDefinitionId,
                    ["ValidationJobCorrelationId"] = jobContext.CorrelationId,
                    ["ValidationProfile"] = jobContext.ValidationProfileName,
                    ["CustomJobExtension"] = ExtensionNameValue,
                    ["GateStepUniqueName"] = jobContext.GateStepUniqueName,
                    ["ExpectedCertificateId"] = jobContext.ExpectedCertificateId,
                    ["ExpectedCertificateStoreId"] = jobContext.CertificateStoreId,
                    ["TestedFromOrchestrator"] = Environment.MachineName
                },
                ["RecommendedAction"] = "Review the validation job failure and do not release the workflow gate until endpoint validation succeeds."
            };
        }

        private static Dictionary<string, object?> NewCheck(string name, string? result)
        {
            return new Dictionary<string, object?>
            {
                ["Validation"] = name,
                ["Result"] = string.IsNullOrWhiteSpace(result) ? "Unknown" : result
            };
        }

        private static HttpHealthResult TestApplicationHttpHealth(string? websiteUrl)
        {
            HttpHealthResult result = new HttpHealthResult();

            if (string.IsNullOrWhiteSpace(websiteUrl))
            {
                result.ApplicationHttpHealthCheck = "NotPerformed";
                result.EndpointReachable = "Unknown";
                result.TlsHandshake = "Unknown";
                result.CertificateChainValidation = "Unknown";
                result.ErrorMessage = "WebsiteUrl was not provided in validation response.";
                return result;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                using HttpClientHandler handler = new HttpClientHandler();
                using HttpClient client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(20);

                using HttpResponseMessage response = client.GetAsync(websiteUrl).GetAwaiter().GetResult();
                stopwatch.Stop();

                result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
                result.HttpStatusCode = (int)response.StatusCode;
                result.HttpResponse = ((int)response.StatusCode).ToString() + " " + response.ReasonPhrase;
                result.EndpointReachable = "PASS";
                result.TlsHandshake = "PASS";
                result.CertificateChainValidation = "PASS";
                result.ApplicationHttpHealthCheck = ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400) ? "PASS" : "WARN";
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
                result.HttpResponse = "No HTTP response";
                result.EndpointReachable = "FAIL";
                result.TlsHandshake = websiteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "FAIL" : "Unknown";
                result.CertificateChainValidation = "Unknown";
                result.ApplicationHttpHealthCheck = "FAIL";
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        private static string DetermineHostnameSanMatch(string? hostName, string? subject, string? san)
        {
            if (string.IsNullOrWhiteSpace(hostName))
            {
                return "Unknown";
            }

            string host = hostName.Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(san) && san.ToLowerInvariant().Contains(host))
            {
                return "PASS";
            }

            if (!string.IsNullOrWhiteSpace(subject) && subject.ToLowerInvariant().Contains("cn=" + host))
            {
                return "PASS";
            }

            return "Unknown";
        }

        private static string CertificateNotExpired(string? validatedAtUtc, string? notAfterUtc)
        {
            if (string.IsNullOrWhiteSpace(notAfterUtc))
            {
                return "NotCaptured";
            }

            if (!DateTimeOffset.TryParse(notAfterUtc, out DateTimeOffset notAfter))
            {
                return "Unknown";
            }

            return notAfter > DateTimeOffset.UtcNow ? "PASS" : "FAIL";
        }

        private static string? GetHostName(string? url, string? fallbackServerName)
        {
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return uri.Host;
            }

            return fallbackServerName;
        }

        private static string GetPathAndQuery(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                string value = uri.PathAndQuery;
                return string.IsNullOrWhiteSpace(value) ? "/" : value;
            }

            return "Unknown";
        }

        private static string GetPort(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return uri.Port.ToString();
            }

            return "Unknown";
        }

        private static string GetProtocol(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return uri.Scheme.ToUpperInvariant();
            }

            return "Unknown";
        }

        private static bool IsPresent(string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private class HttpHealthResult
        {
            public string EndpointReachable { get; set; } = "Unknown";
            public string TlsHandshake { get; set; } = "Unknown";
            public string CertificateChainValidation { get; set; } = "Unknown";
            public string ApplicationHttpHealthCheck { get; set; } = "Unknown";
            public string? HttpResponse { get; set; }
            public int? HttpStatusCode { get; set; }
            public long? ResponseTimeMs { get; set; }
            public string? ErrorMessage { get; set; }
        }

        private static string ValidateResponse(EndpointValidationResponse response, JobContext jobContext)
        {
            if (!string.Equals(response.ValidationStatus, "PASS", StringComparison.OrdinalIgnoreCase))
            {
                return "ValidationStatus was not PASS. Status=" + response.ValidationStatus +
                       "; FailureCategory=" + response.FailureCategory +
                       "; DetailedError=" + response.DetailedError;
            }

            if (IsPreRenewalCapture(jobContext))
            {
                return ValidatePreRenewalCaptureResponse(response);
            }

            if (response.MetadataUpdated != true)
            {
                return "MetadataUpdated was not true. MetadataUpdated=" + response.MetadataUpdated +
                       "; MetadataUpdateHttpStatusCode=" + response.MetadataUpdateHttpStatusCode;
            }

            if (!string.IsNullOrWhiteSpace(response.CertificateStoreId))
            {
                if (!string.Equals(response.CertificateStoreId, jobContext.CertificateStoreId, StringComparison.OrdinalIgnoreCase))
                {
                    return "CertificateStoreId mismatch. Expected=" + jobContext.CertificateStoreId +
                           "; Actual=" + response.CertificateStoreId;
                }
            }

            if (string.IsNullOrWhiteSpace(response.CertificateId))
            {
                return "Validation response did not include CertificateId.";
            }

            if (!int.TryParse(response.CertificateId, out int actualCertificateId))
            {
                return "Validation response CertificateId was not numeric: " + response.CertificateId;
            }

            if (actualCertificateId != jobContext.ExpectedCertificateId)
            {
                return "Validation CertificateId mismatch. Expected=" + jobContext.ExpectedCertificateId +
                       "; Actual=" + actualCertificateId;
            }

            return string.Empty;
        }

        private static string ValidatePreRenewalCaptureResponse(EndpointValidationResponse response)
        {
            if (response.PreviousCertificateEvidence == null)
            {
                return "PreRenewalCapture response did not include PreviousCertificateEvidence.";
            }

            string? serialNumber = GetDictionaryValue(response.PreviousCertificateEvidence, "SerialNumber") ?? response.SerialNumber;
            string? sha1 = GetDictionaryValue(response.PreviousCertificateEvidence, "Sha1Thumbprint") ?? response.Sha1Thumbprint;
            string? sha256 = GetDictionaryValue(response.PreviousCertificateEvidence, "Sha256Thumbprint") ?? response.Sha256Thumbprint;
            string? capturedAtUtc = GetDictionaryValue(response.PreviousCertificateEvidence, "CapturedAtUtc");

            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                return "PreRenewalCapture response did not include previous certificate SerialNumber.";
            }

            if (string.IsNullOrWhiteSpace(sha1))
            {
                return "PreRenewalCapture response did not include previous certificate Sha1Thumbprint.";
            }

            if (string.IsNullOrWhiteSpace(sha256))
            {
                return "PreRenewalCapture response did not include previous certificate Sha256Thumbprint.";
            }

            if (string.IsNullOrWhiteSpace(capturedAtUtc))
            {
                return "PreRenewalCapture response did not include previous certificate CapturedAtUtc.";
            }

            return string.Empty;
        }

        private static bool IsPreRenewalCapture(JobContext jobContext)
        {
            return string.Equals(jobContext.CaptureMode, "PreRenewalCapture", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCaptureMode(string value)
        {
            if (string.Equals(value, "PreRenewalCapture", StringComparison.OrdinalIgnoreCase))
            {
                return "PreRenewalCapture";
            }

            return "PostRenewalValidation";
        }

        private static string GetRequiredString(Dictionary<string, object> properties, string name)
        {
            string value = GetOptionalString(properties, name, string.Empty);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new Exception("Required job property was missing or empty: " + name);
            }

            return value;
        }

        private static string GetRequiredStringWithFallback(Dictionary<string, object> properties, string primaryName, string fallbackName)
        {
            string primaryValue = GetOptionalString(properties, primaryName, string.Empty);

            if (!string.IsNullOrWhiteSpace(primaryValue))
            {
                return primaryValue;
            }

            string fallbackValue = GetOptionalString(properties, fallbackName, string.Empty);

            if (!string.IsNullOrWhiteSpace(fallbackValue))
            {
                return fallbackValue;
            }

            throw new Exception("Required job property was missing or empty: " + primaryName + " or " + fallbackName);
        }

        private static string GetOptionalString(Dictionary<string, object> properties, string name, string defaultValue)
        {
            if (!properties.TryGetValue(name, out object? rawValue) || rawValue == null)
            {
                return defaultValue;
            }

            return rawValue.ToString() ?? defaultValue;
        }

        private static int GetRequiredInt(Dictionary<string, object> properties, string name)
        {
            if (!properties.TryGetValue(name, out object? rawValue) || rawValue == null)
            {
                throw new Exception("Required job property was missing: " + name);
            }

            if (rawValue is int intValue)
            {
                return intValue;
            }

            if (rawValue is long longValue)
            {
                return Convert.ToInt32(longValue);
            }

            if (int.TryParse(rawValue.ToString(), out int parsed))
            {
                return parsed;
            }

            throw new Exception("Required job property was not an integer: " + name + "=" + rawValue);
        }

        private static int GetOptionalInt(Dictionary<string, object> properties, string name, int defaultValue)
        {
            if (!properties.TryGetValue(name, out object? rawValue) || rawValue == null)
            {
                return defaultValue;
            }

            if (rawValue is int intValue)
            {
                return intValue;
            }

            if (rawValue is long longValue)
            {
                return Convert.ToInt32(longValue);
            }

            if (int.TryParse(rawValue.ToString(), out int parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private static void SubmitFailureData(SubmitCustomUpdate submitCustomUpdate, JobContext jobContext, string failureMessage)
        {
            CustomJobReturnData data = NewReturnData(jobContext);
            data.FailureMessage = failureMessage;
            data.CompletedAtUtc = DateTimeOffset.UtcNow.ToString("o");
            data.AssuranceEvidence = BuildFailureAssuranceEvidence(jobContext, failureMessage);

            string json = JsonSerializer.Serialize(
                data,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            submitCustomUpdate.Invoke(json);
        }

        private static void SubmitExceptionData(SubmitCustomUpdate submitCustomUpdate, JobContext jobContext, Exception ex)
        {
            CustomJobReturnData data = NewReturnData(jobContext);
            data.FailureMessage = ex.Message;
            data.ExceptionType = ex.GetType().FullName;
            data.CompletedAtUtc = DateTimeOffset.UtcNow.ToString("o");
            data.AssuranceEvidence = BuildFailureAssuranceEvidence(jobContext, ex.Message);

            string json = JsonSerializer.Serialize(
                data,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            submitCustomUpdate.Invoke(json);
        }

        private static CustomJobReturnData NewReturnData(JobContext jobContext)
        {
            return new CustomJobReturnData
            {
                CustomJobExtension = ExtensionNameValue,
                CorrelationId = jobContext.CorrelationId,
                WorkflowInstanceId = jobContext.WorkflowInstanceId,
                WorkflowDefinitionId = jobContext.WorkflowDefinitionId,
                GateStepUniqueName = jobContext.GateStepUniqueName,
                WaitStepUniqueName = jobContext.WaitStepUniqueName,
                ExpectedCertificateId = jobContext.ExpectedCertificateId,
                ExpectedCertificateStoreId = jobContext.CertificateStoreId,
                ValidationProfileName = jobContext.ValidationProfileName
            };
        }

        private static void WriteLog(string message)
        {
            try
            {
                Directory.CreateDirectory(DefaultLogDirectory);

                string logPath = Path.Combine(DefaultLogDirectory, "EndpointValidationCustomJob.log");
                string line = DateTimeOffset.Now.ToString("o") + " " + message;

                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break the job.
            }
        }

        private class JobContext
        {
            public string ValidationProfileName { get; set; } = string.Empty;
            public string WorkflowInstanceId { get; set; } = string.Empty;
            public string WorkflowDefinitionId { get; set; } = string.Empty;
            public string GateStepUniqueName { get; set; } = string.Empty;
            public string WaitStepUniqueName { get; set; } = string.Empty;
            public string CertificateStoreId { get; set; } = string.Empty;
            public int ExpectedCertificateId { get; set; }
            public string CorrelationId { get; set; } = string.Empty;
            public string ValidationScriptPath { get; set; } = DefaultValidationScriptPath;
            public string CaptureMode { get; set; } = "PostRenewalValidation";
            public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
        }

        private class CustomJobReturnData
        {
            public string? CustomJobExtension { get; set; }
            public string? CorrelationId { get; set; }
            public string? WorkflowInstanceId { get; set; }
            public string? WorkflowDefinitionId { get; set; }
            public string? GateStepUniqueName { get; set; }
            public string? WaitStepUniqueName { get; set; }
            public int ExpectedCertificateId { get; set; }
            public string? ExpectedCertificateStoreId { get; set; }
            public string? ValidationProfileName { get; set; }
            public string? CaptureMode { get; set; }
            public string? CompletedAtUtc { get; set; }
            public string? FailureMessage { get; set; }
            public string? ExceptionType { get; set; }
            public EndpointValidationResponse? Validation { get; set; }
            public Dictionary<string, object?>? AssuranceEvidence { get; set; }
            public string? RawValidationJson { get; set; }
        }

        private class EndpointValidationResponse
        {
            public string? RequestId { get; set; }
            public string? ListenerHost { get; set; }
            public string? CompletedAtUtc { get; set; }

            public string? ValidationProfileName { get; set; }
            public string? ValidationStatus { get; set; }
            public string? ValidationMessage { get; set; }
            public string? FailureCategory { get; set; }
            public string? DetailedError { get; set; }

            public bool? MetadataUpdated { get; set; }
            public int? MetadataUpdateHttpStatusCode { get; set; }

            public string? CertificateId { get; set; }
            public string? CertStoreInventoryItemId { get; set; }
            public string? CertificateStoreId { get; set; }
            public string? SerialNumber { get; set; }
            public string? Sha1Thumbprint { get; set; }
            public string? Sha256Thumbprint { get; set; }
            public string? Subject { get; set; }
            public string? Issuer { get; set; }
            public string? San { get; set; }
            public string? WebsiteUrl { get; set; }
            public string? ServerName { get; set; }
            public string? Platform { get; set; }
            public string? ValidatedAtUtc { get; set; }
            public string? CaptureMode { get; set; }
            public Dictionary<string, object?>? PreviousCertificateEvidence { get; set; }
            public Dictionary<string, object?>? CertificateChangeEvidence { get; set; }
        }
    }
}