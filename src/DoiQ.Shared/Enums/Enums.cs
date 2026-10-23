using System.Text.Json.Serialization;

namespace DoiQ.Shared.Enums;

/// <summary>Enums travel as their names. Unlike the stock converter it refuses numbers (<c>2</c> or <c>"2"</c>), so an undefined value like 999 never gets in.</summary>
public sealed class StrictEnumConverter : JsonStringEnumConverter
{
    public StrictEnumConverter() : base(namingPolicy: null, allowIntegerValues: false) { }
}

/// <summary>Board column. Named BoardStatus (not TaskStatus) to avoid clashing with System.Threading.Tasks.TaskStatus.</summary>
[JsonConverter(typeof(StrictEnumConverter))]
public enum BoardStatus
{
    New = 0,
    Waiting = 1,
    Doing = 2,
    Done = 3,
}

[JsonConverter(typeof(StrictEnumConverter))]
public enum ActorKind
{
    Human = 0,
    Agent = 1,
}

[JsonConverter(typeof(StrictEnumConverter))]
public enum EventType
{
    Created = 0,
    Updated = 1,
    Moved = 2,
    Assigned = 3,
    FieldAdded = 4,
    FieldUpdated = 5,
    CommentAdded = 6,
    FieldDeleted = 7,
    SubtaskAdded = 8,
    SubtaskUpdated = 9,
    SubtaskDeleted = 10,
    Claimed = 11,
    Released = 12,
    Deleted = 13,
}

[JsonConverter(typeof(StrictEnumConverter))]
public enum BoardChangeKind
{
    Created = 0,
    Renamed = 1,
    Deleted = 2,
}

[JsonConverter(typeof(StrictEnumConverter))]
public enum TaskSource
{
    Manual = 0,
    Email = 1,
}
