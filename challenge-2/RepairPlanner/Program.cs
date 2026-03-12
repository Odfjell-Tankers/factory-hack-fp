using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

// --- Read environment variables ---
var projectEndpoint = GetRequiredEnv("AZURE_AI_PROJECT_ENDPOINT");
var modelDeploymentName = GetRequiredEnv("MODEL_DEPLOYMENT_NAME");
var cosmosEndpoint = GetRequiredEnv("COSMOS_ENDPOINT");
var cosmosKey = GetRequiredEnv("COSMOS_KEY");
var cosmosDatabaseName = GetRequiredEnv("COSMOS_DATABASE_NAME");

// --- Set up DI container with logging ---
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.AddSingleton(new CosmosDbOptions(cosmosEndpoint, cosmosKey, cosmosDatabaseName));
services.AddSingleton<CosmosDbService>();
services.AddSingleton<IFaultMappingService, FaultMappingService>();

// await using — like Python's "async with", disposes resources when done
await using var provider = services.BuildServiceProvider();

var logger = provider.GetRequiredService<ILogger<Program>>();
var cosmosDb = provider.GetRequiredService<CosmosDbService>();
var faultMapping = provider.GetRequiredService<IFaultMappingService>();

// --- Create the AIProjectClient using DefaultAzureCredential ---
var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());

var agentLogger = provider.GetRequiredService<ILogger<RepairPlannerAgent>>();
var agent = new RepairPlannerAgent(projectClient, cosmosDb, faultMapping, modelDeploymentName, agentLogger);

// --- Register the agent in Azure AI Foundry ---
await agent.EnsureAgentVersionAsync();

// --- Create a sample diagnosed fault (simulating output from Challenge 1) ---
var sampleFault = new DiagnosedFault
{
    MachineId = "machine-001",
    FaultType = "curing_temperature_excessive",
    Severity = "high",
    Description = "Curing temperature exceeded safe threshold at 182°C, normal range is 165-175°C.",
    DetectedAt = DateTime.UtcNow,
};

// --- Run the repair planning workflow ---
var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);

// --- Print the result ---
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

logger.LogInformation(
    "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
    workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, workOrder.AssignedTo ?? "unassigned");

Console.WriteLine();
Console.WriteLine(JsonSerializer.Serialize(workOrder, jsonOptions));

// --- Helper ---
static string GetRequiredEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing required environment variable: {name}");
