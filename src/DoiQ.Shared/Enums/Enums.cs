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
    /// <summary>Put on another board. The event belongs to the new board; its payload names both.</summary>
    MovedToBoard = 14,
}

[JsonConverter(typeof(StrictEnumConverter))]
public enum BoardChangeKind
{
    Created = 0,
    Renamed = 1,
    Deleted = 2,
}

/// <summary>
/// Where a task came from. An agent that makes a task from something it received sets the source and a sourceRef (the
/// id the sending system gave it). A board keeps one task per source and sourceRef, so a retried intake finds the task
/// it made the first time.
/// </summary>
[JsonConverter(typeof(StrictEnumConverter))]
public enum TaskSource
{
    Manual = 0,
    /// <summary>An email; sourceRef is its Message-ID.</summary>
    Email = 1,
    /// <summary>A text message (SMS or MMS); sourceRef is the message id the SMS provider gave it.</summary>
    Sms = 2,
    /// <summary>A phone call or voicemail; sourceRef is the call id the telephony provider gave it.</summary>
    PhoneCall = 3,
}
