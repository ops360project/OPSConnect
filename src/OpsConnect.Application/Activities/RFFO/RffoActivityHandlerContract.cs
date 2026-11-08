using OpsConnect.Application.Activities.Common;

namespace OpsConnect.Application.Activities.RFFO;

/// <summary>
/// Marker and specialized contract for the RFFO (Request for Field Force Operations) activity update handler.
/// </summary>
public interface IRffoActivityUpdateHandler : IActivityUpdateHandler;

public static class RffoActivityDescriptor
{
    public const string ActivityCode = ActivityModuleConstants.RffoActivityCode;
    public const string DefaultActivityName = "Request for Field Force Operations";
}
