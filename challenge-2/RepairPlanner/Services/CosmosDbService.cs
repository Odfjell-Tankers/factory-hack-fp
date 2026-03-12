using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService
{
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        _logger = logger;

        // Cosmos SDK uses Newtonsoft.Json by default — matches our model attributes
        var client = new CosmosClient(options.Endpoint, options.Key);
        var database = client.GetDatabase(options.DatabaseName);

        _techniciansContainer = database.GetContainer("Technicians");
        _partsContainer = database.GetContainer("PartsInventory");
        _workOrdersContainer = database.GetContainer("WorkOrders");
    }

    /// <summary>
    /// Find available technicians whose skills overlap with the required skills.
    /// </summary>
    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken ct = default)
    {
        // Query all available technicians, then filter by skill overlap in memory
        // (Cosmos SQL doesn't support ARRAY_INTERSECT natively across two dynamic lists)
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.available = true");

        var results = new List<Technician>();

        using var iterator = _techniciansContainer.GetItemQueryIterator<Technician>(
            query, requestOptions: new QueryRequestOptions { MaxItemCount = 50 });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var tech in response)
            {
                // Keep technicians that have at least one matching skill
                if (tech.Skills.Any(s => requiredSkills.Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    results.Add(tech);
                }
            }
        }

        _logger.LogInformation("Found {Count} available technicians matching skills.", results.Count);
        return results;
    }

    /// <summary>
    /// Fetch parts from inventory by their part numbers.
    /// </summary>
    public async Task<List<Part>> GetPartsByPartNumbersAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
        {
            _logger.LogInformation("No parts requested — returning empty list.");
            return [];
        }

        // Build parameterised IN clause: @p0, @p1, ...
        var parameters = new List<(string name, string value)>();
        for (int i = 0; i < partNumbers.Count; i++)
        {
            parameters.Add(($"@p{i}", partNumbers[i]));
        }

        var inClause = string.Join(", ", parameters.Select(p => p.name));
        var queryDef = new QueryDefinition(
            $"SELECT * FROM c WHERE c.partNumber IN ({inClause})");

        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var results = new List<Part>();

        using var iterator = _partsContainer.GetItemQueryIterator<Part>(
            queryDef, requestOptions: new QueryRequestOptions { MaxItemCount = 50 });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        _logger.LogInformation("Fetched {Count} parts for {Requested} part numbers.",
            results.Count, partNumbers.Count);
        return results;
    }

    /// <summary>
    /// Save a work order to the WorkOrders container.
    /// </summary>
    public async Task<string> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        // Partition key is /status
        var response = await _workOrdersContainer.CreateItemAsync(
            workOrder,
            new PartitionKey(workOrder.Status),
            cancellationToken: ct);

        _logger.LogInformation(
            "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo}).",
            workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, workOrder.AssignedTo ?? "unassigned");

        return response.Resource.Id;
    }
}
