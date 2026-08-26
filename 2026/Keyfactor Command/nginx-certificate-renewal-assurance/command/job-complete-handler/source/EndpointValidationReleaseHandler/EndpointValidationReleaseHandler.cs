using Keyfactor.Logging;
using Keyfactor.Platform.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatadorTech.Keyfactor.EndpointValidation
{
    public class EndpointValidationReleaseHandler : IOrchestratorJobCompleteHandler
    {
        private ILogger? _logger;

        public ILogger Logger
        {
            get
            {
                if (_logger == null)
                {
                    _logger = LogHandler.GetReflectedClassLogger(this);
                }

                return _logger;
            }
        }

        public string JobTypes { get; set; }

        private readonly Options _options;

        public class Options
        {
            public string? JobTypes { get; set; }

            public string? TargetClientMachine { get; set; }
            public string? WorkflowDefinitionId { get; set; }
            public string? GateStepUniqueName { get; set; }
            public string? WaitStepUniqueName { get; set; }
            public string? WorkflowSignalKey { get; set; }
            public string? ReleaseComment { get; set; }
            public string? TargetCertificateStoreId { get; set; }

            public string? RfpemInventoryJobTypeId { get; set; }
            public string? EndpointValidationJobTypeId { get; set; }
            public string? EndpointValidationJobTypeName { get; set; }
            public string? ValidationProfileName { get; set; }
            public string? EndpointValidationProfileMappings { get; set; }
            public int CustomJobTimeoutSeconds { get; set; } = 300;
            public string? ValidationScriptPath { get; set; }
            public bool EnableAssuranceMetadataUpdate { get; set; } = true;
            public bool RequireAssuranceMetadataUpdate { get; set; } = true;

            public bool EnableCustomJobScheduling { get; set; } = true;
            public bool EnableWorkflowRelease { get; set; } = false;
            public bool RequireSuccessfulJobResult { get; set; } = true;
            public bool RequireValidationPass { get; set; } = true;
            public bool RequireMetadataUpdated { get; set; } = true;
            public bool RequireValidationCertificateId { get; set; } = true;

            public bool EnableEvidenceCache { get; set; } = true;
            public bool RequirePreRenewalEvidenceForPostValidation { get; set; } = false;
            public string? EvidenceCacheRoot { get; set; }
            public int EvidenceCacheRetentionDays { get; set; } = 14;
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
            public JsonElement? PreviousCertificateEvidence { get; set; }
            public JsonElement? CertificateChangeEvidence { get; set; }
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
            public JsonElement? AssuranceEvidence { get; set; }
            public string? RawValidationJson { get; set; }
        }

        private class CustomJobDataEnvelope
        {
            public long? JobHistoryId { get; set; }
            public string? Data { get; set; }
        }

        private class JobHistoryRecord
        {
            public long? JobHistoryId { get; set; }
            public string? AgentMachine { get; set; }
            public string? JobId { get; set; }
            public string? JobType { get; set; }
            public DateTimeOffset? OperationStart { get; set; }
            public DateTimeOffset? OperationEnd { get; set; }
            public string? Message { get; set; }
            public string? Result { get; set; }
            public string? Status { get; set; }
            public string? StorePath { get; set; }
            public string? ClientMachine { get; set; }
        }

        public EndpointValidationReleaseHandler(IOptions<Options> options)
        {
            _options = options.Value;

            if (string.IsNullOrWhiteSpace(_options.JobTypes))
            {
                throw new Exception("JobTypes must be specified in Options for EndpointValidationReleaseHandler.");
            }

            JobTypes = _options.JobTypes;

            // GitHub reference build: these fallback GUIDs are synthetic examples only.
            // Configure the real job type IDs in the handler manifest for your Command environment.
            if (string.IsNullOrWhiteSpace(_options.RfpemInventoryJobTypeId))
            {
                _options.RfpemInventoryJobTypeId = "11111111-1111-4111-8111-111111111111";
            }

            if (string.IsNullOrWhiteSpace(_options.EndpointValidationJobTypeId))
            {
                _options.EndpointValidationJobTypeId = "22222222-2222-4222-8222-222222222222";
            }

            if (string.IsNullOrWhiteSpace(_options.EndpointValidationJobTypeName))
            {
                _options.EndpointValidationJobTypeName = "EndpointValidation";
            }

            if (_options.CustomJobTimeoutSeconds < 30)
            {
                _options.CustomJobTimeoutSeconds = 30;
            }

            string configuredGateStepUniqueName = FirstNonEmpty(
                _options.GateStepUniqueName,
                _options.WaitStepUniqueName,
                "WaitForEndpointValidation");

            _options.GateStepUniqueName = configuredGateStepUniqueName;

            // Backward compatibility: the existing custom job type and orchestrator extension still
            // understand WaitStepUniqueName. During the naming refactor, keep it populated with
            // the same value as GateStepUniqueName.
            _options.WaitStepUniqueName = configuredGateStepUniqueName;

            if (string.IsNullOrWhiteSpace(_options.WorkflowSignalKey))
            {
                _options.WorkflowSignalKey = configuredGateStepUniqueName + ".ApprovalStatus";
            }

            if (string.IsNullOrWhiteSpace(_options.ReleaseComment))
            {
                _options.ReleaseComment = "Endpoint validation custom job completed successfully. Releasing workflow gate.";
            }

            if (string.IsNullOrWhiteSpace(_options.EvidenceCacheRoot))
            {
                _options.EvidenceCacheRoot = @"C:\ProgramData\Keyfactor\ExtensionData\EndpointValidation\EvidenceCache";
            }

            if (_options.EvidenceCacheRetentionDays < 1)
            {
                _options.EvidenceCacheRetentionDays = 14;
            }
        }

        public bool RunHandler(OrchestratorJobCompleteHandlerContext context)
        {
            Task<bool> task = Task.Run(async () => await AsyncRunHandler(context));
            return task.Result;
        }

        private async Task<bool> AsyncRunHandler(OrchestratorJobCompleteHandlerContext context)
        {
            try
            {
                if (context == null)
                {
                    Logger.LogError("EndpointValidationReleaseHandler received a null context.");
                    return false;
                }

                Logger.LogInformation("EndpointValidationReleaseHandler entered.");
                Logger.LogInformation("Context: {0}", ParseContext(context));

                if (!IsTargetMachine(context))
                {
                    Logger.LogInformation("Completed job did not match this handler's target machine filter. No action taken.");
                    return true;
                }

                JobPhase phase = GetJobPhase(context);

                if (phase == JobPhase.NotTarget)
                {
                    Logger.LogInformation("Completed job did not match RFPEMInventory or EndpointValidation target job types. No action taken.");
                    return true;
                }

                if (_options.RequireSuccessfulJobResult && !IsSuccessfulJobResult(context.JobResult))
                {
                    Logger.LogWarning("Matched job did not report success. Phase={0}, JobResult={1}. No workflow gate will be released.", phase, context.JobResult);
                    return true;
                }

                if (context.Client == null || context.Client.BaseAddress == null)
                {
                    Logger.LogError("Context did not include a usable Command API HttpClient/BaseAddress.");
                    return false;
                }

                using HttpClient commandClient = NewCommandApiClient(context);

                if (phase == JobPhase.RfpemInventory)
                {
                    return await HandleRfpemInventoryCompletion(commandClient, context);
                }

                if (phase == JobPhase.EndpointValidation)
                {
                    return await HandleEndpointValidationCompletion(commandClient, context);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("EndpointValidationReleaseHandler failed unexpectedly. Error={0}", ex.ToString());
                return false;
            }
        }

        private async Task<bool> HandleRfpemInventoryCompletion(HttpClient commandClient, OrchestratorJobCompleteHandlerContext context)
        {
            Logger.LogInformation("Handling RFPEMInventory completion. Looking for suspended workflow candidate.");

            List<WorkflowInstance> candidates = await FindWorkflowCandidatesForInventoryCompletion(commandClient);

            if (candidates.Count == 0)
            {
                Logger.LogInformation(
                    "No suspended workflow candidates matched GateStepUniqueName={0}, WorkflowDefinitionFilter={1}, CertificateStoreFilter={2}. No custom validation job will be scheduled.",
                    _options.GateStepUniqueName,
                    string.IsNullOrWhiteSpace(_options.WorkflowDefinitionId) ? "<none>" : _options.WorkflowDefinitionId,
                    string.IsNullOrWhiteSpace(_options.TargetCertificateStoreId) ? "<runtime-derived>" : _options.TargetCertificateStoreId);

                return true;
            }

            if (candidates.Count > 1)
            {
                Logger.LogError(
                    "Multiple suspended workflow candidates matched inventory completion. Count={0}. Refusing to schedule endpoint validation because correlation is ambiguous.",
                    candidates.Count);

                foreach (WorkflowInstance candidate in candidates)
                {
                    Logger.LogError(
                        "Ambiguous candidate: Id={0}, Title={1}, CertificateId={2}, InitialCertificateId={3}, RenewedCertId={4}, ReferenceId={5}, SuccessfulStoreIds={6}",
                        candidate.Id,
                        candidate.Title,
                        candidate.CertificateId,
                        candidate.InitialCertificateId,
                        candidate.RenewedCertId,
                        candidate.ReferenceId,
                        candidate.SuccessfulCertificateStoreIds);
                }

                return true;
            }

            WorkflowInstance selected = candidates[0];

            if (selected.RenewedCertId == null)
            {
                Logger.LogError("Selected workflow candidate did not include RenewedCertId. WorkflowInstanceId={0}. No custom validation job will be scheduled.", selected.Id);
                return true;
            }

            string? selectedStoreId = ResolveExpectedCertificateStoreId(selected);
            string? selectedValidationProfileName = ResolveValidationProfileName(selected);
            string selectedValidationProfileSource = ResolveValidationProfileNameSource(selected);

            Logger.LogInformation(
                "Selected workflow instance for endpoint validation custom job. WorkflowInstanceId={0}, RenewedCertId={1}, StoreId={2}, ValidationProfileName={3}, ValidationProfileSource={4}.",
                selected.Id,
                selected.RenewedCertId,
                selectedStoreId,
                selectedValidationProfileName,
                selectedValidationProfileSource);

            if (!_options.EnableCustomJobScheduling)
            {
                Logger.LogWarning("EnableCustomJobScheduling is false. This was a dry-run RFPEMInventory handler execution. No custom job was scheduled.");
                return true;
            }

            bool scheduled = await ScheduleEndpointValidationCustomJob(commandClient, context, selected);

            if (scheduled)
            {
                Logger.LogInformation("EndpointValidation custom job was scheduled successfully for WorkflowInstanceId={0}, ExpectedCertificateId={1}.", selected.Id, selected.RenewedCertId);
            }
            else
            {
                Logger.LogError("Failed to schedule EndpointValidation custom job for WorkflowInstanceId={0}, ExpectedCertificateId={1}.", selected.Id, selected.RenewedCertId);
            }

            return true;
        }

        private async Task<bool> HandleEndpointValidationCompletion(HttpClient commandClient, OrchestratorJobCompleteHandlerContext context)
        {
            Logger.LogInformation("Handling EndpointValidation custom job completion.");

            if (_options.EnableEvidenceCache)
            {
                ExpireStaleEvidenceCacheFiles();
            }

            CustomJobReturnData? customJobData = await ReadEndpointValidationCustomJobReturnData(commandClient, context);

            if (customJobData == null)
            {
                Logger.LogError("EndpointValidation custom job returned no usable custom data. Workflow gate will not be released.");
                return true;
            }

            string captureMode = ResolveCaptureMode(customJobData);

            Logger.LogInformation(
                "EndpointValidation custom job data. CaptureMode={0}, CorrelationId={1}, WorkflowInstanceId={2}, ExpectedCertificateId={3}, ValidationStatus={4}, MetadataUpdated={5}, ValidationCertificateId={6}, ValidationStoreId={7}, FailureMessage={8}",
                captureMode,
                customJobData.CorrelationId,
                customJobData.WorkflowInstanceId,
                customJobData.ExpectedCertificateId,
                customJobData.Validation == null ? null : customJobData.Validation.ValidationStatus,
                customJobData.Validation == null ? null : customJobData.Validation.MetadataUpdated,
                customJobData.Validation == null ? null : customJobData.Validation.CertificateId,
                customJobData.Validation == null ? null : customJobData.Validation.CertificateStoreId,
                customJobData.FailureMessage);

            if (IsPreRenewalCaptureMode(captureMode))
            {
                return await HandlePreRenewalCaptureCompletion(customJobData);
            }

            if (!IsCustomJobReturnDataAcceptable(customJobData))
            {
                Logger.LogError("EndpointValidation custom job data did not meet workflow release criteria. Workflow gate will not be released.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(customJobData.WorkflowInstanceId))
            {
                Logger.LogError("EndpointValidation custom job data did not include WorkflowInstanceId. Workflow gate will not be released.");
                return true;
            }

            WorkflowInstance? workflowInstance = await ReadWorkflowInstance(commandClient, customJobData.WorkflowInstanceId);

            if (workflowInstance == null)
            {
                Logger.LogError("Unable to read workflow instance from EndpointValidation custom job data. WorkflowInstanceId={0}.", customJobData.WorkflowInstanceId);
                return true;
            }

            if (!IsWorkflowStillWaitingForRelease(workflowInstance, customJobData))
            {
                Logger.LogError("Workflow instance is not in a releasable state. Workflow gate will not be released. WorkflowInstanceId={0}.", workflowInstance.Id);
                return true;
            }

            JsonElement? preRenewalEvidence = null;

            if (_options.EnableEvidenceCache)
            {
                preRenewalEvidence = ReadPreRenewalEvidenceFromCache(customJobData.WorkflowInstanceId, customJobData.CorrelationId);

                if (preRenewalEvidence == null)
                {
                    Logger.LogWarning("No pre-renewal evidence was found in the local evidence cache for WorkflowInstanceId={0}, CorrelationId={1}.", customJobData.WorkflowInstanceId, customJobData.CorrelationId);

                    if (_options.RequirePreRenewalEvidenceForPostValidation)
                    {
                        Logger.LogError("RequirePreRenewalEvidenceForPostValidation is true. Workflow gate will not be released.");
                        return true;
                    }
                }
                else
                {
                    Logger.LogInformation("Loaded pre-renewal evidence from local cache for WorkflowInstanceId={0}, CorrelationId={1}.", customJobData.WorkflowInstanceId, customJobData.CorrelationId);
                }
            }
            else
            {
                Logger.LogWarning("EnableEvidenceCache is false. Before/after certificate change metadata will not be added.");
            }

            if (_options.EnableAssuranceMetadataUpdate)
            {
                bool assuranceMetadataUpdated = await UpdateAssuranceMetadata(commandClient, customJobData, preRenewalEvidence);

                if (!assuranceMetadataUpdated)
                {
                    Logger.LogError("Assurance metadata update failed for CertificateId={0}.", customJobData.Validation == null ? null : customJobData.Validation.CertificateId);

                    if (_options.RequireAssuranceMetadataUpdate)
                    {
                        Logger.LogError("RequireAssuranceMetadataUpdate is true. Workflow gate will not be released.");
                        return true;
                    }
                }
            }
            else
            {
                Logger.LogWarning("EnableAssuranceMetadataUpdate is false. Assurance metadata will not be written before workflow release.");
            }

            if (!_options.EnableWorkflowRelease)
            {
                Logger.LogWarning("EnableWorkflowRelease is false. This was a dry-run EndpointValidation completion handler execution. No workflow signal was submitted and cache evidence was not archived.");
                return true;
            }

            bool released = await ReleaseWorkflowGate(commandClient, workflowInstance.Id);

            if (released)
            {
                Logger.LogInformation("Workflow gate released successfully. WorkflowInstanceId={0}, SignalKey={1}", workflowInstance.Id, _options.WorkflowSignalKey);

                if (preRenewalEvidence != null && _options.EnableEvidenceCache)
                {
                    ArchivePreRenewalEvidence(customJobData.WorkflowInstanceId, customJobData.CorrelationId, "ConsumedAfterPostRenewalValidation");
                }
            }
            else
            {
                Logger.LogError("Workflow gate release failed. WorkflowInstanceId={0}, SignalKey={1}", workflowInstance.Id, _options.WorkflowSignalKey);
            }

            return true;
        }

        private Task<bool> HandlePreRenewalCaptureCompletion(CustomJobReturnData customJobData)
        {
            Logger.LogInformation("Handling EndpointValidation PreRenewalCapture completion.");

            if (!_options.EnableEvidenceCache)
            {
                Logger.LogError("EnableEvidenceCache is false. Pre-renewal evidence cannot be persisted.");
                return Task.FromResult(true);
            }

            if (!IsPreRenewalCaptureReturnDataAcceptable(customJobData))
            {
                Logger.LogError("Pre-renewal capture data did not meet evidence-cache criteria. Evidence will not be saved.");
                return Task.FromResult(true);
            }

            bool saved = SavePreRenewalEvidenceToCache(customJobData);

            if (saved)
            {
                Logger.LogInformation("Pre-renewal evidence saved to local evidence cache. WorkflowInstanceId={0}, CorrelationId={1}.", customJobData.WorkflowInstanceId, customJobData.CorrelationId);
            }
            else
            {
                Logger.LogError("Failed to save pre-renewal evidence to local evidence cache. WorkflowInstanceId={0}, CorrelationId={1}.", customJobData.WorkflowInstanceId, customJobData.CorrelationId);
            }

            // A PreRenewalCapture job is an evidence-capture checkpoint only. It must not release
            // the post-renewal validation gate or update renewed certificate metadata.
            return Task.FromResult(true);
        }

        private bool IsPreRenewalCaptureReturnDataAcceptable(CustomJobReturnData data)
        {
            if (!string.Equals(data.CustomJobExtension, "Custom.EndpointValidation", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("CustomJobExtension did not match expected value for PreRenewalCapture. Actual={0}.", data.CustomJobExtension);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_options.WorkflowDefinitionId) && !string.Equals(data.WorkflowDefinitionId, _options.WorkflowDefinitionId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("WorkflowDefinitionId mismatch for PreRenewalCapture. Expected={0}, Actual={1}.", _options.WorkflowDefinitionId, data.WorkflowDefinitionId);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(data.FailureMessage))
            {
                Logger.LogError("PreRenewalCapture custom job reported FailureMessage={0}, ExceptionType={1}.", data.FailureMessage, data.ExceptionType);
                return false;
            }

            if (data.Validation == null)
            {
                Logger.LogError("PreRenewalCapture custom job data did not include Validation object.");
                return false;
            }

            if (_options.RequireValidationPass && !string.Equals(data.Validation.ValidationStatus, "PASS", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("PreRenewalCapture validation status was not PASS. Status={0}, FailureCategory={1}, DetailedError={2}.", data.Validation.ValidationStatus, data.Validation.FailureCategory, data.Validation.DetailedError);
                return false;
            }

            if (data.Validation.PreviousCertificateEvidence == null || data.Validation.PreviousCertificateEvidence.Value.ValueKind != JsonValueKind.Object)
            {
                Logger.LogError("PreRenewalCapture validation data did not include PreviousCertificateEvidence object.");
                return false;
            }

            JsonElement previous = data.Validation.PreviousCertificateEvidence.Value;

            string? serialNumber = GetJsonElementString(previous, "SerialNumber");
            string? sha1Thumbprint = GetJsonElementString(previous, "Sha1Thumbprint");
            string? sha256Thumbprint = GetJsonElementString(previous, "Sha256Thumbprint");
            string? capturedAtUtc = GetJsonElementString(previous, "CapturedAtUtc");

            if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(sha1Thumbprint) || string.IsNullOrWhiteSpace(sha256Thumbprint) || string.IsNullOrWhiteSpace(capturedAtUtc))
            {
                Logger.LogError("PreRenewalCapture evidence was missing required fields. Serial={0}, Sha1={1}, Sha256={2}, CapturedAtUtc={3}.", serialNumber, sha1Thumbprint, sha256Thumbprint, capturedAtUtc);
                return false;
            }

            if (string.IsNullOrWhiteSpace(data.WorkflowInstanceId) && string.IsNullOrWhiteSpace(data.CorrelationId))
            {
                Logger.LogError("PreRenewalCapture custom job data did not include WorkflowInstanceId or CorrelationId. Evidence cannot be keyed safely.");
                return false;
            }

            return true;
        }

        private static string ResolveCaptureMode(CustomJobReturnData data)
        {
            return FirstNonEmpty(
                data.CaptureMode,
                data.Validation == null ? null : data.Validation.CaptureMode,
                GetAssuranceString(data, "AutomationContext", "CaptureMode"),
                "PostRenewalValidation") ?? "PostRenewalValidation";
        }

        private static bool IsPreRenewalCaptureMode(string? captureMode)
        {
            return string.Equals(captureMode, "PreRenewalCapture", StringComparison.OrdinalIgnoreCase);
        }

        private string GetEvidenceCacheRoot()
        {
            return string.IsNullOrWhiteSpace(_options.EvidenceCacheRoot)
                ? @"C:\ProgramData\Keyfactor\ExtensionData\EndpointValidation\EvidenceCache"
                : _options.EvidenceCacheRoot!;
        }

        private string GetEvidenceCacheActivePath()
        {
            return Path.Combine(GetEvidenceCacheRoot(), "Active");
        }

        private string GetEvidenceCacheArchivePath()
        {
            return Path.Combine(GetEvidenceCacheRoot(), "Archive");
        }

        private string GetEvidenceCacheExpiredPath()
        {
            return Path.Combine(GetEvidenceCacheRoot(), "Expired");
        }

        private string GetEvidenceCacheLogPath()
        {
            return Path.Combine(GetEvidenceCacheRoot(), "Logs");
        }

        private void EnsureEvidenceCacheFolders()
        {
            Directory.CreateDirectory(GetEvidenceCacheRoot());
            Directory.CreateDirectory(GetEvidenceCacheActivePath());
            Directory.CreateDirectory(GetEvidenceCacheArchivePath());
            Directory.CreateDirectory(GetEvidenceCacheExpiredPath());
            Directory.CreateDirectory(GetEvidenceCacheLogPath());
        }

        private string GetEvidenceCacheKey(string? workflowInstanceId, string? correlationId)
        {
            string key = FirstNonEmpty(workflowInstanceId, correlationId, string.Empty) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new Exception("WorkflowInstanceId or CorrelationId is required to build the evidence cache key.");
            }

            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                key = key.Replace(invalidCharacter, '_');
            }

            key = key.Replace('\\', '_').Replace('/', '_').Replace(':', '_').Replace('*', '_').Replace('?', '_').Replace('"', '_').Replace('<', '_').Replace('>', '_').Replace('|', '_');

            return key.Trim();
        }

        private string GetPreRenewalEvidenceCachePath(string? workflowInstanceId, string? correlationId)
        {
            string cacheKey = GetEvidenceCacheKey(workflowInstanceId, correlationId);
            return Path.Combine(GetEvidenceCacheActivePath(), cacheKey + ".pre-renewal.json");
        }

        private bool SavePreRenewalEvidenceToCache(CustomJobReturnData data)
        {
            try
            {
                EnsureEvidenceCacheFolders();

                JsonElement previous = data.Validation!.PreviousCertificateEvidence!.Value;

                Dictionary<string, object?> record = new Dictionary<string, object?>
                {
                    ["EvidenceModelVersion"] = "v10",
                    ["EvidenceType"] = "PreRenewalCertificateEvidence",
                    ["WorkflowInstanceId"] = data.WorkflowInstanceId,
                    ["WorkflowDefinitionId"] = data.WorkflowDefinitionId,
                    ["CorrelationId"] = data.CorrelationId,
                    ["ValidationProfileName"] = data.ValidationProfileName,
                    ["CaptureMode"] = "PreRenewalCapture",
                    ["CapturedAtUtc"] = FirstNonEmpty(GetJsonElementString(previous, "CapturedAtUtc"), data.Validation.ValidatedAtUtc, data.CompletedAtUtc),
                    ["Endpoint"] = new Dictionary<string, object?>
                    {
                        ["Url"] = GetJsonElementString(previous, "Url"),
                        ["Host"] = GetJsonElementString(previous, "TargetHost"),
                        ["Port"] = GetJsonElementString(previous, "Port"),
                        ["Path"] = GetJsonElementString(previous, "Path"),
                        ["SniHost"] = GetJsonElementString(previous, "SniHost"),
                        ["TestedFrom"] = GetJsonElementString(previous, "TestedFrom")
                    },
                    ["PreviousCertificate"] = new Dictionary<string, object?>
                    {
                        ["SerialNumber"] = GetJsonElementString(previous, "SerialNumber"),
                        ["Sha1Thumbprint"] = GetJsonElementString(previous, "Sha1Thumbprint"),
                        ["Sha256Thumbprint"] = GetJsonElementString(previous, "Sha256Thumbprint"),
                        ["Subject"] = GetJsonElementString(previous, "Subject"),
                        ["Issuer"] = GetJsonElementString(previous, "Issuer"),
                        ["CommonName"] = GetJsonElementString(previous, "CommonName"),
                        ["San"] = GetJsonElementString(previous, "San"),
                        ["NotBeforeUtc"] = GetJsonElementString(previous, "NotBeforeUtc"),
                        ["NotAfterUtc"] = GetJsonElementString(previous, "NotAfterUtc"),
                        ["CapturedAtUtc"] = GetJsonElementString(previous, "CapturedAtUtc")
                    },
                    ["ValidationChecks"] = new Dictionary<string, object?>
                    {
                        ["TlsHandshake"] = GetJsonElementString(previous, "TlsHandshake"),
                        ["HostnameSanMatch"] = GetJsonElementString(previous, "HostnameSanMatch"),
                        ["CertificateNotExpired"] = GetJsonElementString(previous, "CertificateNotExpired"),
                        ["CertificateChainValidation"] = GetJsonElementString(previous, "CertificateChainValidation"),
                        ["HttpHealth"] = GetJsonElementString(previous, "HttpHealth"),
                        ["HttpResponse"] = GetJsonElementString(previous, "HttpResponse")
                    }
                };

                string path = GetPreRenewalEvidenceCachePath(data.WorkflowInstanceId, data.CorrelationId);
                string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(path, json, Encoding.UTF8);

                WriteEvidenceCacheLog("Saved pre-renewal evidence to cache.", "INFO", data.WorkflowInstanceId, data.CorrelationId);
                Logger.LogInformation("Saved pre-renewal evidence cache file. Path={0}", path);

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to save pre-renewal evidence to cache. Error={0}", ex.ToString());
                return false;
            }
        }

        private JsonElement? ReadPreRenewalEvidenceFromCache(string? workflowInstanceId, string? correlationId)
        {
            try
            {
                EnsureEvidenceCacheFolders();

                string path = GetPreRenewalEvidenceCachePath(workflowInstanceId, correlationId);

                if (!File.Exists(path))
                {
                    return null;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement clone = document.RootElement.Clone();

                WriteEvidenceCacheLog("Read pre-renewal evidence from cache.", "INFO", workflowInstanceId, correlationId);
                Logger.LogInformation("Read pre-renewal evidence cache file. Path={0}", path);

                return clone;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to read pre-renewal evidence from cache. WorkflowInstanceId={0}, CorrelationId={1}, Error={2}", workflowInstanceId, correlationId, ex.ToString());
                return null;
            }
        }

        private bool ArchivePreRenewalEvidence(string? workflowInstanceId, string? correlationId, string reason)
        {
            try
            {
                EnsureEvidenceCacheFolders();

                string sourcePath = GetPreRenewalEvidenceCachePath(workflowInstanceId, correlationId);

                if (!File.Exists(sourcePath))
                {
                    Logger.LogWarning("No pre-renewal evidence cache file existed to archive. Path={0}", sourcePath);
                    return false;
                }

                string archiveDatePath = Path.Combine(GetEvidenceCacheArchivePath(), DateTime.Now.ToString("yyyyMMdd"));
                Directory.CreateDirectory(archiveDatePath);

                string baseName = Path.GetFileNameWithoutExtension(sourcePath);
                string destinationPath = Path.Combine(archiveDatePath, baseName + "." + SanitizeArchiveReason(reason) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");

                File.Move(sourcePath, destinationPath, true);

                WriteEvidenceCacheLog("Archived pre-renewal evidence. Destination=" + destinationPath, "INFO", workflowInstanceId, correlationId);
                Logger.LogInformation("Archived pre-renewal evidence cache file. Source={0}, Destination={1}", sourcePath, destinationPath);

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to archive pre-renewal evidence cache file. WorkflowInstanceId={0}, CorrelationId={1}, Error={2}", workflowInstanceId, correlationId, ex.ToString());
                return false;
            }
        }

        private void ExpireStaleEvidenceCacheFiles()
        {
            try
            {
                EnsureEvidenceCacheFolders();

                int retentionDays = _options.EvidenceCacheRetentionDays < 1 ? 14 : _options.EvidenceCacheRetentionDays;
                DateTime cutoff = DateTime.Now.AddDays(-1 * retentionDays);
                string expiredDatePath = Path.Combine(GetEvidenceCacheExpiredPath(), DateTime.Now.ToString("yyyyMMdd"));
                Directory.CreateDirectory(expiredDatePath);

                foreach (FileInfo file in new DirectoryInfo(GetEvidenceCacheActivePath()).GetFiles("*.pre-renewal.json"))
                {
                    if (file.LastWriteTime >= cutoff)
                    {
                        continue;
                    }

                    string destinationPath = Path.Combine(expiredDatePath, Path.GetFileNameWithoutExtension(file.Name) + ".Expired." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Move(file.FullName, destinationPath, true);

                    WriteEvidenceCacheLog("Expired stale pre-renewal evidence. Source=" + file.FullName + " Destination=" + destinationPath, "INFO", null, null);
                    Logger.LogInformation("Expired stale pre-renewal evidence cache file. Source={0}, Destination={1}", file.FullName, destinationPath);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to expire stale evidence cache files. Error={0}", ex.ToString());
            }
        }

        private void WriteEvidenceCacheLog(string message, string level, string? workflowInstanceId, string? correlationId)
        {
            try
            {
                EnsureEvidenceCacheFolders();

                Dictionary<string, object?> record = new Dictionary<string, object?>
                {
                    ["TimestampUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["Level"] = level,
                    ["Message"] = message,
                    ["WorkflowInstanceId"] = workflowInstanceId,
                    ["CorrelationId"] = correlationId,
                    ["MachineName"] = Environment.MachineName
                };

                string line = JsonSerializer.Serialize(record);
                string logFile = Path.Combine(GetEvidenceCacheLogPath(), "EndpointValidationEvidenceCache.log");

                File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Do not fail workflow processing because the auxiliary cache log could not be written.
            }
        }

        private static string SanitizeArchiveReason(string? reason)
        {
            string value = string.IsNullOrWhiteSpace(reason) ? "Archived" : reason.Trim();

            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidCharacter, '_');
            }

            return value;
        }

        private bool IsTargetMachine(OrchestratorJobCompleteHandlerContext context)
        {
            if (!string.IsNullOrWhiteSpace(_options.TargetClientMachine))
            {
                string expected = NormalizeMachineName(_options.TargetClientMachine);
                string actual = NormalizeMachineName(context.ClientMachine);

                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("ClientMachine filter did not match. Expected={0}, Actual={1}", _options.TargetClientMachine, context.ClientMachine);
                    return false;
                }
            }

            return true;
        }

        private JobPhase GetJobPhase(OrchestratorJobCompleteHandlerContext context)
        {
            string jobTypeId = context.JobTypeId.ToString();
            string jobType = context.JobType ?? string.Empty;

            if (StringMatches(jobTypeId, _options.RfpemInventoryJobTypeId) || StringMatches(jobType, "RFPEMInventory") || StringMatches(jobType, "RFPEM Inventory"))
            {
                return JobPhase.RfpemInventory;
            }

            if (StringMatches(jobTypeId, _options.EndpointValidationJobTypeId) || StringMatches(jobType, _options.EndpointValidationJobTypeName))
            {
                return JobPhase.EndpointValidation;
            }

            return JobPhase.NotTarget;
        }

        private static bool StringMatches(string? actual, string? expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string NormalizeMachineName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();

            int dotIndex = trimmed.IndexOf('.');
            if (dotIndex > 0)
            {
                return trimmed.Substring(0, dotIndex);
            }

            return trimmed;
        }

        private static bool IsSuccessfulJobResult(object? jobResult)
        {
            if (jobResult == null)
            {
                return false;
            }

            string result = jobResult.ToString() ?? string.Empty;

            return string.Equals(result, "Success", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(result, "Succeeded", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(result, "Successful", StringComparison.OrdinalIgnoreCase);
        }

        private static HttpClient NewCommandApiClient(OrchestratorJobCompleteHandlerContext context)
        {
            HttpClient client = new HttpClient(
                new HttpClientHandler()
                {
                    UseDefaultCredentials = true,
                    PreAuthenticate = true
                });

            client.BaseAddress = context.Client.BaseAddress;

            client.DefaultRequestHeaders.Add("x-keyfactor-requested-with", "APIClient");
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            return client;
        }

        private async Task<bool> ScheduleEndpointValidationCustomJob(HttpClient commandClient, OrchestratorJobCompleteHandlerContext context, WorkflowInstance workflowInstance)
        {
            string correlationId = Guid.NewGuid().ToString();
            string? validationProfileName = ResolveValidationProfileName(workflowInstance);
            string validationProfileSource = ResolveValidationProfileNameSource(workflowInstance);
            string? expectedCertificateStoreId = ResolveExpectedCertificateStoreId(workflowInstance);

            if (string.IsNullOrWhiteSpace(validationProfileName))
            {
                Logger.LogError("Cannot schedule EndpointValidation custom job because no validation profile name was available. Configure EndpointValidationProfileMappings in the handler manifest, set workflow variable EndpointValidationProfileName, or configure handler option ValidationProfileName as a fallback.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedCertificateStoreId))
            {
                Logger.LogError("Cannot schedule EndpointValidation custom job because no certificate store ID could be derived. Set workflow variable EndpointValidationExpectedCertificateStoreId, configure TargetCertificateStoreId, or ensure Successful Cert Store Ids contains exactly one store ID.");
                return false;
            }

            Logger.LogInformation("Resolved EndpointValidation profile for custom job. WorkflowInstanceId={0}, CertificateStoreId={1}, ValidationProfileName={2}, ValidationProfileSource={3}.", workflowInstance.Id, expectedCertificateStoreId, validationProfileName, validationProfileSource);

            var payload = new
            {
                AgentId = context.AgentId.ToString(),
                JobTypeName = _options.EndpointValidationJobTypeName,
                Schedule = new
                {
                    Immediate = true
                },
                JobFields = BuildEndpointValidationJobFields(validationProfileName, workflowInstance, expectedCertificateStoreId, correlationId)
            };

            string json = JsonSerializer.Serialize(payload);
            string path = "OrchestratorJobs/Custom";

            Logger.LogInformation("Scheduling EndpointValidation custom job. Path={0}, Payload={1}", path, json);

            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await commandClient.PostAsync(path, content);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("EndpointValidation custom job scheduling failed. Status={0}. Response={1}", response.StatusCode, responseText);
                return false;
            }

            Logger.LogInformation("EndpointValidation custom job scheduling response. Status={0}. Response={1}", response.StatusCode, responseText);
            return true;
        }

        private Dictionary<string, object> BuildEndpointValidationJobFields(string validationProfileName, WorkflowInstance workflowInstance, string expectedCertificateStoreId, string correlationId)
        {
            Dictionary<string, object> fields = new Dictionary<string, object>
            {
                { "ValidationProfileName", validationProfileName },
                { "WorkflowInstanceId", workflowInstance.Id },
                { "WorkflowDefinitionId", workflowInstance.DefinitionId ?? string.Empty },
                { "GateStepUniqueName", _options.GateStepUniqueName ?? string.Empty },

                // Temporary backward compatibility. The v7 custom job extension prefers
                // GateStepUniqueName but can still read WaitStepUniqueName while older
                // Command job type definitions are being updated.
                { "WaitStepUniqueName", _options.WaitStepUniqueName ?? string.Empty },

                { "CertificateStoreId", expectedCertificateStoreId },
                { "ExpectedCertificateId", workflowInstance.RenewedCertId == null ? 0 : workflowInstance.RenewedCertId.Value },
                { "CorrelationId", correlationId },
                { "TimeoutSeconds", _options.CustomJobTimeoutSeconds }
            };

            if (!string.IsNullOrWhiteSpace(_options.ValidationScriptPath))
            {
                fields["ValidationScriptPath"] = _options.ValidationScriptPath.Trim();
            }

            return fields;
        }

        private async Task<CustomJobReturnData?> ReadEndpointValidationCustomJobReturnData(HttpClient commandClient, OrchestratorJobCompleteHandlerContext context)
        {
            JobHistoryRecord? history = await FindLatestJobHistoryForJobId(commandClient, context.JobId.ToString());

            if (history == null || history.JobHistoryId == null)
            {
                Logger.LogError("Could not find JobHistoryId for EndpointValidation job. JobId={0}.", context.JobId);
                return null;
            }

            Logger.LogInformation("Using EndpointValidation JobHistoryId={0} for JobId={1}.", history.JobHistoryId, context.JobId);

            string path = "OrchestratorJobs/JobStatus/Data?jobHistoryId=" + Uri.EscapeDataString(history.JobHistoryId.Value.ToString());

            HttpResponseMessage response = await commandClient.GetAsync(path);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("EndpointValidation JobStatus/Data query failed. Path={0}, Status={1}, Response={2}", path, response.StatusCode, responseText);
                return null;
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                Logger.LogError("EndpointValidation JobStatus/Data returned empty response. Path={0}.", path);
                return null;
            }

            Logger.LogInformation("EndpointValidation JobStatus/Data response length={0}.", responseText.Length);

            CustomJobDataEnvelope? envelope;

            try
            {
                envelope = JsonSerializer.Deserialize<CustomJobDataEnvelope>(
                    responseText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to parse JobStatus/Data response envelope. Error={0}, Response={1}", ex.Message, responseText);
                return null;
            }

            if (envelope == null || string.IsNullOrWhiteSpace(envelope.Data))
            {
                Logger.LogError("JobStatus/Data response did not include Data payload. Response={0}", responseText);
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<CustomJobReturnData>(
                    envelope.Data,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to parse EndpointValidation custom job Data JSON. Error={0}, Data={1}", ex.Message, envelope.Data);
                return null;
            }
        }

        private async Task<JobHistoryRecord?> FindLatestJobHistoryForJobId(HttpClient commandClient, string jobId)
        {
            List<JobHistoryRecord> history = await QueryJobHistory(commandClient);

            List<JobHistoryRecord> matches = history
                .Where(item => string.Equals(item.JobId, jobId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.JobHistoryId ?? 0)
                .ToList();

            Logger.LogInformation("Job history entries matching JobId={0}: {1}", jobId, matches.Count);

            return matches.FirstOrDefault();
        }

        private async Task<List<JobHistoryRecord>> QueryJobHistory(HttpClient commandClient)
        {
            string path = "OrchestratorJobs/JobHistory?PageReturned=1&ReturnLimit=1000";
            Logger.LogInformation("Querying orchestrator job history. Path={0}", path);

            HttpResponseMessage response = await commandClient.GetAsync(path);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Orchestrator job history query failed. Status={0}. Response={1}", response.StatusCode, responseText);
                return new List<JobHistoryRecord>();
            }

            return ParseJobHistoryRecords(responseText);
        }

        private bool IsCustomJobReturnDataAcceptable(CustomJobReturnData data)
        {
            if (!string.Equals(data.CustomJobExtension, "Custom.EndpointValidation", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("CustomJobExtension did not match expected value. Actual={0}.", data.CustomJobExtension);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_options.WorkflowDefinitionId) && !string.Equals(data.WorkflowDefinitionId, _options.WorkflowDefinitionId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("WorkflowDefinitionId mismatch. Expected={0}, Actual={1}.", _options.WorkflowDefinitionId, data.WorkflowDefinitionId);
                return false;
            }

            string? returnedGateStepUniqueName = FirstNonEmpty(data.GateStepUniqueName, data.WaitStepUniqueName, string.Empty);

            if (!string.Equals(returnedGateStepUniqueName, _options.GateStepUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("GateStepUniqueName mismatch. Expected={0}, ActualGateStepUniqueName={1}, ActualWaitStepUniqueName={2}.", _options.GateStepUniqueName, data.GateStepUniqueName, data.WaitStepUniqueName);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_options.TargetCertificateStoreId) && !string.Equals(data.ExpectedCertificateStoreId, _options.TargetCertificateStoreId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("ExpectedCertificateStoreId mismatch. Expected={0}, Actual={1}.", _options.TargetCertificateStoreId, data.ExpectedCertificateStoreId);
                return false;
            }

            if (string.IsNullOrWhiteSpace(data.ExpectedCertificateStoreId))
            {
                Logger.LogError("EndpointValidation custom job data did not include ExpectedCertificateStoreId.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(data.FailureMessage))
            {
                Logger.LogError("EndpointValidation custom job reported FailureMessage={0}, ExceptionType={1}.", data.FailureMessage, data.ExceptionType);
                return false;
            }

            if (data.Validation == null)
            {
                Logger.LogError("EndpointValidation custom job data did not include Validation object.");
                return false;
            }

            if (_options.RequireValidationPass)
            {
                if (!string.Equals(data.Validation.ValidationStatus, "PASS", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError("Endpoint validation status was not PASS. Status={0}, FailureCategory={1}, DetailedError={2}.", data.Validation.ValidationStatus, data.Validation.FailureCategory, data.Validation.DetailedError);
                    return false;
                }
            }

            if (_options.RequireMetadataUpdated)
            {
                if (data.Validation.MetadataUpdated != true)
                {
                    Logger.LogError("Endpoint validation did not report MetadataUpdated=true. MetadataUpdated={0}, HttpStatus={1}.", data.Validation.MetadataUpdated, data.Validation.MetadataUpdateHttpStatusCode);
                    return false;
                }
            }

            if (!string.Equals(data.Validation.CertificateStoreId, data.ExpectedCertificateStoreId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Validation CertificateStoreId mismatch. Expected={0}, Actual={1}.", data.ExpectedCertificateStoreId, data.Validation.CertificateStoreId);
                return false;
            }

            if (_options.RequireValidationCertificateId)
            {
                int? validationCertificateId = GetValidationCertificateId(data.Validation);

                if (validationCertificateId == null)
                {
                    Logger.LogError("Endpoint validation response did not include a usable numeric CertificateId.");
                    return false;
                }

                if (validationCertificateId.Value != data.ExpectedCertificateId)
                {
                    Logger.LogError("Validation CertificateId mismatch. Expected={0}, Actual={1}.", data.ExpectedCertificateId, validationCertificateId.Value);
                    return false;
                }
            }

            return true;
        }


        private async Task<bool> UpdateAssuranceMetadata(HttpClient commandClient, CustomJobReturnData data, JsonElement? preRenewalEvidence = null)
        {
            if (data.Validation == null)
            {
                Logger.LogError("Cannot update assurance metadata because Validation object was missing.");
                return false;
            }

            int? certificateId = GetValidationCertificateId(data.Validation);

            if (certificateId == null)
            {
                Logger.LogError("Cannot update assurance metadata because validation CertificateId was missing or non-numeric. CertificateId={0}", data.Validation.CertificateId);
                return false;
            }

            Dictionary<string, object> metadata = BuildAssuranceMetadata(data, preRenewalEvidence);

            if (metadata.Count == 0)
            {
                Logger.LogError("Cannot update assurance metadata because metadata payload was empty.");
                return false;
            }

            var payload = new
            {
                Id = certificateId.Value,
                Metadata = metadata
            };

            string json = JsonSerializer.Serialize(payload);
            string path = "Certificates/Metadata";

            Logger.LogInformation("Updating endpoint validation assurance metadata. Path={0}, CertificateId={1}, FieldCount={2}", path, certificateId.Value, metadata.Count);

            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await commandClient.PutAsync(path, content);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Assurance metadata update failed. Status={0}. Response={1}", response.StatusCode, responseText);
                return false;
            }

            Logger.LogInformation("Assurance metadata update succeeded. CertificateId={0}, FieldCount={1}, Status={2}.", certificateId.Value, metadata.Count, response.StatusCode);
            return true;
        }

        private Dictionary<string, object> BuildAssuranceMetadata(CustomJobReturnData data, JsonElement? preRenewalEvidence = null)
        {
            Dictionary<string, object> metadata = new Dictionary<string, object>();
            EndpointValidationResponse? validation = data.Validation;

            AddMetadata(metadata, "KFValidation_AssuranceModelVersion", GetAssuranceString(data, "EvidenceModelVersion"));
            AddMetadata(metadata, "KFValidation_OutcomeHeadline", "Application TLS Certificate Renewal Verified - No Action Required");
            AddMetadata(metadata, "KFValidation_OutcomeSummary", "The renewed TLS certificate is deployed and being served by the application endpoint. The live served certificate was captured, validated, and matched to Keyfactor Command inventory.");
            AddMetadata(metadata, "KFValidation_OutcomeStatus", GetAssuranceString(data, "Outcome", "Status"));
            AddMetadata(metadata, "KFValidation_ActionRequired", GetAssuranceString(data, "Outcome", "ActionRequired"));
            AddMetadata(metadata, "KFValidation_RecommendedAction", FirstNonEmpty(GetAssuranceString(data, "Outcome", "RecommendedAction"), GetAssuranceString(data, "RecommendedAction")));

            AddMetadata(metadata, "KFValidation_AppUrlTested", FirstNonEmpty(GetAssuranceString(data, "ApplicationHealth", "ApplicationUrlTested"), validation == null ? null : validation.WebsiteUrl));
            AddMetadata(metadata, "KFValidation_HttpResponse", GetAssuranceString(data, "ApplicationHealth", "HttpResponse"));
            AddMetadataInteger(metadata, "KFValidation_HttpStatusCode", GetAssuranceString(data, "ApplicationHealth", "HttpStatusCode"));
            AddMetadata(metadata, "KFValidation_TlsHandshake", GetAssuranceString(data, "ApplicationHealth", "TlsHandshake"));
            AddMetadata(metadata, "KFValidation_HostnameSanMatch", GetAssuranceString(data, "ApplicationHealth", "HostnameSanMatch"));
            AddMetadata(metadata, "KFValidation_CertChainValidation", GetAssuranceString(data, "ApplicationHealth", "CertificateChainValidation"));
            AddMetadataInteger(metadata, "KFValidation_ResponseTimeMs", GetAssuranceString(data, "ApplicationHealth", "ResponseTimeMs"));
            AddMetadata(metadata, "KFValidation_TestedFrom", GetAssuranceString(data, "ApplicationHealth", "TestedFrom"));
            AddMetadata(metadata, "KFValidation_CommandInventoryMatch", GetAssuranceString(data, "RenewalVerification", "CommandInventoryMatch"));

            AddMetadata(metadata, "KFValidation_CheckEndpointReachable", GetChecklistEvidenceResult(data, "Application endpoint reachable"));
            AddMetadata(metadata, "KFValidation_CheckTlsHandshake", GetChecklistEvidenceResult(data, "TLS handshake completed"));
            AddMetadata(metadata, "KFValidation_CheckServedCertCaptured", GetChecklistEvidenceResult(data, "Served certificate captured from live endpoint"));
            AddMetadata(metadata, "KFValidation_CheckInventoryMatch", GetChecklistEvidenceResult(data, "Served certificate matches Keyfactor inventory"));
            AddMetadata(metadata, "KFValidation_CheckHostnameSanMatch", GetChecklistEvidenceResult(data, "CN/SAN matches application hostname"));
            AddMetadata(metadata, "KFValidation_CheckIssuerCaptured", GetChecklistEvidenceResult(data, "Issuer captured"));
            AddMetadata(metadata, "KFValidation_CheckChainTrusted", GetChecklistEvidenceResult(data, "Certificate chain is trusted"));
            AddMetadata(metadata, "KFValidation_CheckHttpHealth", GetChecklistEvidenceResult(data, "Application HTTP health check"));

            AddMetadata(metadata, "KFValidation_EndpointTested", GetAssuranceString(data, "ValidationScope", "EndpointTested"));
            AddMetadata(metadata, "KFValidation_SniUsed", GetAssuranceString(data, "ValidationScope", "SniUsed"));
            AddMetadata(metadata, "KFValidation_PathTested", GetAssuranceString(data, "ValidationScope", "PathTested"));
            AddMetadata(metadata, "KFValidation_ClientPerspective", GetAssuranceString(data, "ValidationScope", "ClientPerspective"));
            AddMetadata(metadata, "KFValidation_ScopeProtocol", GetAssuranceString(data, "ValidationScope", "Protocol"));
            AddMetadata(metadata, "KFValidation_ScopePort", GetAssuranceString(data, "ValidationScope", "Port"));

            AddMetadata(metadata, "KFValidation_WorkflowInstanceId", FirstNonEmpty(GetAssuranceString(data, "AutomationContext", "WorkflowInstanceId"), data.WorkflowInstanceId));
            AddMetadata(metadata, "KFValidation_WorkflowDefinitionId", FirstNonEmpty(GetAssuranceString(data, "AutomationContext", "WorkflowDefinitionId"), data.WorkflowDefinitionId));
            AddMetadata(metadata, "KFValidation_ValidationJobCorrelationId", FirstNonEmpty(GetAssuranceString(data, "AutomationContext", "ValidationJobCorrelationId"), data.CorrelationId));
            AddMetadata(metadata, "KFValidation_ValidationProfile", FirstNonEmpty(GetAssuranceString(data, "AutomationContext", "ValidationProfile"), data.ValidationProfileName));
            AddMetadata(metadata, "KFValidation_CustomJobExtension", FirstNonEmpty(GetAssuranceString(data, "AutomationContext", "CustomJobExtension"), data.CustomJobExtension));
            AddMetadata(metadata, "KFValidation_GateStepUniqueName", FirstNonEmpty(GetAssuranceString(data, "AutomationContext", "GateStepUniqueName"), data.GateStepUniqueName));
            AddMetadataInteger(metadata, "KFValidation_ExpectedCertificateId", GetAssuranceString(data, "AutomationContext", "ExpectedCertificateId"));
            AddMetadata(metadata, "KFValidation_ExpectedStoreId", GetAssuranceString(data, "AutomationContext", "ExpectedCertificateStoreId"));
            AddMetadata(metadata, "KFValidation_TestedFromOrchestrator", GetAssuranceString(data, "AutomationContext", "TestedFromOrchestrator"));

            AddMetadata(metadata, "KFValidation_EvidenceGeneratedAtUtc", GetAssuranceString(data, "GeneratedAtUtc"));
            AddMetadata(metadata, "KFValidation_CustomJobCompletedAtUtc", data.CompletedAtUtc);

            AddBeforeAfterCertificateMetadata(metadata, data, preRenewalEvidence);

            return metadata;
        }

        private void AddBeforeAfterCertificateMetadata(Dictionary<string, object> metadata, CustomJobReturnData data, JsonElement? preRenewalEvidence)
        {
            EndpointValidationResponse? validation = data.Validation;

            if (validation == null)
            {
                return;
            }

            AddMetadata(metadata, "KFValidation_RenewedCertificateSerialNumber", validation.SerialNumber);
            AddMetadata(metadata, "KFValidation_RenewedCertificateSha1Thumbprint", validation.Sha1Thumbprint);
            AddMetadata(metadata, "KFValidation_RenewedCertificateSha256Thumbprint", validation.Sha256Thumbprint);
            AddMetadata(metadata, "KFValidation_RenewedCertificateValidatedAtUtc", validation.ValidatedAtUtc);

            if (preRenewalEvidence == null || preRenewalEvidence.Value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            JsonElement evidence = preRenewalEvidence.Value;

            string? previousSerialNumber = GetJsonElementString(evidence, "PreviousCertificate", "SerialNumber");
            string? previousSha1Thumbprint = GetJsonElementString(evidence, "PreviousCertificate", "Sha1Thumbprint");
            string? previousSha256Thumbprint = GetJsonElementString(evidence, "PreviousCertificate", "Sha256Thumbprint");
            string? previousCapturedAtUtc = FirstNonEmpty(
                GetJsonElementString(evidence, "PreviousCertificate", "CapturedAtUtc"),
                GetJsonElementString(evidence, "CapturedAtUtc"));

            AddMetadata(metadata, "KFValidation_PreviousCertificateSerialNumber", previousSerialNumber);
            AddMetadata(metadata, "KFValidation_PreviousCertificateSha1Thumbprint", previousSha1Thumbprint);
            AddMetadata(metadata, "KFValidation_PreviousCertificateSha256Thumbprint", previousSha256Thumbprint);
            AddMetadata(metadata, "KFValidation_PreviousCertificateCapturedAtUtc", previousCapturedAtUtc);
            AddMetadata(metadata, "KFValidation_PreviousCertificateSubject", GetJsonElementString(evidence, "PreviousCertificate", "Subject"));
            AddMetadata(metadata, "KFValidation_PreviousCertificateIssuer", GetJsonElementString(evidence, "PreviousCertificate", "Issuer"));
            AddMetadata(metadata, "KFValidation_PreRenewalEvidenceCacheStatus", "Consumed");

            bool hasComparisonInputs =
                !string.IsNullOrWhiteSpace(previousSerialNumber) &&
                !string.IsNullOrWhiteSpace(previousSha1Thumbprint) &&
                !string.IsNullOrWhiteSpace(previousSha256Thumbprint) &&
                !string.IsNullOrWhiteSpace(validation.SerialNumber) &&
                !string.IsNullOrWhiteSpace(validation.Sha1Thumbprint) &&
                !string.IsNullOrWhiteSpace(validation.Sha256Thumbprint);

            if (!hasComparisonInputs)
            {
                AddMetadata(metadata, "KFValidation_CertificateChanged", "NotValidated");
                AddMetadata(metadata, "KFValidation_CertificateChangeSummary", "Before/after certificate change comparison could not be completed because one or more certificate identifiers were missing.");
                return;
            }

            bool serialChanged = !string.Equals(previousSerialNumber, validation.SerialNumber, StringComparison.OrdinalIgnoreCase);
            bool sha1Changed = !string.Equals(previousSha1Thumbprint, validation.Sha1Thumbprint, StringComparison.OrdinalIgnoreCase);
            bool sha256Changed = !string.Equals(previousSha256Thumbprint, validation.Sha256Thumbprint, StringComparison.OrdinalIgnoreCase);
            bool certificateChanged = serialChanged && sha1Changed && sha256Changed;

            AddMetadata(metadata, "KFValidation_CertificateChanged", certificateChanged ? "PASS" : "FAIL");
            AddMetadata(metadata, "KFValidation_CertificateChangeSummary", certificateChanged
                ? "Previous and renewed certificate identifiers differ by serial number, SHA1 thumbprint, and SHA256 thumbprint."
                : "Previous and renewed certificate identifiers did not all change. Review renewal evidence before accepting this as a successful certificate change.");
        }

        private static string? GetJsonElementString(JsonElement element, params string[] path)
        {
            JsonElement current = element;

            foreach (string segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out JsonElement next))
                {
                    return null;
                }

                current = next;
            }

            return JsonElementToString(current);
        }

        private static void AddMetadata(Dictionary<string, object> metadata, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            metadata[name] = value.Trim();
        }

        private static void AddMetadataInteger(Dictionary<string, object> metadata, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (int.TryParse(value.Trim(), out int parsed))
            {
                metadata[name] = parsed;
                return;
            }

            // Invalid integer metadata values are skipped.
        }

        private static string? GetAssuranceString(CustomJobReturnData data, params string[] path)
        {
            if (data.AssuranceEvidence == null || data.AssuranceEvidence.Value.ValueKind == JsonValueKind.Undefined || data.AssuranceEvidence.Value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            JsonElement current = data.AssuranceEvidence.Value;

            foreach (string segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out JsonElement next))
                {
                    return null;
                }

                current = next;
            }

            return JsonElementToString(current);
        }

        private static string? GetChecklistResult(CustomJobReturnData data, string validationName)
        {
            if (data.AssuranceEvidence == null || data.AssuranceEvidence.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!data.AssuranceEvidence.Value.TryGetProperty("ValidationChecklist", out JsonElement checklist) || checklist.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement item in checklist.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? name = null;
                string? result = null;

                if (item.TryGetProperty("Validation", out JsonElement validationElement))
                {
                    name = JsonElementToString(validationElement);
                }

                if (item.TryGetProperty("Result", out JsonElement resultElement))
                {
                    result = JsonElementToString(resultElement);
                }

                if (string.Equals(name, validationName, StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }

            return null;
        }

        private static string? GetChecklistEvidenceResult(CustomJobReturnData data, string validationName)
        {
            string? result = GetChecklistResult(data, validationName);

            if (IsEvidenceGapValue(result))
            {
                return null;
            }

            return result;
        }

        private static bool IsEvidenceGapValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            string normalized = value.Trim();

            return
                string.Equals(normalized, "NotPerformed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "NotCaptured", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "NotApplicable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "NotValidated", StringComparison.OrdinalIgnoreCase);
        }

        private static string? JsonElementToString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.ToString();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return element.GetRawText();
            }
        }

        private bool IsWorkflowStillWaitingForRelease(WorkflowInstance workflowInstance, CustomJobReturnData data)
        {
            if (!string.Equals(workflowInstance.Status, "Suspended", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Workflow is not suspended. WorkflowInstanceId={0}, Status={1}.", workflowInstance.Id, workflowInstance.Status);
                return false;
            }

            string expectedWorkflowDefinitionId = FirstNonEmpty(data.WorkflowDefinitionId, _options.WorkflowDefinitionId, string.Empty);

            if (!string.IsNullOrWhiteSpace(expectedWorkflowDefinitionId) && !string.Equals(workflowInstance.DefinitionId, expectedWorkflowDefinitionId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Workflow definition mismatch. WorkflowInstanceId={0}, Expected={1}, Actual={2}.", workflowInstance.Id, expectedWorkflowDefinitionId, workflowInstance.DefinitionId);
                return false;
            }

            if (!string.Equals(workflowInstance.DefinitionWorkflowType, "Expiration", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Workflow type mismatch. WorkflowInstanceId={0}, Expected=Expiration, Actual={1}.", workflowInstance.Id, workflowInstance.DefinitionWorkflowType);
                return false;
            }

            string expectedGateStepUniqueName = FirstNonEmpty(data.GateStepUniqueName, data.WaitStepUniqueName, _options.GateStepUniqueName, string.Empty);

            if (!string.Equals(workflowInstance.CurrentStepUniqueName, expectedGateStepUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Workflow is not waiting at expected step. WorkflowInstanceId={0}, ExpectedStep={1}, ActualStep={2}.", workflowInstance.Id, expectedGateStepUniqueName, workflowInstance.CurrentStepUniqueName);
                return false;
            }

            if (workflowInstance.RenewedCertId == null)
            {
                Logger.LogError("Workflow detail did not include RenewedCertId. WorkflowInstanceId={0}.", workflowInstance.Id);
                return false;
            }

            if (workflowInstance.RenewedCertId.Value != data.ExpectedCertificateId)
            {
                Logger.LogError("Workflow RenewedCertId mismatch. WorkflowInstanceId={0}, WorkflowRenewedCertId={1}, ExpectedCertificateId={2}.", workflowInstance.Id, workflowInstance.RenewedCertId.Value, data.ExpectedCertificateId);
                return false;
            }

            if (!ContainsCertificateStoreId(workflowInstance.SuccessfulCertificateStoreIds, data.ExpectedCertificateStoreId))
            {
                Logger.LogError("Workflow SuccessfulCertificateStoreIds did not include expected store. WorkflowInstanceId={0}, SuccessfulStoreIds={1}, ExpectedStoreId={2}.", workflowInstance.Id, workflowInstance.SuccessfulCertificateStoreIds, data.ExpectedCertificateStoreId);
                return false;
            }

            return true;
        }

        private static int? GetValidationCertificateId(EndpointValidationResponse validationResponse)
        {
            if (validationResponse == null || string.IsNullOrWhiteSpace(validationResponse.CertificateId))
            {
                return null;
            }

            if (int.TryParse(validationResponse.CertificateId, out int certificateId))
            {
                return certificateId;
            }

            return null;
        }

        private async Task<List<WorkflowInstance>> FindWorkflowCandidatesForInventoryCompletion(HttpClient commandClient)
        {
            List<WorkflowInstance> suspendedInstances = await QuerySuspendedWorkflowInstances(commandClient);

            Logger.LogInformation("Suspended workflow candidate count before local filtering: {0}", suspendedInstances.Count);

            List<WorkflowInstance> matchingInstances = new List<WorkflowInstance>();

            foreach (WorkflowInstance candidate in suspendedInstances)
            {
                Logger.LogInformation(
                    "Candidate summary: Id={0}, Status={1}, DefinitionId={2}, WorkflowType={3}, Step={4}, CertificateId={5}, RenewedCertId={6}, SuccessfulStoreIds={7}",
                    candidate.Id,
                    candidate.Status,
                    candidate.DefinitionId,
                    candidate.DefinitionWorkflowType,
                    candidate.CurrentStepUniqueName,
                    candidate.CertificateId,
                    candidate.RenewedCertId,
                    candidate.SuccessfulCertificateStoreIds);

                if (!IsWorkflowSummaryCandidateMatch(candidate))
                {
                    Logger.LogInformation("Candidate summary did not match required workflow/status/step filters. CandidateId={0}", candidate.Id);
                    continue;
                }

                WorkflowInstance? detail = await ReadWorkflowInstance(commandClient, candidate.Id);

                if (detail == null)
                {
                    Logger.LogWarning("Unable to read workflow instance detail for candidate {0}.", candidate.Id);
                    continue;
                }

                Logger.LogInformation(
                    "Candidate detail: Id={0}, Status={1}, DefinitionId={2}, WorkflowType={3}, Step={4}, InitialCertificateId={5}, CertificateId={6}, RenewedCertId={7}, SuccessfulStoreIds={8}, ReferenceId={9}",
                    detail.Id,
                    detail.Status,
                    detail.DefinitionId,
                    detail.DefinitionWorkflowType,
                    detail.CurrentStepUniqueName,
                    detail.InitialCertificateId,
                    detail.CertificateId,
                    detail.RenewedCertId,
                    detail.SuccessfulCertificateStoreIds,
                    detail.ReferenceId);

                if (IsWorkflowInventoryCandidateMatch(detail))
                {
                    matchingInstances.Add(detail);
                }
                else
                {
                    Logger.LogInformation("Candidate detail did not pass inventory-completion correlation. CandidateId={0}.", detail.Id);
                }
            }

            return matchingInstances;
        }

        private bool IsWorkflowSummaryCandidateMatch(WorkflowInstance candidate)
        {
            if (!string.Equals(candidate.Status, "Suspended", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_options.WorkflowDefinitionId) && !string.Equals(candidate.DefinitionId, _options.WorkflowDefinitionId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(candidate.DefinitionWorkflowType, "Expiration", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(candidate.CurrentStepUniqueName, _options.GateStepUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private bool IsWorkflowInventoryCandidateMatch(WorkflowInstance candidate)
        {
            if (!IsWorkflowSummaryCandidateMatch(candidate))
            {
                return false;
            }

            if (candidate.RenewedCertId == null)
            {
                Logger.LogInformation("Candidate did not include RenewedCertId. CandidateId={0}, CertificateId={1}, InitialCertificateId={2}.", candidate.Id, candidate.CertificateId, candidate.InitialCertificateId);
                return false;
            }

            string? expectedStoreId = ResolveExpectedCertificateStoreId(candidate);

            if (string.IsNullOrWhiteSpace(expectedStoreId))
            {
                Logger.LogInformation("Certificate store correlation failed because no expected store ID could be derived. CandidateId={0}, SuccessfulStoreIds={1}.", candidate.Id, candidate.SuccessfulCertificateStoreIds);
                return false;
            }

            if (!ContainsCertificateStoreId(candidate.SuccessfulCertificateStoreIds, expectedStoreId))
            {
                Logger.LogInformation("Certificate store correlation failed. CandidateId={0}, SuccessfulStoreIds={1}, ExpectedStoreId={2}.", candidate.Id, candidate.SuccessfulCertificateStoreIds, expectedStoreId);
                return false;
            }

            return true;
        }


        private string? ResolveValidationProfileName(WorkflowInstance workflowInstance)
        {
            return FirstNonEmpty(
                GetWorkflowDataValue(workflowInstance, "EndpointValidationProfileName"),
                GetWorkflowDataValue(workflowInstance, "ValidationProfileName"),
                ResolveValidationProfileNameFromMapping(workflowInstance),
                _options.ValidationProfileName,
                string.Empty);
        }

        private string ResolveValidationProfileNameSource(WorkflowInstance workflowInstance)
        {
            if (!string.IsNullOrWhiteSpace(GetWorkflowDataValue(workflowInstance, "EndpointValidationProfileName")))
            {
                return "Workflow.CurrentOrInitialData.EndpointValidationProfileName";
            }

            if (!string.IsNullOrWhiteSpace(GetWorkflowDataValue(workflowInstance, "ValidationProfileName")))
            {
                return "Workflow.CurrentOrInitialData.ValidationProfileName";
            }

            if (!string.IsNullOrWhiteSpace(ResolveValidationProfileNameFromMapping(workflowInstance)))
            {
                return "HandlerManifest.EndpointValidationProfileMappings";
            }

            if (!string.IsNullOrWhiteSpace(_options.ValidationProfileName))
            {
                return "HandlerManifest.ValidationProfileNameFallback";
            }

            return "None";
        }

        private string? ResolveValidationProfileNameFromMapping(WorkflowInstance workflowInstance)
        {
            string? expectedStoreId = ResolveExpectedCertificateStoreId(workflowInstance);

            if (string.IsNullOrWhiteSpace(expectedStoreId) || string.IsNullOrWhiteSpace(_options.EndpointValidationProfileMappings))
            {
                return null;
            }

            string? defaultProfile = null;

            foreach (string rawEntry in _options.EndpointValidationProfileMappings.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = rawEntry.Trim();

                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                string[] parts;

                if (entry.Contains("="))
                {
                    parts = entry.Split(new[] { '=' }, 2);
                }
                else if (entry.Contains("|"))
                {
                    parts = entry.Split(new[] { '|' }, 2);
                }
                else
                {
                    Logger.LogWarning("Ignoring invalid EndpointValidationProfileMappings entry because it does not use storeId=profileName or storeId|profileName format. Entry={0}", entry);
                    continue;
                }

                if (parts.Length != 2)
                {
                    continue;
                }

                string storeId = parts[0].Trim();
                string profileName = parts[1].Trim();

                if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(profileName))
                {
                    continue;
                }

                if (string.Equals(storeId, "*", StringComparison.OrdinalIgnoreCase))
                {
                    defaultProfile = profileName;
                    continue;
                }

                if (string.Equals(storeId, expectedStoreId, StringComparison.OrdinalIgnoreCase))
                {
                    return profileName;
                }
            }

            return defaultProfile;
        }

        private string? ResolveExpectedCertificateStoreId(WorkflowInstance workflowInstance)
        {
            string configuredStoreId = FirstNonEmpty(
                GetWorkflowDataValue(workflowInstance, "EndpointValidationExpectedCertificateStoreId"),
                GetWorkflowDataValue(workflowInstance, "EndpointValidationCertificateStoreId"),
                GetWorkflowDataValue(workflowInstance, "CertificateStoreId"),
                _options.TargetCertificateStoreId,
                string.Empty);

            if (!string.IsNullOrWhiteSpace(configuredStoreId))
            {
                return configuredStoreId;
            }

            return GetSingleCertificateStoreId(workflowInstance.SuccessfulCertificateStoreIds);
        }

        private static string? GetWorkflowDataValue(WorkflowInstance workflowInstance, string key)
        {
            if (workflowInstance == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (workflowInstance.CurrentStateData.TryGetValue(key, out string? currentValue) && !string.IsNullOrWhiteSpace(currentValue))
            {
                return currentValue;
            }

            if (workflowInstance.InitialData.TryGetValue(key, out string? initialValue) && !string.IsNullOrWhiteSpace(initialValue))
            {
                return initialValue;
            }

            return null;
        }

        private static string? GetSingleCertificateStoreId(string? value)
        {
            List<string> ids = ParseDelimitedValues(value)
                .Where(item => Guid.TryParse(item, out Guid _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 1)
            {
                return ids[0];
            }

            return null;
        }

        private static List<string> ParseDelimitedValues(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            return value
                .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        private static bool ContainsCertificateStoreId(string? value, string? expectedStoreId)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expectedStoreId))
            {
                return false;
            }

            foreach (string part in ParseDelimitedValues(value))
            {
                if (string.Equals(part.Trim(), expectedStoreId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<List<WorkflowInstance>> QuerySuspendedWorkflowInstances(HttpClient commandClient)
        {
            string queryString = "Status -eq \"Suspended\"";
            string queryPath = "Workflow/Instances?pq.queryString=" + Uri.EscapeDataString(queryString);

            Logger.LogInformation("Querying suspended workflow instances with path: {0}", queryPath);

            HttpResponseMessage response = await commandClient.GetAsync(queryPath);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Suspended workflow instance query failed. Status={0}. Response={1}", response.StatusCode, responseText);
                return new List<WorkflowInstance>();
            }

            return ParseWorkflowInstances(responseText);
        }

        private async Task<WorkflowInstance?> ReadWorkflowInstance(HttpClient commandClient, string workflowInstanceId)
        {
            string path = "Workflow/Instances/" + workflowInstanceId;

            Logger.LogInformation("Reading workflow instance detail. Path={0}", path);

            HttpResponseMessage response = await commandClient.GetAsync(path);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Workflow instance detail query failed. InstanceId={0}, Status={1}, Response={2}", workflowInstanceId, response.StatusCode, responseText);
                return null;
            }

            List<WorkflowInstance> instances = ParseWorkflowInstances(responseText);

            if (instances.Count == 0)
            {
                return null;
            }

            return instances[0];
        }

        private async Task<bool> ReleaseWorkflowGate(HttpClient commandClient, string workflowInstanceId)
        {
            if (string.IsNullOrWhiteSpace(workflowInstanceId))
            {
                Logger.LogError("Cannot release workflow gate because workflowInstanceId was empty.");
                return false;
            }

            var payload = new
            {
                SignalKey = _options.WorkflowSignalKey,
                Data = new
                {
                    Approved = true,
                    Comment = _options.ReleaseComment
                }
            };

            string json = JsonSerializer.Serialize(payload);
            string path = "Workflow/Instances/" + workflowInstanceId + "/Signals";

            Logger.LogInformation("Submitting workflow signal. Path={0}, Payload={1}", path, json);

            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await commandClient.PostAsync(path, content);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Workflow signal failed. Status={0}. Response={1}", response.StatusCode, responseText);
                return false;
            }

            return true;
        }

        private static List<WorkflowInstance> ParseWorkflowInstances(string json)
        {
            List<WorkflowInstance> instances = new List<WorkflowInstance>();

            if (string.IsNullOrWhiteSpace(json))
            {
                return instances;
            }

            using JsonDocument document = JsonDocument.Parse(json);

            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    WorkflowInstance? instance = ParseWorkflowInstance(item);
                    if (instance != null)
                    {
                        instances.Add(instance);
                    }
                }

                return instances;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("Results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in results.EnumerateArray())
                    {
                        WorkflowInstance? instance = ParseWorkflowInstance(item);
                        if (instance != null)
                        {
                            instances.Add(instance);
                        }
                    }

                    return instances;
                }

                if (root.TryGetProperty("Data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in data.EnumerateArray())
                    {
                        WorkflowInstance? instance = ParseWorkflowInstance(item);
                        if (instance != null)
                        {
                            instances.Add(instance);
                        }
                    }

                    return instances;
                }

                WorkflowInstance? single = ParseWorkflowInstance(root);
                if (single != null)
                {
                    instances.Add(single);
                }
            }

            return instances;
        }

        private static WorkflowInstance? ParseWorkflowInstance(JsonElement element)
        {
            string? id = GetStringProperty(element, "Id") ?? GetStringProperty(element, "id");

            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            WorkflowInstance instance = new WorkflowInstance
            {
                Id = id,
                Status = GetStringProperty(element, "Status") ?? GetStringProperty(element, "status"),
                CurrentStepUniqueName = GetStringProperty(element, "CurrentStepUniqueName") ?? GetStringProperty(element, "currentStepUniqueName"),
                CurrentStepDisplayName = GetStringProperty(element, "CurrentStepDisplayName") ?? GetStringProperty(element, "currentStepDisplayName"),
                Title = GetStringProperty(element, "Title") ?? GetStringProperty(element, "title"),
                LastModified = GetDateTimeOffsetProperty(element, "LastModified") ?? GetDateTimeOffsetProperty(element, "lastModified"),
                StartDate = GetDateTimeOffsetProperty(element, "StartDate") ?? GetDateTimeOffsetProperty(element, "startDate"),
                ReferenceId = GetIntProperty(element, "ReferenceId") ?? GetIntProperty(element, "referenceId")
            };

            if (element.TryGetProperty("Definition", out JsonElement definition) && definition.ValueKind == JsonValueKind.Object)
            {
                instance.DefinitionId = GetStringProperty(definition, "Id") ?? GetStringProperty(definition, "id");
                instance.DefinitionDisplayName = GetStringProperty(definition, "DisplayName") ?? GetStringProperty(definition, "displayName");
                instance.DefinitionWorkflowType = GetStringProperty(definition, "WorkflowType") ?? GetStringProperty(definition, "workflowType");
            }

            if (element.TryGetProperty("InitialData", out JsonElement initialData) && initialData.ValueKind == JsonValueKind.Object)
            {
                instance.InitialData = ParseStringDictionary(initialData);
                instance.InitialCertificateId = GetIntProperty(initialData, "CertificateId") ?? GetIntProperty(initialData, "certificateId");
                instance.AlertId = GetIntProperty(initialData, "AlertId") ?? GetIntProperty(initialData, "alertId");
            }

            if (element.TryGetProperty("CurrentStateData", out JsonElement currentStateData) && currentStateData.ValueKind == JsonValueKind.Object)
            {
                instance.CurrentStateData = ParseStringDictionary(currentStateData);
                instance.CertificateId = GetIntProperty(currentStateData, "CertificateId") ?? GetIntProperty(currentStateData, "certificateId");
                instance.RenewedCertId = GetIntProperty(currentStateData, "RenewedCertId") ?? GetIntProperty(currentStateData, "renewedCertId");
                instance.AlertId = GetIntProperty(currentStateData, "AlertId") ?? GetIntProperty(currentStateData, "alertId") ?? instance.AlertId;

                instance.SuccessfulCertificateStoreIds =
                    GetStringProperty(currentStateData, "Successful Cert Store Ids") ??
                    GetStringProperty(currentStateData, "SuccessfulCertStoreIds") ??
                    GetStringProperty(currentStateData, "successfulCertStoreIds");

                instance.FailedCertificateStoreIds =
                    GetStringProperty(currentStateData, "Failed Cert Store Ids") ??
                    GetStringProperty(currentStateData, "FailedCertStoreIds") ??
                    GetStringProperty(currentStateData, "failedCertStoreIds");
            }

            return instance;
        }

        private static List<JobHistoryRecord> ParseJobHistoryRecords(string json)
        {
            List<JobHistoryRecord> records = new List<JobHistoryRecord>();

            if (string.IsNullOrWhiteSpace(json))
            {
                return records;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    JobHistoryRecord? record = ParseJobHistoryRecord(item);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }

                return records;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("Results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in results.EnumerateArray())
                    {
                        JobHistoryRecord? record = ParseJobHistoryRecord(item);
                        if (record != null)
                        {
                            records.Add(record);
                        }
                    }

                    return records;
                }

                if (root.TryGetProperty("Data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in data.EnumerateArray())
                    {
                        JobHistoryRecord? record = ParseJobHistoryRecord(item);
                        if (record != null)
                        {
                            records.Add(record);
                        }
                    }

                    return records;
                }

                JobHistoryRecord? single = ParseJobHistoryRecord(root);
                if (single != null)
                {
                    records.Add(single);
                }
            }

            return records;
        }

        private static JobHistoryRecord? ParseJobHistoryRecord(JsonElement element)
        {
            return new JobHistoryRecord
            {
                JobHistoryId = GetLongProperty(element, "JobHistoryId") ?? GetLongProperty(element, "jobHistoryId") ?? GetLongProperty(element, "Id") ?? GetLongProperty(element, "id"),
                AgentMachine = GetStringProperty(element, "AgentMachine") ?? GetStringProperty(element, "agentMachine"),
                JobId = GetStringProperty(element, "JobId") ?? GetStringProperty(element, "jobId"),
                JobType = GetStringProperty(element, "JobType") ?? GetStringProperty(element, "jobType") ?? GetStringProperty(element, "JobTypeName") ?? GetStringProperty(element, "jobTypeName"),
                OperationStart = GetDateTimeOffsetProperty(element, "OperationStart") ?? GetDateTimeOffsetProperty(element, "operationStart"),
                OperationEnd = GetDateTimeOffsetProperty(element, "OperationEnd") ?? GetDateTimeOffsetProperty(element, "operationEnd"),
                Message = GetStringProperty(element, "Message") ?? GetStringProperty(element, "message"),
                Result = GetStringProperty(element, "Result") ?? GetStringProperty(element, "result"),
                Status = GetStringProperty(element, "Status") ?? GetStringProperty(element, "status"),
                StorePath = GetStringProperty(element, "StorePath") ?? GetStringProperty(element, "storePath"),
                ClientMachine = GetStringProperty(element, "ClientMachine") ?? GetStringProperty(element, "clientMachine")
            };
        }


        private static Dictionary<string, string> ParseStringDictionary(JsonElement element)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (element.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? value = null;

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString();
                }
                else if (property.Value.ValueKind == JsonValueKind.Number || property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False)
                {
                    value = property.Value.ToString();
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    values[property.Name] = value;
                }
            }

            return values;
        }

        private static string? GetStringProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            if (property.ValueKind == JsonValueKind.Number || property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            {
                return property.ToString();
            }

            return null;
        }

        private static int? GetIntProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int intValue))
            {
                return intValue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                string? stringValue = property.GetString();

                if (int.TryParse(stringValue, out int parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static long? GetLongProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long longValue))
            {
                return longValue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                string? stringValue = property.GetString();

                if (long.TryParse(stringValue, out long parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static DateTimeOffset? GetDateTimeOffsetProperty(JsonElement element, string propertyName)
        {
            string? value = GetStringProperty(element, propertyName);

            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(value, out DateTimeOffset parsed))
            {
                return parsed;
            }

            return null;
        }

        private string ParseContext(OrchestratorJobCompleteHandlerContext context)
        {
            string[] pairs = new string[12];

            pairs[0] = string.Join(" : ", nameof(context.AgentId), context.AgentId.ToString());
            pairs[1] = string.Join(" : ", nameof(context.Username), context.Username);
            pairs[2] = string.Join(" : ", nameof(context.ClientMachine), context.ClientMachine);
            pairs[3] = string.Join(" : ", nameof(context.JobResult), context.JobResult.ToString());
            pairs[4] = string.Join(" : ", nameof(context.JobId), context.JobId);
            pairs[5] = string.Join(" : ", nameof(context.JobType), context.JobType);
            pairs[6] = string.Join(" : ", nameof(context.JobTypeId), context.JobTypeId.ToString());
            pairs[7] = string.Join(" : ", nameof(context.OperationType), context.OperationType.ToString());
            pairs[8] = string.Join(" : ", nameof(context.CertificateId), context.CertificateId == null ? "null" : context.CertificateId.ToString());
            pairs[9] = string.Join(" : ", nameof(context.RequestTimestamp), context.RequestTimestamp == null ? "null" : context.RequestTimestamp.ToString());
            pairs[10] = string.Join(" : ", nameof(context.CurrentRetryCount), context.CurrentRetryCount.ToString());
            pairs[11] = string.Join(" : ", nameof(context.Client), context.Client?.BaseAddress == null ? "null" : context.Client.BaseAddress.ToString());

            return string.Join(",\r\n", pairs);
        }

        private enum JobPhase
        {
            NotTarget = 0,
            RfpemInventory = 1,
            EndpointValidation = 2
        }

        private class WorkflowInstance
        {
            public string Id { get; set; } = string.Empty;
            public string? Status { get; set; }
            public string? CurrentStepUniqueName { get; set; }
            public string? CurrentStepDisplayName { get; set; }
            public string? Title { get; set; }
            public DateTimeOffset? LastModified { get; set; }
            public DateTimeOffset? StartDate { get; set; }
            public int? ReferenceId { get; set; }

            public string? DefinitionId { get; set; }
            public string? DefinitionDisplayName { get; set; }
            public string? DefinitionWorkflowType { get; set; }

            public int? AlertId { get; set; }
            public int? InitialCertificateId { get; set; }
            public int? CertificateId { get; set; }
            public int? RenewedCertId { get; set; }

            public string? SuccessfulCertificateStoreIds { get; set; }
            public string? FailedCertificateStoreIds { get; set; }

            public Dictionary<string, string> InitialData { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> CurrentStateData { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
