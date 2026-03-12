using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

// Primary constructor — parameters become fields (like Python's __init__)
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        The output schema is enforced — populate all fields accurately.

        Rules:
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - estimatedDuration: integer minutes (e.g. 90)
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable with integer durations
        """;

    // LLMs sometimes return numbers as strings — handle that gracefully
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // JSON schema derived from WorkOrder type — enforces structured output from the LLM
    private static readonly JsonElement WorkOrderSchema =
        AIJsonUtilities.CreateJsonSchema<WorkOrder>(JsonOptions);

    /// <summary>Register (or update) the Prompt Agent definition in Azure AI Foundry.</summary>
    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Creating agent '{AgentName}' with model '{Model}'.", AgentName, modelDeploymentName);

        // Structured output — LLM is constrained to return JSON matching the WorkOrder schema
        var responseFormat = ChatResponseFormat.ForJsonSchema(
            WorkOrderSchema,
            schemaName: "work_order");

        logger.LogDebug("WorkOrder JSON schema: {Schema}", WorkOrderSchema.GetRawText());

        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions,
            ResponseFormat = responseFormat,
        };

        var version = await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            ct);

        logger.LogInformation("Agent version: {Version}", version.Value.Version);
    }

    /// <summary>
    /// End-to-end workflow: look up mappings → query Cosmos → invoke LLM → parse → save.
    /// </summary>
    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Planning repair for {MachineId}, fault={FaultType}.",
            fault.MachineId, fault.FaultType);

        // 1. Get required skills and parts from the static mapping
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredPartNumbers = faultMapping.GetRequiredParts(fault.FaultType);

        // 2. Query Cosmos DB for matching technicians and parts
        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var parts = await cosmosDb.GetPartsByPartNumbersAsync(requiredPartNumbers, ct);

        if (technicians.Count == 0)
        {
            logger.LogWarning(
                "No available technicians with required skills [{Skills}] for fault '{FaultType}'. " +
                "Work order will be created as unassigned.",
                string.Join(", ", requiredSkills), fault.FaultType);
        }

        if (parts.Count == 0 && requiredPartNumbers.Count > 0)
        {
            logger.LogWarning(
                "Required parts [{Parts}] not found in inventory for fault '{FaultType}'.",
                string.Join(", ", requiredPartNumbers), fault.FaultType);
        }

        // 3. Build a context-rich prompt for the LLM
        var prompt = BuildPrompt(fault, technicians, parts, requiredSkills, requiredPartNumbers);

        // 4. Invoke the Foundry agent
        logger.LogInformation("Invoking agent '{AgentName}'.", AgentName);
        var agent = projectClient.GetAIAgent(name: AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null, ct);
        var responseText = response.Text ?? "";

        // 5. Parse JSON response into a WorkOrder, applying safe defaults
        var workOrder = ParseWorkOrder(responseText, fault, technicians);

        // 6. Save to Cosmos DB
        await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

        return workOrder;
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        List<Technician> technicians,
        List<Part> parts,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyList<string> requiredPartNumbers)
    {
        var techSummary = technicians.Count == 0
            ? "No available technicians found."
            : string.Join("\n", technicians.Select(t =>
                $"- {t.Id} | {t.Name} | Skills: {string.Join(", ", t.Skills)} | Shift: {t.ShiftSchedule}"));

        var partsSummary = parts.Count == 0
            ? "No parts required or available."
            : string.Join("\n", parts.Select(p =>
                $"- {p.PartNumber} | {p.Name} | In stock: {p.QuantityInStock} | Cost: ${p.UnitCost}"));

        return $"""
            Create a repair work order for the following diagnosed fault:

            Machine ID: {fault.MachineId}
            Fault Type: {fault.FaultType}
            Severity: {fault.Severity}
            Description: {fault.Description}
            Detected At: {fault.DetectedAt:O}

            Required Skills: {string.Join(", ", requiredSkills)}
            Required Part Numbers: {string.Join(", ", requiredPartNumbers)}

            Available Technicians:
            {techSummary}

            Available Parts:
            {partsSummary}

            Populate all WorkOrder fields based on the context above.
            """;
    }

    private WorkOrder ParseWorkOrder(string responseText, DiagnosedFault fault, List<Technician> availableTechnicians)
    {
        // Structured output guarantees valid JSON, but keep defensive parsing as safety net
        var json = responseText.Trim();

        // Defensive: strip markdown fences in case structured output is bypassed
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        WorkOrder? workOrder;
        try
        {
            workOrder = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Structured output parse failed — falling back to defaults.");
            workOrder = null;
        }

        // ?? means "if null, use this instead" (like Python's "or")
        workOrder ??= new WorkOrder();

        // Apply required fields and safe defaults
        workOrder.Id = Guid.NewGuid().ToString();
        workOrder.MachineId = fault.MachineId;

        // ??= means "assign if null/empty" (like Python's: x = x or default_value)
        if (string.IsNullOrEmpty(workOrder.WorkOrderNumber))
            workOrder.WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyy}-{Random.Shared.Next(1000, 9999)}";

        if (string.IsNullOrEmpty(workOrder.Status))
            workOrder.Status = "new";

        // Override LLM priority with deterministic calculation based on fault severity
        workOrder.Priority = DeterminePriority(fault.Severity);
        workOrder.Type ??= "corrective";
        workOrder.CreatedDate = DateTime.UtcNow;

        // Clear assignment if no technicians are available
        if (availableTechnicians.Count == 0)
        {
            workOrder.AssignedTo = null;
            workOrder.Notes = string.IsNullOrEmpty(workOrder.Notes)
                ? "UNASSIGNED: No technicians with required skills are currently available."
                : $"{workOrder.Notes} | UNASSIGNED: No technicians with required skills are currently available.";
        }

        return workOrder;
    }

    /// <summary>
    /// Map fault severity to work order priority.
    /// Critical faults → critical priority, high → high, etc.
    /// </summary>
    private static string DeterminePriority(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => "critical",
        "high" => "high",
        "medium" => "medium",
        "low" => "low",
        _ => "medium", // default for unknown severities
    };
}
