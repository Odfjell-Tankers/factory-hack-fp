using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

/// <summary>Work order output saved to the WorkOrders Cosmos container.</summary>
public sealed class WorkOrder
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("workOrderNumber")]
    [JsonProperty("workOrderNumber")]
    public string WorkOrderNumber { get; set; } = string.Empty;

    [JsonPropertyName("machineId")]
    [JsonProperty("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonProperty("type")]
    public string Type { get; set; } = "corrective";

    [JsonPropertyName("priority")]
    [JsonProperty("priority")]
    public string Priority { get; set; } = "medium";

    // Partition key for the WorkOrders container
    [JsonPropertyName("status")]
    [JsonProperty("status")]
    public string Status { get; set; } = "new";

    [JsonPropertyName("assignedTo")]
    [JsonProperty("assignedTo")]
    public string? AssignedTo { get; set; }

    [JsonPropertyName("createdDate")]
    [JsonProperty("createdDate")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("scheduledDate")]
    [JsonProperty("scheduledDate")]
    public DateTime? ScheduledDate { get; set; }

    [JsonPropertyName("completedDate")]
    [JsonProperty("completedDate")]
    public DateTime? CompletedDate { get; set; }

    [JsonPropertyName("estimatedDuration")]
    [JsonProperty("estimatedDuration")]
    public int EstimatedDuration { get; set; }

    [JsonPropertyName("actualDuration")]
    [JsonProperty("actualDuration")]
    public int? ActualDuration { get; set; }

    [JsonPropertyName("partsUsed")]
    [JsonProperty("partsUsed")]
    public List<WorkOrderPartUsage> PartsUsed { get; set; } = [];

    [JsonPropertyName("tasks")]
    [JsonProperty("tasks")]
    public List<RepairTask> Tasks { get; set; } = [];

    [JsonPropertyName("notes")]
    [JsonProperty("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("cost")]
    [JsonProperty("cost")]
    public double Cost { get; set; }
}
