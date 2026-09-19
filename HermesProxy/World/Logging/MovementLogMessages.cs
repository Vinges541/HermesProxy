using HermesProxy.World.Enums;
using Microsoft.Extensions.Logging;

namespace HermesProxy.World.Logging;

/// <summary>
/// Source-generated logging for movement info translation.
/// </summary>
/// <remarks>
/// EventId 1610-1619 is reserved for this file.
/// </remarks>
internal static partial class MovementLogMessages
{
    [LoggerMessage(
        EventId = 1610,
        Level = LogLevel.Error,
        Message = "Violation of MovementFlags found. MovementFlags: {Flags}, MovementFlags2: {FlagsExtra}. Mask {Mask} will be removed.")]
    public static partial void ViolatingFlagsRemoved(
        ILogger logger, uint flags, uint flagsExtra, MovementFlagModern mask);
}
