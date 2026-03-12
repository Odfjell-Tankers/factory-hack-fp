namespace RepairPlanner.Services;

/// <summary>Configuration for the Cosmos DB connection.</summary>
public sealed record CosmosDbOptions(string Endpoint, string Key, string DatabaseName);
